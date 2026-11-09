using System;
using System.Globalization;
using System.IO;
using System.Threading;

using MewStage.Rendering.Backends.Vulkan;

using Xunit;

namespace MewStage.Ui.Tests.Lavapipe;

/// <summary>
/// The gate every lavapipe UI test passes through, and the count of the tests that actually RAN. A skipped test and
/// a passing test look the same in a summary, so the collection fixture prints the count at the end and the CI
/// script fails when it is zero.
/// </summary>
public static class UiLavapipeGate
{
    /// <summary>The xUnit collection every lavapipe UI test joins (serial: one software GPU).</summary>
    public const string Collection = "ui-lavapipe";

    private static int s_ran;

    /// <summary>How many tests passed the gate so far in this process.</summary>
    public static int Ran => Volatile.Read(ref s_ran);

    /// <summary>True when the environment opted in to the Vulkan smoke run.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("MEWSTAGE_VK_SMOKE") == "1";

    /// <summary>Skips the calling test unless a Vulkan device is available and the smoke run is enabled; counts it otherwise.</summary>
    public static void Require()
    {
        Skip.IfNot(Enabled, "MEWSTAGE_VK_SMOKE != 1 (no Vulkan loader / ICD provided; see docs/testing.md)");
        Skip.IfNot(VulkanOffscreen.IsAvailable(out string detail), "Vulkan unavailable: " + detail);
        Interlocked.Increment(ref s_ran);
    }
}

/// <summary>Prints (and appends to <c>MEWSTAGE_VK_SMOKE_REPORT</c>, when set) how many lavapipe UI tests ran.</summary>
public sealed class UiLavapipeReport : IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"[lavapipe] {UiLavapipeGate.Ran} lavapipe tests RAN in MewStage.Ui.Tests (MEWSTAGE_VK_SMOKE={(UiLavapipeGate.Enabled ? "1" : "unset")})");
        Console.WriteLine(line);
        string? report = Environment.GetEnvironmentVariable("MEWSTAGE_VK_SMOKE_REPORT");
        if (!string.IsNullOrEmpty(report))
        {
            File.AppendAllText(report, line + Environment.NewLine);
        }
    }
}

/// <summary>The collection definition that carries the report fixture.</summary>
[CollectionDefinition(UiLavapipeGate.Collection)]
public sealed class UiLavapipeCollection : ICollectionFixture<UiLavapipeReport>
{
}
