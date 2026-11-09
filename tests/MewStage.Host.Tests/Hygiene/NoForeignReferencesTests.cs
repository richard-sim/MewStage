// MewStage is a standalone repository: nothing in it may refer to the private code base parts of it were extracted from,
// to that project's issue ledgers, or to its working notes. This gate walks every text file the repository authors
// (everything except the vendored upstream content under third_party/MewUI and third_party/MewVG; the MewStage-authored
// files inside those trees ARE scanned) and fails on any forbidden token.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Xunit;

namespace MewStage.Host.Tests.Hygiene;

public sealed partial class NoForeignReferencesTests
{
    private static readonly string[] TextExtensions =
    [
        ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".md", ".toml", ".json", ".yml", ".yaml", ".py", ".sh",
        ".ps1", ".cmd", ".hlsl", ".hlsli", ".txt", ".patch", ".editorconfig", ".gitignore", ".gitattributes", ".svg", ".xml",
        ".manifest", ".marker",
    ];

    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", ".vs", ".idea", ".vendor-cache", "TestResults", "artifacts", "publish", ".sync", ".tools"];

    // MewStage-authored files inside the vendored trees (the vendor lock's preserve globs). Everything else under
    // third_party/MewUI and third_party/MewVG is upstream content and out of scope.
    private static readonly string[] AuthoredInsideVendoredTrees =
    [
        "third_party/MewUI/build/MewUI.Local.props",
        "third_party/MewUI/src/MewUI/MewStage.InternalsVisibleTo.cs",
        "third_party/MewUI/extensions/MewUI.Svg/MewUI.Svg.csproj",
        "third_party/MewUI/extensions/MewUI.Svg/Generated/README.md",
        "third_party/MewVG/build/MewVG.Local.props",
        "third_party/MewVG/src/MewVG.Core/MewStage.InternalsVisibleTo.cs",
    ];

    [GeneratedRegex(@"VSim|vsim|Raw R/C|Raw RC|RawRC|RawUI|(?<![A-Za-z0-9-])F-[0-9]{3}(?![0-9])|UI-BUG-|WI-E-|ED-D-|verification-sweep-tracker|docs/journal/")]
    private static partial Regex ForbiddenTokens();

    [Fact]
    public void Repository_HasNoForeignReferences()
    {
        DirectoryInfo root = RepoRoot.Find();
        List<string> hits = [];
        int scanned = 0;
        foreach (string file in Files(root))
        {
            scanned++;
            string text = File.ReadAllText(file, Encoding.UTF8);
            int line = 1;
            int lineStart = 0;
            foreach (Match m in ForbiddenTokens().Matches(text))
            {
                // Advance the line counter to the match.
                for (int i = lineStart; i < m.Index; i++)
                {
                    if (text[i] == '\n')
                    {
                        line++;
                    }
                }

                lineStart = m.Index;
                if (IsSelfReference(file, root))
                {
                    continue;
                }

                hits.Add(Path.GetRelativePath(root.FullName, file) + ":" + line + ": " + m.Value);
            }
        }

        Assert.True(scanned > 50, "the walk found only " + scanned + " text files; the root detection is wrong");
        Assert.True(hits.Count == 0, "foreign references found (" + hits.Count + "):\n  " + string.Join("\n  ", hits.Take(50)));
    }

    [Fact]
    public void Gate_CatchesEachForbiddenToken()
    {
        foreach (string token in new[] { "VSim.Core", "vsim/docs", "Raw R/C", "RawRC", "RawUI", "F-219", "UI-BUG-39", "WI-E-002", "ED-D-09" })
        {
            Assert.True(ForbiddenTokens().IsMatch(token), "not caught: " + token);
        }

        foreach (string allowed in new[] { "F-2026", "PAPER-MF-1987", "F-19", "OFF-123" })
        {
            Assert.False(ForbiddenTokens().IsMatch(allowed), "false positive: " + allowed);
        }
    }

    // This file names the tokens it forbids.
    private static bool IsSelfReference(string file, DirectoryInfo root)
    {
        return Path.GetRelativePath(root.FullName, file).Replace('\\', '/') == "tests/MewStage.Host.Tests/Hygiene/NoForeignReferencesTests.cs";
    }

    private static IEnumerable<string> Files(DirectoryInfo root)
    {
        foreach (string file in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
            string[] parts = rel.Split('/');
            if (parts.Any(p => SkippedFolders.Contains(p, StringComparer.Ordinal)))
            {
                continue;
            }

            bool vendored = rel.StartsWith("third_party/MewUI/", StringComparison.Ordinal) || rel.StartsWith("third_party/MewVG/", StringComparison.Ordinal);
            if (vendored && !AuthoredInsideVendoredTrees.Contains(rel, StringComparer.Ordinal))
            {
                continue;
            }

            string ext = Path.GetExtension(file);
            string name = Path.GetFileName(file);
            if (!TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) && !TextExtensions.Contains(name, StringComparer.OrdinalIgnoreCase) && name != "LICENSE" && name != "NOTICE")
            {
                continue;
            }

            yield return file;
        }
    }
}
