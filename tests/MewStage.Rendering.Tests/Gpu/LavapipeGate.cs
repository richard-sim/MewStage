using System;
using System.Globalization;
using System.IO;
using System.Threading;

using MewStage.Rendering.Backends.Vulkan;

using Xunit;

namespace MewStage.Rendering.Tests.Gpu;

/// <summary>
/// The gate every Vulkan test passes through, and the counter that proves the gate opened. A skipped test and a
/// passing test are the same color in a summary, so <see cref="LavapipeReport"/> writes "N lavapipe tests RAN" at
/// the end of the collection: a green run with 0 is a setup failure (no loader, no ICD, wrong variable), not a pass.
/// </summary>
public static class LavapipeGate
{
    /// <summary>The xUnit collection every lavapipe-gated test joins so the report fixture sees them all.</summary>
    public const string Collection = "lavapipe";

    private static int s_ran;

    /// <summary>How many gated tests have run so far in this process.</summary>
    public static int Ran => Volatile.Read(ref s_ran);

    /// <summary>MEWSTAGE_VK_SMOKE=1 is the opt-in; without it every Vulkan test skips.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("MEWSTAGE_VK_SMOKE") == "1";

    /// <summary>Skips unless the opt-in is set AND a Vulkan device can be created; otherwise counts the test as RAN.</summary>
    public static void Require()
    {
        Skip.IfNot(Enabled, "MEWSTAGE_VK_SMOKE != 1 (no Vulkan loader / ICD provided; see docs/testing.md)");
        Skip.IfNot(VulkanOffscreen.IsAvailable(out string detail), "Vulkan unavailable: " + detail);
        Interlocked.Increment(ref s_ran);
    }
}

/// <summary>Collection fixture: disposed after the last test of the collection, it reports the RAN count to the
/// console and, when <c>MEWSTAGE_VK_SMOKE_REPORT</c> names a file, appends it there for a script to read.</summary>
public sealed class LavapipeReport : IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"[lavapipe] {LavapipeGate.Ran} lavapipe tests RAN in MewStage.Rendering.Tests (MEWSTAGE_VK_SMOKE={(LavapipeGate.Enabled ? "1" : "unset")})");
        Console.WriteLine(line);
        string? report = Environment.GetEnvironmentVariable("MEWSTAGE_VK_SMOKE_REPORT");
        if (!string.IsNullOrEmpty(report))
        {
            File.AppendAllText(report, line + Environment.NewLine);
        }
    }
}

[CollectionDefinition(LavapipeGate.Collection)]
public sealed class LavapipeCollection : ICollectionFixture<LavapipeReport>
{
}
