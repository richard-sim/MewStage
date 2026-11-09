// Kit mode (--standard): MewUI on ITS OWN window host, with no SDL window, no app GPU device and no render graph. This
// is the configuration upstream's tooling is built for: the platform host (Win32 on Windows, X11 elsewhere) owns the
// OS window and the message loop, the MewVG OpenGL backend draws, and because the process runs JIT in Debug the
// DevTools (element inspector, visual tree, performance monitor, profiler) and Hot Reload (dotnet watch) all work.
// The Overlay and Workbench modes cannot host the two DevTools that open OS windows of their own; this mode exists so
// the same demo controls, styles and view models can be inspected and iterated on with the full tool set.
//
// The fonts are the same files the embedded host loads (DemoFonts), fed to MewUI's FontResources instead of our
// FontCollection: Roboto from the UI backend's embedded resource, Lora from assets/fonts.
using System;
using System.IO;

using Aprillz.MewUI;

using MewStage.Ui.Backend;

namespace MewStage.Demo.Kit;

/// <summary>The <c>--standard</c> mode: the kit page on upstream's platform host (see the file header).</summary>
internal static class KitMode
{
    /// <summary>Register the platform host and the MewVG backend for this OS. Repeatable within a process.</summary>
    public static void RegisterPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            Win32Platform.Register();
            MewVGWin32Backend.Register();
        }
        else
        {
            X11Platform.Register();
            MewVGX11Backend.Register();
        }
    }

    /// <summary>Register Roboto and the available Lora faces with MewUI's font resources (the platform host's font source).</summary>
    public static void RegisterFonts()
    {
        Add(UiGraphicsFactory.BuiltInDefaultFont());
        DemoFonts.Register(Add);

        static void Add(byte[] ttf)
        {
            using var stream = new MemoryStream(ttf, writable: false);
            FontResources.Register(stream, ".ttf");
        }
    }

    /// <summary>Run the kit page until its window closes.</summary>
    /// <returns>The process exit code (0).</returns>
    public static int Run()
    {
        RegisterPlatform();
        RegisterFonts();
        ThemeManager.Default = ThemeVariant.Dark;
        var page = KitPage.Build();
        Application.Run(page.Window);
        return 0;
    }
}
