// DX12 enum mapping — MewStage.Rendering. Maps the Core enums to DXGI/D3D12 (Vortice).
// DX12 is the Windows primary backend; compiled cross-platform, runtime Windows-only. No dynamic
// rendering in DX12 — the recorder maps BeginRendering→OMSetRenderTargets + ClearRenderTargetView/DSV.

using MewStage.Graphics;

using Vortice.Direct3D12;

using D3D = Vortice.Direct3D12;
using DXGI = Vortice.DXGI;

namespace MewStage.Rendering.Backends.Dx12;

internal static class Dx12Map
{
    public static DXGI.Format Format(Format f)
    {
        return f switch
        {
            Graphics.Format.R8_UNorm => DXGI.Format.R8_UNorm,
            Graphics.Format.R8_UInt => DXGI.Format.R8_UInt,
            Graphics.Format.R8G8_UNorm => DXGI.Format.R8G8_UNorm,
            Graphics.Format.R8G8B8A8_UNorm => DXGI.Format.R8G8B8A8_UNorm,
            Graphics.Format.R8G8B8A8_UNorm_sRGB => DXGI.Format.R8G8B8A8_UNorm_SRgb,
            Graphics.Format.B8G8R8A8_UNorm => DXGI.Format.B8G8R8A8_UNorm,
            Graphics.Format.B8G8R8A8_UNorm_sRGB => DXGI.Format.B8G8R8A8_UNorm_SRgb,
            Graphics.Format.R16_UInt => DXGI.Format.R16_UInt,
            Graphics.Format.R16_UNorm => DXGI.Format.R16_UNorm,
            Graphics.Format.R16_Float => DXGI.Format.R16_Float,
            Graphics.Format.R16G16_Float => DXGI.Format.R16G16_Float,
            Graphics.Format.R16G16B16A16_Float => DXGI.Format.R16G16B16A16_Float,
            Graphics.Format.R32_UInt => DXGI.Format.R32_UInt,
            Graphics.Format.R32_Float => DXGI.Format.R32_Float,
            Graphics.Format.R32G32_Float => DXGI.Format.R32G32_Float,
            Graphics.Format.R32G32B32_Float => DXGI.Format.R32G32B32_Float,
            Graphics.Format.R32G32B32A32_Float => DXGI.Format.R32G32B32A32_Float,
            Graphics.Format.R11G11B10_Float => DXGI.Format.R11G11B10_Float,
            Graphics.Format.R10G10B10A2_UNorm => DXGI.Format.R10G10B10A2_UNorm,
            Graphics.Format.D32_Float => DXGI.Format.D32_Float,
            Graphics.Format.D24_UNorm_S8_UInt => DXGI.Format.D24_UNorm_S8_UInt,
            Graphics.Format.D32_Float_S8_UInt => DXGI.Format.D32_Float_S8X24_UInt,
            Graphics.Format.BC5_UNorm => DXGI.Format.BC5_UNorm,
            Graphics.Format.BC7_UNorm => DXGI.Format.BC7_UNorm,
            Graphics.Format.BC7_UNorm_sRGB => DXGI.Format.BC7_UNorm_SRgb,
            _ => DXGI.Format.Unknown,
        };
    }

    public static bool IsDepth(Format f)
    {
        return f is Graphics.Format.D32_Float or Graphics.Format.D24_UNorm_S8_UInt or Graphics.Format.D32_Float_S8_UInt;
    }

    public static ResourceStates State(ResourceState s)
    {
        ResourceStates r = ResourceStates.Common;
        if (s == ResourceState.Common)
        {
            return ResourceStates.Common;
        }

        if ((s & ResourceState.VertexBuffer) != 0 || (s & ResourceState.ConstantBuffer) != 0)
        {
            r |= ResourceStates.VertexAndConstantBuffer;
        }

        if ((s & ResourceState.IndexBuffer) != 0)
        {
            r |= ResourceStates.IndexBuffer;
        }

        if ((s & ResourceState.IndirectArgument) != 0)
        {
            r |= ResourceStates.IndirectArgument;
        }

        if ((s & ResourceState.ShaderResource) != 0)
        {
            r |= ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
        }

        if ((s & ResourceState.UnorderedAccess) != 0)
        {
            r |= ResourceStates.UnorderedAccess;
        }

        if ((s & ResourceState.RenderTarget) != 0)
        {
            r |= ResourceStates.RenderTarget;
        }

        if ((s & ResourceState.DepthWrite) != 0)
        {
            r |= ResourceStates.DepthWrite;
        }

        if ((s & ResourceState.DepthRead) != 0)
        {
            r |= ResourceStates.DepthRead;
        }

        if ((s & ResourceState.CopySource) != 0)
        {
            r |= ResourceStates.CopySource;
        }

        if ((s & ResourceState.CopyDest) != 0)
        {
            r |= ResourceStates.CopyDest;
        }

        if ((s & ResourceState.Present) != 0)
        {
            r |= ResourceStates.Present;   // PRESENT == COMMON (0) on D3D12
        }

        if ((s & ResourceState.ResolveSource) != 0)
        {
            r |= ResourceStates.ResolveSource;
        }

        if ((s & ResourceState.ResolveDest) != 0)
        {
            r |= ResourceStates.ResolveDest;
        }

        return r;
    }

    public static ResourceFlags ResourceFlags(ResourceUsage u)
    {
        ResourceFlags f = D3D.ResourceFlags.None;
        if ((u & ResourceUsage.RenderTarget) != 0)
        {
            f |= D3D.ResourceFlags.AllowRenderTarget;
        }

        if ((u & ResourceUsage.DepthStencil) != 0)
        {
            f |= D3D.ResourceFlags.AllowDepthStencil;
        }

        if ((u & ResourceUsage.UnorderedAccess) != 0)
        {
            f |= D3D.ResourceFlags.AllowUnorderedAccess;
        }

        return f;
    }

    public static HeapType HeapType(MemoryClass m)
    {
        return m switch
        {
            MemoryClass.Upload => D3D.HeapType.Upload,
            MemoryClass.Readback => D3D.HeapType.Readback,
            MemoryClass.Shared => D3D.HeapType.Upload,   // UMA: CPU-write/GPU-read in place
            _ => D3D.HeapType.Default,
        };
    }

    public static SamplerDescription Sampler(in SamplerDesc d)
    {
        TextureAddressMode addr = d.Address switch { SamplerAddressMode.ClampToEdge => TextureAddressMode.Clamp, SamplerAddressMode.MirrorRepeat => TextureAddressMode.Mirror, _ => TextureAddressMode.Wrap };
        // A non-comparison sampler must use a non-comparison filter AND ComparisonFunc=Never: D3D12 ignores the
        // ComparisonFunc on a non-comparison filter and warns (CreateSamplerComparisonFuncIgnored) if it is set to
        // anything but Never. A comparison (PCF shadow) sampler uses a Comparison* filter + reversed-Z GreaterEqual.
        Filter filt = d.Comparison
            ? (d.Filter == SamplerFilter.Nearest ? Filter.ComparisonMinMagMipPoint
               : d.Filter == SamplerFilter.Anisotropic ? Filter.ComparisonAnisotropic
               : Filter.ComparisonMinMagMipLinear)
            : (d.Filter == SamplerFilter.Nearest ? Filter.MinMagMipPoint
               : d.Filter == SamplerFilter.Anisotropic ? Filter.Anisotropic
               : Filter.MinMagMipLinear);
        return new SamplerDescription { Filter = filt, AddressU = addr, AddressV = addr, AddressW = addr, MaxAnisotropy = (uint)System.Math.Max(1f, d.MaxAnisotropy), MinLOD = 0f, MaxLOD = float.MaxValue, ComparisonFunction = d.Comparison ? ComparisonFunction.GreaterEqual : ComparisonFunction.Never };
    }
}
