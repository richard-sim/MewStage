using System;
using System.Globalization;
using System.IO;
using System.Threading;

using MewStage.Rendering.Backends.Vulkan;

using Xunit;

namespace MewStage.Demo.Tests.Support;

/// <summary>
/// The gate every offscreen journey passes through, and the count of the journeys that actually RAN (a skipped test
/// and a passing test look the same in a summary; the collection fixture prints the count and the CI script checks it).
/// </summary>
public static class DemoLavapipeGate
{
    /// <summary>The xUnit collection every journey joins (serial: one SDL instance, one software GPU).</summary>
    public const string Collection = "demo-lavapipe";

    private static int s_ran;

    /// <summary>How many journeys passed the gate so far in this process.</summary>
    public static int Ran => Volatile.Read(ref s_ran);

    /// <summary>True when the environment opted in to the Vulkan smoke run.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("MEWSTAGE_VK_SMOKE") == "1";

    /// <summary>Where journeys dump PNGs (<c>MEWSTAGE_JOURNEY_OUT</c>), or null for no dumps.</summary>
    public static string? OutputDirectory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("MEWSTAGE_JOURNEY_OUT");
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
    }

    /// <summary>Skips the calling test unless a Vulkan device is available and the smoke run is enabled; counts it otherwise.</summary>
    public static void Require()
    {
        Skip.IfNot(Enabled, "MEWSTAGE_VK_SMOKE != 1 (no Vulkan loader / ICD provided; see docs/testing.md)");
        Skip.IfNot(VulkanOffscreen.IsAvailable(out string detail), "Vulkan unavailable: " + detail);
        Interlocked.Increment(ref s_ran);
    }
}

/// <summary>Prints (and appends to <c>MEWSTAGE_VK_SMOKE_REPORT</c>, when set) how many journeys ran.</summary>
public sealed class DemoLavapipeReport : IDisposable
{
    public void Dispose()
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"[lavapipe] {DemoLavapipeGate.Ran} lavapipe tests RAN in MewStage.Demo.Tests (MEWSTAGE_VK_SMOKE={(DemoLavapipeGate.Enabled ? "1" : "unset")})");
        Console.WriteLine(line);
        string? report = Environment.GetEnvironmentVariable("MEWSTAGE_VK_SMOKE_REPORT");
        if (!string.IsNullOrEmpty(report))
        {
            File.AppendAllText(report, line + Environment.NewLine);
        }
    }
}

/// <summary>The collection definition that carries the report fixture.</summary>
[CollectionDefinition(DemoLavapipeGate.Collection)]
public sealed class DemoLavapipeCollection : ICollectionFixture<DemoLavapipeReport>
{
}
