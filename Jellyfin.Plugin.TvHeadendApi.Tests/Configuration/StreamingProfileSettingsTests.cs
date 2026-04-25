// Tests for the streaming profile selection configuration model.

using System.Text.Json;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Configuration;

/// <summary>
/// Tests for streaming profile selection configuration types.
/// </summary>
public class StreamingProfileSettingsTests
{
    [Fact]
    public void DefaultConstructor_SetsExpectedDefaults()
    {
        var settings = new StreamingProfileSettings();

        Assert.Equal(PlaybackMode.Auto, settings.DefaultPlaybackMode);
        Assert.Equal(string.Empty, settings.DefaultTvHeadendProfile);
        Assert.Equal("pass", settings.PassThroughProfile);
        Assert.Equal("matroska", settings.JellyfinTranscodeProfile);
        Assert.Equal("pass", settings.FallbackTvHeadendProfile);
        Assert.Empty(settings.ChannelOverrides);
        Assert.Empty(settings.ChannelGroupOverrides);
        Assert.Empty(settings.ClientRules);
        Assert.Empty(settings.UserRules);
        Assert.False(settings.EnableResolutionDiagnostics);
    }

    [Fact]
    public void PluginConfiguration_StreamingProfileSettings_IsInitialized()
    {
        var config = new PluginConfiguration();

        Assert.NotNull(config.StreamingProfileSettings);
        Assert.Equal(PlaybackMode.Auto, config.StreamingProfileSettings.DefaultPlaybackMode);
    }

    [Fact]
    public void StreamingProfileRule_DefaultValues_AreCorrect()
    {
        var rule = new StreamingProfileRule();

        Assert.True(rule.Enabled);
        Assert.Equal(string.Empty, rule.RuleName);
        Assert.Equal(string.Empty, rule.Description);
        Assert.Equal(0, rule.Priority);
        Assert.Equal(StreamingProfileRuleMatchType.ClientNameExact, rule.MatchType);
        Assert.Equal(string.Empty, rule.MatchValue);
        Assert.Equal(PlaybackMode.Auto, rule.PlaybackMode);
        Assert.Equal(string.Empty, rule.TvHeadendProfileName);
    }

    [Fact]
    public void ChannelProfileOverride_DefaultValues_AreCorrect()
    {
        var over = new ChannelProfileOverride();

        Assert.Equal(string.Empty, over.ChannelId);
        Assert.Equal(PlaybackMode.Auto, over.PlaybackMode);
        Assert.Equal(string.Empty, over.TvHeadendProfileName);
        Assert.Equal(string.Empty, over.Description);
    }

    [Fact]
    public void ChannelGroupProfileOverride_DefaultValues_AreCorrect()
    {
        var over = new ChannelGroupProfileOverride();

        Assert.Equal(string.Empty, over.ChannelGroup);
        Assert.Equal(PlaybackMode.Auto, over.PlaybackMode);
        Assert.Equal(string.Empty, over.TvHeadendProfileName);
        Assert.Equal(string.Empty, over.Description);
    }

    [Fact]
    public void StreamingProfileSettings_SerializationRoundTrip_PreservesValues()
    {
        var settings = new StreamingProfileSettings
        {
            DefaultPlaybackMode = PlaybackMode.TvHeadendTranscode,
            DefaultTvHeadendProfile = "jellyfin",
            PassThroughProfile = "pass",
            JellyfinTranscodeProfile = "matroska",
            FallbackTvHeadendProfile = "pass",
            EnableResolutionDiagnostics = true,
            ChannelOverrides =
            {
                new ChannelProfileOverride
                {
                    ChannelId = "ch-123",
                    PlaybackMode = PlaybackMode.PassThrough,
                    TvHeadendProfileName = "pass",
                    Description = "Problematic channel"
                }
            },
            ClientRules =
            {
                new StreamingProfileRule
                {
                    Enabled = true,
                    RuleName = "iOS compatibility",
                    MatchType = StreamingProfileRuleMatchType.ClientNameContains,
                    MatchValue = "Swiftfin",
                    PlaybackMode = PlaybackMode.JellyfinTranscode,
                    Priority = 10
                }
            }
        };

        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<StreamingProfileSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(PlaybackMode.TvHeadendTranscode, deserialized.DefaultPlaybackMode);
        Assert.Equal("jellyfin", deserialized.DefaultTvHeadendProfile);
        Assert.True(deserialized.EnableResolutionDiagnostics);
        Assert.Single(deserialized.ChannelOverrides);
        Assert.Equal("ch-123", deserialized.ChannelOverrides[0].ChannelId);
        Assert.Single(deserialized.ClientRules);
        Assert.Equal("iOS compatibility", deserialized.ClientRules[0].RuleName);
        Assert.Equal(StreamingProfileRuleMatchType.ClientNameContains, deserialized.ClientRules[0].MatchType);
    }

    [Fact]
    public void PlaybackMode_HasExpectedValues()
    {
        Assert.Equal(0, (int)PlaybackMode.Auto);
        Assert.Equal(1, (int)PlaybackMode.PassThrough);
        Assert.Equal(2, (int)PlaybackMode.TvHeadendTranscode);
        Assert.Equal(3, (int)PlaybackMode.JellyfinTranscode);
    }

    [Fact]
    public void StreamingProfileRuleMatchType_HasExpectedValues()
    {
        Assert.Equal(0, (int)StreamingProfileRuleMatchType.ClientNameExact);
        Assert.Equal(1, (int)StreamingProfileRuleMatchType.ClientNameContains);
        Assert.Equal(2, (int)StreamingProfileRuleMatchType.DeviceNameExact);
        Assert.Equal(3, (int)StreamingProfileRuleMatchType.DeviceNameContains);
        Assert.Equal(4, (int)StreamingProfileRuleMatchType.UserIdExact);
    }

    [Fact]
    public void LegacyStreamingProfile_StillPresent()
    {
        var config = new PluginConfiguration();
        Assert.Equal("pass", config.StreamingProfile);
    }
}
