// The inspector: a property grid over the view model for the selected outliner object. Three panels (cube, light,
// camera) are built once and shown by selection through IsVisible bindings; every editor binds two-way into the view
// model, which the mode pushes into the shared scene each frame, so an edit shows in both viewports.
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewStage.Demo.Workbench;

/// <summary>The property grid (see the file header).</summary>
internal sealed class Inspector
{
    /// <summary>Frame the cube in camera A.</summary>
    public static readonly Command FrameCameraACommand = new("demo.workbench.frame-a", "_Frame cube");

    /// <summary>Frame the cube in camera B.</summary>
    public static readonly Command FrameCameraBCommand = new("demo.workbench.frame-b", "_Frame cube");

    private Inspector(FrameworkElement root, Grid cubePanel, Grid lightPanel, Slider spinSlider, TextBox spinText, ComboBox colors, CheckBox wireframe)
    {
        Root = root;
        CubePanel = cubePanel;
        LightPanel = lightPanel;
        SpinSlider = spinSlider;
        SpinText = spinText;
        Colors = colors;
        Wireframe = wireframe;
    }

    /// <summary>The panel to dock.</summary>
    public FrameworkElement Root { get; }

    /// <summary>The cube's editors (visible while the cube is selected).</summary>
    public Grid CubePanel { get; }

    /// <summary>The light's editors (visible while the light is selected).</summary>
    public Grid LightPanel { get; }

    /// <summary>The spin-speed slider (bound two-way with <see cref="SpinText"/> through the view model).</summary>
    public Slider SpinSlider { get; }

    /// <summary>The spin-speed text box.</summary>
    public TextBox SpinText { get; }

    /// <summary>The color ComboBox.</summary>
    public ComboBox Colors { get; }

    /// <summary>The wireframe CheckBox.</summary>
    public CheckBox Wireframe { get; }

    /// <summary>Build the inspector over <paramref name="vm"/>.</summary>
    public static Inspector Build(WorkbenchViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        // Cube.
        Slider spinSlider = new Slider().Minimum(0).Maximum(3).BindValue(vm.SpinSpeed);
        TextBox spinText = new TextBox().Width(64).BindText(vm.SpinSpeed, WorkbenchViewModel.FormatNumber, text => WorkbenchViewModel.ParseNumber(text, vm.SpinSpeed.Value));
        StackPanel spinRow = new StackPanel().Horizontal().Spacing(8);
        spinRow.Add(spinSlider.Width(150).CenterVertical());
        spinRow.Add(spinText);
        ComboBox colors = new ComboBox().Items(CubeColors.Names).BindSelectedIndex(vm.ColorIndex).Width(150);
        Slider scale = new Slider().Minimum(0.25).Maximum(3).BindValue(vm.Scale).Width(222);
        CheckBox wireframe = new CheckBox().Content("Edges only").BindIsChecked(vm.Wireframe);
        CheckBox grid = new CheckBox().Content("Floor grid").BindIsChecked(vm.ShowGrid);
        Grid cube = Form(("Spin speed", spinRow), ("Color", colors), ("Scale", scale), ("Wireframe", wireframe), ("Grid", grid));
        cube.BindIsVisible(vm.SelectedIndex, i => i == 0);

        // Light.
        Slider yaw = new Slider().Minimum(-Math.PI).Maximum(Math.PI).BindValue(vm.LightYaw).Width(222);
        Slider pitch = new Slider().Minimum(0.05).Maximum(1.5).BindValue(vm.LightPitch).Width(222);
        Grid light = Form(("Yaw", yaw), ("Pitch", pitch));
        light.BindIsVisible(vm.SelectedIndex, i => i == 1);

        // Cameras.
        Grid cameraA = Form(("View", new TextBlock().BindText(vm.CameraAInfo).TextWrapping(TextWrapping.Wrap).Width(222)), ("", DemoButton.Create(DemoStyles.Ghost, "Frame cube", FrameCameraACommand)));
        cameraA.BindIsVisible(vm.SelectedIndex, i => i == 2);
        Grid cameraB = Form(("View", new TextBlock().BindText(vm.CameraBInfo).TextWrapping(TextWrapping.Wrap).Width(222)), ("", DemoButton.Create(DemoStyles.Ghost, "Frame cube", FrameCameraBCommand)));
        cameraB.BindIsVisible(vm.SelectedIndex, i => i == 3);

        StackPanel root = new StackPanel().Vertical().Spacing(8).Margin(10);
        TextBlock title = new TextBlock().FontWeight(FontWeight.SemiBold);
        title.BindText(vm.SelectedIndex, i => WorkbenchViewModel.Objects[Math.Clamp(i, 0, WorkbenchViewModel.Objects.Length - 1)].Name);
        root.Add(title);
        root.Add(cube);
        root.Add(light);
        root.Add(cameraA);
        root.Add(cameraB);
        return new Inspector(root, cube, light, spinSlider, spinText, colors, wireframe);
    }

    private static Grid Form(params (string Label, FrameworkElement Editor)[] rows)
    {
        Grid grid = new Grid().Columns("90,*").Spacing(8);
        var rowDefs = new GridLength[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            rowDefs[i] = GridLength.Auto;
        }

        grid.Rows(rowDefs);
        for (int i = 0; i < rows.Length; i++)
        {
            grid.Add(new TextBlock().Text(rows[i].Label).CenterVertical().Row(i).Column(0));
            grid.Add(rows[i].Editor.Row(i).Column(1).Left().CenterVertical());
        }

        return grid;
    }
}
