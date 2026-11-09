using System;
using System.Collections.Generic;

using MewStage.Host;

namespace MewStage.Demo;

/// <summary>What the demo shows.</summary>
internal enum DemoMode
{
    /// <summary>The start page: pick a mode.</summary>
    StartPage,

    /// <summary>MewUI composited over a 3D world in one backbuffer (a game HUD and its menus).</summary>
    Overlay,

    /// <summary>A 3D viewport inside a MewUI tool window (an editor).</summary>
    Workbench,

    /// <summary>MewUI on its own platform host (Win32 / X11 + MewVG GL): the control kit page, DevTools, Preview.</summary>
    Kit,
}

/// <summary>The parsed command line: <c>MewStage.Demo [--overlay | --workbench | --standard] [--render-dx12 | --render-vk] [--warp]</c>.</summary>
internal sealed class DemoOptions
{
    /// <summary>The usage line printed for a bad argument.</summary>
    public const string Usage = "usage: MewStage.Demo [--overlay | --workbench | --standard] [--render-dx12 | --render-vk] [--warp]";

    /// <summary>The mode to start in (the start page when no mode flag was given).</summary>
    public DemoMode Mode { get; init; } = DemoMode.StartPage;

    /// <summary>The GPU API (DX12 on Windows, Vulkan elsewhere, flags override).</summary>
    public GpuBackend Backend { get; init; }

    /// <summary>DX12 only: use the WARP software adapter.</summary>
    public bool Warp { get; init; }

    /// <summary>Arguments nobody understood (the caller prints <see cref="Usage"/> and exits non-zero when non-empty).</summary>
    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>Parse <paramref name="args"/>; the last mode flag wins; the backend follows <see cref="GpuBackends.Select"/> (an explicit DX12 flag beats Vulkan).</summary>
    public static DemoOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        DemoMode mode = DemoMode.StartPage;
        bool warp = false;
        var unknown = new List<string>();
        foreach (string a in args)
        {
            switch (a)
            {
                case "--overlay":
                    mode = DemoMode.Overlay;
                    break;
                case "--workbench":
                    mode = DemoMode.Workbench;
                    break;
                case "--standard":
                    mode = DemoMode.Kit;
                    break;
                case "--warp":
                    warp = true;
                    break;
                case "--render-dx12":
                case "--render-vk":
                    break;
                default:
                    unknown.Add(a);
                    break;
            }
        }

        return new DemoOptions { Mode = mode, Backend = GpuBackends.Select(args), Warp = warp, Unknown = unknown };
    }

    /// <summary>The flags that reproduce this backend choice on a relaunch (Kit mode restarts the process).</summary>
    public string[] BackendArgs()
    {
        var list = new List<string>(2) { Backend == GpuBackend.Dx12 ? "--render-dx12" : "--render-vk" };
        if (Warp)
        {
            list.Add("--warp");
        }

        return [.. list];
    }
}
