// Provides effective relay token policy values with safe lower bounds from plugin configuration.

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Reads relay token policy values from <see cref="Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration"/>
/// and applies safe minimum bounds to prevent misconfiguration.
/// </summary>
public sealed class RelayTokenOptions
{
    /// <summary>Minimum allowed stream token TTL in seconds.</summary>
    internal const int MinStreamTtlSeconds = 10;

    /// <summary>Minimum allowed image token TTL in minutes.</summary>
    internal const int MinImageTtlMinutes = 1;

    /// <summary>Minimum allowed cleanup interval in minutes.</summary>
    internal const int MinCleanupIntervalMinutes = 1;

    /// <summary>Minimum allowed clock skew in seconds.</summary>
    internal const int MinClockSkewSeconds = 0;

    private readonly ConfigurationProvider _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenOptions"/> class.
    /// </summary>
    /// <param name="configProvider">Plugin configuration provider.</param>
    public RelayTokenOptions(ConfigurationProvider configProvider)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <summary>Gets a value indicating whether relay token security is enabled.</summary>
    public bool Enabled => _configProvider.Configuration?.EnableRelayTokenSecurity ?? true;

    /// <summary>Gets the effective stream token TTL in seconds (minimum 10).</summary>
    public int StreamTtlSeconds => Math.Max(MinStreamTtlSeconds, _configProvider.Configuration?.StreamTokenTtlSeconds ?? 120);

    /// <summary>Gets the effective stream token max uses (0 = unlimited).</summary>
    public int StreamMaxUses => Math.Max(0, _configProvider.Configuration?.StreamTokenMaxUses ?? 25);

    /// <summary>Gets the effective image token TTL in minutes (0 = never expire, minimum 1 if non-zero).</summary>
    public int ImageTtlMinutes
    {
        get
        {
            var configured = _configProvider.Configuration?.ImageTokenTtlMinutes ?? 0;
            if (configured <= 0)
            {
                return 0; // 0 = never expire
            }

            return Math.Max(MinImageTtlMinutes, configured);
        }
    }

    /// <summary>Gets a value indicating whether image tokens are configured to never expire (TTL = 0).</summary>
    public bool ImageTokenNeverExpires => ImageTtlMinutes == 0;

    /// <summary>Gets the effective image token max uses (0 = unlimited).</summary>
    public int ImageMaxUses => Math.Max(0, _configProvider.Configuration?.ImageTokenMaxUses ?? 0);

    /// <summary>Gets a value indicating whether token reuse is enabled.</summary>
    public bool TokenReuse => _configProvider.Configuration?.EnableTokenReuse ?? true;

    /// <summary>Gets a value indicating whether image tokens are reused per user to reduce DB growth.</summary>
    public bool ImageTokenReusePerUser => _configProvider.Configuration?.ImageTokenReusePerUser ?? true;

    /// <summary>Gets a value indicating whether strict scope validation is enabled.</summary>
    public bool StrictScope => _configProvider.Configuration?.StrictScopeValidation ?? true;

    /// <summary>Gets the effective cleanup interval in minutes (minimum 1).</summary>
    public int CleanupIntervalMinutes => Math.Max(MinCleanupIntervalMinutes, _configProvider.Configuration?.CleanupExpiredTokensIntervalMinutes ?? 60);

    /// <summary>Gets the effective clock skew tolerance in seconds (minimum 0).</summary>
    public int ClockSkewSeconds => Math.Max(MinClockSkewSeconds, _configProvider.Configuration?.TokenValidationClockSkewSeconds ?? 5);

    /// <summary>Gets the effective stream token TTL as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan StreamTtl => TimeSpan.FromSeconds(StreamTtlSeconds);

    /// <summary>Gets the effective image token TTL as a <see cref="TimeSpan"/>. <see cref="TimeSpan.MaxValue"/> when never-expire.</summary>
    public TimeSpan ImageTtl => ImageTokenNeverExpires ? TimeSpan.MaxValue : TimeSpan.FromMinutes(ImageTtlMinutes);

    /// <summary>Gets the effective cleanup interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(CleanupIntervalMinutes);

    /// <summary>Gets the effective clock skew as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ClockSkew => TimeSpan.FromSeconds(ClockSkewSeconds);
}
