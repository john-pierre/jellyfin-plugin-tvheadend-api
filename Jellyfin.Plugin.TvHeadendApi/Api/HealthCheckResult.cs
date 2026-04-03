using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// Health-check result model returned by the HealthCheck endpoint.
/// </summary>
public class HealthCheckResult
{
    /// <summary>Gets or sets the connection status string.</summary>
    public string Connection { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVHeadend server version.</summary>
    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of channels.</summary>
    public int ChannelCount { get; set; }

    /// <summary>Gets or sets the number of DVR entries.</summary>
    public int DvrEntryCount { get; set; }

    /// <summary>Gets the list of available streaming profiles.</summary>
    public IList<string> AvailableProfiles { get; } = new List<string>();

    /// <summary>Gets the current plugin settings summary lines.</summary>
    public IList<string> PluginSettings { get; } = new List<string>();

    /// <summary>Gets any warnings detected.</summary>
    public IList<string> Warnings { get; } = new List<string>();

    /// <summary>Gets configuration recommendations.</summary>
    public IList<string> Recommendations { get; } = new List<string>();
}
