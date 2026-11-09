// GPU -> CPU readback of a viewport texture as a render-graph copy pass (the journey tests' color readback; an
// application would use it for picking or screenshots). One readback buffer per frame in flight: the copy recorded in
// frame N is complete when the scheduler hands slot N back (its BeginFrame waited that slot's fence), so results
// arrive FramesInFlight frames later without a stall; tests call Complete() after a wait.
using System;

using MewStage.Graphics;
using MewStage.Rendering.Graph;

namespace MewStage.Viewport;

/// <summary>Reads a 2D texture back to the CPU through a copy pass (see the file header).</summary>
public sealed class TextureReadback : IDisposable
{
    // DX12 copies texture rows into the buffer at a 256-byte row pitch (D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
    // Vulkan and the headless backend write tight rows (ICommandList.CopyTextureToBuffer contract).
    private const int Dx12RowPitchAlignment = 256;

    private static readonly RgExecute NoOp = static (in _) => { };

    private readonly IGraphicsDevice _device;
    private readonly bool _tightRows;
    private readonly int _bytesPerPixel;
    private readonly IBuffer?[] _ring;
    private readonly ulong[] _ringBytes;
    private readonly (int W, int H, long Frame)[] _pending;
    private readonly Action<PassBuilder> _setup;
    private readonly RgExecute _exec;
    private readonly Action<PassBuilder> _restoreSetup;
    private int _slot = -1;
    private long _frame;
    private RgHandle _src;
    private ResourceState _restore;
    private ITexture? _srcTex;
    private int _w;
    private int _h;
    private byte[] _result = [];

    /// <summary>Creates a readback for textures of <paramref name="bytesPerPixel"/> bytes per texel.</summary>
    /// <param name="device">The GPU device.</param>
    /// <param name="bytesPerPixel">Bytes per texel of the textures read back.</param>
    /// <param name="tightRows">True on Vulkan / headless, false on DX12 (256-byte aligned rows).</param>
    /// <param name="framesInFlight">The scheduler's frame depth (one buffer per frame).</param>
    public TextureReadback(IGraphicsDevice device, int bytesPerPixel, bool tightRows, int framesInFlight)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerPixel);
        _bytesPerPixel = bytesPerPixel;
        _tightRows = tightRows;
        int n = Math.Max(1, framesInFlight);
        _ring = new IBuffer?[n];
        _ringBytes = new ulong[n];
        _pending = new (int, int, long)[n];
        _setup = Setup;
        _exec = Execute;
        _restoreSetup = RestoreSetup;
    }

    /// <summary>Width of the last completed result.</summary>
    public int ResultWidth { get; private set; }

    /// <summary>Height of the last completed result.</summary>
    public int ResultHeight { get; private set; }

    /// <summary>The frame number (count of <see cref="AddCopyPass"/> calls) the last result was recorded in, or -1.</summary>
    public long ResultFrame { get; private set; } = -1;

    /// <summary>The last completed result, tight rows of width * bytesPerPixel.</summary>
    public ReadOnlySpan<byte> Result => _result.AsSpan(0, ResultWidth * ResultHeight * _bytesPerPixel);

    /// <summary>
    /// Adds a copy of <paramref name="texture"/> (graph handle <paramref name="handle"/>) to this frame. The slot
    /// reused by this copy held a copy recorded FramesInFlight frames ago: that one is complete and becomes the result.
    /// </summary>
    /// <param name="graph">This frame's graph.</param>
    /// <param name="handle">The texture's graph handle.</param>
    /// <param name="texture">The texture.</param>
    /// <param name="width">Texture width, pixels.</param>
    /// <param name="height">Texture height, pixels.</param>
    /// <param name="restoreTo">When set, a second pass returns the source to this state after the copy (a persistent
    /// texture whose next import declares it).</param>
    public void AddCopyPass(RenderGraph graph, RgHandle handle, ITexture texture, int width, int height, ResourceState? restoreTo = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(texture);
        _slot = (_slot + 1) % _ring.Length;
        if (_pending[_slot].Frame > 0)
        {
            Harvest(_slot);
        }

        ulong bytes = (ulong)(RowPitch(width) * height);
        if (_ring[_slot] is null || _ringBytes[_slot] < bytes)
        {
            if (_ring[_slot] is { } old)
            {
                _device.DeferDestroy(old);   // a grown slot may still be in flight
            }

            _ring[_slot] = _device.CreateBuffer(new BufferDesc { SizeBytes = bytes, Memory = MemoryClass.Readback, Usage = ResourceUsage.CopyDest, DebugName = "viewport-readback" });
            _ringBytes[_slot] = bytes;
        }

        _frame++;
        _pending[_slot] = (width, height, _frame);
        _src = handle;
        _srcTex = texture;
        _w = width;
        _h = height;
        graph.AddPass("viewport-readback", PassKind.Graphics, _setup);
        if (restoreTo.HasValue)
        {
            _restore = restoreTo.Value;
            graph.AddPass("viewport-readback-restore", PassKind.Graphics, _restoreSetup);
        }
    }

    /// <summary>The (R, G, B, A) bytes of texel (<paramref name="x"/>, <paramref name="y"/>) of the last result (4-byte formats, in the texture's channel order); false when out of range or no result yet.</summary>
    public bool TryReadPixel(int x, int y, out byte c0, out byte c1, out byte c2, out byte c3)
    {
        c0 = c1 = c2 = c3 = 0;
        if (_bytesPerPixel != 4 || ResultFrame < 0 || (uint)x >= (uint)ResultWidth || (uint)y >= (uint)ResultHeight)
        {
            return false;
        }

        int o = ((y * ResultWidth) + x) * 4;
        c0 = _result[o];
        c1 = _result[o + 1];
        c2 = _result[o + 2];
        c3 = _result[o + 3];
        return true;
    }

    /// <summary>Harvests every recorded copy (call only after the GPU is idle, e.g. tests after a queue wait).</summary>
    public void Complete()
    {
        // Oldest first, so the newest copy ends as the result.
        for (int k = 1; k <= _ring.Length; k++)
        {
            int s = (_slot + k) % _ring.Length;
            if (_pending[s].Frame > 0)
            {
                Harvest(s);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // A viewport detached mid-run still has copies in flight: fence-aware release.
        for (int i = 0; i < _ring.Length; i++)
        {
            if (_ring[i] is { } b)
            {
                _device.DeferDestroy(b);
            }

            _ring[i] = null;
        }
    }

    private int RowPitch(int w)
    {
        return _tightRows ? w * _bytesPerPixel : ((w * _bytesPerPixel) + Dx12RowPitchAlignment - 1) / Dx12RowPitchAlignment * Dx12RowPitchAlignment;
    }

    private void RestoreSetup(PassBuilder s)
    {
        s.Write(_src, _restore, ShaderStage.None);
        s.SetExecute(NoOp);
    }

    // Declared as a WRITE in the CopySource state: a read-only pass has no graph output and the compiler culls it
    // (only passes that write an imported / output resource are roots).
    private void Setup(PassBuilder s)
    {
        s.Write(_src, ResourceState.CopySource, ShaderStage.None);
        s.SetExecute(_exec);
    }

    private void Execute(in RgPassContext ctx)
    {
        ctx.CommandList.CopyTextureToBuffer(_srcTex!, _ring[_slot]!, (uint)_w, (uint)_h);
    }

    private unsafe void Harvest(int slot)
    {
        (int w, int h, long frame) = _pending[slot];
        _pending[slot] = default;
        IBuffer? buf = _ring[slot];
        if (buf is not IMappedBuffer mapped || frame <= ResultFrame)
        {
            return;
        }

        int tight = w * _bytesPerPixel;
        int pitch = RowPitch(w);
        if (_result.Length < tight * h)
        {
            _result = new byte[tight * h];
        }

        var src = new ReadOnlySpan<byte>((void*)mapped.MappedPointer, pitch * h);
        for (int y = 0; y < h; y++)
        {
            src.Slice(y * pitch, tight).CopyTo(_result.AsSpan(y * tight, tight));
        }

        ResultWidth = w;
        ResultHeight = h;
        ResultFrame = frame;
    }
}
