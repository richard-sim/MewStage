// UiMewVGContext: the Aprillz.MewVG.MewVGContext subclass over the MewStage GPU backend. It mirrors upstream's MewVGGL
// (MewVG.GL/MewVGGL.cs): it forwards the abstract image API onto NanoVgRenderer's ITexture-backed images and hands the
// renderer (an internal INVGRenderer) to the internal MewVGContext constructor, reachable through the vendored
// MewVG.Core's InternalsVisibleTo grant (third_party/MewVG/src/MewVG.Core/MewStage.InternalsVisibleTo.cs).

using Aprillz.MewVG;

using MewStage.Graphics;

namespace MewStage.Ui.Backend;

/// <summary>
/// A MewVG context over the MewStage render abstraction. Call <see cref="Begin"/> with the active recorder (after
/// BeginRendering, before EndFrame) so the deferred <c>Flush</c> records the frame's draws.
/// </summary>
public sealed class UiMewVGContext : MewVGContext
{
    private UiMewVGContext(NanoVgRenderer renderer)
        : base(renderer)
    {
        Renderer = renderer;
    }

    /// <summary>
    /// Create a context backed by the application's GPU device. <paramref name="uiVs"/>/<paramref name="uiPs"/> are the
    /// cooked ui.hlsl vertex/pixel shaders. <paramref name="depthStencilFormat"/> must have a stencil aspect (e.g.
    /// <see cref="Format.D24_UNorm_S8_UInt"/>); the render pass binds a matching attachment cleared to 0.
    /// <paramref name="flipClipY"/>: clip-space Y sign (ui.hlsl gFlipY) - false for Vulkan NDC (Y-down), true for
    /// DX12/Metal NDC (Y-up). <paramref name="framesInFlight"/>: the scheduler's frame depth (ring buffers).
    /// </summary>
    public static UiMewVGContext Create(
        IGraphicsDevice device,
        byte[] uiVs,
        byte[] uiPs,
        Format colorFormat,
        Format depthStencilFormat,
        bool flipClipY = false,
        int framesInFlight = 2)
    {
        return new(new NanoVgRenderer(device, uiVs, uiPs, colorFormat, depthStencilFormat, flipClipY, framesInFlight));
    }

    /// <summary>A context for the scratch surfaces (vector-image and bitmap caches): the same renderer with fresh
    /// buffers per flush, because several scratch renders can land in one frame (<see cref="NanoVgRenderer"/>).</summary>
    public static UiMewVGContext CreateForScratch(
        IGraphicsDevice device,
        byte[] uiVs,
        byte[] uiPs,
        Format colorFormat,
        Format depthStencilFormat,
        bool flipClipY,
        int framesInFlight)
    {
        return new(new NanoVgRenderer(device, uiVs, uiPs, colorFormat, depthStencilFormat, flipClipY, framesInFlight, transientBuffers: true));
    }

    /// <summary>Bind the recorder this frame's draws record onto (call after BeginRendering, before EndFrame).</summary>
    public void Begin(IGraphicsRecorder recorder)
    {
        Renderer.Begin(recorder);
    }

    #region Images

    /// <inheritdoc />
    public override int CreateImageRGBA(int width, int height, MewVGImageFlags imageFlags, ReadOnlySpan<byte> data)
    {
        return Renderer.CreateTexture(NVGtextureType.RGBA, width, height, imageFlags, data);
    }

    /// <inheritdoc />
    public override int CreateImageBGRA(int width, int height, MewVGImageFlags imageFlags, ReadOnlySpan<byte> data)
    {
        return Renderer.CreateTexture(NVGtextureType.BGRA, width, height, imageFlags, data);
    }

    /// <inheritdoc />
    public override int CreateImageAlpha(int width, int height, MewVGImageFlags imageFlags, ReadOnlySpan<byte> data)
    {
        return Renderer.CreateTexture(NVGtextureType.Alpha, width, height, imageFlags, data);
    }

    /// <inheritdoc />
    public override bool UpdateImage(int image, ReadOnlySpan<byte> data)
    {
        if (!Renderer.GetTextureSize(image, out int w, out int h))
        {
            return false;
        }

        return Renderer.UpdateTexture(image, 0, 0, w, h, data);
    }

    /// <inheritdoc />
    public override bool UpdateImageBGRA(int image, ReadOnlySpan<byte> data)
    {
        return UpdateImage(image, data);
    }

    /// <inheritdoc />
    public override bool ImageSize(int image, out int width, out int height)
    {
        return Renderer.GetTextureSize(image, out width, out height);
    }

    /// <inheritdoc />
    public override void DeleteImage(int image)
    {
        Renderer.DeleteTexture(image);
    }

    /// <summary><paramref name="textureId"/> is a <see cref="ExternalTextures"/> key (MewVG's GL-name slot).</summary>
    public override int CreateImageFromHandle(int textureId, int width, int height, MewVGImageFlags flags)
    {
        return Renderer.CreateExternalImage(textureId, width, height, flags);
    }

    /// <summary>Wrap a registered external texture (<see cref="GpuTextureSource"/>) without ownership.</summary>
    public override int CreateImageFromNativeHandle(nint nativeHandle, int width, int height, MewVGImageFlags flags)
    {
        return Renderer.CreateExternalImage(nativeHandle, width, height, flags);
    }

    /// <inheritdoc />
    public override int ImageHandle(int image)
    {
        return Renderer.GetImageHandle(image);
    }

    #endregion

    /// <summary>Upload a sub-region into an existing image (used by the FontStashSharp atlas bridge).</summary>
    public bool UpdateImageRegion(int image, int x, int y, int width, int height, ReadOnlySpan<byte> data)
    {
        return Renderer.UpdateTexture(image, x, y, width, height, data);
    }

    /// <summary>The renderer (tests inspect its counters).</summary>
    internal NanoVgRenderer Renderer { get; }

    /// <inheritdoc />
    protected override void DisposeBackend()
    {
        Renderer.Dispose();
    }
}
