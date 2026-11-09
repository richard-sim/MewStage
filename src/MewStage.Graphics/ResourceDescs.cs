// Resource, view and barrier descriptions and the small value types the device contract uses. Blittable value
// structs, so creation and binding allocate nothing; the backends translate them to D3D12 / Vulkan create-info.

using System;

namespace MewStage.Graphics;

/// <summary>Bind-usage flags a resource must be created with (they inform heap and usage selection).</summary>
[Flags]
public enum ResourceUsage : uint
{
    /// <summary>No usage.</summary>
    None = 0,

    /// <summary>Bound as a vertex buffer.</summary>
    VertexBuffer = 1 << 0,

    /// <summary>Bound as an index buffer.</summary>
    IndexBuffer = 1 << 1,

    /// <summary>Bound as a constant buffer.</summary>
    ConstantBuffer = 1 << 2,

    /// <summary>Read through a shader-resource view.</summary>
    ShaderResource = 1 << 3,

    /// <summary>Read and written through an unordered-access view.</summary>
    UnorderedAccess = 1 << 4,

    /// <summary>Written as a render target.</summary>
    RenderTarget = 1 << 5,

    /// <summary>Written as a depth-stencil attachment.</summary>
    DepthStencil = 1 << 6,

    /// <summary>Read as indirect arguments.</summary>
    IndirectArgs = 1 << 7,

    /// <summary>The source of copies.</summary>
    CopySource = 1 << 8,

    /// <summary>The destination of copies.</summary>
    CopyDest = 1 << 9,
}

/// <summary>Texture dimensionality.</summary>
public enum TextureDimension : byte
{
    /// <summary>A one-dimensional texture.</summary>
    Tex1D = 0,

    /// <summary>A two-dimensional texture.</summary>
    Tex2D = 1,

    /// <summary>A three-dimensional texture.</summary>
    Tex3D = 2,

    /// <summary>A cube map.</summary>
    Cube = 3,

    /// <summary>An array of two-dimensional textures.</summary>
    Tex2DArray = 4,

    /// <summary>An array of cube maps.</summary>
    CubeArray = 5,
}

/// <summary>Buffer creation parameters.</summary>
public readonly struct BufferDesc
{
    /// <summary>Size in bytes.</summary>
    public ulong SizeBytes { get; init; }

    /// <summary>Element stride for structured buffers (0 for raw / byte-address buffers).</summary>
    public uint StructureStride { get; init; }

    /// <summary>Memory residency class.</summary>
    public MemoryClass Memory { get; init; }

    /// <summary>Bind usages the buffer must support.</summary>
    public ResourceUsage Usage { get; init; }

    /// <summary>Debug name shown in captures and validation messages.</summary>
    public string? DebugName { get; init; }
}

/// <summary>Texture creation parameters.</summary>
public readonly struct TextureDesc
{
    /// <summary>Dimensionality.</summary>
    public TextureDimension Dimension { get; init; }

    /// <summary>Texel format.</summary>
    public Format Format { get; init; }

    /// <summary>Width in texels.</summary>
    public uint Width { get; init; }

    /// <summary>Height in texels.</summary>
    public uint Height { get; init; }

    /// <summary>Depth for 3D textures, array layer count otherwise.</summary>
    public uint DepthOrArrayLayers { get; init; }

    /// <summary>Mip level count.</summary>
    public uint MipLevels { get; init; }

    /// <summary>Multisample count (1 = no multisampling).</summary>
    public uint SampleCount { get; init; }

    /// <summary>Bind usages the texture must support.</summary>
    public ResourceUsage Usage { get; init; }

    /// <summary>Debug name shown in captures and validation messages.</summary>
    public string? DebugName { get; init; }

    /// <summary>The color a render target is cleared to every frame, when the application always clears it to the
    /// same value. D3D12 passes it as the resource's optimized clear value (a fast clear and no mismatching-clear
    /// warning); Vulkan and the headless device ignore it. Depth-stencil textures always get the reversed-Z clear
    /// (depth 0, stencil 0) and need nothing here.</summary>
    public ClearColor? ClearColor { get; init; }
}

/// <summary>A logical render-graph resource description. Transient aliasing keys on it.</summary>
public readonly struct ResourceDesc
{
    /// <summary>True for a buffer, false for a texture.</summary>
    public bool IsBuffer { get; init; }

    /// <summary>The buffer description when <see cref="IsBuffer"/> is true.</summary>
    public BufferDesc Buffer { get; init; }

    /// <summary>The texture description when <see cref="IsBuffer"/> is false.</summary>
    public TextureDesc Texture { get; init; }

    /// <summary>Describes a buffer.</summary>
    /// <param name="buffer">The buffer description.</param>
    /// <returns>A resource description wrapping <paramref name="buffer"/>.</returns>
    public static ResourceDesc OfBuffer(in BufferDesc buffer)
    {
        return new() { IsBuffer = true, Buffer = buffer };
    }

    /// <summary>Describes a texture.</summary>
    /// <param name="texture">The texture description.</param>
    /// <returns>A resource description wrapping <paramref name="texture"/>.</returns>
    public static ResourceDesc OfTexture(in TextureDesc texture)
    {
        return new() { IsBuffer = false, Texture = texture };
    }
}

/// <summary>Buffer view (SRV / UAV / CBV / vertex / index / indirect) creation parameters.</summary>
public readonly struct BufferViewDesc
{
    /// <summary>View kind.</summary>
    public ViewKind Kind { get; init; }

    /// <summary>Format for a typed buffer SRV / UAV; <see cref="Format.Unknown"/> for structured or raw views.</summary>
    public Format Format { get; init; }

    /// <summary>Byte offset of the viewed range.</summary>
    public ulong Offset { get; init; }

    /// <summary>Byte size of the viewed range.</summary>
    public ulong SizeBytes { get; init; }

    /// <summary>Element stride for structured views.</summary>
    public uint StructureStride { get; init; }
}

/// <summary>Texture view (SRV / UAV / RTV / DSV over a subresource range) creation parameters.</summary>
public readonly struct TextureViewDesc
{
    /// <summary>View kind.</summary>
    public ViewKind Kind { get; init; }

    /// <summary>View format (may reinterpret a compatible texture format).</summary>
    public Format Format { get; init; }

    /// <summary>View dimensionality.</summary>
    public TextureDimension Dimension { get; init; }

    /// <summary>First mip level.</summary>
    public uint BaseMip { get; init; }

    /// <summary>Mip level count.</summary>
    public uint MipCount { get; init; }

    /// <summary>First array layer.</summary>
    public uint BaseArrayLayer { get; init; }

    /// <summary>Array layer count.</summary>
    public uint ArrayLayerCount { get; init; }
}

/// <summary>Value handle to a bound buffer range (handle + offset + size), so binding allocates nothing. The 64-bit
/// <see cref="Handle"/> is a backend-opaque id into the device's resource table.</summary>
public readonly struct GpuBufferView : IEquatable<GpuBufferView>
{
    /// <summary>Creates a raw (unstructured) view.</summary>
    /// <param name="handle">The buffer's backend handle.</param>
    /// <param name="offset">Byte offset into the buffer.</param>
    /// <param name="sizeBytes">Byte size of the range.</param>
    public GpuBufferView(ulong handle, ulong offset, ulong sizeBytes)
    {
        Handle = handle;
        Offset = offset;
        SizeBytes = sizeBytes;
        Stride = 0;
    }

    /// <summary>Creates a structured view.</summary>
    /// <param name="handle">The buffer's backend handle.</param>
    /// <param name="offset">Byte offset into the buffer.</param>
    /// <param name="sizeBytes">Byte size of the range.</param>
    /// <param name="stride">Element stride in bytes.</param>
    public GpuBufferView(ulong handle, ulong offset, ulong sizeBytes, uint stride)
    {
        Handle = handle;
        Offset = offset;
        SizeBytes = sizeBytes;
        Stride = stride;
    }

    /// <summary>The buffer's backend handle.</summary>
    public ulong Handle { get; }

    /// <summary>Byte offset into the buffer.</summary>
    public ulong Offset { get; }

    /// <summary>Byte size of the range.</summary>
    public ulong SizeBytes { get; }

    /// <summary>Element stride in bytes for a STRUCTURED view (HLSL <c>StructuredBuffer&lt;T&gt;</c>); 0 = raw
    /// (ByteAddressBuffer). D3D12 validates the view kind against the shader's declaration, so a structured shader
    /// resource must carry its stride; Vulkan binds both as storage buffers and ignores it.</summary>
    public uint Stride { get; }

    /// <summary>True when the view refers to a buffer.</summary>
    public bool IsValid => Handle != 0;

    /// <summary>Value equality.</summary>
    public static bool operator ==(GpuBufferView left, GpuBufferView right)
    {
        return left.Equals(right);
    }

    /// <summary>Value inequality.</summary>
    public static bool operator !=(GpuBufferView left, GpuBufferView right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public bool Equals(GpuBufferView other)
    {
        return Handle == other.Handle && Offset == other.Offset && SizeBytes == other.SizeBytes && Stride == other.Stride;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is GpuBufferView v && Equals(v);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(Handle, Offset, SizeBytes, Stride);
    }
}

/// <summary>A resource transition or UAV barrier the render graph synthesizes.</summary>
public readonly struct ResourceBarrierDesc
{
    /// <summary>The resource's backend handle.</summary>
    public ulong ResourceHandle { get; init; }

    /// <summary>State before the barrier.</summary>
    public ResourceState Before { get; init; }

    /// <summary>State after the barrier.</summary>
    public ResourceState After { get; init; }

    /// <summary>True for a UAV barrier (no state change; orders read-write access between dispatches).</summary>
    public bool IsUavBarrier { get; init; }

    /// <summary>Describes a state transition.</summary>
    /// <param name="handle">The resource's backend handle.</param>
    /// <param name="before">State before the barrier.</param>
    /// <param name="after">State after the barrier.</param>
    /// <returns>The barrier description.</returns>
    public static ResourceBarrierDesc Transition(ulong handle, ResourceState before, ResourceState after)
    {
        return new() { ResourceHandle = handle, Before = before, After = after, IsUavBarrier = false };
    }

    /// <summary>Describes a UAV barrier.</summary>
    /// <param name="handle">The resource's backend handle.</param>
    /// <returns>The barrier description.</returns>
    public static ResourceBarrierDesc Uav(ulong handle)
    {
        return new() { ResourceHandle = handle, IsUavBarrier = true };
    }
}

/// <summary>A wait on a fence timeline value before a submission begins.</summary>
public readonly struct FenceWait
{
    /// <summary>Creates a wait.</summary>
    /// <param name="fence">The fence.</param>
    /// <param name="value">The timeline value to wait for.</param>
    public FenceWait(IFence fence, ulong value)
    {
        Fence = fence;
        Value = value;
    }

    /// <summary>The fence whose timeline the queue waits on before it starts the submission.</summary>
    public IFence Fence { get; }

    /// <summary>The timeline value to wait for.</summary>
    public ulong Value { get; }
}

/// <summary>A signal of a fence timeline value when a submission completes.</summary>
public readonly struct FenceSignal
{
    /// <summary>Creates a signal.</summary>
    /// <param name="fence">The fence.</param>
    /// <param name="value">The timeline value to signal.</param>
    public FenceSignal(IFence fence, ulong value)
    {
        Fence = fence;
        Value = value;
    }

    /// <summary>The fence whose timeline the queue signals once the submission completes.</summary>
    public IFence Fence { get; }

    /// <summary>The timeline value to signal.</summary>
    public ulong Value { get; }
}

/// <summary>Sampler filtering.</summary>
public enum SamplerFilter : byte
{
    /// <summary>Nearest texel.</summary>
    Nearest = 0,

    /// <summary>Linear interpolation.</summary>
    Linear = 1,

    /// <summary>Anisotropic filtering.</summary>
    Anisotropic = 2,
}

/// <summary>Sampler addressing for coordinates outside [0, 1].</summary>
public enum SamplerAddressMode : byte
{
    /// <summary>Tile.</summary>
    Repeat = 0,

    /// <summary>Clamp to the edge texel.</summary>
    ClampToEdge = 1,

    /// <summary>Tile with mirroring.</summary>
    MirrorRepeat = 2,
}

/// <summary>Sampler creation parameters. The backend keys a small sampler cache on these.</summary>
public readonly struct SamplerDesc : IEquatable<SamplerDesc>
{
    /// <summary>Filtering.</summary>
    public SamplerFilter Filter { get; init; }

    /// <summary>Addressing.</summary>
    public SamplerAddressMode Address { get; init; }

    /// <summary>Maximum anisotropy, applied only when <see cref="Filter"/> is <see cref="SamplerFilter.Anisotropic"/>.</summary>
    public float MaxAnisotropy { get; init; }

    /// <summary>Comparison sampler (shadow-map PCF), comparing GreaterEqual under reversed-Z.</summary>
    public bool Comparison { get; init; }

    /// <summary>Nearest filtering, clamped addressing.</summary>
    public static SamplerDesc NearestClamp => new() { Filter = SamplerFilter.Nearest, Address = SamplerAddressMode.ClampToEdge };

    /// <summary>Linear filtering, repeating addressing.</summary>
    public static SamplerDesc LinearRepeat => new() { Filter = SamplerFilter.Linear, Address = SamplerAddressMode.Repeat };

    /// <summary>Linear filtering, clamped addressing.</summary>
    public static SamplerDesc LinearClamp => new() { Filter = SamplerFilter.Linear, Address = SamplerAddressMode.ClampToEdge };

    /// <summary>Linear comparison sampler, clamped addressing.</summary>
    public static SamplerDesc ShadowCompare => new() { Filter = SamplerFilter.Linear, Address = SamplerAddressMode.ClampToEdge, Comparison = true };

    /// <summary>Value equality.</summary>
    public static bool operator ==(SamplerDesc left, SamplerDesc right)
    {
        return left.Equals(right);
    }

    /// <summary>Value inequality.</summary>
    public static bool operator !=(SamplerDesc left, SamplerDesc right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public bool Equals(SamplerDesc other)
    {
        return Filter == other.Filter && Address == other.Address && MaxAnisotropy.Equals(other.MaxAnisotropy) && Comparison == other.Comparison;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is SamplerDesc s && Equals(s);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine((int)Filter, (int)Address, MaxAnisotropy, Comparison);
    }
}
