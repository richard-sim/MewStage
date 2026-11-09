// EmbeddedWindowBackend: the minimal IWindowBackend for an embedded (no OS window) MewUI Window. It gives the Window
// a stable non-zero Handle (Window.PerformLayout early-returns when Handle == 0), records the client size in DIPs and
// counts Invalidate() calls. With no ApplicationDispatcher installed, MewUI's RequestLayout / RequestRender fall back
// to backend Invalidate, so the count is the authoritative "the window wants a redraw" signal the host's OnRequest
// gate consults. Modal dialogs call SetEnabled(false) on the owner backend; HasModalDisable surfaces that for input
// gating. Close() routes to the host (EmbeddedUiHost.CloseWindow), which does what an OS backend's destroy does:
// Closing / Closed, out of the stack, graphics released, visual tree disposed. Everything else (chrome, drag, cursor,
// IME, position, present) is a no-op: there is no native window.

using Aprillz.MewUI;
using Aprillz.MewUI.Input;
using Aprillz.MewUI.Platform;

namespace MewStage.Ui.Backend;

internal sealed class EmbeddedWindowBackend : IWindowBackend
{
    // A fixed, non-zero pseudo-handle. Never an OS HWND; only used as a "backend present + Handle!=0" sentinel.
    private static nint s_next = 0x4D657721; // 'Mew!'

    private readonly EmbeddedUiHost? _host;
    private readonly Window? _window;

    /// <summary>A backend with no host: Close() is a no-op (tests of the backend alone).</summary>
    public EmbeddedWindowBackend()
    {
    }

    /// <summary>A backend for <paramref name="window"/> on <paramref name="host"/>: Close() closes it through the host.</summary>
    public EmbeddedWindowBackend(EmbeddedUiHost host, Window window)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>The pseudo-handle (non-zero so MewUI lays the window out; 0 once the window closed, like a destroyed HWND).</summary>
    public nint Handle { get; private set; } = s_next++;

    /// <summary>True once <see cref="EmbeddedUiHost.CloseWindow(Window)"/> closed the window.</summary>
    public bool IsClosed => Handle == 0;

    /// <summary>The host's last step of a close: the handle goes to 0 (MewUI's layout and input early-return on it).</summary>
    public void MarkClosed()
    {
        Handle = 0;
    }

    /// <summary>Invalidate() calls since the last <see cref="ResetInvalidationCount"/>: the window wants a redraw.</summary>
    public int InvalidationCount { get; private set; }

    /// <summary>A modal child called SetEnabled(false) on this window.</summary>
    public bool HasModalDisable { get; private set; }

    /// <summary>The client width MewUI was last told, in DIPs.</summary>
    public double ClientWidthDip { get; private set; } = 1;

    /// <summary>The client height MewUI was last told, in DIPs.</summary>
    public double ClientHeightDip { get; private set; } = 1;

    public void RaiseInvalidate()
    {
        InvalidationCount++;
    }

    public void ResetInvalidationCount()
    {
        InvalidationCount = 0;
    }

    public void SetClientSizePx(int wPx, int hPx, double dpiScale)
    {
        double s = dpiScale <= 0 ? 1.0 : dpiScale;
        ClientWidthDip = wPx / s;
        ClientHeightDip = hPx / s;
    }

    public void Invalidate(bool erase)
    {
        InvalidationCount++;
    }

    public void SetClientSize(double widthDip, double heightDip)
    {
        ClientWidthDip = widthDip;
        ClientHeightDip = heightDip;
    }
    public void SetEnabled(bool enabled) { HasModalDisable = !enabled; }

    // --- everything below is a no-op: there is no native window ---
    public void SetResizable(bool resizable) { }
    // MewUI main: Show() became CreateSurface()/PresentSurface(). The host renders every window into its own GPU
    // surface inside the `ui` pass (EmbeddedUiHost), so a window has nothing to present by itself.
    public void PresentSurface() { }
    public void Hide() { }
    public void Close()
    {
        if (_host is not null && _window is not null && !IsClosed)
        {
            _ = _host.CloseWindow(_window, this);
        }
    }
    public void SetTitle(string title) { }
    public void SetIcon(IconSource? icon) { }
    public Point GetPosition()
    {
        return default;
    }

    public void SetPosition(double leftDip, double topDip) { }
    public void SetPositionPx(int leftPx, int topPx) { }
    public void CaptureMouse() { }
    public void ReleaseMouseCapture() { }
    public Point ClientToScreen(Point clientPointDip)
    {
        return clientPointDip;
    }

    public Point ScreenToClient(Point screenPointPx)
    {
        return screenPointPx;
    }

    public void EnsureTheme(bool isDark) { }
    public void CenterOnOwner() { }
    public void Activate() { }
    public void SetOwner(nint ownerHandle) { }
    public void SetOpacity(double opacity) { }
    public void SetAllowsTransparency(bool allowsTransparency) { }
    public void SetCursor(CursorType cursorType) { }
    public void SetImeMode(ImeMode mode) { }
    public void CancelImeComposition() { }
    public void Dispose() { }
}
