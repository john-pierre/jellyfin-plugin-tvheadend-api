// Tests for the streaming profile resolution engine.

using System.Collections.Generic;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.StreamingProfile;

/// <summary>
/// Tests for <see cref="StreamingProfileResolver"/>.
/// </summary>
public class StreamingProfileResolverTests
{
    private static StreamingProfileResolver CreateResolver(PluginConfiguration config)
    {
        var provider = new ConfigurationProvider(() => config);
        return new StreamingProfileResolver(
            NullLogger<StreamingProfileResolver>.Instance,
            provider);
    }

    // ── Null / missing config ──────────────────────────────────────────

    [Fact]
    public void Resolve_NullConfig_ReturnsSafeFallback()
    {
        var provider = new ConfigurationProvider(() => null);
        var resolver = new StreamingProfileResolver(
            NullLogger<StreamingProfileResolver>.Instance,
            provider);

        var result = resolver.Resolve(new StreamingProfileContext());

        Assert.Equal(ResolutionSource.SafeFallback, result.Source);
        Assert.Equal("pass", result.EffectiveTvHeadendProfile);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void Resolve_NullContext_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var resolver = CreateResolver(config);

        Assert.Throws<System.ArgumentNullException>(() => resolver.Resolve(null!));
    }

    // ── Legacy fallback ────────────────────────────────────────────────

    [Fact]
    public void Resolve_DefaultConfig_UsesLegacyStreamingProfile()
    {
        var config = new PluginConfiguration();
        // StreamingProfile defaults to "pass", StreamingProfileSettings has empty DefaultTvHeadendProfile
        var resolver = CreateResolver(config);

        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch1" });

        Assert.Equal(ResolutionSource.LegacyFallback, result.Source);
        Assert.Equal("pass", result.EffectiveTvHeadendProfile);
        Assert.False(result.UsedFallback);
    }

    // ── Global default ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_GlobalDefaultProfile_UsesGlobalDefault()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "jellyfin";

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch1" });

        Assert.Equal(ResolutionSource.GlobalDefault, result.Source);
        Assert.Equal("jellyfin", result.EffectiveTvHeadendProfile);
    }

    [Fact]
    public void Resolve_GlobalDefaultPassThrough_UsesPassThroughProfile()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultPlaybackMode = PlaybackMode.PassThrough;

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext());

        Assert.Equal(ResolutionSource.GlobalDefault, result.Source);
        Assert.Equal(PlaybackMode.PassThrough, result.EffectivePlaybackMode);
        Assert.Equal("pass", result.EffectiveTvHeadendProfile);
    }

    [Fact]
    public void Resolve_GlobalDefaultJellyfinTranscode_UsesJellyfinTranscodeProfile()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultPlaybackMode = PlaybackMode.JellyfinTranscode;

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext());

        Assert.Equal("matroska", result.EffectiveTvHeadendProfile);
        Assert.Equal(PlaybackMode.JellyfinTranscode, result.EffectivePlaybackMode);
    }

    // ── Channel override ───────────────────────────────────────────────

    [Fact]
    public void Resolve_ChannelOverride_TakesPrecedenceOverGlobalDefault()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "jellyfin";
        config.StreamingProfileSettings.ChannelOverrides.Add(new ChannelProfileOverride
        {
            ChannelId = "ch-problematic",
            PlaybackMode = PlaybackMode.TvHeadendTranscode,
            TvHeadendProfileName = "webtv-h264",
            Description = "Problematic MPEG2 channel"
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch-problematic" });

        Assert.Equal(ResolutionSource.ChannelOverride, result.Source);
        Assert.Equal("webtv-h264", result.EffectiveTvHeadendProfile);
        Assert.Equal(PlaybackMode.TvHeadendTranscode, result.EffectivePlaybackMode);
        Assert.Equal("Problematic MPEG2 channel", result.MatchedRuleName);
    }

    [Fact]
    public void Resolve_ChannelOverride_CaseInsensitiveMatch()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.ChannelOverrides.Add(new ChannelProfileOverride
        {
            ChannelId = "CH-ABC",
            TvHeadendProfileName = "special"
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch-abc" });

        Assert.Equal(ResolutionSource.ChannelOverride, result.Source);
        Assert.Equal("special", result.EffectiveTvHeadendProfile);
    }

    [Fact]
    public void Resolve_ChannelOverride_NoMatchFallsThrough()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default-prof";
        config.StreamingProfileSettings.ChannelOverrides.Add(new ChannelProfileOverride
        {
            ChannelId = "ch-other",
            TvHeadendProfileName = "special"
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch-different" });

        Assert.Equal(ResolutionSource.GlobalDefault, result.Source);
        Assert.Equal("default-prof", result.EffectiveTvHeadendProfile);
    }

    // ── Channel group override ─────────────────────────────────────────

    [Fact]
    public void Resolve_ChannelGroupOverride_MatchesGroup()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ChannelGroupOverrides.Add(new ChannelGroupProfileOverride
        {
            ChannelGroup = "Sports",
            PlaybackMode = PlaybackMode.PassThrough,
            TvHeadendProfileName = "pass",
            Description = "Sports channels"
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch1", ChannelGroup = "Sports" });

        Assert.Equal(ResolutionSource.ChannelGroupOverride, result.Source);
        Assert.Equal("pass", result.EffectiveTvHeadendProfile);
    }

    [Fact]
    public void Resolve_ChannelOverride_BeatsChannelGroupOverride()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.ChannelOverrides.Add(new ChannelProfileOverride
        {
            ChannelId = "ch1",
            TvHeadendProfileName = "channel-specific"
        });
        config.StreamingProfileSettings.ChannelGroupOverrides.Add(new ChannelGroupProfileOverride
        {
            ChannelGroup = "Sports",
            TvHeadendProfileName = "group-profile"
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch1", ChannelGroup = "Sports" });

        Assert.Equal(ResolutionSource.ChannelOverride, result.Source);
        Assert.Equal("channel-specific", result.EffectiveTvHeadendProfile);
    }

    // ── Client rules ───────────────────────────────────────────────────

    [Fact]
    public void Resolve_ClientRuleExactMatch_Wins()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "Swiftfin iOS",
            MatchType = StreamingProfileRuleMatchType.ClientNameExact,
            MatchValue = "Swiftfin",
            PlaybackMode = PlaybackMode.JellyfinTranscode,
            TvHeadendProfileName = "matroska",
            Priority = 10
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ClientName = "Swiftfin" });

        Assert.Equal(ResolutionSource.ClientRule, result.Source);
        Assert.Equal("matroska", result.EffectiveTvHeadendProfile);
        Assert.Equal("Swiftfin iOS", result.MatchedRuleName);
    }

    [Fact]
    public void Resolve_ClientRuleContainsMatch_Wins()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "Web clients",
            MatchType = StreamingProfileRuleMatchType.ClientNameContains,
            MatchValue = "Web",
            PlaybackMode = PlaybackMode.JellyfinTranscode,
            Priority = 20
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ClientName = "Jellyfin Web" });

        Assert.Equal(ResolutionSource.ClientRule, result.Source);
        Assert.Equal("Web clients", result.MatchedRuleName);
    }

    [Fact]
    public void Resolve_DeviceNameExactMatch_Wins()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "Shield TV",
            MatchType = StreamingProfileRuleMatchType.DeviceNameExact,
            MatchValue = "SHIELD Android TV",
            PlaybackMode = PlaybackMode.PassThrough,
            Priority = 5
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { DeviceName = "SHIELD Android TV" });

        Assert.Equal(ResolutionSource.ClientRule, result.Source);
        Assert.Equal("pass", result.EffectiveTvHeadendProfile);
    }

    [Fact]
    public void Resolve_DeviceNameContainsMatch_Wins()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "iPhone rule",
            MatchType = StreamingProfileRuleMatchType.DeviceNameContains,
            MatchValue = "iPhone",
            PlaybackMode = PlaybackMode.JellyfinTranscode,
            Priority = 5
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { DeviceName = "iPhone 15 Pro" });

        Assert.Equal(ResolutionSource.ClientRule, result.Source);
    }

    [Fact]
    public void Resolve_DisabledRule_IsSkipped()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = false,
            RuleName = "Disabled",
            MatchType = StreamingProfileRuleMatchType.ClientNameExact,
            MatchValue = "Swiftfin",
            TvHeadendProfileName = "disabled-profile",
            Priority = 1
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ClientName = "Swiftfin" });

        Assert.Equal(ResolutionSource.GlobalDefault, result.Source);
        Assert.Equal("default", result.EffectiveTvHeadendProfile);
    }

    [Fact]
    public void Resolve_PriorityOrdering_LowestPriorityWins()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "Low priority",
            MatchType = StreamingProfileRuleMatchType.ClientNameContains,
            MatchValue = "Jellyfin",
            TvHeadendProfileName = "low-prio-profile",
            Priority = 100
        });
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "High priority",
            MatchType = StreamingProfileRuleMatchType.ClientNameContains,
            MatchValue = "Jellyfin",
            TvHeadendProfileName = "high-prio-profile",
            Priority = 1
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ClientName = "Jellyfin Web" });

        Assert.Equal("high-prio-profile", result.EffectiveTvHeadendProfile);
        Assert.Equal("High priority", result.MatchedRuleName);
    }

    // ── User rules ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_UserRuleExactMatch_Wins()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.UserRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "Admin passthrough",
            MatchType = StreamingProfileRuleMatchType.UserIdExact,
            MatchValue = "admin-user-guid",
            PlaybackMode = PlaybackMode.PassThrough,
            Priority = 1
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { UserId = "admin-user-guid" });

        Assert.Equal(ResolutionSource.UserRule, result.Source);
        Assert.Equal("pass", result.EffectiveTvHeadendProfile);
        Assert.Equal("Admin passthrough", result.MatchedRuleName);
    }

    [Fact]
    public void Resolve_ClientRule_BeatsUserRule()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "Client rule",
            MatchType = StreamingProfileRuleMatchType.ClientNameExact,
            MatchValue = "Swiftfin",
            TvHeadendProfileName = "client-profile",
            Priority = 1
        });
        config.StreamingProfileSettings.UserRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "User rule",
            MatchType = StreamingProfileRuleMatchType.UserIdExact,
            MatchValue = "user-123",
            TvHeadendProfileName = "user-profile",
            Priority = 1
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext
        {
            ClientName = "Swiftfin",
            UserId = "user-123"
        });

        Assert.Equal(ResolutionSource.ClientRule, result.Source);
        Assert.Equal("client-profile", result.EffectiveTvHeadendProfile);
    }

    // ── Fallback behavior ──────────────────────────────────────────────

    [Fact]
    public void Resolve_EmptyProfileInOverride_UsesFallback()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.FallbackTvHeadendProfile = "fallback-prof";
        config.StreamingProfileSettings.ChannelOverrides.Add(new ChannelProfileOverride
        {
            ChannelId = "ch1",
            PlaybackMode = PlaybackMode.Auto,
            TvHeadendProfileName = string.Empty,
        });
        // Also clear the legacy and default profiles to trigger fallback
        config.StreamingProfile = string.Empty;
        config.StreamingProfileSettings.DefaultTvHeadendProfile = string.Empty;

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch1" });

        Assert.Equal(ResolutionSource.ChannelOverride, result.Source);
        Assert.Equal("fallback-prof", result.EffectiveTvHeadendProfile);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void Resolve_EmptyLegacyProfile_UsesSafeFallback()
    {
        var config = new PluginConfiguration();
        config.StreamingProfile = string.Empty;

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext());

        Assert.Equal(ResolutionSource.SafeFallback, result.Source);
        Assert.Equal("pass", result.EffectiveTvHeadendProfile);
        Assert.True(result.UsedFallback);
    }

    // ── Explainability ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_AlwaysProducesReasons()
    {
        var config = new PluginConfiguration();
        var resolver = CreateResolver(config);

        var result = resolver.Resolve(new StreamingProfileContext());

        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void Resolve_ChannelOverride_ReasonsContainChannelId()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.ChannelOverrides.Add(new ChannelProfileOverride
        {
            ChannelId = "ch-xyz",
            TvHeadendProfileName = "special"
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ChannelId = "ch-xyz" });

        Assert.Contains(result.Reasons, r => r.Contains("ch-xyz", System.StringComparison.Ordinal));
    }

    // ── Rule with empty MatchValue ─────────────────────────────────────

    [Fact]
    public void Resolve_RuleWithEmptyMatchValue_NeverMatches()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "Empty match",
            MatchType = StreamingProfileRuleMatchType.ClientNameExact,
            MatchValue = string.Empty,
            TvHeadendProfileName = "should-not-match",
            Priority = 1
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ClientName = "Anything" });

        Assert.Equal(ResolutionSource.GlobalDefault, result.Source);
    }

    // ── Context with null client/device ────────────────────────────────

    [Fact]
    public void Resolve_NullClientName_ClientRuleDoesNotMatch()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.DefaultTvHeadendProfile = "default";
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule
        {
            Enabled = true,
            RuleName = "Client rule",
            MatchType = StreamingProfileRuleMatchType.ClientNameExact,
            MatchValue = "Swiftfin",
            TvHeadendProfileName = "client-profile",
            Priority = 1
        });

        var resolver = CreateResolver(config);
        var result = resolver.Resolve(new StreamingProfileContext { ClientName = null });

        Assert.Equal(ResolutionSource.GlobalDefault, result.Source);
    }
}
