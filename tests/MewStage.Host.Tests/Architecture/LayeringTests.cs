// The project layering (docs/architecture.md). Walks the ProjectReference closure of each project FILE: a reference
// copies its assembly into the output whether or not any code uses it, so the closure is what ships, not
// Assembly.GetReferencedAssemblies (which drops unused references).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Xunit;

namespace MewStage.Host.Tests.Architecture;

public sealed class LayeringTests
{
    // Every MewStage project may reference the MewStage projects listed (directly or transitively) and nothing else of ours.
    private static readonly Dictionary<string, string[]> AllowedMewStageReferences = new(StringComparer.Ordinal)
    {
        ["MewStage.Graphics"] = [],
        ["MewStage.Input"] = [],
        ["MewStage.Rendering"] = ["MewStage.Graphics"],
        ["MewStage.Ui.Navigation"] = [],
        ["MewStage.Ui.Backend"] = ["MewStage.Graphics", "MewStage.Rendering", "MewStage.Input", "MewStage.Ui.Navigation"],
        ["MewStage.Scene"] = ["MewStage.Graphics", "MewStage.Rendering"],
        ["MewStage.Viewport"] = ["MewStage.Graphics", "MewStage.Rendering", "MewStage.Input", "MewStage.Ui.Navigation", "MewStage.Ui.Backend"],
        ["MewStage.Host"] = ["MewStage.Graphics", "MewStage.Rendering", "MewStage.Input", "MewStage.Ui.Navigation", "MewStage.Ui.Backend"],
        ["MewStage.Demo"] = ["MewStage.Graphics", "MewStage.Rendering", "MewStage.Input", "MewStage.Ui.Navigation", "MewStage.Ui.Backend", "MewStage.Scene", "MewStage.Viewport", "MewStage.Host"],
    };

    // The vendored projects each MewStage project may reach (directly or transitively).
    private static readonly Dictionary<string, string[]> AllowedVendoredReferences = new(StringComparer.Ordinal)
    {
        ["MewStage.Graphics"] = [],
        ["MewStage.Input"] = [],
        ["MewStage.Rendering"] = [],
        ["MewStage.Ui.Navigation"] = ["MewUI"],
        ["MewStage.Ui.Backend"] = ["MewUI", "MewVG.Core"],
        ["MewStage.Scene"] = [],
        ["MewStage.Viewport"] = ["MewUI", "MewVG.Core"],
        ["MewStage.Host"] = ["MewUI", "MewVG.Core"],
        ["MewStage.Demo"] = ["MewUI", "MewVG.Core", "MewVG.GL", "MewUI.Platform.Win32", "MewUI.Platform.X11", "MewUI.Backend.MewVG.Win32", "MewUI.Backend.MewVG.X11", "MewUI.Preview", "MewUI.MewDock", "MewUI.Svg", "MewUI.Geometry", "MewUI.MewCharts"],
    };

    // Build-time Roslyn components (analyzers and source generators) upstream projects reference with
    // ReferenceOutputAssembly=false: they ship nothing and are not part of the layering.
    private static readonly string[] BuildTimeComponents = ["MewUI.Analyzers", "MewUI.Generators", "LiveChartsGenerators", "Svg.Generators"];

    // Package references each MewStage project may declare directly. Graphics and Input take none.
    private static readonly Dictionary<string, string[]> AllowedPackages = new(StringComparer.Ordinal)
    {
        ["MewStage.Graphics"] = [],
        ["MewStage.Input"] = [],
        ["MewStage.Rendering"] = ["Silk.NET.Vulkan", "Silk.NET.Vulkan.Extensions.KHR", "Silk.NET.Vulkan.Extensions.EXT", "Vortice.Direct3D12", "Vortice.DXGI"],
        ["MewStage.Ui.Navigation"] = [],
        ["MewStage.Ui.Backend"] = ["FontStashSharp"],
        ["MewStage.Scene"] = [],
        ["MewStage.Viewport"] = [],
        ["MewStage.Host"] = ["ppy.SDL3-CS"],
        ["MewStage.Demo"] = [],
    };

    public static TheoryData<string> Projects => [.. AllowedMewStageReferences.Keys];

    [Theory]
    [MemberData(nameof(Projects))]
    public void Project_ReachesOnlyItsTier(string project)
    {
        DirectoryInfo root = RepoRoot.Find();
        string path = ProjectPath(root, project);
        Assert.True(File.Exists(path), "project missing: " + path);
        List<string> problems = [];
        foreach ((string reached, string via) in Closure(path))
        {
            string name = Path.GetFileNameWithoutExtension(reached);
            if (BuildTimeComponents.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            bool ours = name.StartsWith("MewStage.", StringComparison.Ordinal);
            string[] allowed = ours ? AllowedMewStageReferences[project] : AllowedVendoredReferences[project];
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                problems.Add(project + " -> " + name + " (via " + via + ")");
            }
        }

        Assert.True(problems.Count == 0, "forbidden references:\n  " + string.Join("\n  ", problems));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Project_DeclaresOnlyItsPackages(string project)
    {
        DirectoryInfo root = RepoRoot.Find();
        string path = ProjectPath(root, project);
        string[] packages = XDocument.Load(path).Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .ToArray();
        string[] unexpected = packages.Except(AllowedPackages[project], StringComparer.Ordinal).ToArray();
        Assert.True(unexpected.Length == 0, project + " declares unexpected packages: " + string.Join(", ", unexpected));
    }

    [Fact]
    public void OnlyTheBackend_CompilesAgainstMewVgCoreDirectly()
    {
        DirectoryInfo root = RepoRoot.Find();
        foreach (string project in AllowedMewStageReferences.Keys)
        {
            string[] direct = DirectReferences(ProjectPath(root, project)).Select(Path.GetFileNameWithoutExtension).ToArray()!;
            bool referencesMewVg = direct.Contains("MewVG.Core", StringComparer.Ordinal);
            Assert.True(referencesMewVg == (project == "MewStage.Ui.Backend"),
                project + (referencesMewVg ? " references MewVG.Core directly; only MewStage.Ui.Backend may" : " must reference MewVG.Core directly"));
        }
    }

    [Fact]
    public void InternalsVisibleToGrants_NameOnlyTheBackendAndItsTests()
    {
        DirectoryInfo root = RepoRoot.Find();
        string[] files =
        [
            Path.Combine(root.FullName, "third_party", "MewUI", "src", "MewUI", "MewStage.InternalsVisibleTo.cs"),
            Path.Combine(root.FullName, "third_party", "MewVG", "src", "MewVG.Core", "MewStage.InternalsVisibleTo.cs"),
        ];
        string[] allowed = ["MewStage.Ui.Backend", "MewStage.Ui.Tests", "MewStage.Demo.Tests"];
        foreach (string file in files)
        {
            Assert.True(File.Exists(file), "grant file missing: " + file);
            foreach (string line in File.ReadAllLines(file))
            {
                int start = line.IndexOf("InternalsVisibleTo(\"", StringComparison.Ordinal);
                if (start < 0)
                {
                    continue;
                }

                start += "InternalsVisibleTo(\"".Length;
                string grantee = line[start..line.IndexOf('"', start)];
                Assert.Contains(grantee, allowed);
            }
        }
    }

    [Fact]
    public void Closure_DetectsAForbiddenReference()
    {
        // The gate must fail when a forbidden reference is added: a temporary project that reaches the Demo from Graphics.
        string dir = Path.Combine(Path.GetTempPath(), "mewstage-layering-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DirectoryInfo root = RepoRoot.Find();
            string demo = ProjectPath(root, "MewStage.Demo");
            string fake = Path.Combine(dir, "Fake.csproj");
            File.WriteAllText(fake, "<Project><ItemGroup><ProjectReference Include=\"" + demo + "\" /></ItemGroup></Project>");
            string[] reached = Closure(fake).Select(r => Path.GetFileNameWithoutExtension(r.Project)).ToArray();
            Assert.Contains("MewStage.Demo", reached);
            Assert.Contains("MewStage.Graphics", reached);
            Assert.Contains("MewUI", reached);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string ProjectPath(DirectoryInfo root, string project)
    {
        return Path.Combine(root.FullName, "src", project, project + ".csproj");
    }

    private static IEnumerable<string> DirectReferences(string project)
    {
        string dir = Path.GetDirectoryName(project)!;
        foreach (XElement item in XDocument.Load(project).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            string? include = (string?)item.Attribute("Include");
            if (!string.IsNullOrEmpty(include))
            {
                yield return Path.GetFullPath(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
            }
        }
    }

    /// <summary>Every project reachable from <paramref name="start"/> through ProjectReference items (including the
    /// imports a project pulls in, such as MewUI.Consumer.targets), with the project that referenced it.</summary>
    private static IEnumerable<(string Project, string Via)> Closure(string start)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { start };
        Queue<string> queue = new();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            string project = queue.Dequeue();
            foreach (string target in DirectReferences(project).Concat(ImportedReferences(project)))
            {
                if (!seen.Add(target))
                {
                    continue;
                }

                yield return (target, Path.GetFileNameWithoutExtension(project));
                if (File.Exists(target))
                {
                    queue.Enqueue(target);
                }
            }
        }
    }

    /// <summary>ProjectReference items declared by files a project imports with a literal relative path (the consumer
    /// targets add the binding generator). Property-based imports are not followed.</summary>
    private static IEnumerable<string> ImportedReferences(string project)
    {
        string dir = Path.GetDirectoryName(project)!;
        foreach (XElement import in XDocument.Load(project).Descendants().Where(e => e.Name.LocalName == "Import"))
        {
            string? include = (string?)import.Attribute("Project");
            if (string.IsNullOrEmpty(include))
            {
                continue;
            }

            include = include.Replace("$(MSBuildThisFileDirectory)", "", StringComparison.Ordinal);
            if (include.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            string imported = Path.GetFullPath(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
            if (!File.Exists(imported))
            {
                continue;
            }

            string importedDir = Path.GetDirectoryName(imported)!;
            foreach (XElement item in XDocument.Load(imported).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                string? reference = (string?)item.Attribute("Include");
                if (string.IsNullOrEmpty(reference))
                {
                    continue;
                }

                reference = reference.Replace("$(MSBuildThisFileDirectory)", "", StringComparison.Ordinal);
                yield return Path.GetFullPath(Path.Combine(importedDir, reference.Replace('\\', Path.DirectorySeparatorChar)));
            }
        }
    }
}
