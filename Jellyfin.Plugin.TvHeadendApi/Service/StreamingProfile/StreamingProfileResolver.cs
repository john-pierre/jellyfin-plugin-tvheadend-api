// Central streaming profile resolver — deterministic hierarchical resolution with explainability.

using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Resolves the effective TVHeadend streaming profile using a deterministic hierarchy:
/// channel override → channel group override → client rule → user rule → global default → legacy → safe fallback.
/// Every resolution step is recorded for diagnostics.
/// </summary>
internal sealed class StreamingProfileResolver : IStreamingProfileResolver
{
    private const string SafeFallbackProfile = "pass";

    private readonly ILogger<StreamingProfileResolver> _logger;
    private readonly ConfigurationProvider _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingProfileResolver"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    public StreamingProfileResolver(
        ILogger<StreamingProfileResolver> logger,
        ConfigurationProvider configProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <inheritdoc />
    public StreamingProfileResolutionResult Resolve(StreamingProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var config = _configProvider.Configuration;
        if (config == null)
        {
            return BuildSafeFallback("Plugin configuration is not available.");
        }

        var settings = config.StreamingProfileSettings;

        // 1. Channel override
        if (!string.IsNullOrWhiteSpace(context.ChannelId) && settings.ChannelOverrides.Count > 0)
        {
            var match = settings.ChannelOverrides.FirstOrDefault(
                o => string.Equals(o.ChannelId, context.ChannelId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                var result = BuildFromOverride(match.PlaybackMode, match.TvHeadendProfileName, settings, config);
                result.Source = ResolutionSource.ChannelOverride;
                result.MatchedRuleName = string.IsNullOrWhiteSpace(match.Description)
                    ? string.Format(CultureInfo.InvariantCulture, "Channel override for {0}", match.ChannelId)
                    : match.Description;
                result.Reasons.Insert(0, string.Format(
                    CultureInfo.InvariantCulture,
                    "Channel override matched: ChannelId={0}, Mode={1}, Profile='{2}'",
                    match.ChannelId,
                    match.PlaybackMode,
                    result.EffectiveTvHeadendProfile));
                LogResult(result, settings);
                return result;
            }
        }

        // 2. Channel group override
        if (!string.IsNullOrWhiteSpace(context.ChannelGroup) && settings.ChannelGroupOverrides.Count > 0)
        {
            var match = settings.ChannelGroupOverrides.FirstOrDefault(
                o => string.Equals(o.ChannelGroup, context.ChannelGroup, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                var result = BuildFromOverride(match.PlaybackMode, match.TvHeadendProfileName, settings, config);
                result.Source = ResolutionSource.ChannelGroupOverride;
                result.MatchedRuleName = string.IsNullOrWhiteSpace(match.Description)
                    ? string.Format(CultureInfo.InvariantCulture, "Channel group override for '{0}'", match.ChannelGroup)
                    : match.Description;
                result.Reasons.Insert(0, string.Format(
                    CultureInfo.InvariantCulture,
                    "Channel group override matched: Group='{0}', Mode={1}, Profile='{2}'",
                    match.ChannelGroup,
                    match.PlaybackMode,
                    result.EffectiveTvHeadendProfile));
                LogResult(result, settings);
                return result;
            }
        }

        // 3. Client/device rules (sorted by priority ascending)
        if (settings.ClientRules.Count > 0)
        {
            var matchedRule = settings.ClientRules
                .Where(r => r.Enabled)
                .OrderBy(r => r.Priority)
                .FirstOrDefault(r => MatchesRule(r, context));
            if (matchedRule != null)
            {
                var result = BuildFromOverride(matchedRule.PlaybackMode, matchedRule.TvHeadendProfileName, settings, config);
                result.Source = ResolutionSource.ClientRule;
                result.MatchedRuleName = matchedRule.RuleName;
                result.Reasons.Insert(0, string.Format(
                    CultureInfo.InvariantCulture,
                    "Client rule matched: '{0}' (Priority={1}, MatchType={2}, MatchValue='{3}'), Mode={4}, Profile='{5}'",
                    matchedRule.RuleName,
                    matchedRule.Priority,
                    matchedRule.MatchType,
                    matchedRule.MatchValue,
                    matchedRule.PlaybackMode,
                    result.EffectiveTvHeadendProfile));
                LogResult(result, settings);
                return result;
            }
        }

        // 4. User rules (sorted by priority ascending)
        if (settings.UserRules.Count > 0)
        {
            var matchedRule = settings.UserRules
                .Where(r => r.Enabled)
                .OrderBy(r => r.Priority)
                .FirstOrDefault(r => MatchesRule(r, context));
            if (matchedRule != null)
            {
                var result = BuildFromOverride(matchedRule.PlaybackMode, matchedRule.TvHeadendProfileName, settings, config);
                result.Source = ResolutionSource.UserRule;
                result.MatchedRuleName = matchedRule.RuleName;
                result.Reasons.Insert(0, string.Format(
                    CultureInfo.InvariantCulture,
                    "User rule matched: '{0}' (Priority={1}), Mode={2}, Profile='{3}'",
                    matchedRule.RuleName,
                    matchedRule.Priority,
                    matchedRule.PlaybackMode,
                    result.EffectiveTvHeadendProfile));
                LogResult(result, settings);
                return result;
            }
        }

        // 5. Global default
        if (!string.IsNullOrWhiteSpace(settings.DefaultTvHeadendProfile)
            || settings.DefaultPlaybackMode != PlaybackMode.Auto)
        {
            var result = BuildFromOverride(settings.DefaultPlaybackMode, settings.DefaultTvHeadendProfile, settings, config);
            result.Source = ResolutionSource.GlobalDefault;
            result.Reasons.Insert(0, string.Format(
                CultureInfo.InvariantCulture,
                "Global default: Mode={0}, Profile='{1}'",
                settings.DefaultPlaybackMode,
                result.EffectiveTvHeadendProfile));
            LogResult(result, settings);
            return result;
        }

        // 6. Legacy fallback — use PluginConfiguration.StreamingProfile
        if (!string.IsNullOrWhiteSpace(config.StreamingProfile))
        {
            var result = new StreamingProfileResolutionResult
            {
                EffectivePlaybackMode = PlaybackMode.Auto,
                EffectiveTvHeadendProfile = config.StreamingProfile,
                Source = ResolutionSource.LegacyFallback,
            };
            result.Reasons.Add(string.Format(
                CultureInfo.InvariantCulture,
                "No streaming profile settings configured. Using legacy StreamingProfile='{0}'.",
                config.StreamingProfile));
            LogResult(result, settings);
            return result;
        }

        // 7. Safe fallback
        return BuildSafeFallback("No profile configuration found anywhere. Using safe fallback.");
    }

    private static StreamingProfileResolutionResult BuildFromOverride(
        PlaybackMode mode,
        string? explicitProfile,
        StreamingProfileSettings settings,
        PluginConfiguration config)
    {
        var result = new StreamingProfileResolutionResult
        {
            EffectivePlaybackMode = mode,
        };

        // Determine the effective TVHeadend profile name based on the mode.
        if (!string.IsNullOrWhiteSpace(explicitProfile))
        {
            result.EffectiveTvHeadendProfile = explicitProfile;
            result.Reasons.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Explicit profile '{0}' from override.",
                explicitProfile));
        }
        else
        {
            result.EffectiveTvHeadendProfile = ResolveProfileForMode(mode, settings, config);
            result.Reasons.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Profile resolved from mode {0}: '{1}'.",
                mode,
                result.EffectiveTvHeadendProfile));
        }

        // Final safety net — if profile is still empty, use fallback.
        if (string.IsNullOrWhiteSpace(result.EffectiveTvHeadendProfile))
        {
            result.EffectiveTvHeadendProfile = !string.IsNullOrWhiteSpace(settings.FallbackTvHeadendProfile)
                ? settings.FallbackTvHeadendProfile
                : SafeFallbackProfile;
            result.UsedFallback = true;
            result.Reasons.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Profile was empty after resolution. Fell back to '{0}'.",
                result.EffectiveTvHeadendProfile));
        }

        return result;
    }

    private static string ResolveProfileForMode(
        PlaybackMode mode,
        StreamingProfileSettings settings,
        PluginConfiguration config)
    {
        return mode switch
        {
            PlaybackMode.PassThrough => !string.IsNullOrWhiteSpace(settings.PassThroughProfile)
                ? settings.PassThroughProfile
                : "pass",
            PlaybackMode.TvHeadendTranscode => !string.IsNullOrWhiteSpace(settings.DefaultTvHeadendProfile)
                ? settings.DefaultTvHeadendProfile
                : config.StreamingProfile,
            PlaybackMode.JellyfinTranscode => !string.IsNullOrWhiteSpace(settings.JellyfinTranscodeProfile)
                ? settings.JellyfinTranscodeProfile
                : "matroska",
            // Auto — use global default or legacy
            _ => !string.IsNullOrWhiteSpace(settings.DefaultTvHeadendProfile)
                ? settings.DefaultTvHeadendProfile
                : config.StreamingProfile,
        };
    }

    private static bool MatchesRule(StreamingProfileRule rule, StreamingProfileContext context)
    {
        if (string.IsNullOrWhiteSpace(rule.MatchValue))
        {
            return false;
        }

        return rule.MatchType switch
        {
            StreamingProfileRuleMatchType.ClientNameExact =>
                !string.IsNullOrWhiteSpace(context.ClientName)
                && string.Equals(context.ClientName, rule.MatchValue, StringComparison.OrdinalIgnoreCase),

            StreamingProfileRuleMatchType.ClientNameContains =>
                !string.IsNullOrWhiteSpace(context.ClientName)
                && context.ClientName.Contains(rule.MatchValue, StringComparison.OrdinalIgnoreCase),

            StreamingProfileRuleMatchType.DeviceNameExact =>
                !string.IsNullOrWhiteSpace(context.DeviceName)
                && string.Equals(context.DeviceName, rule.MatchValue, StringComparison.OrdinalIgnoreCase),

            StreamingProfileRuleMatchType.DeviceNameContains =>
                !string.IsNullOrWhiteSpace(context.DeviceName)
                && context.DeviceName.Contains(rule.MatchValue, StringComparison.OrdinalIgnoreCase),

            StreamingProfileRuleMatchType.UserIdExact =>
                MatchesUserId(context.UserId, rule.MatchValue),

            _ => false,
        };
    }

    /// <summary>
    /// Compares a resolved user id against a rule value, tolerating different GUID formats
    /// (dashed "D", compact "N", braced "B") so a rule still matches regardless of how the id was entered.
    /// </summary>
    private static bool MatchesUserId(string? contextUserId, string ruleValue)
    {
        if (string.IsNullOrWhiteSpace(contextUserId))
        {
            return false;
        }

        if (string.Equals(contextUserId, ruleValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Guid.TryParse(contextUserId, out var contextGuid)
            && Guid.TryParse(ruleValue, out var ruleGuid)
            && contextGuid == ruleGuid;
    }

    private static StreamingProfileResolutionResult BuildSafeFallback(string reason)
    {
        var result = new StreamingProfileResolutionResult
        {
            EffectivePlaybackMode = PlaybackMode.Auto,
            EffectiveTvHeadendProfile = SafeFallbackProfile,
            Source = ResolutionSource.SafeFallback,
            UsedFallback = true,
        };
        result.Reasons.Add(reason);
        return result;
    }

    private void LogResult(StreamingProfileResolutionResult result, StreamingProfileSettings settings)
    {
        if (!settings.EnableResolutionDiagnostics)
        {
            return;
        }

        _logger.LogInformation(
            "Streaming profile resolved: Source={Source}, Mode={Mode}, Profile='{Profile}', Rule='{Rule}', Fallback={Fallback}",
            result.Source,
            result.EffectivePlaybackMode,
            result.EffectiveTvHeadendProfile,
            result.MatchedRuleName ?? "(none)",
            result.UsedFallback);
    }
}
