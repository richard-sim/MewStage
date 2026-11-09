// The user settings folder: the only place MewStage writes outside its own tree (the Workbench dock layout). Default
// <ApplicationData>/MewStage (Windows: %APPDATA%\MewStage; Linux: ~/.config/MewStage), overridable through
// MEWSTAGE_SETTINGS_DIR or Directory for tests and portable installs. Files are small text (JSON written by MewDock).
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace MewStage.Host;

/// <summary>Reads and writes the small text files of the user settings folder (see the file header).</summary>
public static class UserSettings
{
    /// <summary>The environment variable that overrides the folder.</summary>
    public const string DirectoryVariable = "MEWSTAGE_SETTINGS_DIR";

    /// <summary>The settings folder (created on the first write). Assign to redirect (tests).</summary>
    public static string Directory
    {
        get => field ??= Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } env
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MewStage");
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The full path of <paramref name="fileName"/> in the folder.</summary>
    public static string PathFor(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        return Path.Combine(Directory, fileName);
    }

    /// <summary>The text of <paramref name="fileName"/>, or null when it does not exist or cannot be read.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A settings file that cannot be read (locked, corrupt encoding, permissions) means 'no saved settings'; the caller falls back to defaults and the next write replaces it.")]
    public static string? TryRead(string fileName)
    {
        string path = PathFor(fileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[settings] cannot read " + path + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="fileName"/>, creating the folder; false (and a line on stderr) when the write failed.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed settings write (read-only profile, full disk) must not take the application down; it is reported and the session continues without persistence.")]
    public static bool TryWrite(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string path = PathFor(fileName);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, text);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[settings] cannot write " + path + ": " + ex.Message);
            return false;
        }
    }
}
