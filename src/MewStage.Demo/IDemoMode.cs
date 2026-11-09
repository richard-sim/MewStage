using MewStage.Rendering.Graph;

namespace MewStage.Demo;

/// <summary>
/// One screen of the embedded demo (the start page, Overlay, Workbench). The app runs exactly one at a time: it calls
/// <see cref="Enter"/> once (show windows, create GPU objects), <see cref="Tick"/> after each frame's input pump,
/// <see cref="Render"/> while the frame's render graph is open (add the world passes under the UI), and
/// <see cref="Leave"/> when another mode takes over or the app closes (the device is idle by then).
/// </summary>
internal interface IDemoMode
{
    /// <summary>Take the screen: set the UI design space, show windows, create what the mode draws with.</summary>
    void Enter(DemoApp app);

    /// <summary>Advance by <paramref name="dt"/> seconds (after the pump, before the UI dispatch).</summary>
    void Tick(double dt);

    /// <summary>Add this frame's passes under the UI into <paramref name="backbuffer"/>.</summary>
    void Render(RenderGraph graph, RgHandle backbuffer);

    /// <summary>Give the screen up: hide windows, release GPU objects (the device is idle).</summary>
    void Leave();
}
