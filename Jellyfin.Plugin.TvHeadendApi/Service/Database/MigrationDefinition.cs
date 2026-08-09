using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Defines a single database migration with version, name, and SQL statements.
/// </summary>
internal sealed class MigrationDefinition
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationDefinition"/> class.
    /// </summary>
    /// <param name="version">Sequential version number.</param>
    /// <param name="name">Human-readable migration name.</param>
    /// <param name="statements">SQL statements to execute.</param>
    public MigrationDefinition(int version, string name, string[] statements)
    {
        Version = version;
        Name = name;
        Statements = statements;
    }

    /// <summary>Gets the sequential version number.</summary>
    public int Version { get; }

    /// <summary>Gets the human-readable migration name.</summary>
    public string Name { get; }

    /// <summary>Gets the SQL statements for this migration.</summary>
    public string[] Statements { get; }
}
