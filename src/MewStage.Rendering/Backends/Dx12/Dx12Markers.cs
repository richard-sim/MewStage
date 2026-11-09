// D3D12 debug events (PIX, RenderDoc, the debug layer). Vortice encodes the legacy PIX event blob, so no
// WinPixEventRuntime is needed; the color is carried by the tool's own convention and ignored here.

using D3D = Vortice.Direct3D12;

namespace MewStage.Rendering.Backends.Dx12;

internal static class Dx12Markers
{
    public static void Begin(D3D.ID3D12GraphicsCommandList list, uint color, string name)
    {
        _ = color;
        list.BeginEvent(name);
    }

    public static void Marker(D3D.ID3D12GraphicsCommandList list, uint color, string name)
    {
        _ = color;
        list.SetMarker(name);
    }

    public static void Begin(D3D.ID3D12CommandQueue queue, uint color, string name)
    {
        _ = color;
        queue.BeginEvent(name);
    }

    public static void Marker(D3D.ID3D12CommandQueue queue, uint color, string name)
    {
        _ = color;
        queue.SetMarker(name);
    }
}
