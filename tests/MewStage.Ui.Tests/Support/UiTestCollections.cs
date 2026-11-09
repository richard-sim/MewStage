using System.Diagnostics;

using Aprillz.MewUI.Animation;

using Xunit;

namespace MewStage.Ui.Tests.Support;

/// <summary>
/// MewUI's animation manager is per thread but shared by every host on it: a clock another test started (a ComboBox
/// opening, a style transition) is render demand for EVERY host until it completes, so tests that assert an idle
/// OnRequest host does not render run in this serial collection and settle the clocks first.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdleHostCollection
{
    public const string Name = "mewui-idle-host";

    /// <summary>Runs every finite MewUI animation clock left by an earlier test to completion: one pulse starts the
    /// clocks still waiting for their first pulse, the next two an hour later end them (no UI animation is that long).</summary>
    public static void SettleAnimations()
    {
        long now = Stopwatch.GetTimestamp();
        AnimationManager.Instance.UpdateAt(now);
        AnimationManager.Instance.UpdateAt(now + (3600 * Stopwatch.Frequency));
        AnimationManager.Instance.UpdateAt(now + (3600 * Stopwatch.Frequency));
    }
}
