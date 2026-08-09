// Classifies SQLite database errors into categories for health reporting and recovery decisions.

using System;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Classifies SQLite errors into categories used by health tracking and recovery.
/// </summary>
internal static class DatabaseErrorClassifier
{
    /// <summary>
    /// Determines whether the exception indicates database corruption.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns><c>true</c> if the error indicates corruption; otherwise <c>false</c>.</returns>
    public static bool IsCorruption(Exception ex)
    {
        if (ex is SqliteException sqlEx)
        {
            // SQLITE_CORRUPT = 11, SQLITE_NOTADB = 26
            return sqlEx.SqliteErrorCode is 11 or 26;
        }

        return ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the exception indicates a locked or busy database.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns><c>true</c> if the database is locked/busy; otherwise <c>false</c>.</returns>
    public static bool IsLocked(Exception ex)
    {
        if (ex is SqliteException sqlEx)
        {
            // SQLITE_BUSY = 5, SQLITE_LOCKED = 6
            return sqlEx.SqliteErrorCode is 5 or 6;
        }

        return ex.Message.Contains("locked", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the exception indicates disk-full or I/O failure.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns><c>true</c> if the error is I/O-related; otherwise <c>false</c>.</returns>
    public static bool IsIoError(Exception ex)
    {
        if (ex is SqliteException sqlEx)
        {
            // SQLITE_IOERR = 10, SQLITE_FULL = 13, SQLITE_CANTOPEN = 14
            return sqlEx.SqliteErrorCode is 10 or 13 or 14;
        }

        return ex is System.IO.IOException;
    }

    /// <summary>
    /// Returns a short human-readable error type string for the given exception.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns>A short error type string.</returns>
    public static string Classify(Exception ex)
    {
        if (IsCorruption(ex))
        {
            return "Corruption";
        }

        if (IsLocked(ex))
        {
            return "Locked";
        }

        if (IsIoError(ex))
        {
            return "IoError";
        }

        if (ex is SqliteException)
        {
            return "SqliteError";
        }

        return "Unknown";
    }
}
