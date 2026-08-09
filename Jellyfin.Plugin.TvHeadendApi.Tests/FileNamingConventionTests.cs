// Enforces the repository's file-per-type rule, which docs/guides/naming-conventions.md declares absolute.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// `docs/guides/naming-conventions.md` states: "Each code file should contain one primary type and
/// the file name MUST exactly match that type — Exception: None. This rule is absolute."
/// </summary>
/// <remarks>
/// The rule had drifted twice before this test existed: `Model/Statistic/TvhLogEntry.cs` declared
/// `TvheadendLogEntry`, and `Service/Health/HealthState.cs` declared four types and no `HealthState`.
/// Both made documentation and glossaries cite identifiers that did not exist. A stated-but-unchecked
/// convention decays; this keeps it honest.
/// </remarks>
public class FileNamingConventionTests
{
    private static readonly Regex TypeDeclaration = new(
        @"^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*(?:class|record|interface|enum|struct)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Files that legitimately declare no top-level public or internal type.
    /// </summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        "AssemblyInfo.cs",
    };

    [Fact]
    public void EveryPluginSourceFile_DeclaresATypeMatchingItsFileName()
    {
        var sourceRoot = FindPluginSourceRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = Path.GetFileName(file);
            if (Exempt.Contains(fileName))
            {
                continue;
            }

            var declared = TypeDeclaration.Matches(File.ReadAllText(file))
                .Select(m => m.Groups["name"].Value)
                .ToList();

            // A file with no top-level public/internal type declares nothing to match against.
            if (declared.Count == 0)
            {
                continue;
            }

            var expected = Path.GetFileNameWithoutExtension(fileName);

            // Partial types are split across files by design (e.g. DvrService.SingleTimer.cs), so the
            // file name only has to START with the type name for those.
            var partialStem = expected.Split('.')[0];

            if (!declared.Contains(expected, StringComparer.Ordinal)
                && !declared.Contains(partialStem, StringComparer.Ordinal))
            {
                offenders.Add($"{relative} declares [{string.Join(", ", declared)}] but no type named '{expected}'");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "docs/guides/naming-conventions.md requires the file name to match the primary type exactly "
            + "(\"Exception: None. This rule is absolute.\"). Violations:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root and returns the plugin source folder.
    /// </summary>
    /// <returns>Absolute path to the plugin project directory.</returns>
    private static string FindPluginSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Jellyfin.Plugin.TvHeadendApi");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Plugin.cs")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the Jellyfin.Plugin.TvHeadendApi source folder.");
    }
}
