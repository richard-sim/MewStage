// The demo's 3D content as render-graph passes: a lit, spinning cube and an optional floor grid, drawn into whatever
// color target the caller hands over (a swapchain backbuffer in Overlay mode, an offscreen viewport target in
// Workbench mode). The renderer owns the GPU objects that outlive a frame (pipelines, the cube's vertex and index
// buffers); per-frame constants go through the scheduler's upload ring, so steady-state frames allocate nothing. The
// scene state it draws (CubeScene: settings and spin angle) is shared, so several renderers (two viewports) show one
// scene from their own cameras.
using System;
using System.Numerics;
using System.Runtime.InteropServices;

using MewStage.Graphics;
using MewStage.Rendering.Camera;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;

namespace MewStage.Scene;

/// <summary>Draws the cube scene into a render-graph target (see the file header).</summary>
public sealed class CubeSceneRenderer : IDisposable
{
    /// <summary>The depth format of the transient depth buffer the scene pass creates.</summary>
    public const Format DepthFormat = Format.D32_Float;

    private const ulong VertexBytes = CubeMesh.VertexCount * CubeVertex.StrideBytes;
    private const ulong IndexBytes = CubeMesh.IndexCount * sizeof(ushort);

    private readonly IGraphicsDevice _device;
    private readonly IPipelineLayout _layout;
    private readonly IRenderPipeline _cubeSolid;
    private readonly IRenderPipeline _cubeWire;
    private readonly IRenderPipeline _grid;
    private readonly IBuffer _vertices;
    private readonly IBuffer _indices;
    private readonly RgExecute _execute;
    private readonly Action<PassBuilder> _setup;

    // Per-frame state captured between AddPasses and the pass's execute.
    private RgHandle _target;
    private RgHandle _depth;
    private uint _width;
    private uint _height;
    private bool _clear;
    private ClearColor _background;
    private bool _drawGrid;
    private GpuBufferView _cubeCbv;
    private GpuBufferView _gridCbv;
    private bool _disposed;

    /// <param name="device">The GPU device.</param>
    /// <param name="cubeVs">Cooked <c>cube.hlsl</c> vertex shader for this device's API.</param>
    /// <param name="cubePs">Cooked <c>cube.hlsl</c> pixel shader.</param>
    /// <param name="gridVs">Cooked <c>grid.hlsl</c> vertex shader.</param>
    /// <param name="gridPs">Cooked <c>grid.hlsl</c> pixel shader.</param>
    /// <param name="colorFormat">The format of the targets the passes draw into.</param>
    /// <param name="scene">The scene state to draw (shared between renderers).</param>
    public CubeSceneRenderer(IGraphicsDevice device, byte[] cubeVs, byte[] cubePs, byte[] gridVs, byte[] gridPs, Format colorFormat, CubeScene scene)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(scene);
        _device = device;
        Scene = scene;
        _execute = Execute;
        _setup = Setup;
        _layout = device.CreatePipelineLayout(new PipelineLayoutDesc { CbvCount = 1, DebugName = "scene" });

        RenderPipelineDesc Cube(string name, bool wire)
        {
            return new RenderPipelineDesc
            {
                Name = name,
                VertexShader = cubeVs,
                PixelShader = cubePs,
                VertexEntry = "VSMain",
                PixelEntry = "PSMain",
                Layout = _layout,
                VertexAttributes = CubeVertex.Attributes(),
                VertexStride = CubeVertex.Stride,
                ColorFormats = [colorFormat],
                DepthFormat = DepthFormat,
                DepthTest = true,
                DepthWrite = true,
                DepthCompare = DepthCompare.GreaterEqual,
                Cull = wire ? CullMode.None : CullMode.Back,
                Topology = PrimitiveTopology.TriangleList,
                Blend = BlendMode.Opaque,
                SampleCount = 1,
                Wireframe = wire,
            };
        }

        _cubeSolid = device.CreateRenderPipeline(Cube("cube", wire: false));
        _cubeWire = device.CreateRenderPipeline(Cube("cube-wire", wire: true));
        _grid = device.CreateRenderPipeline(new RenderPipelineDesc
        {
            Name = "grid",
            VertexShader = gridVs,
            PixelShader = gridPs,
            VertexEntry = "VSMain",
            PixelEntry = "PSMain",
            Layout = _layout,
            VertexAttributes = [],
            VertexStride = 0,
            ColorFormats = [colorFormat],
            DepthFormat = DepthFormat,
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = DepthCompare.GreaterEqual,
            Cull = CullMode.None,
            Topology = PrimitiveTopology.TriangleList,
            Blend = BlendMode.PremultipliedAlpha,
            SampleCount = 1,
        });

        // The cube's geometry: written once through host-visible buffers (24 vertices, 36 indices).
        _vertices = device.CreateBuffer(new BufferDesc { SizeBytes = VertexBytes, Memory = MemoryClass.Upload, Usage = ResourceUsage.VertexBuffer, DebugName = "cube-vb" });
        _indices = device.CreateBuffer(new BufferDesc { SizeBytes = IndexBytes, Memory = MemoryClass.Upload, Usage = ResourceUsage.IndexBuffer, DebugName = "cube-ib" });
        Span<CubeVertex> verts = stackalloc CubeVertex[CubeMesh.VertexCount];
        Span<ushort> idx = stackalloc ushort[CubeMesh.IndexCount];
        CubeMesh.Build(verts, idx);
        unsafe
        {
            MemoryMarshal.AsBytes(verts).CopyTo(new Span<byte>((void*)((IMappedBuffer)_vertices).MappedPointer, CubeMesh.VertexCount * CubeVertex.Stride));
            MemoryMarshal.AsBytes(idx).CopyTo(new Span<byte>((void*)((IMappedBuffer)_indices).MappedPointer, CubeMesh.IndexCount * sizeof(ushort)));
        }
    }

    /// <summary>The scene state this renderer draws.</summary>
    public CubeScene Scene { get; }

    /// <summary>The scene's settings (<see cref="CubeScene.Settings"/>).</summary>
    public SceneSettings Settings => Scene.Settings;

    /// <summary>Frames the renderer has recorded.</summary>
    public long FramesDrawn { get; private set; }

    /// <summary>
    /// Add the scene pass for this frame: it writes <paramref name="target"/> (<paramref name="clear"/> = clear it to
    /// <paramref name="background"/> first, else load it), creates a transient reversed-Z depth buffer, draws the grid
    /// (when enabled) and the cube from <paramref name="camera"/>. Constants come from <paramref name="frame"/>'s
    /// upload ring. Returns the view-projection the frame was drawn with.
    /// </summary>
    public Matrix4x4 AddPasses(RenderGraph graph, RgHandle target, uint width, uint height, in RenderCamera camera, FrameContext frame, bool clear, ClearColor background)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _target = target;
        _width = width;
        _height = height;
        _clear = clear;
        _background = background;
        _drawGrid = Settings.ShowGrid;

        RenderCamera cam = camera;
        float aspect = height == 0 ? 1f : (float)width / height;
        Matrix4x4 viewProj = cam.ViewProjection(aspect);

        UploadAllocation cube = frame.Upload.Allocate((ulong)Marshal.SizeOf<CubeConstants>());
        Vector3 light = Settings.LightDirection();
        cube.Write(new CubeConstants
        {
            World = Scene.World(),
            ViewProj = viewProj,
            LightDir = new Vector4(light, Settings.Ambient),
            Tint = new Vector4(Settings.Tint, 1f),
        });
        _cubeCbv = cube.View;

        if (_drawGrid)
        {
            Matrix4x4.Invert(viewProj, out Matrix4x4 inv);
            UploadAllocation grid = frame.Upload.Allocate((ulong)Marshal.SizeOf<GridConstants>());
            grid.Write(new GridConstants
            {
                ViewProj = viewProj,
                InvViewProj = inv,
                Eye = new Vector4(cam.Position, 18f),
                Params = new Vector4(0.5f, 4f, 1.2f, 2f),
                MinorColor = new Vector4(0.55f, 0.58f, 0.64f, 0.35f),
                MajorColor = new Vector4(0.70f, 0.73f, 0.78f, 0.55f),
                AxisXColor = new Vector4(0.90f, 0.30f, 0.30f, 0.9f),
                AxisYColor = new Vector4(0.30f, 0.85f, 0.40f, 0.9f),
            });
            _gridCbv = grid.View;
        }

        graph.AddPass("scene", PassKind.Graphics, _setup);
        FramesDrawn++;
        return viewProj;
    }

    private void Setup(PassBuilder s)
    {
        s.Write(_target, ResourceState.RenderTarget);
        _depth = s.CreateDepth(new RgTextureDesc { Name = "scene-depth", Format = DepthFormat, Width = _width, Height = _height, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 });
        s.SetExecute(_execute);
    }

    private void Execute(in RgPassContext ctx)
    {
        IGraphicsRecorder rec = ctx.Recorder;
        Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = ctx.Resolve(_target), Clear = _clear, ClearValue = _background }];
        var da = new DepthAttachment { TextureHandle = ctx.Resolve(_depth), Clear = true, ClearDepth = 0f };
        rec.BeginRendering(ca, in da, _width, _height);
        rec.SetViewportScissor(0, 0, _width, _height);

        if (_drawGrid)
        {
            rec.SetPipeline(_grid);
            rec.BindConstantBuffer(0, in _gridCbv);
            rec.Draw(3, 1, 0, 0);
        }

        rec.SetPipeline(Settings.Wireframe ? _cubeWire : _cubeSolid);
        rec.BindConstantBuffer(0, in _cubeCbv);
        rec.BindVertexBuffer(0, new GpuBufferView(_vertices.Handle, 0, VertexBytes), (uint)CubeVertex.Stride);
        rec.BindIndexBuffer(new GpuBufferView(_indices.Handle, 0, IndexBytes), index32: false);
        rec.DrawIndexed(CubeMesh.IndexCount, 1, 0, 0, 0);
        rec.EndRendering();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cubeSolid.Dispose();
        _cubeWire.Dispose();
        _grid.Dispose();
        _vertices.Dispose();
        _indices.Dispose();
        _layout.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct CubeConstants
    {
        public Matrix4x4 World;
        public Matrix4x4 ViewProj;
        public Vector4 LightDir;
        public Vector4 Tint;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct GridConstants
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public Vector4 Eye;
        public Vector4 Params;
        public Vector4 MinorColor;
        public Vector4 MajorColor;
        public Vector4 AxisXColor;
        public Vector4 AxisYColor;
    }
}
