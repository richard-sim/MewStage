using System;
using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Demo.Overlay;
using MewStage.Demo.Tests.Support;

using Xunit;

namespace MewStage.Demo.Tests.Lavapipe;

/// <summary>The start page offscreen: it renders in DIP space on a cleared backbuffer, and its Overlay button switches modes.</summary>
[Collection(DemoLavapipeGate.Collection)]
public sealed class StartPageJourneys
{
    private static string S(FormattableString s)
    {
        return s.ToString(CultureInfo.InvariantCulture);
    }

    [SkippableFact]
    public void StartPage_Renders_AndTheOverlayButtonSwitchesModes()
    {
        DemoLavapipeGate.Require();
        using var j = new Journey(DemoMode.StartPage);
        j.Frames(3);
        StartPage page = Assert.IsType<StartPage>(j.App.Mode);
        Assert.NotNull(page.Window);
        Assert.False(j.App.Host.Ui.Host!.UseDesignSpace);
        Assert.True(j.App.Host.UiClearsBackground);

        // Cleared to the page background, text and buttons drawn.
        (int R, int G, int B) corner = j.Readback.Px(4, 4);
        int bright = j.Readback.Count(p => p.R > 200 && p.G > 200 && p.B > 200);
        Console.WriteLine(S($"[start] corner={corner} bright={bright}"));
        Assert.True(corner.R < 40 && corner.B < 50, S($"the page background is off: {corner}"));
        Assert.True(bright > 400, S($"the page text is missing ({bright} bright pixels)"));
        j.Readback.Dump("start-page");

        // Every mode button has a handler (a command-bound button with none would gray out by itself).
        Assert.True(Ui.Navigation.NavCommands.CanExecute(page.Window, StartPage.WorkbenchCommand));
        Assert.True(Ui.Navigation.NavCommands.CanExecute(page.Window, StartPage.OverlayCommand));
        Assert.True(Ui.Navigation.NavCommands.CanExecute(page.Window, StartPage.KitCommand));

        // Click Overlay: the mode switches at the next frame, the design space is on, the world is drawn.
        DemoButton overlay = FindButton(page.Window, StartPage.OverlayCommand);
        Window pageWindow = page.Window;
        j.Click(overlay);
        j.Frames(2);
        OverlayMode mode = Assert.IsType<OverlayMode>(j.App.Mode);
        Assert.Equal(0, (int)pageWindow.Handle);   // the page left by closing its window
        Assert.True(j.App.Host.Ui.Host.UseDesignSpace);
        Assert.False(j.App.Host.UiClearsBackground);
        Assert.True(mode.Scene!.FramesDrawn >= 2);

        // And back: the pause menu's Start page button returns to the picker in DIP space.
        mode.OpenPause();
        j.Frames(1);
        Window hudWindow = mode.Hud!.Window;
        Window pauseWindow = mode.Pause!.Window;
        j.Click(FindButton(pauseWindow, PauseMenu.ToStartCommand));
        j.Frames(2);
        StartPage again = Assert.IsType<StartPage>(j.App.Mode);
        Assert.False(j.App.Host.Ui.Host.UseDesignSpace);
        Assert.Null(mode.Scene);   // left: the renderer was disposed
        Assert.Equal(0, (int)hudWindow.Handle);     // and every window it showed was closed
        Assert.Equal(0, (int)pauseWindow.Handle);

        // And into the Workbench, whose window replaces the page in DIP space.
        j.Click(FindButton(again.Window!, StartPage.WorkbenchCommand));
        j.Frames(3);
        Assert.IsType<Workbench.WorkbenchMode>(j.App.Mode);
        Assert.True(j.App.Host.UiClearsBackground);
    }

    private static DemoButton FindButton(Element root, Command command)
    {
        return VisualTree.Find(root, e => e is DemoButton b && ReferenceEquals(b.Command, command)) as DemoButton
            ?? throw new InvalidOperationException("no DemoButton bound to " + command.Id);
    }
}
