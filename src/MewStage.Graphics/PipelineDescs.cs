// Graphics and compute pipeline-state descriptions, consumed by IGraphicsDevice.CreateRenderPipeline /
// CreateComputePipeline. Shaders are COOKED bytecode (SPIR-V for Vulkan, DXIL for D3D12); nothing compiles at run
// time. Reversed-Z depth (Greater / GreaterEqual tests) and counter-clockwise front faces are the fixed conventions.

namespace MewStage.Graphics;

/// <summary>Face culling.</summary>
public enum CullMode : byte
{
    /// <summary>Cull nothing.</summary>
    None = 0,

    /// <summary>Cull front faces.</summary>
    Front = 1,

    /// <summary>Cull back faces.</summary>
    Back = 2,
}

/// <summary>Primitive topology.</summary>
public enum PrimitiveTopology : byte
{
    /// <summary>Independent triangles.</summary>
    TriangleList = 0,

    /// <summary>A triangle strip.</summary>
    TriangleStrip = 1,

    /// <summary>Independent lines.</summary>
    LineList = 2,

    /// <summary>Points.</summary>
    PointList = 3,
}

/// <summary>Fixed-function blend state. <see cref="Multiplicative"/> is dst *= src: color {Zero, SrcColor, Add},
/// alpha {Zero, One, Add}.</summary>
public enum BlendMode : byte
{
    /// <summary>No blending.</summary>
    Opaque = 0,

    /// <summary>Straight-alpha source-over.</summary>
    AlphaBlend = 1,

    /// <summary>Additive.</summary>
    Additive = 2,

    /// <summary>Premultiplied-alpha source-over (the UI pass).</summary>
    PremultipliedAlpha = 3,

    /// <summary>Multiplicative.</summary>
    Multiplicative = 4,
}

/// <summary>Depth comparison. The reversed-Z default is <see cref="Greater"/>.</summary>
public enum DepthCompare : byte
{
    /// <summary>Pass when the fragment is nearer (reversed-Z: greater).</summary>
    Greater = 0,

    /// <summary>Pass when nearer or equal.</summary>
    GreaterEqual = 1,

    /// <summary>Pass when equal (a forward pass tested against a depth prepass).</summary>
    Equal = 2,

    /// <summary>Pass when farther or equal.</summary>
    LessEqual = 3,

    /// <summary>Always pass.</summary>
    Always = 4,
}

/// <summary>Stencil comparison. <see cref="Always"/> is the no-op default.</summary>
public enum StencilCompare : byte
{
    /// <summary>Always pass.</summary>
    Always = 0,

    /// <summary>Pass when the masked buffer value equals the masked reference.</summary>
    Equal = 1,

    /// <summary>Pass when the masked buffer value differs from the masked reference.</summary>
    NotEqual = 2,

    /// <summary>Never pass.</summary>
    Never = 3,
}

/// <summary>Stencil buffer operation on fail, depth-fail or pass. NanoVG's concave fill uses Keep plus
/// increment / decrement with wrap on pass; its cover pass uses Zero.</summary>
public enum StencilOp : byte
{
    /// <summary>Leave the value.</summary>
    Keep = 0,

    /// <summary>Write zero.</summary>
    Zero = 1,

    /// <summary>Write the reference value.</summary>
    Replace = 2,

    /// <summary>Increment with wrap.</summary>
    IncrementWrap = 3,

    /// <summary>Decrement with wrap.</summary>
    DecrementWrap = 4,

    /// <summary>Bitwise invert.</summary>
    Invert = 5,
}

/// <summary>Per-face stencil state. The default (Always / Keep / Keep / Keep) is a no-op. NanoVG's concave fill
/// needs asymmetric front and back operations, so <see cref="RenderPipelineDesc.StencilFront"/> and
/// <see cref="RenderPipelineDesc.StencilBack"/> are described independently.</summary>
public readonly struct StencilFaceState
{
    /// <summary>The test between the reference value and the stencil buffer that decides pass or fail for this face.</summary>
    public StencilCompare Compare { get; init; }

    /// <summary>Operation when the stencil test fails.</summary>
    public StencilOp Fail { get; init; }

    /// <summary>Operation when the stencil test passes but the depth test fails.</summary>
    public StencilOp DepthFail { get; init; }

    /// <summary>Operation when both tests pass.</summary>
    public StencilOp Pass { get; init; }

    /// <summary>A no-op face: the test always passes and the buffer is untouched.</summary>
    public static StencilFaceState Disabled => new() { Compare = StencilCompare.Always, Fail = StencilOp.Keep, DepthFail = StencilOp.Keep, Pass = StencilOp.Keep };
}

/// <summary>One vertex attribute: location, format and byte offset within the interleaved vertex.</summary>
public readonly struct VertexAttribute
{
    /// <summary>Shader input location (Vulkan binds by it).</summary>
    public int Location { get; init; }

    /// <summary>Attribute format.</summary>
    public Format Format { get; init; }

    /// <summary>Byte offset within the vertex.</summary>
    public int Offset { get; init; }

    /// <summary>D3D12 semantic name matching the HLSL vertex input (for example "POSITION"); Vulkan ignores it.
    /// Null means "TEXCOORD" with <see cref="Location"/> as the index.</summary>
    public string? Semantic { get; init; }

    /// <summary>D3D12 semantic index (the N in POSITION0 / TEXCOORD1), used only when <see cref="Semantic"/> is set.</summary>
    public int SemanticIndex { get; init; }
}

/// <summary>Compute pipeline description: cooked bytecode plus its layout.</summary>
public readonly struct ComputePipelineDesc
{
    /// <summary>Debug name.</summary>
    public string Name { get; init; }

    /// <summary>Cooked bytecode (SPIR-V for Vulkan, DXIL for D3D12).</summary>
    public byte[] ByteCode { get; init; }

    /// <summary>Entry point name (for example "CSMain").</summary>
    public string EntryPoint { get; init; }

    /// <summary>The pipeline layout.</summary>
    public IPipelineLayout Layout { get; init; }
}

/// <summary>Graphics pipeline description. Dynamic-rendering style: color and depth formats are declared here, there is
/// no render-pass object. Depth uses a reversed-Z test; the front face is counter-clockwise.</summary>
public readonly struct RenderPipelineDesc
{
    /// <summary>Debug name.</summary>
    public string Name { get; init; }

    /// <summary>Cooked vertex shader bytecode.</summary>
    public byte[] VertexShader { get; init; }

    /// <summary>Cooked pixel shader bytecode.</summary>
    public byte[] PixelShader { get; init; }

    /// <summary>Vertex entry point name (for example "VSMain").</summary>
    public string VertexEntry { get; init; }

    /// <summary>Pixel entry point name (for example "PSMain").</summary>
    public string PixelEntry { get; init; }

    /// <summary>The pipeline layout.</summary>
    public IPipelineLayout Layout { get; init; }

    /// <summary>Vertex attributes; empty means no vertex buffer (the shader derives positions from the vertex index).</summary>
    public VertexAttribute[] VertexAttributes { get; init; }

    /// <summary>Vertex stride in bytes.</summary>
    public int VertexStride { get; init; }

    /// <summary>Color attachment formats.</summary>
    public Format[] ColorFormats { get; init; }

    /// <summary>Depth attachment format; <see cref="Format.Unknown"/> means no depth attachment.</summary>
    public Format DepthFormat { get; init; }

    /// <summary>Enable the depth test.</summary>
    public bool DepthTest { get; init; }

    /// <summary>Enable depth writes.</summary>
    public bool DepthWrite { get; init; }

    /// <summary>Depth comparison (default <see cref="DepthCompare.Greater"/>, reversed-Z).</summary>
    public DepthCompare DepthCompare { get; init; }

    /// <summary>Face culling.</summary>
    public CullMode Cull { get; init; }

    /// <summary>Primitive topology.</summary>
    public PrimitiveTopology Topology { get; init; }

    /// <summary>Blend state.</summary>
    public BlendMode Blend { get; init; }

    /// <summary>Multisample count (0 or 1 = none).</summary>
    public int SampleCount { get; init; }

    /// <summary>Rasterize triangles as lines. Ignored where the device lacks non-solid fill: the pipeline is then solid.</summary>
    public bool Wireframe { get; init; }

    /// <summary>Enable the stencil test and writes. When false (the default) the stencil fields below are ignored.</summary>
    public bool StencilEnable { get; init; }

    /// <summary>Stencil attachment format (for example <see cref="Format.D24_UNorm_S8_UInt"/>); <see cref="Format.Unknown"/>
    /// derives it from <see cref="DepthFormat"/>.</summary>
    public Format StencilFormat { get; init; }

    /// <summary>Stencil read mask (the bits the comparison reads).</summary>
    public byte StencilReadMask { get; init; }

    /// <summary>Stencil write mask (the bits the operations write).</summary>
    public byte StencilWriteMask { get; init; }

    /// <summary>Front-face stencil state.</summary>
    public StencilFaceState StencilFront { get; init; }

    /// <summary>Back-face stencil state.</summary>
    public StencilFaceState StencilBack { get; init; }

    /// <summary>When true the color write mask is off: a stencil-marking pass writes the buffer and no color.</summary>
    public bool ColorWriteDisable { get; init; }
}
