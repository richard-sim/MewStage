using System.Text.Json;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Demo.Tests.Support;
using MewStage.Demo.Workbench;
using MewStage.Input;
using MewStage.Scene;

using Xunit;

namespace MewStage.Demo.Tests.Headless;

/// <summary>The Workbench's pieces without a GPU: the default dock layout is valid FlexLayout JSON, the view model pushes every edit into the scene, and the inspector's slider and text box agree through the view model.</summary>
public sealed class WorkbenchTests
{
    [Fact]
    public void DefaultLayout_IsFlexLayoutJson_WithTheFourPanes()
    {
        using var doc = JsonDocument.Parse(WorkbenchMode.DefaultLayout);
        JsonElement layout = doc.RootElement.GetProperty("layout");
        Assert.Equal("row", layout.GetProperty("type").GetString());
        JsonElement children = layout.GetProperty("children");
        Assert.Equal(3, children.GetArrayLength());
        string json = WorkbenchMode.DefaultLayout;
        foreach (string component in new[] { "outliner", "viewport", "stats", "inspector" })
        {
            Assert.Contains("\"component\":\"" + component + "\"", json, System.StringComparison.Ordinal);
        }

        Assert.Contains("\"isDocument\":false", json, System.StringComparison.Ordinal);   // the tool tabs
        Assert.DoesNotContain("viewport2", json, System.StringComparison.Ordinal);       // off by default
    }

    [Fact]
    public void ViewModel_Apply_PushesEveryEditIntoTheScene()
    {
        var vm = new WorkbenchViewModel();
        var scene = new CubeScene();
        vm.SpinSpeed.Set(2.0);
        vm.Scale.Set(1.5);
        vm.ColorIndex.Set(2);
        vm.Wireframe.Set(true);
        vm.ShowGrid.Set(false);
        vm.LightYaw.Set(0.5);
        vm.LightPitch.Set(1.0);
        vm.Apply(scene);
        Assert.Equal(2.0f, scene.Settings.SpinSpeed);
        Assert.Equal(1.5f, scene.Settings.Scale);
        Assert.Equal(CubeColors.Tint(2), scene.Settings.Tint);
        Assert.True(scene.Settings.Wireframe);
        Assert.False(scene.Settings.ShowGrid);
        Assert.Equal(0.5f, scene.Settings.LightYaw);
        Assert.Equal(1.0f, scene.Settings.LightPitch);
        Assert.Equal(1.35f, scene.Center.Z, 4);   // the cube sits on the floor: center = 0.9 * scale
    }

    [Fact]
    public void Inspector_SliderAndTextBox_AgreeThroughTheViewModel()
    {
        var vm = new WorkbenchViewModel();
        var inspector = Inspector.Build(vm);
        using var screen = new HeadlessScreen(640, 480);
        Window window = new Window().Content(inspector.Root);
        screen.Show(window);
        screen.Frame();
        Assert.Equal("0.80", inspector.SpinText.Text);

        // Right on the focused slider steps and commits: the text follows.
        Assert.True(inspector.SpinSlider.Focus());
        var down = new RawInputSnapshot();
        down.KeyDown[(int)KeyCode.Right] = true;
        screen.Bridge.Dispatch(window, down);
        screen.Frame();
        screen.Bridge.Dispatch(window, new RawInputSnapshot());
        screen.Frame();
        Assert.True(vm.SpinSpeed.Value > 0.8);
        Assert.Equal(WorkbenchViewModel.FormatNumber(vm.SpinSpeed.Value), inspector.SpinText.Text);

        // The outliner selection shows one panel at a time.
        Assert.True(inspector.CubePanel.IsVisible);
        Assert.False(inspector.LightPanel.IsVisible);
        vm.SelectedIndex.Set(1);
        screen.Frame();
        Assert.False(inspector.CubePanel.IsVisible);
        Assert.True(inspector.LightPanel.IsVisible);

        // A bad number in the text box keeps the value.
        Assert.Equal(1.25, WorkbenchViewModel.ParseNumber("1.25", 0), 9);
        Assert.Equal(0.8, WorkbenchViewModel.ParseNumber("fast", 0.8), 9);
    }

    [Fact]
    public void Outliner_ListsTheFourObjects_AndSelectsTheCubeFirst()
    {
        var vm = new WorkbenchViewModel();
        ListBox list = Outliner.Build(vm);
        using var screen = new HeadlessScreen(400, 300);
        Window window = new Window { Padding = Thickness.Zero }.Content(list);
        screen.Show(window);
        screen.Frame();
        Assert.Equal(0, list.SelectedIndex);

        // A click on the third row (the user's gesture commits through the two-way binding; a programmatic set would not).
        Rect r = list.TranslateRect(new Rect(0, 0, list.Bounds.Width, list.Bounds.Height), window);
        var move = new RawInputSnapshot { MouseX = (float)(r.X + 40), MouseY = (float)(r.Y + 8 + (2 * 30) + 15) };
        screen.Bridge.Dispatch(window, move);
        screen.Frame();
        var press = new RawInputSnapshot { MouseX = move.MouseX, MouseY = move.MouseY };
        press.MouseButtonDown[(int)Input.MouseButton.Left] = true;
        screen.Bridge.Dispatch(window, press);
        screen.Frame();
        screen.Bridge.Dispatch(window, move);
        screen.Frame();
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal(SceneObjectKind.CameraA, vm.Selected);
    }
}
