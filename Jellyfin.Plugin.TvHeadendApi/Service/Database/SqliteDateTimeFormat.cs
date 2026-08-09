using System;
using System.Globalization;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Formats <see cref="DateTime"/> values exactly the way Microsoft.Data.Sqlite persists them,
/// so raw-SQL comparisons line up with the TEXT that EF Core wrote.
/// </summary>
/// <remarks>
/// SQLite has no date type: EF Core stores <see cref="DateTime"/> as TEXT using a
/// <c>yyyy-MM-dd HH:mm:ss.FFFFFFF</c> pattern (space separator, no offset suffix), and every
/// comparison against those columns is therefore an ordinary string comparison. Formatting a
/// cutoff with the round-trip specifier <c>"o"</c> produces <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>
/// instead — and because <c>' '</c> (0x20) sorts before <c>'T'</c> (0x54), every row sharing the
/// cutoff's calendar date compared as older than the cutoff no matter what time it carried.
/// That silently deleted still-valid relay tokens on every cleanup pass and shortened all other
/// retention windows by up to a day. Always build cutoffs through this type.
/// </remarks>
internal static class SqliteDateTimeFormat
{
    /// <summary>
    /// The storage pattern Microsoft.Data.Sqlite uses for <see cref="DateTime"/> columns.
    /// A fixed-width fractional part is used for cutoffs so ordering never depends on how many
    /// trailing zeros the stored value happened to keep.
    /// </summary>
    internal const string Pattern = "yyyy-MM-dd HH:mm:ss.fffffff";

    /// <summary>
    /// Renders a UTC timestamp in the SQLite storage format used by this plugin's tables.
    /// </summary>
    /// <param name="value">The UTC timestamp to render.</param>
    /// <returns>The timestamp as stored-comparable TEXT.</returns>
    internal static string ToSqliteText(DateTime value)
    {
        return value.ToString(Pattern, CultureInfo.InvariantCulture);
    }
}
