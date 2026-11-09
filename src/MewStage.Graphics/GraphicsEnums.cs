// The backend-agnostic vocabulary shared by the GPU device backends (MewStage.Rendering) and everything that
// records work through them. This project is BCL-only: the concrete backends pull Vortice / Silk.NET in.
//
// Conventions used throughout MewStage: row-major, row-vector matrices (mul(v, M) in HLSL); reversed-Z depth in
// [0, 1] (clear depth 0, test GreaterEqual); counter-clockwise front faces. See docs/architecture.md.

using System;
using System.Diagnostics.CodeAnalysis;

namespace MewStage.Graphics;

/// <summary>GPU submission queue class. On single-queue hardware compute and copy alias the graphics queue.</summary>
public enum QueueKind : byte
{
    /// <summary>The graphics (universal) queue.</summary>
    Graphics = 0,

    /// <summary>A dedicated asynchronous compute queue.</summary>
    AsyncCompute = 1,

    /// <summary>A dedicated transfer queue.</summary>
    Copy = 2,
}

/// <summary>Descriptor heap class.</summary>
public enum DescriptorKind : byte
{
    /// <summary>Constant-buffer, shader-resource and unordered-access views.</summary>
    CbvSrvUav = 0,

    /// <summary>Samplers.</summary>
    Sampler = 1,
}

/// <summary>Render-graph pass execution kind.</summary>
public enum PassKind : byte
{
    /// <summary>Draws into render targets.</summary>
    Graphics = 0,

    /// <summary>Dispatches compute work.</summary>
    Compute = 1,

    /// <summary>Copies between resources.</summary>
    Copy = 2,
}

/// <summary>Shader stage mask for resource reads and writes declared on a render-graph pass.</summary>
[Flags]
public enum ShaderStage : ushort
{
    /// <summary>No stage.</summary>
    None = 0,

    /// <summary>The vertex stage.</summary>
    Vertex = 1 << 0,

    /// <summary>The pixel (fragment) stage.</summary>
    Pixel = 1 << 1,

    /// <summary>The compute stage.</summary>
    Compute = 1 << 2,

    /// <summary>The geometry stage.</summary>
    Geometry = 1 << 3,

    /// <summary>The hull (tessellation control) stage.</summary>
    Hull = 1 << 4,

    /// <summary>The domain (tessellation evaluation) stage.</summary>
    Domain = 1 << 5,

    /// <summary>The mesh stage.</summary>
    Mesh = 1 << 6,

    /// <summary>The amplification (task) stage.</summary>
    Amplification = 1 << 7,

    /// <summary>Every stage.</summary>
    All = Vertex | Pixel | Compute | Geometry | Hull | Domain | Mesh | Amplification,
}

/// <summary>GPU resource format, mapped per backend. Names mirror the DXGI / VkFormat conventions.</summary>
[SuppressMessage("Naming", "CA1707", Justification = "Graphics format names mirror DXGI / VkFormat conventions (R8G8B8A8_UNorm).")]
[SuppressMessage("Naming", "CA1720", Justification = "Format names carry their component types by convention.")]
public enum Format : ushort
{
    /// <summary>No format.</summary>
    Unknown = 0,

    /// <summary>8-bit single channel, unsigned normalized.</summary>
    R8_UNorm,

    /// <summary>8-bit single channel, unsigned integer.</summary>
    R8_UInt,

    /// <summary>8-bit two channels, unsigned normalized.</summary>
    R8G8_UNorm,

    /// <summary>8-bit RGBA, unsigned normalized.</summary>
    R8G8B8A8_UNorm,

    /// <summary>8-bit RGBA, sRGB-encoded.</summary>
    R8G8B8A8_UNorm_sRGB,

    /// <summary>8-bit BGRA, unsigned normalized (the usual swapchain format).</summary>
    B8G8R8A8_UNorm,

    /// <summary>8-bit BGRA, sRGB-encoded.</summary>
    B8G8R8A8_UNorm_sRGB,

    /// <summary>16-bit single channel, unsigned integer.</summary>
    R16_UInt,

    /// <summary>16-bit single channel, unsigned normalized.</summary>
    R16_UNorm,

    /// <summary>16-bit single channel, floating point.</summary>
    R16_Float,

    /// <summary>16-bit two channels, floating point.</summary>
    R16G16_Float,

    /// <summary>16-bit RGBA, floating point.</summary>
    R16G16B16A16_Float,

    /// <summary>32-bit single channel, unsigned integer.</summary>
    R32_UInt,

    /// <summary>32-bit single channel, floating point.</summary>
    R32_Float,

    /// <summary>32-bit two channels, floating point.</summary>
    R32G32_Float,

    /// <summary>32-bit three channels, floating point.</summary>
    R32G32B32_Float,

    /// <summary>32-bit RGBA, floating point.</summary>
    R32G32B32A32_Float,

    /// <summary>Packed 11/11/10-bit floating point RGB.</summary>
    R11G11B10_Float,

    /// <summary>Packed 10/10/10/2-bit unsigned normalized RGBA.</summary>
    R10G10B10A2_UNorm,

    /// <summary>32-bit floating point depth.</summary>
    D32_Float,

    /// <summary>24-bit unsigned normalized depth with an 8-bit stencil.</summary>
    D24_UNorm_S8_UInt,

    /// <summary>32-bit floating point depth with an 8-bit stencil.</summary>
    D32_Float_S8_UInt,

    /// <summary>BC5 block-compressed two channels.</summary>
    BC5_UNorm,

    /// <summary>BC7 block-compressed RGBA.</summary>
    BC7_UNorm,

    /// <summary>BC7 block-compressed RGBA, sRGB-encoded.</summary>
    BC7_UNorm_sRGB,

    /// <summary>ASTC 4x4 block-compressed RGBA.</summary>
    ASTC_4x4_UNorm,

    /// <summary>ASTC 4x4 block-compressed RGBA, sRGB-encoded.</summary>
    ASTC_4x4_UNorm_sRGB,
}

/// <summary>Explicit resource state for barrier synthesis. Maps to D3D12 resource states and to Vulkan image layouts
/// plus access and stage masks. Owned by the render graph: feature code never sets these directly.</summary>
[Flags]
public enum ResourceState : uint
{
    /// <summary>No specific state (D3D12 COMMON; a Vulkan image in the GENERAL or UNDEFINED layout).</summary>
    Common = 0,

    /// <summary>Read as a vertex buffer.</summary>
    VertexBuffer = 1 << 0,

    /// <summary>Read as an index buffer.</summary>
    IndexBuffer = 1 << 1,

    /// <summary>Read as a constant buffer.</summary>
    ConstantBuffer = 1 << 2,

    /// <summary>Read as indirect draw or dispatch arguments.</summary>
    IndirectArgument = 1 << 3,

    /// <summary>Read through a shader-resource view.</summary>
    ShaderResource = 1 << 4,

    /// <summary>Read and written through an unordered-access view.</summary>
    UnorderedAccess = 1 << 5,

    /// <summary>Written as a render target.</summary>
    RenderTarget = 1 << 6,

    /// <summary>Written as a depth-stencil attachment.</summary>
    DepthWrite = 1 << 7,

    /// <summary>Read as a depth-stencil attachment (no writes).</summary>
    DepthRead = 1 << 8,

    /// <summary>The source of a copy.</summary>
    CopySource = 1 << 9,

    /// <summary>The destination of a copy.</summary>
    CopyDest = 1 << 10,

    /// <summary>Ready to present.</summary>
    Present = 1 << 11,

    /// <summary>The source of a multisample resolve.</summary>
    ResolveSource = 1 << 12,

    /// <summary>The destination of a multisample resolve.</summary>
    ResolveDest = 1 << 13,

    /// <summary>Depth tested read-only AND sampled in the same pass. A combined value, so every backend map must
    /// name it explicitly (the state-map tests enumerate the enum).</summary>
    DepthReadShaderResource = DepthRead | ShaderResource,
}

/// <summary>Resource view kinds.</summary>
public enum ViewKind : byte
{
    /// <summary>Shader-resource view.</summary>
    Srv = 0,

    /// <summary>Unordered-access view.</summary>
    Uav = 1,

    /// <summary>Constant-buffer view.</summary>
    Cbv = 2,

    /// <summary>Render-target view.</summary>
    Rtv = 3,

    /// <summary>Depth-stencil view.</summary>
    Dsv = 4,

    /// <summary>Vertex buffer binding.</summary>
    Vertex = 5,

    /// <summary>Index buffer binding.</summary>
    Index = 6,

    /// <summary>Indirect argument buffer binding.</summary>
    Indirect = 7,
}

/// <summary>Device loss or removal status.</summary>
public enum DeviceStatus : byte
{
    /// <summary>The device is healthy.</summary>
    Ok = 0,

    /// <summary>The device was removed (adapter unplugged, driver restart).</summary>
    Removed = 1,

    /// <summary>The device was reset.</summary>
    Reset = 2,

    /// <summary>The device hung and was recovered by the OS.</summary>
    Hung = 3,

    /// <summary>The device ran out of memory.</summary>
    OutOfMemory = 4,
}

/// <summary>Memory residency class for a buffer.</summary>
public enum MemoryClass : byte
{
    /// <summary>Device-local, GPU-only (D3D12 DEFAULT heap; Vulkan DEVICE_LOCAL).</summary>
    DeviceLocal = 0,

    /// <summary>CPU-to-GPU staging (D3D12 UPLOAD heap; Vulkan HOST_VISIBLE | HOST_COHERENT), persistently mapped.</summary>
    Upload = 1,

    /// <summary>GPU-to-CPU results (D3D12 READBACK heap; Vulkan HOST_VISIBLE | HOST_CACHED), persistently mapped.</summary>
    Readback = 2,

    /// <summary>Unified memory (integrated GPUs): CPU writes and GPU reads in place, no staging copy.</summary>
    Shared = 3,
}

/// <summary>How compute is submitted relative to graphics.</summary>
public enum ComputeSubmissionMode : byte
{
    /// <summary>Compute records on the graphics queue.</summary>
    SameQueue = 0,

    /// <summary>Compute records on a real second queue and may overlap graphics.</summary>
    AsyncCompute = 1,
}

/// <summary>Bindless / descriptor-indexing tier. The baseline is <see cref="None"/> (per-draw tables).</summary>
public enum BindlessTier : byte
{
    /// <summary>Per-draw descriptor tables only.</summary>
    None = 0,

    /// <summary>Bounded descriptor indexing.</summary>
    Tier1 = 1,

    /// <summary>Unbounded descriptor indexing.</summary>
    Full = 2,
}

/// <summary>Indirect command-generation tier. The baseline is <see cref="None"/> (a direct draw loop).</summary>
public enum IndirectTier : byte
{
    /// <summary>No indirect drawing.</summary>
    None = 0,

    /// <summary>Multi-draw indirect with a CPU-side count.</summary>
    MultiDraw = 1,

    /// <summary>Multi-draw indirect with a GPU-side count.</summary>
    DrawIndirectCount = 2,
}

/// <summary>Variable-rate-shading tier. The baseline is <see cref="None"/>.</summary>
public enum VrsTier : byte
{
    /// <summary>Uniform full-rate shading.</summary>
    None = 0,

    /// <summary>A shading rate per draw.</summary>
    PerDraw = 1,

    /// <summary>A shading rate per screen tile.</summary>
    PerTile = 2,
}
