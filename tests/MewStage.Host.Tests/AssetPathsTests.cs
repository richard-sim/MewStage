using System;
using System.IO;

using Xunit;

namespace MewStage.Host.Tests;

/// <summary>
/// The two asset layouts: a package reads <c>&lt;exe dir&gt;/assets</c> and nothing else; a development tree is found by
/// walking up to the first <c>assets/</c> holding the marker. A folder without the marker is never a root, so a stale
/// <c>Assets</c> beside an old build cannot be mistaken for a package on a case-insensitive file system.
/// </summary>
public sealed class AssetPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mewstage-assetpaths-" + Guid.NewGuid().ToString("N"));

    public AssetPathsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a file handle may linger on Windows; the temp folder is harmless
        }
    }

    private string MakeRoot(params string[] under)
    {
        string dir = Path.Combine(_root, Path.Combine(under));
        Directory.CreateDirectory(Path.Combine(dir, AssetPaths.FolderName));
        File.WriteAllText(Path.Combine(dir, AssetPaths.FolderName, AssetPaths.Marker), "asset root\n");
        return dir;
    }

    [Fact]
    public void Packaged_IsTheExeDirsAssets_AndNothingElse()
    {
        string exeDir = MakeRoot("publish");
        MakeRoot();   // a root above it too: the package must win and the walk must not run
        AssetPaths.Location loc = AssetPaths.Locate(exeDir, _root);
        Assert.True(loc.IsPackaged);
        Assert.Equal(Path.GetFullPath(Path.Combine(exeDir, "assets")), loc.Root);
    }

    [Fact]
    public void Development_WalksUpFromTheExeDir_ThenTheCurrentDirectory()
    {
        string repo = MakeRoot("repo");
        string bin = Path.Combine(repo, "tests", "X", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(bin);
        AssetPaths.Location loc = AssetPaths.Locate(bin, null);
        Assert.False(loc.IsPackaged);
        Assert.Equal(Path.GetFullPath(Path.Combine(repo, "assets")), loc.Root);

        // Nothing above the exe: the current directory's ancestors are tried next.
        string elsewhere = Path.Combine(_root, "elsewhere", "deep");
        Directory.CreateDirectory(elsewhere);
        string cwd = Path.Combine(repo, "scripts");
        Directory.CreateDirectory(cwd);
        loc = AssetPaths.Locate(elsewhere, cwd);
        Assert.Equal(Path.GetFullPath(Path.Combine(repo, "assets")), loc.Root);
    }

    [Fact]
    public void NoMarker_IsNoRoot()
    {
        string dir = Path.Combine(_root, "stale", "bin");
        Directory.CreateDirectory(Path.Combine(dir, "assets"));          // an assets folder with no marker
        Directory.CreateDirectory(Path.Combine(_root, "stale", "Assets"));
        AssetPaths.Location loc = AssetPaths.Locate(dir, dir);
        Assert.Null(loc.Root);
        Assert.False(loc.IsPackaged);
    }

    [Fact]
    public void ThisTestRun_FindsTheRepositoryAssets_InDevelopmentLayout()
    {
        // The test assembly runs from tests/.../bin/...; the walk lands on the repository's assets/.
        Assert.False(AssetPaths.IsPackaged);
        Assert.NotNull(AssetPaths.Root);
        Assert.Equal(Path.GetFullPath(Path.Combine(RepoRoot.Find().FullName, "assets")), AssetPaths.Root);
        Assert.NotNull(AssetPaths.Find("shaders", "shaders.manifest.toml"));
        Assert.NotNull(AssetPaths.FindDirectory("shaders/cooked"));             // '/' spelling is accepted
        Assert.Null(AssetPaths.Find("no-such-folder", "nothing.bin"));
        Assert.Equal(Path.Combine(AssetPaths.Root, "shaders", "cooked"), AssetPaths.PathFor("shaders/cooked"));
    }

    [Fact]
    public void Require_NamesTheRootSearched()
    {
        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => AssetPaths.Require("fonts", "Missing.ttf"));
        Assert.Contains("Missing.ttf", ex.Message, StringComparison.Ordinal);
        Assert.Contains("development asset root", ex.Message, StringComparison.Ordinal);
        Assert.Contains(AssetPaths.Root!, ex.Message, StringComparison.Ordinal);
    }
}
