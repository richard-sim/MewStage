using System;
using System.IO;

namespace MewStage.Host.Tests;

/// <summary>Locates the repository root from the test assembly's folder: the first ancestor holding MewStage.sln.</summary>
internal static class RepoRoot
{
    public static DirectoryInfo Find()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MewStage.sln")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException("MewStage.sln not found above " + AppContext.BaseDirectory);
    }
}
