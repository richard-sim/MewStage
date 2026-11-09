// NanoVgRenderer: MewVG's renderer contract (the internal Aprillz.MewVG.INVGRenderer) on MewStage.Graphics'
// IGraphicsDevice / IGraphicsRecorder, drawing through the cooked ui.hlsl. The reference is upstream's OpenGL renderer
// (third_party/MewVG/src/MewVG.GL/GLNVGContext.cs at the pinned MewVG v0.10.1); the internals are reachable through
// the vendored MewVG.Core's InternalsVisibleTo grant.
//
// How each MewVG call is drawn:
//   * FILL. MewVG hands a fill as separate rows: fringe-only paths (NFill == 0, the anti-aliasing strip) and one body
//     path (a TRIANGLE LIST from the tessellator, never a fan; see the NFill note at StripToTris). The body row's
//     Convex flag says whether body and fringe tile without overlap ("convex" is judged on FILL rows only; the fringe
//     rows are never flagged). Convex: body + fringe drawn directly. Otherwise ("stencil-once"): body and fringe drawn
//     with stencil Equal-0 -> Increment so every pixel is written ONCE, then the low stencil bits are cleared over the
//     bounds. Upstream GL accumulates such fills into an R8 coverage target with MAX blending; stencil-once gives the
//     same result wherever body and fringe overlap (the body draws first, at full coverage) and keeps the first-drawn
//     row where two fringe rows overlap, with the draw count of stencil-then-cover and without a render-target switch
//     inside the `ui` pass.
//   * STROKE. Direct, except a translucent stroke whose rows can overlap (not a single convex path): stencil-once over
//     the stroke's bounding box (upstream's PaintHasTransparency rule).
//   * CLIP (stencil bit 0x80, exclusion polarity: SET = clipped out; a clip stack intersects). MewVG insets the clip
//     body by half a fringe and adds fringe rows, because GL turns a clip into an anti-aliased coverage mask. The
//     stencil clip is binary, so it takes the coverage-0.5 contour: body winding, PLUS every fringe fragment whose
//     coverage is at least 0.5 (ClipCoverageThreshold). Without the fringe rows every clip shrinks by half a device
//     pixel.
//   * MASK FILL. A fill of 2000+ points arrives as an 8-bit coverage mask rasterized by the core (CoverageRasterizer):
//     a bounds quad shaded with the paint and multiplied by the mask (ui.hlsl t1), the mask image kept by the core's
//     MaskImageCache.
//   * Blending is premultiplied src-over for everything (ConvertPaint premultiplies the paint colors). Composite
//     operations other than SourceOver are not honored (MewUI uses none).
//
// Uploads go through this renderer's own persistent host-visible buffers, one per frame in flight (render-graph passes
// get only an IGraphicsRecorder), so the steady state allocates no managed memory. Per-draw selectors live in a CBV at
// b1 (UiView), the form ui.hlsl declares. Row-vector convention: ui.hlsl applies the 2x3 paint / scissor affines as
// mul(v, M), uploaded row-major exactly as MewVG lays them out.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Aprillz.MewVG;

using MewStage.Graphics;

namespace MewStage.Ui.Backend;

/// <summary>
/// The GPU backend for the vendored MewVG core: implements <see cref="INVGRenderer"/> plus the image API that
/// <see cref="UiMewVGContext"/> forwards, recording draws onto an <see cref="IGraphicsRecorder"/> with the cooked
/// <c>ui.hlsl</c> pipelines.
/// </summary>
internal sealed class NanoVgRenderer : INVGRenderer, IDisposable
{
    private const int UniformArraySize = 13;        // 13 float4 == ui.hlsl UiFrag.frag[13]
    private const int UniformFloatCount = UniformArraySize * 4;
    private const int UniformBytes = UniformFloatCount * 4;          // 208
    private const int UniformStride = 256;          // CBV alignment (D3D12); SPIR-V tolerant
    /// <summary>Shader-visible CBV/SRV descriptors one ui draw takes from the device's per-frame table budget (BindDraw:
    /// b0 paint CBV, b1 view CBV, t0 image SRV, t1 mask SRV; the DX12 recorder re-allocates the whole table per draw).
    /// UI draws x this must stay well inside the backend's per-frame descriptor capacity.</summary>
    public const int DescriptorsPerDraw = 4;

    /// <summary>Flushes a transient renderer keeps a coverage mask before rewriting it (see the constructor).</summary>
    internal const int TransientMaskFramesInFlight = 64;
    private const int ClipStencilRef = 0x80;        // the clip bit
    private const int LowStencilMask = 0x7F;        // winding / stencil-once bits; never disturb the clip bit

    /// <summary>A clip covers a fringe fragment when its anti-aliasing coverage is at least one half: the stencil
    /// clip is binary, and 0.5 is where upstream's coverage-mask clip crosses from mostly-out to mostly-in (the
    /// geometric path edge, MewVG placing the fringe's 0..1 ramp symmetrically across it).</summary>
    internal const float ClipCoverageThreshold = 0.5f;

    // ui.hlsl gType selector values (UiView.gType; NOT the GL GLNVGShaderType enum).
    private const int TypeGradRect = 0;     // gradient/solid filled rounded-rect
    private const int TypeImage = 1;        // image pattern
    private const int TypeStencilFill = 2;  // stencil-only pass (color write is off on its pipelines)
    private const int TypeTextGlyph = 3;    // text glyph (atlas)
    private const int TypeRadial = 6;       // radial gradient (LUT)
    private const int TypeLinear = 7;       // linear gradient (LUT)

    /// <summary>ui.hlsl's UiView CBV (b1), one per draw. Layout: three 16-byte registers, no member straddling a
    /// register (HLSL cbuffer packing), so the sequential C# layout is byte-identical.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct UiView
    {
        public Vector2 ViewSize;   // reg0.xy  framebuffer size in frame units
        public int Type;           // reg0.z   paint type
        public int TexType;        // reg0.w   texture interpretation
        public float FlipY;        // reg1.x   clip-space Y sign: +1 Vulkan, -1 DX12/Metal
        public float MaskScale;    // reg1.y   mask texels per frame unit (the device pixel ratio)
        public int MaskEnabled;    // reg1.z   1: multiply by the t1 coverage mask
        public float Pad;          // reg1.w
        public Vector2 MaskOrigin; // reg2.xy  mask top-left in frame units
        public Vector2 MaskSize;   // reg2.zw  mask size in texels
    }

    private enum CallType { None = 0, Clip, ClipReset, Fill, Stroke, Triangles, MaskFill }

    private enum Pso { Convex, Fringe, Triangles, ConvexClip, FringeClip, TrianglesClip, Once, OnceStrip, ClearLow, Winding, ClipFringe, ClipMark, ClipReset, Count }

    private struct Call
    {
        public CallType Type;
        public int Image;
        public int PathOffset;
        public int PathCount;
        public int TriangleOffset;      // fill: the clear quad (stencil-once); triangles: the vertices; clip: viewport quad
        public int TriangleCount;
        public int UniformOffset;
        public int MergedFringeOffset;
        public int MergedFringeCount;
        public bool MergedFringeIsStrip;
        public int MergedStrokeOffset;
        public int MergedStrokeCount;
        public bool MergedStrokeIsStrip;
        public bool StencilOnce;        // fill/stroke rows may overlap: draw each pixel once
        public int MaskImage;
        public float MaskOriginX, MaskOriginY;
    }

    private struct Path
    {
        public int FillOffset;
        public int FillCount;
        public int StrokeOffset;
        public int StrokeCount;
    }

    private struct Texture
    {
        public int Id;
        public NVGtextureType Type;
        public MewVGImageFlags Flags;
        public int Width;
        public int Height;
        public ITexture? Tex;
        public ITextureView? Srv;
        public ResourceState CurrentState;   // the texture's last barrier target (init Common); ShaderResource after each upload
        public bool External;                // an externally owned texture (CreateExternalImage): never uploaded into
    }

    private readonly IGraphicsDevice _device;
    private readonly byte[] _vs;
    private readonly byte[] _ps;
    private readonly Format _colorFormat;
    private readonly Format _depthStencilFormat;

    // ---- Batched texture upload ----
    // Per-glyph synchronous Acquire+Submit+Wait churn (one command allocator/list + fence + staging buffer per glyph)
    // exhausted the driver on text-heavy screen transitions (DXGI DEVICE_REMOVED). UpdateTexture/UploadTextureRegion
    // STAGE rows into an upload buffer and ENQUEUE a copy record; all queued copies are recorded onto ONE reused
    // command list + one fence and flushed in a SINGLE submit (ONE wait) at the start of Flush().
    private struct PendingUpload
    {
        public IBuffer Staging;        // host-visible CopySource (disposed after the batch submit completes)
        public ITexture DstTex;
        public ulong DstHandle;        // texture handle (groups copies to the same texture)
        public ResourceState Before;   // the texture's running state BEFORE this batch (group leader only)
        public int X, Y, W, H;
        public uint AlignedRowPitch;
        public uint Bpp;
    }
    private PendingUpload[] _pending = new PendingUpload[16];
    private int _pendingCount;
    private ICommandList? _uploadCmd;  // persistent, reused upload command list (NOT the frame's render list)
    private IFence? _uploadFence;      // persistent upload fence; monotonic _uploadFenceVal
    private ulong _uploadFenceVal;
    private readonly ICommandList[] _uploadLists = new ICommandList[1];
    private readonly FenceSignal[] _uploadSig = new FenceSignal[1];

    private IPipelineLayout? _layout;

    // The pipelines, one per Pso member, all over the same layout and shaders; see CreatePipelines for each one's
    // stencil state. Direct draws: Convex (TriangleList: convex body, multi-path fringe, multi-path stroke), Fringe
    // (TriangleStrip: single-path fringe or stroke, mask quad), Triangles (text glyphs); the *Clip variants are the
    // same plus the stencil test "clip bit clear". Once / OnceStrip are the stencil-once draws (Equal 0 over all bits,
    // so the clip bit is honored too, then increment the low bits); ClearLow zeroes the low bits over a quad. Winding
    // / ClipFringe / ClipMark / ClipReset are the four path-clip passes.
    private readonly IRenderPipeline?[] _pipelines = new IRenderPipeline?[(int)Pso.Count];
    private ISampler? _sampler;

    // CPU-side staging (persistent, reused: alloc-free steady state).
    private Call[] _calls = new Call[256];
    private int _callCount;
    private Path[] _paths = new Path[256];
    private int _pathCount;
    private NVGvertex[] _verts = new NVGvertex[8192];
    private int _vertCount;
    private float[][] _uniforms = Array.Empty<float[]>();
    private int _uniformCount;
    private int _uniformCapacity;

    private Texture[] _textures = new Texture[8];
    private int _textureCount;
    private int _textureId;
    private readonly int _dummyTex;
    private readonly MaskImageCache _maskImages;

    // GPU persistent buffers, one per frame in flight: the GpuScheduler runs FramesInFlight frames deep, so a single
    // buffer rewritten every frame would be overwritten while the GPU still reads it for an earlier frame (flicker).
    // _frameSlot advances ONCE per presented UI frame (in Flush); the scheduler's BeginFrame already waited that slot.
    private readonly int _framesInFlight;
    private int _frameSlot;
    private readonly IBuffer?[] _vb;
    private readonly ulong[] _vbCap;
    private readonly IBuffer?[] _ub;          // per-draw paint uniform ring (one UniformStride slot per uniform set)
    private readonly ulong[] _ubCap;
    private readonly IBuffer?[] _viewCbv;     // per-draw UiView ring (one UniformStride slot per bind)
    private readonly ulong[] _viewCbvCap;
    private float _viewW, _viewH, _devicePxRatio = 1f;
    // Clip-space Y sign written into UiView.FlipY: false => +1 (Vulkan NDC, Y-down); true => -1 (DX12/Metal, Y-up).
    private readonly bool _flipClipY;

    // Test introspection (InternalsVisibleTo MewStage.Rendering.Tests): the view-CBV sizing regression, and how the
    // last flush drew (the draw-path tests assert the path a geometry took, not only its pixels).
    internal int ViewCbvCapacitySlots => (int)(_viewCbvCap[_frameSlot] / UniformStride);
    internal int ViewDrawSeq { get; private set; }
    internal bool ViewGuardWarnedThisFlush { get; private set; }
    internal int LastFlushStencilOnceFills { get; private set; }
    internal int LastFlushConvexFills { get; private set; }
    internal int LastFlushStencilOnceStrokes { get; private set; }
    internal int LastFlushMaskFills { get; private set; }
    internal int LastFlushClipFringeDraws { get; private set; }

    /// <summary>How many NanoVG calls (fills, strokes, triangle batches, clips) the last flush recorded: the UI's draw
    /// count for a statistics readout.</summary>
    public int LastFlushCallCount { get; private set; }

    // Current recording target (set by Begin() before MewVGContext.EndFrame()->Flush()).
    private IGraphicsRecorder? _rec;

    // Scratch renderer (UiRenderDevice.ScratchVg): several flushes can land in ONE frame (every vector image or
    // bitmap cache rasterized that frame), more than the ring has slots, so a ring buffer rewritten per flush would be
    // overwritten while an earlier flush of the same frame still reads it. In this mode every flush gets fresh
    // buffers and the previous ones go through the device's deferred destroy; rasterizations are rare events (first
    // show, a size change), so the allocation is cheap where it matters.
    private readonly bool _transientBuffers;

    /// <summary>Create the renderer.</summary>
    /// <param name="device">The GPU device.</param>
    /// <param name="vs">The cooked ui.hlsl vertex shader.</param>
    /// <param name="ps">The cooked ui.hlsl pixel shader.</param>
    /// <param name="colorFormat">The color attachment format of every target this renderer draws into.</param>
    /// <param name="depthStencilFormat">The stencil-capable depth format (D24S8).</param>
    /// <param name="flipClipY">True on DX12 / Metal (clip Y up), false on Vulkan.</param>
    /// <param name="framesInFlight">The scheduler's frame depth (ring buffers).</param>
    /// <param name="transientBuffers">True for a renderer that flushes several times per frame into offscreen
    /// targets (scratch surfaces): fresh vertex / uniform buffers per flush, old ones deferred-destroyed.</param>
    public NanoVgRenderer(IGraphicsDevice device, byte[] vs, byte[] ps, Format colorFormat, Format depthStencilFormat, bool flipClipY = false, int framesInFlight = 2, bool transientBuffers = false)
    {
        _device = device;
        _transientBuffers = transientBuffers;
        _framesInFlight = framesInFlight < 1 ? 1 : framesInFlight;
        _vb = new IBuffer?[_framesInFlight];
        _vbCap = new ulong[_framesInFlight];
        _ub = new IBuffer?[_framesInFlight];
        _ubCap = new ulong[_framesInFlight];
        _viewCbv = new IBuffer?[_framesInFlight];
        _viewCbvCap = new ulong[_framesInFlight];
        _vs = vs;
        _ps = ps;
        _colorFormat = colorFormat;
        _depthStencilFormat = depthStencilFormat;
        _flipClipY = flipClipY;
        CreatePipelines();
        _dummyTex = CreateTexture(NVGtextureType.Alpha, 1, 1, 0, [0]);
        // Coverage masks: R8 images read texel-exact (ui.hlsl Load), rewritten in place only once every frame in
        // flight that may still read them has passed (the cache tracks that with _framesInFlight).
        // A transient renderer advances its "frame" once per flush, so a mask reuse window of the scheduler's depth
        // would be too short; its masks are kept for many flushes before they may be rewritten.
        _maskImages = new MaskImageCache(
            (width, height, coverage) => CreateTexture(NVGtextureType.Alpha, width, height, MewVGImageFlags.Nearest, coverage),
            (image, width, height, coverage) => UpdateTexture(image, 0, 0, width, height, coverage),
            DeleteTexture,
            transientBuffers ? TransientMaskFramesInFlight : _framesInFlight);
    }

    /// <summary>Bind the recorder this frame's <see cref="Flush"/> records onto. Call inside a render pass (after
    /// BeginRendering) and BEFORE EndFrame. The depth-stencil attachment MUST have a stencil aspect cleared to 0.</summary>
    public void Begin(IGraphicsRecorder recorder)
    {
        _rec = recorder;
    }

    // ---- multi-window batch (one `ui` pass composites the visible screen stack) ----
    // While batching, N windows' BeginFrame/EndFrame cycles accumulate into ONE frame's CPU staging; EndBatch performs
    // the single real flush (one ring-slot advance, one upload, every window's draws in command order).
    private bool _batching;

    /// <summary>Open a multi-window batch: reset the CPU staging once; subsequent per-window BeginFrame/EndFrame
    /// cycles accumulate into it until <see cref="EndBatch"/> flushes once.</summary>
    public void BeginBatch()
    {
        _batching = true;
        _maskImages.BeginFrame();
        _callCount = 0;
        _pathCount = 0;
        _vertCount = 0;
        _uniformCount = 0;
    }

    /// <summary>Close the batch and perform the ONE real flush.</summary>
    public void EndBatch()
    {
        _batching = false;
        Flush();
    }

    // ---- INVGRenderer ----

    public void BeginFrame(float windowWidth, float windowHeight, float devicePixelRatio)
    {
        _viewW = windowWidth;
        _viewH = windowHeight;
        _devicePxRatio = devicePixelRatio > 0f ? devicePixelRatio : 1f;
        // In a batch the staging (and the mask cache's frame) advanced ONCE in BeginBatch for the presented frame.
        if (_batching)
        {
            return;
        }

        _maskImages.BeginFrame();
        _callCount = 0;
        _pathCount = 0;
        _vertCount = 0;
        _uniformCount = 0;
    }

    public void Cancel()
    {
        _batching = false;   // a canceled frame ends any open batch
        // A dropped frame may still have queued uploads (glyphs, masks); flush them so their staging buffers are
        // submitted and freed rather than leaked.
        FlushPendingUploads();
        _callCount = 0;
        _pathCount = 0;
        _vertCount = 0;
        _uniformCount = 0;
    }

    public void Flush()
    {
        if (_batching)
        {
            return;   // EndBatch does the real flush once for the whole window stack
        }
        // Uploads (atlas, masks) MUST complete before any draw samples them. The host's `ui` pass records them on the
        // frame's own command list before its render pass (RecordPendingUploads), so normally none are left here; any
        // that are (standalone use, tests) go as a SEPARATE submission (copies are forbidden inside a render pass on
        // Vulkan).
        FlushPendingUploads();
        _drawGuardWarned = false;
        ViewGuardWarnedThisFlush = false;
        LastFlushStencilOnceFills = LastFlushConvexFills = LastFlushStencilOnceStrokes = LastFlushMaskFills = LastFlushClipFringeDraws = 0;

        // Rotate to this frame's ring slot ONCE per presented UI frame (Flush == one UI frame).
        _frameSlot = (_frameSlot + 1) % _framesInFlight;
        if (_transientBuffers)
        {
            RetireSlotBuffers(_frameSlot);
        }

        LastFlushCallCount = _callCount;
        if (_callCount <= 0 || _rec is null)
        {
            _callCount = _pathCount = _vertCount = _uniformCount = 0;
            return;
        }

        UploadVertices();
        UploadUniforms();
        ViewDrawSeq = 0;
        EnsureViewCbv();

        IGraphicsRecorder rec = _rec;
        bool clipActive = false;   // toggled by Clip/ClipReset in stream order; selects the clip-tested pipelines
        for (int i = 0; i < _callCount; i++)
        {
            ref Call call = ref _calls[i];
            switch (call.Type)
            {
                case CallType.Fill:
                    DoFill(rec, in call, clipActive);
                    break;
                case CallType.Stroke:
                    DoStroke(rec, in call, clipActive);
                    break;
                case CallType.Triangles:
                    DoTriangles(rec, in call, clipActive);
                    break;
                case CallType.MaskFill:
                    DoMaskFill(rec, in call, clipActive);
                    break;
                case CallType.Clip:
                    ExecuteClip(rec, in call);
                    clipActive = true;
                    break;
                case CallType.ClipReset:
                    ExecuteClipReset(rec, in call);
                    clipActive = false;
                    break;
                default:
                    break;
            }
        }

        _callCount = _pathCount = _vertCount = _uniformCount = 0;
    }

    public void RenderFill(
        ref MewVGPaint paint,
        NVGcompositeOperationState compositeOperation,
        ref NVGscissorState scissor,
        float fringe,
        ReadOnlySpan<float> bounds,
        ReadOnlySpan<NVGpathData> paths,
        ReadOnlySpan<NVGvertex> verts)
    {
        ref Call call = ref AllocCall();
        call.Type = CallType.Fill;
        call.PathOffset = AllocPaths(paths.Length);
        call.PathCount = paths.Length;
        call.Image = paint.Image;

        // Convexity is judged on FILL rows only (upstream GLNVGContext.RenderFill): MewVG emits the fringe as separate
        // rows that are never flagged, so "one path and it is convex" is false for every anti-aliased fill.
        int fillRows = 0, fillRow = -1;
        for (int i = 0; i < paths.Length; i++)
        {
            if (paths[i].NFill > 0)
            {
                fillRows++;
                fillRow = i;
            }
        }

        call.StencilOnce = !(fillRows == 1 && paths[fillRow].Convex);

        bool singleFringePath = paths.Length == 1 && paths[0].NStroke > 0;
        int maxVerts = 4;   // the stencil-once clear quad
        for (int i = 0; i < paths.Length; i++)
        {
            maxVerts += paths[i].NFill + (singleFringePath ? paths[i].NStroke : StripToTriCount(paths[i].NStroke));
        }

        int vertOffset = AllocVerts(maxVerts);

        // Body rows: copied through unchanged (a triangle list; see the NFill note at StripToTris).
        for (int i = 0; i < paths.Length; i++)
        {
            ref Path copy = ref _paths[call.PathOffset + i];
            copy = default;
            ref readonly NVGpathData path = ref paths[i];
            if (path.NFill > 0)
            {
                ReadOnlySpan<NVGvertex> fillSrc = verts.Slice(path.FillOffset, path.NFill);
                copy.FillOffset = vertOffset;
                fillSrc.CopyTo(_verts.AsSpan(vertOffset));
                copy.FillCount = fillSrc.Length;
                vertOffset += fillSrc.Length;
            }
        }

        // Fringe rows: a strip each; converted to triangles when there is more than one so they draw in one call.
        call.MergedFringeOffset = vertOffset;
        call.MergedFringeIsStrip = singleFringePath;
        for (int i = 0; i < paths.Length; i++)
        {
            ref Path copy = ref _paths[call.PathOffset + i];
            ref readonly NVGpathData path = ref paths[i];
            if (path.NStroke > 0)
            {
                ReadOnlySpan<NVGvertex> strokeSrc = verts.Slice(path.StrokeOffset, path.NStroke);
                copy.StrokeOffset = vertOffset;
                int written = singleFringePath ? CopyStrip(strokeSrc, _verts.AsSpan(vertOffset)) : StripToTris(strokeSrc, _verts.AsSpan(vertOffset));
                copy.StrokeCount = written;
                vertOffset += written;
            }
        }

        call.MergedFringeCount = vertOffset - call.MergedFringeOffset;

        // The stencil-once clear quad: the bounds grown by one fringe (the fringe reaches half a fringe past them;
        // upstream's coverage pass uses the same margin).
        call.TriangleOffset = vertOffset;
        call.TriangleCount = 4;
        WriteQuad(_verts.AsSpan(vertOffset, 4), bounds[0] - fringe, bounds[1] - fringe, bounds[2] + fringe, bounds[3] + fringe);

        call.UniformOffset = AllocUniforms(1);
        if (!ConvertPaint(_uniforms[call.UniformOffset], ref paint, ref scissor, fringe, fringe, -1.0f))
        {
            call.Type = CallType.None;
            return;
        }   // paint failed: mark the call dead so Flush skips it

        SetVal(_uniforms[call.UniformOffset], 12, 0, -1.0f); // strokeMult < 0 => analytic fill coverage
    }

    public void RenderStroke(
        ref MewVGPaint paint,
        NVGcompositeOperationState compositeOperation,
        ref NVGscissorState scissor,
        float fringe,
        float strokeWidth,
        ReadOnlySpan<NVGpathData> paths,
        ReadOnlySpan<NVGvertex> verts)
    {
        ref Call call = ref AllocCall();
        call.Type = CallType.Stroke;
        call.PathOffset = AllocPaths(paths.Length);
        call.PathCount = paths.Length;
        call.Image = paint.Image;
        // Rows that can overlap only show when the paint is translucent (an opaque overlap is invisible).
        bool convexStroke = paths.Length == 1 && paths[0].Convex;
        call.StencilOnce = !convexStroke && PaintHasTransparency(in paint);

        bool singleStrokePath = paths.Length == 1;
        int maxVerts = 4;   // the stencil-once clear quad
        for (int i = 0; i < paths.Length; i++)
        {
            maxVerts += singleStrokePath ? paths[i].NStroke : StripToTriCount(paths[i].NStroke);
        }

        int vertOffset = AllocVerts(maxVerts);
        call.MergedStrokeOffset = vertOffset;
        call.MergedStrokeIsStrip = singleStrokePath;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int i = 0; i < paths.Length; i++)
        {
            ref Path copy = ref _paths[call.PathOffset + i];
            copy = default;
            ref readonly NVGpathData path = ref paths[i];
            if (path.NStroke > 0)
            {
                copy.StrokeOffset = vertOffset;
                ReadOnlySpan<NVGvertex> strokeSrc = verts.Slice(path.StrokeOffset, path.NStroke);
                if (call.StencilOnce)
                {
                    for (int v = 0; v < strokeSrc.Length; v++)
                    {
                        x0 = MathF.Min(x0, strokeSrc[v].X);
                        y0 = MathF.Min(y0, strokeSrc[v].Y);
                        x1 = MathF.Max(x1, strokeSrc[v].X);
                        y1 = MathF.Max(y1, strokeSrc[v].Y);
                    }
                }

                int written = singleStrokePath ? CopyStrip(strokeSrc, _verts.AsSpan(vertOffset)) : StripToTris(strokeSrc, _verts.AsSpan(vertOffset));
                copy.StrokeCount = written;
                vertOffset += written;
            }
        }

        call.MergedStrokeCount = vertOffset - call.MergedStrokeOffset;
        if (call.StencilOnce && x0 <= x1)
        {
            // The stroke vertices already include its fringe; one more fringe covers rasterization at the edge.
            call.TriangleOffset = vertOffset;
            call.TriangleCount = 4;
            WriteQuad(_verts.AsSpan(vertOffset, 4), x0 - fringe, y0 - fringe, x1 + fringe, y1 + fringe);
        }
        else
        {
            call.StencilOnce = false;
        }

        call.UniformOffset = AllocUniforms(1);
        if (!ConvertPaint(_uniforms[call.UniformOffset], ref paint, ref scissor, strokeWidth, fringe, -1.0f))
        {
            call.Type = CallType.None;
            return;
        }
    }

    private static bool PaintHasTransparency(in MewVGPaint paint)
    {
        return paint.InnerColor.A < OpaqueAlpha || paint.OuterColor.A < OpaqueAlpha;
    }

    /// <summary>Upstream GLNVGContext.PaintHasTransparency's threshold: an alpha this close to 1 is opaque.</summary>
    private const float OpaqueAlpha = 0.999f;

    public void RenderMaskFill(
        ref MewVGPaint paint,
        NVGcompositeOperationState compositeOperation,
        ref NVGscissorState scissor,
        float fringe,
        ReadOnlySpan<float> bounds,
        ReadOnlySpan<byte> coverage,
        int maskWidth,
        int maskHeight,
        float maskOriginX,
        float maskOriginY,
        object? cacheKey,
        int cacheVersion)
    {
        int maskImage = _maskImages.Acquire(coverage, maskWidth, maskHeight, cacheKey, cacheVersion);
        if (maskImage == 0)
        {
            return;
        }

        ref Call call = ref AllocCall();
        call.Type = CallType.MaskFill;
        call.Image = paint.Image;
        call.MaskImage = maskImage;
        call.MaskOriginX = maskOriginX;
        call.MaskOriginY = maskOriginY;
        // Bounds quad with the fill-body texcoord (0.5, 1): the analytic fill coverage reads 1, so only the mask
        // shapes the coverage (upstream GLNVGContext.RenderMaskFill).
        call.TriangleOffset = AllocVerts(4);
        call.TriangleCount = 4;
        WriteQuad(_verts.AsSpan(call.TriangleOffset, 4), bounds[0], bounds[1], bounds[2], bounds[3]);

        call.UniformOffset = AllocUniforms(1);
        if (!ConvertPaint(_uniforms[call.UniformOffset], ref paint, ref scissor, fringe, fringe, -1.0f))
        {
            call.Type = CallType.None;
            return;
        }

        SetVal(_uniforms[call.UniformOffset], 12, 0, -1.0f); // strokeMult < 0 => analytic fill coverage
    }

    /// <summary>The axis-aligned scissor a text run is clipped to (world DIPs): the graphics context's clip
    /// bounds. Path clips need nothing here - they are in the stencil, which the clip-tested pipelines honor.</summary>
    internal readonly struct TextClip
    {
        public TextClip(float x, float y, float width, float height, float devicePxPerDip)
        {
            Enabled = true;
            CenterX = x + (width * 0.5f);
            CenterY = y + (height * 0.5f);
            ExtentX = MathF.Max(0f, width * 0.5f);
            ExtentY = MathF.Max(0f, height * 0.5f);
            DevicePxPerDip = devicePxPerDip > 0f ? devicePxPerDip : 1f;
        }
        public readonly bool Enabled;
        public readonly float CenterX, CenterY, ExtentX, ExtentY, DevicePxPerDip;
    }

    /// <summary>FontStashSharp text sink: glyph triangles sampling the font atlas (ui.hlsl type 3), modulated by
    /// <paramref name="color"/> and clipped to <paramref name="clip"/>. Called by FontStashQuadSink.DrawQuad.</summary>
    internal void DrawTextTriangles(ReadOnlySpan<NVGvertex> tris, int image, MewVGColor color, in TextClip clip = default)
    {
        ref Call call = ref AllocCall();
        call.Type = CallType.Triangles;
        call.Image = image;
        call.TriangleOffset = AllocVerts(tris.Length);
        call.TriangleCount = tris.Length;
        tris.CopyTo(_verts.AsSpan(call.TriangleOffset, tris.Length));

        call.UniformOffset = AllocUniforms(1);
        float[] frag = _uniforms[call.UniformOffset];
        Array.Clear(frag);
        MewVGColor inner = Premultiply(color);
        SetVec4(frag, 6, inner.R, inner.G, inner.B, inner.A);
        if (clip.Enabled)
        {
            // ui.hlsl ScissorMask: |inverse(scissorXform) * p| - extent, scaled to a one-device-pixel ramp (what
            // ConvertPaint writes for a NanoVG scissor: scale = |xform column| / fringe, fringe = 1 device px).
            SetVec4(frag, 0, 1f, 0f, 0f, 0f);
            SetVec4(frag, 1, 0f, 1f, 0f, 0f);
            SetVec4(frag, 2, -clip.CenterX, -clip.CenterY, 1f, 0f);
            SetVec4(frag, 8, clip.ExtentX, clip.ExtentY, clip.DevicePxPerDip, clip.DevicePxPerDip);
        }
        else
        {
            SetVec4(frag, 8, 1f, 1f, 1f, 1f);             // no scissor (zero matrix: ScissorMask reads 1)
        }

        SetVal(frag, 12, 1, -1.0f);                       // strokeThr (no discard)
        SetVal(frag, 12, 2, 0f);                          // texType 0 (atlas, straight)
        SetVal(frag, 12, 3, TypeTextGlyph);
    }

    public void RenderClip(
        ref NVGscissorState scissor,
        float fringe,
        ReadOnlySpan<float> bounds,
        ReadOnlySpan<NVGpathData> paths,
        ReadOnlySpan<NVGvertex> verts)
    {
        // The clip's body rows (winding) and fringe rows (coverage >= 0.5), plus a full-viewport quad for the
        // mark / clear passes. ExecuteClip sets the clip bit OUTSIDE the result (exclusion polarity).
        ref Call call = ref AllocCall();
        call.Type = CallType.Clip;
        call.PathOffset = AllocPaths(paths.Length);
        call.PathCount = paths.Length;

        int maxVerts = 4;
        for (int i = 0; i < paths.Length; i++)
        {
            maxVerts += paths[i].NFill + StripToTriCount(paths[i].NStroke);
        }

        int vertOffset = AllocVerts(maxVerts);
        for (int i = 0; i < paths.Length; i++)
        {
            ref Path copy = ref _paths[call.PathOffset + i];
            copy = default;
            ref readonly NVGpathData path = ref paths[i];
            if (path.NFill > 0)
            {
                ReadOnlySpan<NVGvertex> fillSrc = verts.Slice(path.FillOffset, path.NFill);
                copy.FillOffset = vertOffset;
                fillSrc.CopyTo(_verts.AsSpan(vertOffset));
                copy.FillCount = fillSrc.Length;
                vertOffset += fillSrc.Length;
            }
        }

        call.MergedFringeOffset = vertOffset;
        for (int i = 0; i < paths.Length; i++)
        {
            if (paths[i].NStroke > 0)
            {
                vertOffset += StripToTris(verts.Slice(paths[i].StrokeOffset, paths[i].NStroke), _verts.AsSpan(vertOffset));
            }
        }

        call.MergedFringeCount = vertOffset - call.MergedFringeOffset;
        call.TriangleOffset = vertOffset;
        call.TriangleCount = 4;
        WriteQuad(_verts.AsSpan(vertOffset, 4), 0f, 0f, _viewW, _viewH);

        // [0] winding / mark / clear: a stencil-only uniform that never discards. [1] the fringe pass: analytic fill
        // coverage (strokeMult < 0) with strokeThr = 0.5, so ui.hlsl discards the outer half of the ramp.
        call.UniformOffset = AllocUniforms(2);
        float[] u = _uniforms[call.UniformOffset];
        Array.Clear(u);
        SetVal(u, 12, 1, -1.0f);
        SetVal(u, 12, 3, TypeStencilFill);
        float[] f = _uniforms[call.UniformOffset + 1];
        Array.Clear(f);
        SetVal(f, 12, 0, -1.0f);
        SetVal(f, 12, 1, ClipCoverageThreshold);
        SetVal(f, 12, 3, TypeStencilFill);
    }

    public void ResetClip()
    {
        ref Call call = ref AllocCall();
        call.Type = CallType.ClipReset;
        call.TriangleOffset = AllocVerts(4);
        WriteQuad(_verts.AsSpan(call.TriangleOffset, 4), 0f, 0f, _viewW, _viewH);
        call.TriangleCount = 4;
        call.UniformOffset = AllocUniforms(1);
        float[] u = _uniforms[call.UniformOffset];
        Array.Clear(u);
        SetVal(u, 12, 1, -1.0f);
        SetVal(u, 12, 3, TypeStencilFill);
    }

    /// <summary>An axis-aligned quad as a TriangleStrip, with the fill-body texcoord (0.5, 1) = full coverage.</summary>
    private static void WriteQuad(Span<NVGvertex> quad, float x0, float y0, float x1, float y1)
    {
        quad[0] = new NVGvertex(x1, y1, 0.5f, 1.0f);
        quad[1] = new NVGvertex(x1, y0, 0.5f, 1.0f);
        quad[2] = new NVGvertex(x0, y1, 0.5f, 1.0f);
        quad[3] = new NVGvertex(x0, y0, 0.5f, 1.0f);
    }

    private static int CopyStrip(ReadOnlySpan<NVGvertex> strip, Span<NVGvertex> outp)
    {
        strip.CopyTo(outp);
        return strip.Length;
    }

    // ---- draw dispatch ----

    private void DoFill(IGraphicsRecorder rec, in Call call, bool clip)
    {
        Span<Path> paths = _paths.AsSpan(call.PathOffset, call.PathCount);
        int paintType = PaintType(call.UniformOffset);
        int bodyStart = -1, bodyCount = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            if (paths[i].FillCount > 0)
            {
                if (bodyStart < 0)
                {
                    bodyStart = paths[i].FillOffset;
                }

                bodyCount += paths[i].FillCount;
            }
        }

        if (!call.StencilOnce)
        {
            LastFlushConvexFills++;
            // Bind the paint type ConvertPaint wrote (LUT gradient, image or box gradient). A hard-coded
            // box-gradient type here paints every LUT-gradient and image fill on this path WHITE (gate: the gradient
            // sink test).
            rec.SetPipeline(clip ? Pipeline(Pso.ConvexClip) : Pipeline(Pso.Convex));
            if (clip)
            {
                rec.SetStencilReference(0);
            }

            BindDraw(rec, call.UniformOffset, call.Image, paintType);
            if (bodyStart >= 0)
            {
                SafeDraw(rec, bodyCount, bodyStart, "fill.body");
            }

            if (call.MergedFringeCount > 0)
            {
                rec.SetPipeline(call.MergedFringeIsStrip ? (clip ? Pipeline(Pso.FringeClip) : Pipeline(Pso.Fringe)) : (clip ? Pipeline(Pso.ConvexClip) : Pipeline(Pso.Convex)));
                if (clip)
                {
                    rec.SetStencilReference(0);
                }

                BindDraw(rec, call.UniformOffset, call.Image, paintType);
                SafeDraw(rec, call.MergedFringeCount, call.MergedFringeOffset, "fill.fringe");
            }

            return;
        }

        // Stencil-once: the body first (full coverage wins every overlap), then the fringe, then clear the low bits.
        // Equal-0 over all eight bits also rejects fragments whose clip bit is set, so one pipeline serves both.
        LastFlushStencilOnceFills++;
        rec.SetPipeline(Pipeline(Pso.Once));
        rec.SetStencilReference(0);
        BindDraw(rec, call.UniformOffset, call.Image, paintType);
        if (bodyStart >= 0)
        {
            SafeDraw(rec, bodyCount, bodyStart, "fill.once.body");
        }

        if (call.MergedFringeCount > 0)
        {
            rec.SetPipeline(call.MergedFringeIsStrip ? Pipeline(Pso.OnceStrip) : Pipeline(Pso.Once));
            rec.SetStencilReference(0);
            BindDraw(rec, call.UniformOffset, call.Image, paintType);
            SafeDraw(rec, call.MergedFringeCount, call.MergedFringeOffset, "fill.once.fringe");
        }

        ClearLowBits(rec, in call, "fill.once.clear");
    }

    private void DoStroke(IGraphicsRecorder rec, in Call call, bool clip)
    {
        if (call.MergedStrokeCount <= 0)
        {
            return;
        }

        int paintType = PaintType(call.UniformOffset);
        if (call.StencilOnce)
        {
            LastFlushStencilOnceStrokes++;
            rec.SetPipeline(call.MergedStrokeIsStrip ? Pipeline(Pso.OnceStrip) : Pipeline(Pso.Once));
            rec.SetStencilReference(0);
            BindDraw(rec, call.UniformOffset, call.Image, paintType);
            SafeDraw(rec, call.MergedStrokeCount, call.MergedStrokeOffset, "stroke.once");
            ClearLowBits(rec, in call, "stroke.once.clear");
            return;
        }

        rec.SetPipeline(call.MergedStrokeIsStrip ? (clip ? Pipeline(Pso.FringeClip) : Pipeline(Pso.Fringe)) : (clip ? Pipeline(Pso.TrianglesClip) : Pipeline(Pso.Triangles)));
        if (clip)
        {
            rec.SetStencilReference(0);
        }

        BindDraw(rec, call.UniformOffset, call.Image, paintType);
        SafeDraw(rec, call.MergedStrokeCount, call.MergedStrokeOffset, "stroke");
    }

    private void ClearLowBits(IGraphicsRecorder rec, in Call call, string tag)
    {
        rec.SetPipeline(Pipeline(Pso.ClearLow));
        rec.SetStencilReference(0);
        BindDraw(rec, call.UniformOffset, call.Image, PaintType(call.UniformOffset));
        SafeDraw(rec, call.TriangleCount, call.TriangleOffset, tag);
    }

    private void DoTriangles(IGraphicsRecorder rec, in Call call, bool clip)
    {
        rec.SetPipeline(clip ? Pipeline(Pso.TrianglesClip) : Pipeline(Pso.Triangles));
        if (clip)
        {
            rec.SetStencilReference(0);
        }

        BindDraw(rec, call.UniformOffset, call.Image, PaintType(call.UniformOffset));
        SafeDraw(rec, call.TriangleCount, call.TriangleOffset, "triangles");
    }

    private void DoMaskFill(IGraphicsRecorder rec, in Call call, bool clip)
    {
        LastFlushMaskFills++;
        rec.SetPipeline(clip ? Pipeline(Pso.FringeClip) : Pipeline(Pso.Fringe));
        if (clip)
        {
            rec.SetStencilReference(0);
        }

        BindDraw(rec, call.UniformOffset, call.Image, PaintType(call.UniformOffset), call.MaskImage, call.MaskOriginX, call.MaskOriginY);
        SafeDraw(rec, call.TriangleCount, call.TriangleOffset, "maskfill");
    }

    // ---- path clip (stencil clip bit 0x80, exclusion polarity) ----

    // (1) winding of the body rows into the low bits; (2) the fringe fragments with coverage >= 0.5 set the low bits;
    // (3) SET the clip bit where the low bits are 0 (outside the clip shape); (4) clear the low bits. The clip bit
    // accumulates exclusions across a clip stack (the core replays the whole stack after a Restore), so the visible
    // region is the intersection of all clip shapes.
    private void ExecuteClip(IGraphicsRecorder rec, in Call call)
    {
        Span<Path> paths = _paths.AsSpan(call.PathOffset, call.PathCount);

        rec.SetPipeline(Pipeline(Pso.Winding));
        rec.SetStencilReference(0);
        BindDraw(rec, call.UniformOffset, 0, TypeStencilFill);
        int s = -1, c = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            if (paths[i].FillCount > 0)
            {
                if (s < 0)
                {
                    s = paths[i].FillOffset;
                }

                c += paths[i].FillCount;
            }
        }

        if (s >= 0 && c > 0)
        {
            SafeDraw(rec, c, s, "clip.winding");
        }

        if (call.MergedFringeCount > 0)
        {
            LastFlushClipFringeDraws++;
            rec.SetPipeline(Pipeline(Pso.ClipFringe));
            rec.SetStencilReference(1);       // Replace writes (1 & 0x7F): any nonzero low value means inside
            BindDraw(rec, call.UniformOffset + 1, 0, TypeStencilFill);
            SafeDraw(rec, call.MergedFringeCount, call.MergedFringeOffset, "clip.fringe");
        }

        rec.SetPipeline(Pipeline(Pso.ClipMark));
        rec.SetStencilReference(ClipStencilRef);
        BindDraw(rec, call.UniformOffset, 0, TypeStencilFill);
        SafeDraw(rec, call.TriangleCount, call.TriangleOffset, "clip.mark");

        rec.SetPipeline(Pipeline(Pso.ClearLow));
        rec.SetStencilReference(0);
        BindDraw(rec, call.UniformOffset, 0, TypeStencilFill);
        SafeDraw(rec, call.TriangleCount, call.TriangleOffset, "clip.clearlow");
    }

    private void ExecuteClipReset(IGraphicsRecorder rec, in Call call)
    {
        rec.SetPipeline(Pipeline(Pso.ClipReset));
        rec.SetStencilReference(0);
        BindDraw(rec, call.UniformOffset, 0, TypeStencilFill);
        SafeDraw(rec, call.TriangleCount, call.TriangleOffset, "clip.reset");
    }

    // Runaway-draw guard (DX12 DEVICE_HUNG defence): validate (firstVertex, vertexCount) against the uploaded vertex
    // count BEFORE every draw; skip and log instead of issuing an out-of-range or absurd draw. Logs once per flush.
    private const int MaxReasonableDrawVerts = 4_000_000;   // far above any real UI frame; below TDR territory
    private bool _drawGuardWarned;
    private void SafeDraw(IGraphicsRecorder rec, int count, int first, string tag)
    {
        if (count <= 0 || first < 0)
        {
            return;
        }

        long last = (long)first + count;
        if (last > _vertCount || count > MaxReasonableDrawVerts)
        {
            if (!_drawGuardWarned)
            {
                _drawGuardWarned = true;
                Console.Error.WriteLine(
                    $"[ui] skipped draw ({tag}): first={first} count={count} (first+count={last}) exceeds uploaded verts={_vertCount}");
            }

            return;
        }

        rec.Draw((uint)count, 1, (uint)first, 0);
    }

    // The type selector lives in frag[12].w of the uniform set; the texture interpretation in frag[12].z.
    private int PaintType(int uniformOffset)
    {
        return (int)_uniforms[uniformOffset][(12 * 4) + 3];
    }

    private int PaintTexType(int uniformOffset)
    {
        return (int)_uniforms[uniformOffset][(12 * 4) + 2];
    }

    private unsafe void BindDraw(IGraphicsRecorder rec, int uniformOffset, int image, int type, int maskImage = 0, float maskOriginX = 0f, float maskOriginY = 0f)
    {
        rec.BindConstantBuffer(0, new GpuBufferView(_ub[_frameSlot]!.Handle, (ulong)(uniformOffset * UniformStride), UniformBytes));
        ITextureView srv = ResolveSrv(image != 0 ? image : _dummyTex) ?? ResolveSrv(_dummyTex)!;
        rec.BindTexture(0, srv);
        var view = new UiView
        {
            ViewSize = new Vector2(_viewW, _viewH),
            Type = type,
            TexType = PaintTexType(uniformOffset),
            FlipY = _flipClipY ? -1f : 1f,
            MaskScale = _devicePxRatio,
        };
        ITextureView maskSrv = ResolveSrv(_dummyTex)!;
        if (maskImage != 0)
        {
            ref Texture m = ref FindTexture(maskImage);
            if (m.Id != 0 && m.Srv is not null)
            {
                maskSrv = m.Srv;
                view.MaskEnabled = 1;
                view.MaskOrigin = new Vector2(maskOriginX, maskOriginY);
                view.MaskSize = new Vector2(m.Width, m.Height);
            }
        }

        rec.BindTexture(1, maskSrv);   // ui.hlsl t1: always bound (a dummy when no mask), so every draw is valid
        rec.BindSampler(0, _sampler!);

        int slot = ViewDrawSeq++;
        // Fail-safe: EnsureViewCbv sizes the ring for the worst-case bind count, so this cannot trip in normal
        // operation - but never write past the mapped buffer (on DX12 that corrupts the adjacent paint CBV).
        int capacitySlots = (int)(_viewCbvCap[_frameSlot] / UniformStride);
        if (slot >= capacitySlots)
        {
            if (!ViewGuardWarnedThisFlush)
            {
                ViewGuardWarnedThisFlush = true;
                Console.Error.WriteLine(
                    $"[ui] nvg-view CBV overflow: slot={slot} cap={capacitySlots} - draw skipped (undersized view buffer)");
            }

            return;
        }

        ulong off = (ulong)(slot * UniformStride);
        byte* dst = (byte*)((IMappedBuffer)_viewCbv[_frameSlot]!).MappedPointer + off;
        *(UiView*)dst = view;
        rec.BindConstantBuffer(1, new GpuBufferView(_viewCbv[_frameSlot]!.Handle, off, (ulong)Unsafe.SizeOf<UiView>()));
    }

    /// <summary>The most binds one recorded call issues: a clip is winding + fringe + mark + clear.</summary>
    internal const int MaxBindsPerCall = 4;

    /// <summary>Transient mode: hand this slot's buffers to the deferred-destroy queue so the next upload allocates
    /// fresh ones (the previous flush of this frame may still be read by the GPU through them).</summary>
    private void RetireSlotBuffers(int slot)
    {
        if (_vb[slot] is not null)
        {
            _device.DeferDestroy(_vb[slot]!);
            _vb[slot] = null;
            _vbCap[slot] = 0;
        }

        if (_ub[slot] is not null)
        {
            _device.DeferDestroy(_ub[slot]!);
            _ub[slot] = null;
            _ubCap[slot] = 0;
        }

        if (_viewCbv[slot] is not null)
        {
            _device.DeferDestroy(_viewCbv[slot]!);
            _viewCbv[slot] = null;
            _viewCbvCap[slot] = 0;
        }
    }

    private void EnsureViewCbv()
    {
        // Size by the WORST-CASE BIND COUNT, not the uniform count: BindDraw writes one slot per bind, and a call
        // issues up to MaxBindsPerCall binds (a clip: 4; a stencil-once fill: 3; a convex fill: 2; others: 1).
        ulong need = (ulong)Math.Max(_callCount * MaxBindsPerCall, 16) * UniformStride;
        if (_viewCbv[_frameSlot] is null || need > _viewCbvCap[_frameSlot])
        {
            // Defer the old buffer for THIS slot (a still-in-flight command list may reference it).
            if (_viewCbv[_frameSlot] is not null)
            {
                _device.DeferDestroy(_viewCbv[_frameSlot]!);
            }

            _viewCbvCap[_frameSlot] = Math.Max(need, UniformStride * 64);
            _viewCbv[_frameSlot] = _device.CreateBuffer(new BufferDesc { SizeBytes = _viewCbvCap[_frameSlot], Memory = MemoryClass.Upload, Usage = ResourceUsage.ConstantBuffer, DebugName = "nvg-view" });
        }
    }

    private ITextureView? ResolveSrv(int image)
    {
        ref Texture t = ref FindTexture(image);
        return t.Id != 0 ? t.Srv : null;
    }

    // ---- GPU upload (persistent Upload buffers, written via mapped pointer; alloc-free steady state) ----

    private unsafe void UploadVertices()
    {
        ulong bytes = (ulong)_vertCount * (ulong)Unsafe.SizeOf<NVGvertex>();
        if (bytes == 0)
        {
            return;
        }

        int slot = _frameSlot;
        if (_vb[slot] is null || bytes > _vbCap[slot])
        {
            // Use-after-free fix: a previously-submitted frame (the scheduler is FramesInFlight deep) may still
            // reference this slot's old vertex buffer. Defer its free to the device's frame-stamped deferred-destroy
            // queue (freed only once FramesInFlight frames have retired) instead of disposing it out from under the GPU.
            if (_vb[slot] is not null)
            {
                _device.DeferDestroy(_vb[slot]!);
            }

            _vbCap[slot] = Math.Max(bytes, 8192);
            _vb[slot] = _device.CreateBuffer(new BufferDesc { SizeBytes = _vbCap[slot], Memory = MemoryClass.Upload, Usage = ResourceUsage.VertexBuffer, DebugName = "nvg-vb" });
        }

        byte* dst = (byte*)((IMappedBuffer)_vb[slot]!).MappedPointer;
        fixed (NVGvertex* src = _verts)
        {
            Buffer.MemoryCopy(src, dst, _vbCap[slot], bytes);
        }

        _rec!.BindVertexBuffer(0, new GpuBufferView(_vb[slot]!.Handle, 0, bytes), (uint)Unsafe.SizeOf<NVGvertex>());
    }

    private unsafe void UploadUniforms()
    {
        if (_uniformCount == 0)
        {
            return;
        }

        ulong bytes = (ulong)_uniformCount * UniformStride;
        int slot = _frameSlot;
        if (_ub[slot] is null || bytes > _ubCap[slot])
        {
            // Use-after-free fix: defer this slot's old uniform buffer (a still-in-flight command list may reference it).
            if (_ub[slot] is not null)
            {
                _device.DeferDestroy(_ub[slot]!);
            }

            _ubCap[slot] = Math.Max(bytes, UniformStride * 64);
            _ub[slot] = _device.CreateBuffer(new BufferDesc { SizeBytes = _ubCap[slot], Memory = MemoryClass.Upload, Usage = ResourceUsage.ConstantBuffer, DebugName = "nvg-ub" });
        }

        byte* dst = (byte*)((IMappedBuffer)_ub[slot]!).MappedPointer;
        for (int i = 0; i < _uniformCount; i++)
        {
            float[] u = _uniforms[i];
            fixed (float* src = u)
            {
                Buffer.MemoryCopy(src, dst + (i * UniformStride), UniformBytes, UniformBytes);
            }
        }
    }

    // ---- texture / image API (consumed by UiMewVGContext) ----

    public int CreateTexture(NVGtextureType type, int width, int height, MewVGImageFlags flags, ReadOnlySpan<byte> data)
    {
        int idx = AllocTextureSlot();
        ref Texture tex = ref _textures[idx];
        tex.Type = type;
        tex.Flags = flags;
        tex.Width = width;
        tex.Height = height;

        Format fmt = type switch
        {
            NVGtextureType.RGBA => Format.R8G8B8A8_UNorm,
            NVGtextureType.BGRA => Format.B8G8R8A8_UNorm,
            _ => Format.R8_UNorm,
        };
        ITexture gpu = _device.CreateTexture(new TextureDesc
        {
            Dimension = TextureDimension.Tex2D,
            Format = fmt,
            Width = (uint)width,
            Height = (uint)height,
            DepthOrArrayLayers = 1,
            MipLevels = 1,
            SampleCount = 1,
            Usage = ResourceUsage.ShaderResource | ResourceUsage.CopyDest,
            DebugName = "nvg-tex",
        });
        ITextureView srv = _device.CreateView(gpu, new TextureViewDesc
        {
            Kind = ViewKind.Srv,
            Format = fmt,
            Dimension = TextureDimension.Tex2D,
            BaseMip = 0,
            MipCount = 1,
            BaseArrayLayer = 0,
            ArrayLayerCount = 1,
        });
        tex.Tex = gpu;
        tex.Srv = srv;
        tex.CurrentState = ResourceState.Common;
        if (!data.IsEmpty)
        {
            UploadTextureRegion(ref tex, 0, 0, width, height, data);
        }

        return tex.Id;
    }

    public void DeleteTexture(int id)
    {
        for (int i = 0; i < _textureCount; i++)
        {
            if (_textures[i].Id != id)
            {
                continue;
            }

            if ((_textures[i].Flags & MewVGImageFlags.NoDelete) == 0)
            {
                // Use-after-free fix: a previously-submitted draw may still sample this atlas (the scheduler is
                // FramesInFlight deep). Defer the view + texture to the device's frame-stamped deferred-destroy
                // queue (freed only after FramesInFlight frames retire) rather than freeing them immediately.
                if (_textures[i].Srv is not null)
                {
                    _device.DeferDestroy(_textures[i].Srv!);
                }

                if (_textures[i].Tex is not null)
                {
                    _device.DeferDestroy(_textures[i].Tex!);
                }
            }

            _textures[i] = default;
            return;
        }
    }

    public bool UpdateTexture(int image, int x, int y, int width, int height, ReadOnlySpan<byte> data)
    {
        ref Texture tex = ref FindTexture(image);
        if (tex.Id == 0 || tex.Tex is null || tex.External)
        {
            return false;
        }

        UploadTextureRegion(ref tex, x, y, width, height, data);
        return true;
    }

    public bool GetTextureSize(int image, out int width, out int height)
    {
        ref Texture tex = ref FindTexture(image);
        if (tex.Id == 0)
        {
            width = height = 0;
            return false;
        }

        width = tex.Width;
        height = tex.Height;
        return true;
    }

    /// <summary>An image slot over an EXTERNALLY OWNED texture registered in <see cref="ExternalTextures"/>
    /// under <paramref name="key"/>. The slot is always NoDelete (DeleteTexture drops only the slot; the producer frees
    /// the texture). Returns 0 for an unknown key (MewVGImage then falls back to its CPU path).</summary>
    public int CreateExternalImage(nint key, int width, int height, MewVGImageFlags flags)
    {
        if (!ExternalTextures.TryGet(key, out ExternalTextures.Entry e))
        {
            return 0;
        }

        int idx = AllocTextureSlot();
        ref Texture tex = ref _textures[idx];
        tex.Type = NVGtextureType.RGBA;
        tex.Flags = flags | MewVGImageFlags.NoDelete;
        tex.Width = width > 0 ? width : e.Width;
        tex.Height = height > 0 ? height : e.Height;
        tex.Tex = e.Texture;
        tex.Srv = e.View;
        tex.CurrentState = ResourceState.ShaderResource;   // the producer's contract (GpuTextureSource.cs header)
        tex.External = true;
        return tex.Id;
    }

    public int GetImageHandle(int image)
    {
        ref Texture tex = ref FindTexture(image);
        return tex.Id != 0 && tex.Tex is not null ? (int)tex.Tex.Handle : 0;
    }

    private static int BytesPerPixel(NVGtextureType t)
    {
        return t == NVGtextureType.Alpha ? 1 : 4;
    }

    private unsafe void UploadTextureRegion(ref Texture tex, int x, int y, int width, int height, ReadOnlySpan<byte> data)
    {
        // Stage the rows into a 256-byte-row-aligned upload buffer (DX12 PlacedSubresourceFootprint requirement;
        // Vulkan reuses the same aligned layout via bufferRowLength) and ENQUEUE a copy record. The actual
        // buffer->texture copy + barriers are recorded onto a SINGLE reused command list and submitted ONCE per
        // frame in FlushPendingUploads() (called at the top of Flush()). This eliminates the per-glyph
        // Acquire+Submit+Wait churn (and the leaked, never-disposed command allocator/list) that exhausted the
        // driver on text-heavy screen transitions -> DXGI DEVICE_REMOVED.
        int bpp = BytesPerPixel(tex.Type);
        int w = width, h = height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // GUARD: an out-of-bounds buffer->texture copy is a classic GPU page-fault -> DXGI DEVICE_REMOVED.
        // Reject (and report) before enqueuing so a bad glyph-atlas dst region can never reach the GPU. Also
        // tells us whether OOB uploads were the fault: if this fires, that WAS the device-removed root cause.
        if (x < 0 || y < 0 || x + w > tex.Width || y + h > tex.Height)
        {
            Console.Error.WriteLine($"[ui] glyph upload OOB: dst ({x},{y},{w},{h}) exceeds atlas {tex.Width}x{tex.Height} - skipped");
            return;
        }

        uint srcRowBytes = (uint)(w * bpp);
        uint alignedRowPitch = (srcRowBytes + 255u) & ~255u;     // 256-aligned row pitch
        ulong stagingBytes = alignedRowPitch * (ulong)h;
        if (stagingBytes == 0)
        {
            return;
        }

        IBuffer stage = _device.CreateBuffer(new BufferDesc { SizeBytes = stagingBytes, Memory = MemoryClass.Upload, Usage = ResourceUsage.CopySource, DebugName = "nvg-stage" });
        byte* sp = (byte*)((IMappedBuffer)stage).MappedPointer;
        // Copy each tightly-packed source row into its 256-aligned slot; zero-pad the tail of each aligned row.
        int srcAvail = data.Length;
        fixed (byte* src = data)
        {
            for (int row = 0; row < h; row++)
            {
                byte* dstRow = sp + (row * alignedRowPitch);
                long srcOff = row * srcRowBytes;
                long copy = srcRowBytes;
                if (srcOff >= srcAvail)
                {
                    copy = 0;
                }
                else if (srcOff + copy > srcAvail)
                {
                    copy = srcAvail - srcOff;
                }

                if (copy > 0)
                {
                    Buffer.MemoryCopy(src + srcOff, dstRow, alignedRowPitch, (ulong)copy);
                }
                // Zero-pad the aligned tail (and any short final row) so the GPU never reads uninitialized bytes.
                if (copy < alignedRowPitch)
                {
                    new Span<byte>(dstRow + copy, (int)(alignedRowPitch - copy)).Clear();
                }
            }
        }

        // Enqueue. Capture the texture's CURRENT running state as this record's "before"; FlushPendingUploads
        // collapses it to one before->CopyDest barrier per distinct texture (group leader). Mark the texture
        // ShaderResource now: by the time any draw runs (after the flush) it WILL be ShaderResource, and a
        // subsequent same-frame enqueue to this texture must see CopyDest-equivalent grouping, which the
        // group-by-handle logic handles using only the first record's Before.
        if (_pendingCount >= _pending.Length)
        {
            Array.Resize(ref _pending, _pending.Length * 2);
        }

        ref PendingUpload pu = ref _pending[_pendingCount++];
        pu.Staging = stage;
        pu.DstTex = tex.Tex!;
        pu.DstHandle = tex.Tex!.Handle;
        pu.Before = tex.CurrentState;
        pu.X = x;
        pu.Y = y;
        pu.W = w;
        pu.H = h;
        pu.AlignedRowPitch = alignedRowPitch;
        pu.Bpp = (uint)bpp;
        tex.CurrentState = ResourceState.ShaderResource;
    }

    /// <summary>Record every queued texture upload onto <paramref name="cmd"/> - the frame's own command list,
    /// OUTSIDE a render pass (the host's `ui` pass calls this before its BeginRendering). No submit and no fence wait:
    /// the staging buffers go to the device's fence-aware DeferDestroy. A separate submit + CPU fence wait in the middle of
    /// the frame would queue behind the frames in flight, and on Vulkan would take the frame's present sync.</summary>
    public void RecordPendingUploads(ICommandList cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (_pendingCount == 0)
        {
            return;
        }

        RecordUploadCommands(cmd);
        for (int i = 0; i < _stagingToDisposeCount; i++)
        {
            _device.DeferDestroy(_stagingToDispose[i]);
            _stagingToDispose[i] = null!;
        }

        _stagingToDisposeCount = 0;
        _pendingCount = 0;
    }

    /// <summary>Record all queued texture uploads onto ONE reused command list + fence and submit them in a
    /// SINGLE submission (one wait), then dispose the batch staging buffers. The fallback for uploads nobody
    /// recorded on a frame list (<see cref="RecordPendingUploads"/>): called at the top of Flush() (uploads must
    /// complete before any draw samples the atlas) and from Cancel()/Dispose() so staging buffers never leak.</summary>
    private void FlushPendingUploads()
    {
        if (_pendingCount == 0)
        {
            return;
        }

        _uploadCmd ??= _device.GraphicsQueue.AcquireCommandList();
        _uploadFence ??= _device.CreateFence(0);
        ICommandList cmd = _uploadCmd;
        cmd.Begin();
        RecordUploadCommands(cmd);
        cmd.Close();

        ulong val = ++_uploadFenceVal;
        _uploadLists[0] = cmd;
        _uploadSig[0] = new FenceSignal(_uploadFence, val);
        try
        {
            _device.GraphicsQueue.Submit(_uploadLists, default, _uploadSig);
            _uploadFence.Wait(val);
        }
        catch (Exception ex)
        {
            LogDeviceRemoved("ui upload submit/wait", ex);
            throw;
        }
        finally
        {
            // Dispose all staging buffers consumed this batch (collected into _stagingToDispose during
            // recording). Safe after the fence wait above (or after a fault: the GPU is gone anyway).
            DisposeBatchStaging();
            _pendingCount = 0;
        }
    }

    // The barrier + copy records for every pending upload; the consumed staging buffers land in _stagingToDispose.
    private void RecordUploadCommands(ICommandList cmd)
    {
        // Group copies by destination texture: one Before->CopyDest barrier per texture (using the first
        // record's captured Before — the texture's real running state for this batch), then all its copies,
        // then one CopyDest->ShaderResource. A texture appearing once leaves its CurrentState=ShaderResource,
        // which is exactly what UploadTextureRegion already set at enqueue time.
        Span<ResourceBarrierDesc> b = stackalloc ResourceBarrierDesc[1];
        for (int i = 0; i < _pendingCount; i++)
        {
            ref PendingUpload pi = ref _pending[i];
            if (pi.Staging is null)
            {
                continue;   // already consumed as part of an earlier group in this pass
            }

            ulong handle = pi.DstHandle;

            b[0] = ResourceBarrierDesc.Transition(handle, pi.Before, ResourceState.CopyDest);
            cmd.ResourceBarrier(b);

            // Emit this record and every later record targeting the same texture, in order.
            for (int j = i; j < _pendingCount; j++)
            {
                ref PendingUpload pj = ref _pending[j];
                if (pj.Staging is null || pj.DstHandle != handle)
                {
                    continue;
                }

                cmd.CopyBufferToTextureRegion(pj.Staging, pj.DstTex, (uint)pj.X, (uint)pj.Y, (uint)pj.W, (uint)pj.H, pj.AlignedRowPitch, pj.Bpp);
                if (_stagingToDisposeCount >= _stagingToDispose.Length)
                {
                    Array.Resize(ref _stagingToDispose, _stagingToDispose.Length * 2);
                }

                _stagingToDispose[_stagingToDisposeCount++] = pj.Staging;   // dispose after the batch submit completes
                pj.Staging = null!;   // mark consumed within this pass
            }

            b[0] = ResourceBarrierDesc.Transition(handle, ResourceState.CopyDest, ResourceState.ShaderResource);
            cmd.ResourceBarrier(b);
        }
    }

    // Staging buffers consumed in the current FlushPendingUploads batch, awaiting disposal after the submit.
    private IBuffer[] _stagingToDispose = new IBuffer[16];
    private int _stagingToDisposeCount;
    private void DisposeBatchStaging()
    {
        for (int i = 0; i < _stagingToDisposeCount; i++)
        {
            _stagingToDispose[i]?.Dispose();
            _stagingToDispose[i] = null!;
        }

        _stagingToDisposeCount = 0;
    }

    private void LogDeviceRemoved(string where, Exception? ex)
    {
        try
        {
            string reason = _device.PollStatus() == DeviceStatus.Removed ? "DeviceRemoved" : "Ok";
            Console.Error.WriteLine($"[ui] GPU fault during {where}: device status={reason}; {ex?.GetType().Name}: {ex?.Message}");
            // DX12-only: dump Device Removed Extended Data (breadcrumb + page fault). Reflection-free type check
            // so the Vulkan backend is untouched and this stays NativeAOT-safe.
            if (_device is Rendering.Backends.Dx12.Dx12Device dx12)
            {
                dx12.DumpDeviceRemoved($"ui:{where}");
            }
        }
        catch { /* diagnostics must never mask the original fault */ }
    }

    // ---- ConvertPaint (port of GLNVGContext.ConvertPaint) ----

    private static MewVGColor Premultiply(MewVGColor c)
    {
        return new(c.R * c.A, c.G * c.A, c.B * c.A, c.A);
    }

    private bool ConvertPaint(float[] frag, ref MewVGPaint paint, ref NVGscissorState scissor, float width, float fringe, float strokeThr)
    {
        Span<float> invxform = stackalloc float[6];
        Array.Clear(frag);

        MewVGColor inner = Premultiply(paint.InnerColor);
        MewVGColor outer = Premultiply(paint.OuterColor);
        SetVec4(frag, 6, inner.R, inner.G, inner.B, inner.A);
        SetVec4(frag, 7, outer.R, outer.G, outer.B, outer.A);

        if (scissor.Extent[0] < -0.5f || scissor.Extent[1] < -0.5f)
        {
            SetVec4(frag, 0, 0, 0, 0, 0);
            SetVec4(frag, 1, 0, 0, 0, 0);
            SetVec4(frag, 2, 0, 0, 0, 0);
            SetVec4(frag, 8, 1f, 1f, 1f, 1f);
        }
        else
        {
            Span<float> sx = stackalloc float[6];
            for (int i = 0; i < 6; i++)
            {
                sx[i] = scissor.Xform[i];
            }

            NVGMath.TransformInverse(invxform, sx);
            SetMat3x4(frag, 0, invxform);
            float ssx = MathF.Sqrt((scissor.Xform[0] * scissor.Xform[0]) + (scissor.Xform[2] * scissor.Xform[2])) / fringe;
            float ssy = MathF.Sqrt((scissor.Xform[1] * scissor.Xform[1]) + (scissor.Xform[3] * scissor.Xform[3])) / fringe;
            SetVec4(frag, 8, scissor.Extent[0], scissor.Extent[1], ssx, ssy);
        }

        float extentX = paint.Extent[0], extentY = paint.Extent[1];
        SetVec4(frag, 9, extentX, extentY, paint.Radius, paint.Feather);
        float strokeMult = ((width * 0.5f) + (fringe * 0.5f)) / fringe;
        SetVec4(frag, 12, strokeMult, strokeThr, 0f, 0f);

        Span<float> px = stackalloc float[6];
        for (int i = 0; i < 6; i++)
        {
            px[i] = paint.Xform[i];
        }

        if (paint.PaintKind == (int)NVGpaintKind.GradientRadial)
        {
            ref Texture tex = ref FindTexture(paint.Image);
            if (tex.Id == 0)
            {
                return false;
            }

            SetVec4(frag, 10, paint.Center[0], paint.Center[1], paint.Radius2[0], paint.Radius2[1]);
            SetVec4(frag, 11, paint.Focal[0], paint.Focal[1], paint.SpreadMethod, 0f);
            SetVal(frag, 12, 2, 0f);
            SetVal(frag, 12, 3, TypeRadial);
            NVGMath.TransformInverse(invxform, px);
        }
        else if (paint.PaintKind == (int)NVGpaintKind.GradientLinear)
        {
            ref Texture tex = ref FindTexture(paint.Image);
            if (tex.Id == 0)
            {
                return false;
            }

            SetVec4(frag, 10, paint.Center[0], paint.Center[1], 0f, 0f);
            SetVec4(frag, 11, paint.Focal[0], paint.Focal[1], paint.SpreadMethod, 0f);
            SetVal(frag, 12, 2, 0f);
            SetVal(frag, 12, 3, TypeLinear);
            NVGMath.TransformInverse(invxform, px);
        }
        else if (paint.Image != 0)
        {
            ref Texture tex = ref FindTexture(paint.Image);
            if (tex.Id == 0)
            {
                return false;
            }

            if ((tex.Flags & MewVGImageFlags.FlipY) != 0)
            {
                Span<float> m1 = stackalloc float[6];
                Span<float> m2 = stackalloc float[6];
                NVGMath.TransformTranslate(m1, 0f, extentY * 0.5f);
                NVGMath.TransformMultiply(m1, px);
                NVGMath.TransformScale(m2, 1f, -1f);
                NVGMath.TransformMultiply(m2, m1);
                NVGMath.TransformTranslate(m1, 0f, -extentY * 0.5f);
                NVGMath.TransformMultiply(m1, m2);
                NVGMath.TransformInverse(invxform, m1);
            }
            else
            {
                NVGMath.TransformInverse(invxform, px);
            }

            SetVal(frag, 12, 3, TypeImage);
            if (tex.Type is NVGtextureType.RGBA or NVGtextureType.BGRA)
            {
                SetVal(frag, 12, 2, (tex.Flags & MewVGImageFlags.Premultiplied) != 0 ? 0f : 1f);
            }
            else
            {
                SetVal(frag, 12, 2, 2f);
            }
        }
        else
        {
            SetVal(frag, 12, 3, TypeGradRect);
            NVGMath.TransformInverse(invxform, px);
        }

        SetMat3x4(frag, 3, invxform);
        return true;
    }

    // ---- uniform helpers ----
    private static void SetVal(float[] d, int vec, int comp, float v)
    {
        d[(vec * 4) + comp] = v;
    }

    private static void SetVec4(float[] d, int vec, float x, float y, float z, float w)
    {
        int b = vec * 4;
        d[b] = x;
        d[b + 1] = y;
        d[b + 2] = z;
        d[b + 3] = w;
    }
    private static void SetMat3x4(float[] d, int vec, ReadOnlySpan<float> t)
    {
        SetVec4(d, vec + 0, t[0], t[1], 0f, 0f);
        SetVec4(d, vec + 1, t[2], t[3], 0f, 0f);
        SetVec4(d, vec + 2, t[4], t[5], 1f, 0f);
    }

    // ---- strip/fan conversion ----
    private static int StripToTriCount(int n)
    {
        return n < 3 ? 0 : (n - 2) * 3;
    }

    // THE NFill CONTRACT. Upstream NanoVG's nvg__expandFill emits the fill body as a triangle FAN, and this
    // renderer must NOT fan-triangulate it. The VENDORED MewVG does
    // NOT: NVGContext.ExpandFill runs a real tessellator and writes `triangleCount * 3` vertices — three per
    // triangle — in every branch (the tessellated one AND the "direct convex" one; see `var cverts =
    // triangleCount * 3`). So NFill is a TRIANGLE LIST and must be copied through verbatim.
    //
    // Re-fanning a triangle list is INVISIBLE on a convex shape (every bogus triangle still lies inside the hull
    // and merely overdraws the same opaque color on the direct-draw path) and CATASTROPHIC on a concave one
    // (the bogus triangles corrupt the stencil winding). A UI with only convex fills never shows it, which is how the
    // defect can survive every screen; a glyph outline or a concave icon exposes it at once.
    // Gate tests: the concave-fill sink test (a concave "L" must fill its arms and leave its notch empty) and the
    // glyph-clip sink test (the same contract through RenderClip, with an opposite-wound hole).
    private static int StripToTris(ReadOnlySpan<NVGvertex> strip, Span<NVGvertex> outp)
    {
        if (strip.Length < 3)
        {
            return 0;
        }

        int w = 0;
        for (int i = 0; i < strip.Length - 2; i++)
        {
            if ((i & 1) == 0)
            {
                outp[w++] = strip[i];
                outp[w++] = strip[i + 1];
                outp[w++] = strip[i + 2];
            }
            else
            {
                outp[w++] = strip[i + 1];
                outp[w++] = strip[i];
                outp[w++] = strip[i + 2];
            }
        }

        return w;
    }

    // ---- allocators (grow-then-reuse; alloc-free in steady state) ----
    private ref Call AllocCall()
    {
        if (_callCount + 1 > _calls.Length)
        {
            Array.Resize(ref _calls, _calls.Length * 2);
        }

        ref Call c = ref _calls[_callCount++];
        c = default;
        return ref c;
    }
    private int AllocPaths(int n)
    {
        if (_pathCount + n > _paths.Length)
        {
            Array.Resize(ref _paths, Math.Max(_paths.Length * 2, _pathCount + n));
        }

        int o = _pathCount;
        _pathCount += n;
        return o;
    }
    private int AllocVerts(int n)
    {
        if (_vertCount + n > _verts.Length)
        {
            Array.Resize(ref _verts, Math.Max(_verts.Length * 2, _vertCount + n));
        }

        int o = _vertCount;
        _vertCount += n;
        return o;
    }
    private int AllocUniforms(int n)
    {
        if (_uniformCount + n > _uniformCapacity)
        {
            int cap = Math.Max(_uniformCapacity * 2, Math.Max(_uniformCount + n, 64));
            Array.Resize(ref _uniforms, cap);
            for (int i = _uniformCapacity; i < cap; i++)
            {
                _uniforms[i] = new float[UniformFloatCount];
            }

            _uniformCapacity = cap;
        }

        int o = _uniformCount;
        _uniformCount += n;
        return o;
    }

    private int AllocTextureSlot()
    {
        for (int i = 0; i < _textureCount; i++)
        {
            if (_textures[i].Id == 0)
            {
                _textures[i] = default;
                _textures[i].Id = ++_textureId;
                return i;
            }
        }

        if (_textureCount + 1 > _textures.Length)
        {
            Array.Resize(ref _textures, _textures.Length * 2);
        }

        int idx = _textureCount++;
        _textures[idx] = default;
        _textures[idx].Id = ++_textureId;
        return idx;
    }

    // The empty slot an unknown image id resolves to. Every caller tests Id != 0 before reading a slot, so a stale
    // or foreign id reads as "no texture" instead of as whatever happens to live in slot 0 (the dummy image).
    private Texture _noTexture;

    private ref Texture FindTexture(int id)
    {
        if (id != 0)
        {
            for (int i = 0; i < _textureCount; i++)
            {
                if (_textures[i].Id == id)
                {
                    return ref _textures[i];
                }
            }
        }

        _noTexture = default;
        return ref _noTexture;
    }

    // ---- pipeline creation (cooked ui.hlsl) ----
    private void CreatePipelines()
    {
        _layout = _device.CreatePipelineLayout(new PipelineLayoutDesc
        {
            CbvCount = 2,            // b0 paint (UiFrag), b1 per-draw selectors (UiView)
            TextureSrvCount = 2,     // t0 image / atlas / gradient LUT, t1 coverage mask
            SamplerCount = 1,
            RootConstants32 = 0,
            DebugName = "ui-nvg",
        });
        _sampler = _device.CreateSampler(new SamplerDesc { Filter = SamplerFilter.Linear, Address = SamplerAddressMode.ClampToEdge });

        VertexAttribute[] attrs =
        {
            new() { Location = 0, Semantic = "POSITION", Format = Format.R32G32_Float, Offset = 0 },
            new() { Location = 1, Semantic = "TEXCOORD", Format = Format.R32G32_Float, Offset = 8 },
        };
        Format[] colors = { _colorFormat };
        int stride = Unsafe.SizeOf<NVGvertex>(); // 16

        RenderPipelineDesc Base(string name, PrimitiveTopology topo, BlendMode blend)
        {
            return new RenderPipelineDesc
            {
                Name = name,
                VertexShader = _vs,
                PixelShader = _ps,
                VertexEntry = "VSMain",
                PixelEntry = "PSMain",
                Layout = _layout,
                VertexAttributes = attrs,
                VertexStride = stride,
                ColorFormats = colors,
                DepthFormat = _depthStencilFormat,
                DepthTest = false,
                DepthWrite = false,
                Cull = CullMode.None,
                Topology = topo,
                Blend = blend,
                SampleCount = 1,
            };
        }
        // Premultiplied src-over: ConvertPaint premultiplies the paint colors.
        _pipelines[(int)Pso.Convex] = _device.CreateRenderPipeline(Base("ui-convex", PrimitiveTopology.TriangleList, BlendMode.PremultipliedAlpha));
        _pipelines[(int)Pso.Fringe] = _device.CreateRenderPipeline(Base("ui-fringe", PrimitiveTopology.TriangleStrip, BlendMode.PremultipliedAlpha));
        _pipelines[(int)Pso.Triangles] = _device.CreateRenderPipeline(Base("ui-tris", PrimitiveTopology.TriangleList, BlendMode.PremultipliedAlpha));

        static StencilFaceState Face(StencilCompare cmp, StencilOp pass)
        {
            return new() { Compare = cmp, Fail = StencilOp.Keep, DepthFail = StencilOp.Keep, Pass = pass };
        }

        // Direct draws under a clip: stencil test "clip bit clear" (ref 0, read mask 0x80), write nothing.
        RenderPipelineDesc ClipTest(string name, PrimitiveTopology topo)
        {
            return Base(name, topo, BlendMode.PremultipliedAlpha) with
            {
                StencilFormat = _depthStencilFormat,
                StencilEnable = true,
                StencilReadMask = ClipStencilRef,
                StencilWriteMask = 0x00,
                StencilFront = Face(StencilCompare.Equal, StencilOp.Keep),
                StencilBack = Face(StencilCompare.Equal, StencilOp.Keep),
            };
        }

        _pipelines[(int)Pso.ConvexClip] = _device.CreateRenderPipeline(ClipTest("ui-convex-clip", PrimitiveTopology.TriangleList));
        _pipelines[(int)Pso.FringeClip] = _device.CreateRenderPipeline(ClipTest("ui-fringe-clip", PrimitiveTopology.TriangleStrip));
        _pipelines[(int)Pso.TrianglesClip] = _device.CreateRenderPipeline(ClipTest("ui-tris-clip", PrimitiveTopology.TriangleList));

        // Stencil-once: draw where ALL bits are 0 (low bits untouched this call AND clip bit clear), then mark the
        // pixel (0 -> 1 on the low bits; the Equal-0 test guarantees the increment never wraps) so later fragments
        // of the same call are rejected.
        RenderPipelineDesc Once(string name, PrimitiveTopology topo)
        {
            return Base(name, topo, BlendMode.PremultipliedAlpha) with
            {
                StencilFormat = _depthStencilFormat,
                StencilEnable = true,
                StencilReadMask = 0xFF,
                StencilWriteMask = LowStencilMask,
                StencilFront = Face(StencilCompare.Equal, StencilOp.IncrementWrap),
                StencilBack = Face(StencilCompare.Equal, StencilOp.IncrementWrap),
            };
        }

        _pipelines[(int)Pso.Once] = _device.CreateRenderPipeline(Once("ui-once", PrimitiveTopology.TriangleList));
        _pipelines[(int)Pso.OnceStrip] = _device.CreateRenderPipeline(Once("ui-once-strip", PrimitiveTopology.TriangleStrip));

        // Clear the low bits over a quad (stencil-once reset; clip pass 4), preserving the clip bit.
        _pipelines[(int)Pso.ClearLow] = _device.CreateRenderPipeline(Base("ui-clear-low", PrimitiveTopology.TriangleStrip, BlendMode.Opaque) with
        {
            StencilFormat = _depthStencilFormat,
            StencilEnable = true,
            StencilReadMask = 0xFF,
            StencilWriteMask = LowStencilMask,
            StencilFront = Face(StencilCompare.Always, StencilOp.Zero),
            StencilBack = Face(StencilCompare.Always, StencilOp.Zero),
            ColorWriteDisable = true,
        });

        // Clip pass 1: winding of the body rows into the low bits (incr front / decr back, wrapping).
        _pipelines[(int)Pso.Winding] = _device.CreateRenderPipeline(Base("ui-winding", PrimitiveTopology.TriangleList, BlendMode.Opaque) with
        {
            StencilFormat = _depthStencilFormat,
            StencilEnable = true,
            StencilReadMask = 0xFF,
            StencilWriteMask = LowStencilMask,
            StencilFront = Face(StencilCompare.Always, StencilOp.IncrementWrap),
            StencilBack = Face(StencilCompare.Always, StencilOp.DecrementWrap),
            ColorWriteDisable = true,
        });
        // Clip pass 2: fringe fragments that survive the coverage threshold write a nonzero low value (ref 1).
        _pipelines[(int)Pso.ClipFringe] = _device.CreateRenderPipeline(Base("ui-clip-fringe", PrimitiveTopology.TriangleList, BlendMode.Opaque) with
        {
            StencilFormat = _depthStencilFormat,
            StencilEnable = true,
            StencilReadMask = 0xFF,
            StencilWriteMask = LowStencilMask,
            StencilFront = Face(StencilCompare.Always, StencilOp.Replace),
            StencilBack = Face(StencilCompare.Always, StencilOp.Replace),
            ColorWriteDisable = true,
        });
        // Clip pass 3: SET the clip bit where the low bits are 0. Ref 0x80: the compare reads (stencil & 0x7F) == 0
        // and Replace writes (0x80 & 0x80) == 0x80.
        _pipelines[(int)Pso.ClipMark] = _device.CreateRenderPipeline(Base("ui-clip-mark", PrimitiveTopology.TriangleStrip, BlendMode.Opaque) with
        {
            StencilFormat = _depthStencilFormat,
            StencilEnable = true,
            StencilReadMask = LowStencilMask,
            StencilWriteMask = ClipStencilRef,
            StencilFront = Face(StencilCompare.Equal, StencilOp.Replace),
            StencilBack = Face(StencilCompare.Equal, StencilOp.Replace),
            ColorWriteDisable = true,
        });
        // ResetClip: clear the clip bit (whole viewport).
        _pipelines[(int)Pso.ClipReset] = _device.CreateRenderPipeline(Base("ui-clip-reset", PrimitiveTopology.TriangleStrip, BlendMode.Opaque) with
        {
            StencilFormat = _depthStencilFormat,
            StencilEnable = true,
            StencilReadMask = 0xFF,
            StencilWriteMask = ClipStencilRef,
            StencilFront = Face(StencilCompare.Always, StencilOp.Zero),
            StencilBack = Face(StencilCompare.Always, StencilOp.Zero),
            ColorWriteDisable = true,
        });
    }

    private bool _disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Flush queued uploads so their staging buffers are submitted and freed (only when there is pending work).
        // The host drained the graphics queue before this runs, so the device is live and idle; the try/catch guards
        // a genuine device-lost at teardown.
        if (_pendingCount > 0)
        {
            try
            {
                FlushPendingUploads();
            }
            catch (Exception)
            {
                // The device may already be lost at teardown; nothing else to free then.
            }
        }

        DisposeBatchStaging();
        _maskImages.Dispose();
        _uploadCmd?.Dispose();
        _uploadFence?.Dispose();
        for (int i = 0; i < _textureCount; i++)
        {
            if (_textures[i].Id != 0 && (_textures[i].Flags & MewVGImageFlags.NoDelete) == 0)
            {
                _textures[i].Srv?.Dispose();
                _textures[i].Tex?.Dispose();
            }
        }

        for (int i = 0; i < _framesInFlight; i++)
        {
            _vb[i]?.Dispose();
            _ub[i]?.Dispose();
            _viewCbv[i]?.Dispose();
        }

        DisposePipelines();
    }

    // A host is rebuilt on every device swap; every object it made must go with it (a leaked sampler is a Vulkan
    // validation error at vkDestroyDevice).
    private void DisposePipelines()
    {
        for (int i = 0; i < _pipelines.Length; i++)
        {
            _pipelines[i]?.Dispose();
            _pipelines[i] = null;
        }

        _layout?.Dispose();
        _sampler?.Dispose();
    }

    private IRenderPipeline Pipeline(Pso pso)
    {
        return _pipelines[(int)pso] ?? throw new InvalidOperationException("ui pipelines are not created");
    }
}
