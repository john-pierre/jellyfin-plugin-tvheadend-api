// Guards against binding to EF Core APIs whose signatures change between the versions that
// different Jellyfin servers ship.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// The plugin is loaded into the Jellyfin process and resolves Entity Framework Core from the
/// SERVER, not from its own package references. Any EF Core API whose signature changed between
/// the versions in the supported matrix therefore compiles fine and then throws
/// <see cref="MissingMethodException"/> at runtime on the servers that ship the other version.
/// </summary>
/// <remarks>
/// This is not hypothetical: <c>RelayTokenRepository</c> called the EF Core 8 overload of
/// <c>ExecuteUpdateAsync</c>, which does not exist in the EF Core that Jellyfin 10.11+ loads.
/// Every relay token validation threw, and relay stream requests answered HTTP 500 — the default
/// delivery mode was broken on 10.11+ while every unit test stayed green, because the tests run
/// against the plugin's own EF Core package.
/// </remarks>
public class EfCoreApiCompatibilityTests
{
    /// <summary>
    /// EF Core bulk-operation entry points whose signatures are not stable across the EF Core
    /// versions shipped by the supported Jellyfin servers. Use
    /// <c>DbContext.Database.ExecuteSqlRawAsync</c> instead — that surface has been stable.
    /// </summary>
    private static readonly HashSet<string> UnstableEfCoreMembers = new(StringComparer.Ordinal)
    {
        "ExecuteUpdate",
        "ExecuteUpdateAsync",
        "ExecuteDelete",
        "ExecuteDeleteAsync",
    };

    [Fact]
    public void PluginAssembly_DoesNotReferenceVersionUnstableEfCoreApis()
    {
        var assemblyPath = typeof(Plugin).Assembly.Location;
        Assert.True(File.Exists(assemblyPath), $"Plugin assembly not found at '{assemblyPath}'.");

        var offenders = FindReferencedMemberNames(assemblyPath)
            .Where(UnstableEfCoreMembers.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The plugin assembly references EF Core APIs whose signatures differ between the EF Core "
            + "versions shipped by supported Jellyfin servers: "
            + string.Join(", ", offenders)
            + ". These compile cleanly and then throw MissingMethodException on servers carrying the "
            + "other EF Core version. Use DbContext.Database.ExecuteSqlRawAsync instead.");
    }

    /// <summary>
    /// Reads every member the assembly references from other assemblies, straight out of the
    /// metadata tables. Reflection cannot be used here: loading the referenced members is exactly
    /// what fails at runtime.
    /// </summary>
    /// <param name="assemblyPath">Path to the compiled plugin assembly.</param>
    /// <returns>The referenced member names.</returns>
    private static IEnumerable<string> FindReferencedMemberNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        var names = new List<string>();
        foreach (var handle in metadata.MemberReferences)
        {
            var memberReference = metadata.GetMemberReference(handle);
            names.Add(metadata.GetString(memberReference.Name));
        }

        return names;
    }
}
