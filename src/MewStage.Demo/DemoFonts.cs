// The demo's typefaces, registered in ONE place for both hosts: the embedded MewUI host (Overlay / Workbench, through
// FontCollection.Add) and upstream's platform host (Kit mode, through FontResources). Roboto is the body face and
// ships embedded in MewStage.Ui.Backend, so it is always there; Lora (four styles, SIL OFL 1.1, assets/fonts) is the
// display face the start page and HUD use. A missing Lora file is tolerated: the text falls back to Roboto.
using System;
using System.IO;

using MewStage.Host;
using MewStage.Ui.Backend;

namespace MewStage.Demo;

/// <summary>Font registration shared by every mode (see the file header).</summary>
internal static class DemoFonts
{
    /// <summary>The body family (embedded in the UI backend).</summary>
    public const string Body = "Roboto";

    /// <summary>The display family (serif; <c>assets/fonts/Lora-*.ttf</c>).</summary>
    public const string Display = "Lora";

    private static readonly string[] LoraFiles =
    [
        "Lora-Regular.ttf",
        "Lora-Italic.ttf",
        "Lora-Bold.ttf",
        "Lora-BoldItalic.ttf",
    ];

    /// <summary>The Lora files under <c>assets/fonts</c> that exist, in registration order.</summary>
    public static string[] AvailableFiles()
    {
        var found = new System.Collections.Generic.List<string>(LoraFiles.Length);
        foreach (string name in LoraFiles)
        {
            if (AssetPaths.Find("fonts", name) is { } path)
            {
                found.Add(path);
            }
        }

        return [.. found];
    }

    /// <summary>Feed every available face's bytes to <paramref name="add"/>.</summary>
    public static void Register(Action<byte[]> add)
    {
        ArgumentNullException.ThrowIfNull(add);
        foreach (string path in AvailableFiles())
        {
            add(File.ReadAllBytes(path));
        }
    }

    /// <summary>Register the faces with an embedded host's collection (the <c>registerFonts</c> callback of <see cref="AppHost"/>).</summary>
    public static void Register(FontCollection fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        Register(bytes => fonts.Add(bytes));
    }
}
