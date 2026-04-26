// Tests for the streaming profile controller endpoints.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

/// <summary>
/// Tests for <see cref="StreamingProfileController"/>.
/// </summary>
public class StreamingProfileControllerTests
{
    private static IStreamingProfileResolver CreateResolver(PluginConfiguration? config = null)
    {
        var cfg = config ?? new PluginConfiguration();
        return new StreamingProfileResolver(
            NullLogger<StreamingProfileResolver>.Instance,
            new ConfigurationProvider(() => cfg));
    }

    private static Mock<IGuideService> CreateGuide() => new Mock<IGuideService>();

    [Fact]
    public void Constructor_NullResolver_ThrowsArgumentNullException()
    {
        var discovery = new Mock<IProfileDiscoveryService>();
        Assert.Throws<ArgumentNullException>(() => new StreamingProfileController(null!, discovery.Object, CreateGuide().Object));
    }

    [Fact]
    public void Constructor_NullDiscovery_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new StreamingProfileController(CreateResolver(), null!, CreateGuide().Object));
    }

    [Fact]
    public void Constructor_NullGuide_ThrowsArgumentNullException()
    {
        var discovery = new Mock<IProfileDiscoveryService>();
        Assert.Throws<ArgumentNullException>(() => new StreamingProfileController(CreateResolver(), discovery.Object, null!));
    }

    [Fact]
    public void Resolve_WithDefaults_ReturnsOkWithResult()
    {
        var discovery = new Mock<IProfileDiscoveryService>();
        var sut = new StreamingProfileController(CreateResolver(), discovery.Object, CreateGuide().Object);

        var result = sut.Resolve();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resolution = Assert.IsType<StreamingProfileResolutionResult>(ok.Value);
        Assert.NotNull(resolution);
        Assert.NotEmpty(resolution.Reasons);
    }

    [Fact]
    public void Resolve_WithChannelOverride_MatchesOverride()
    {
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.ChannelOverrides.Add(new ChannelProfileOverride
        {
            ChannelId = "ch-test",
            TvHeadendProfileName = "override-profile",
            PlaybackMode = PlaybackMode.TvHeadendTranscode
        });

        var discovery = new Mock<IProfileDiscoveryService>();
        var sut = new StreamingProfileController(CreateResolver(config), discovery.Object, CreateGuide().Object);

        var result = sut.Resolve(channelId: "ch-test");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resolution = Assert.IsType<StreamingProfileResolutionResult>(ok.Value);
        Assert.Equal(ResolutionSource.ChannelOverride, resolution.Source);
        Assert.Equal("override-profile", resolution.EffectiveTvHeadendProfile);
    }

    [Fact]
    public async Task GetDiscoveredProfiles_ReturnsOkWithProfiles()
    {
        var discovery = new Mock<IProfileDiscoveryService>();
        discovery.Setup(x => x.GetAvailableProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredProfile>
            {
                new("uuid-1", "pass"),
                new("uuid-2", "matroska"),
            });

        var sut = new StreamingProfileController(CreateResolver(), discovery.Object, CreateGuide().Object);

        var result = await sut.GetDiscoveredProfiles(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var profiles = Assert.IsType<List<DiscoveredProfile>>(ok.Value);
        Assert.Equal(2, profiles.Count);
    }

    [Fact]
    public async Task ValidateProfiles_ReturnsOkWithWarnings()
    {
        var discovery = new Mock<IProfileDiscoveryService>();
        discovery.Setup(x => x.ValidateConfiguredProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Profile 'bogus' not found in TVHeadend." });

        var sut = new StreamingProfileController(CreateResolver(), discovery.Object, CreateGuide().Object);

        var result = await sut.ValidateProfiles(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var warnings = Assert.IsType<List<string>>(ok.Value);
        Assert.Single(warnings);
    }

    [Fact]
    public void RefreshCache_ReturnsOk()
    {
        var discovery = new Mock<IProfileDiscoveryService>();
        var sut = new StreamingProfileController(CreateResolver(), discovery.Object, CreateGuide().Object);

        var result = sut.RefreshCache();

        Assert.IsType<OkObjectResult>(result);
        discovery.Verify(x => x.InvalidateCache(), Times.Once);
    }

    [Fact]
    public async Task GetChannels_ReturnsOkWithSortedChannels()
    {
        var guide = new Mock<IGuideService>();
        guide.Setup(x => x.GetChannelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChannelInfo>
            {
                new ChannelInfo { Id = "uuid-2", Name = "ZDF" },
                new ChannelInfo { Id = "uuid-1", Name = "ARD" },
                new ChannelInfo { Id = "", Name = "Empty" },
            });

        var discovery = new Mock<IProfileDiscoveryService>();
        var sut = new StreamingProfileController(CreateResolver(), discovery.Object, guide.Object);

        var result = await sut.GetChannels(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var channels = Assert.IsType<List<ChannelOption>>(ok.Value);
        Assert.Equal(2, channels.Count);
        Assert.Equal("ARD", channels[0].Name);
        Assert.Equal("ZDF", channels[1].Name);
    }

    [Fact]
    public async Task GetChannelGroups_ReturnsOkWithSortedGroups()
    {
        var guide = new Mock<IGuideService>();
        guide.Setup(x => x.GetChannelTagsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                { "tag-2", "Sports" },
                { "tag-1", "News" },
                { "tag-3", "" },
            });

        var discovery = new Mock<IProfileDiscoveryService>();
        var sut = new StreamingProfileController(CreateResolver(), discovery.Object, guide.Object);

        var result = await sut.GetChannelGroups(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var groups = Assert.IsType<List<ChannelGroupOption>>(ok.Value);
        Assert.Equal(2, groups.Count);
        Assert.Equal("News", groups[0].Name);
        Assert.Equal("Sports", groups[1].Name);
    }
}
