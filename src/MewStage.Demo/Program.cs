using System;

using Aprillz.MewUI;

namespace MewStage.Demo;

/// <summary>The demo executable's entry point. Modes are selected by command-line flag or from the start page.</summary>
internal static class Program
{
    /// <summary>Runs the demo.</summary>
    /// <param name="args">Command-line arguments: <c>--overlay</c>, <c>--workbench</c>, <c>--standard</c>,
    /// <c>--render-dx12</c>, <c>--render-vk</c>, <c>--warp</c>.</param>
    /// <returns>The process exit code.</returns>
    public static int Main(string[] args)
    {
        var options = DemoOptions.Parse(args);
        if (options.Unknown.Count > 0)
        {
            Console.Error.WriteLine("unknown argument(s): " + string.Join(' ', options.Unknown));
            Console.Error.WriteLine(DemoOptions.Usage);
            return 2;
        }

        // Kit mode runs on upstream's platform host, which owns the process's UI loop. The editor Preview runs Main in
        // full and hosts MewUI windows itself, so under it the kit page is the only sensible target: an SDL window
        // from the other modes would open on screen.
        if (options.Mode == DemoMode.Kit || Design.IsPreviewMode)
        {
            return Kit.KitMode.Run();
        }

        using var app = new DemoApp(options);
        app.Run();
        return app.ExitCode;
    }
}
