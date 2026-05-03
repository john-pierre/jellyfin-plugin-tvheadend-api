// Tests for RelayTokenOptions — effective config with safe lower bounds.

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

/// <summary>
/// Tests for <see cref="RelayTokenOptions"/>.
/// </summary>
public class RelayTokenOptionsTests
{
    private static RelayTokenOptions CreateOptions(PluginConfiguration? config = null)
    {
        config ??= new PluginConfiguration();
        var provider = new ConfigurationProvider(() => config);
        return new RelayTokenOptions(provider);
    }

    [Fact]
    public void Defaults_ReturnExpectedValues()
    {
        var opts = CreateOptions();

        Assert.True(opts.Enabled);
        Assert.Equal(120, opts.StreamTtlSeconds);
        Assert.Equal(5, opts.StreamMaxUses);
        Assert.Equal(0, opts.ImageTtlMinutes);
        Assert.Equal(0, opts.ImageMaxUses);
        Assert.True(opts.ImageTokenNeverExpires);
        Assert.True(opts.TokenReuse);
        Assert.True(opts.StrictScope);
        Assert.Equal(60, opts.CleanupIntervalMinutes);
        Assert.Equal(5, opts.ClockSkewSeconds);
    }

    [Fact]
    public void StreamTtlSeconds_EnforcesMinimum()
    {
        var config = new PluginConfiguration { StreamTokenTtlSeconds = 1 };
        var opts = CreateOptions(config);

        Assert.Equal(RelayTokenOptions.MinStreamTtlSeconds, opts.StreamTtlSeconds);
    }

    [Fact]
    public void ImageTtlMinutes_ZeroMeansNeverExpire()
    {
        var config = new PluginConfiguration { ImageTokenTtlMinutes = 0 };
        var opts = CreateOptions(config);

        Assert.Equal(0, opts.ImageTtlMinutes);
        Assert.True(opts.ImageTokenNeverExpires);
        Assert.Equal(TimeSpan.MaxValue, opts.ImageTtl);
    }

    [Fact]
    public void ImageTtlMinutes_NegativeMeansNeverExpire()
    {
        var config = new PluginConfiguration { ImageTokenTtlMinutes = -5 };
        var opts = CreateOptions(config);

        Assert.Equal(0, opts.ImageTtlMinutes);
        Assert.True(opts.ImageTokenNeverExpires);
    }

    [Fact]
    public void ImageTtlMinutes_SmallPositiveEnforcesMinimum()
    {
        // A value below MinImageTtlMinutes but > 0 should be clamped to the minimum.
        // MinImageTtlMinutes is 1, so any positive value >= 1 is valid.
        var config = new PluginConfiguration { ImageTokenTtlMinutes = 1 };
        var opts = CreateOptions(config);

        Assert.Equal(RelayTokenOptions.MinImageTtlMinutes, opts.ImageTtlMinutes);
        Assert.False(opts.ImageTokenNeverExpires);
    }

    [Fact]
    public void StreamMaxUses_AllowsZeroForUnlimited()
    {
        var config = new PluginConfiguration { StreamTokenMaxUses = 0 };
        var opts = CreateOptions(config);

        Assert.Equal(0, opts.StreamMaxUses);
    }

    [Fact]
    public void StreamMaxUses_RejectsNegativeAsZero()
    {
        var config = new PluginConfiguration { StreamTokenMaxUses = -5 };
        var opts = CreateOptions(config);

        Assert.Equal(0, opts.StreamMaxUses);
    }

    [Fact]
    public void CleanupIntervalMinutes_EnforcesMinimum()
    {
        var config = new PluginConfiguration { CleanupExpiredTokensIntervalMinutes = 0 };
        var opts = CreateOptions(config);

        Assert.Equal(RelayTokenOptions.MinCleanupIntervalMinutes, opts.CleanupIntervalMinutes);
    }

    [Fact]
    public void StreamTtl_ReturnsTimeSpanFromSeconds()
    {
        var config = new PluginConfiguration { StreamTokenTtlSeconds = 300 };
        var opts = CreateOptions(config);

        Assert.Equal(TimeSpan.FromSeconds(300), opts.StreamTtl);
    }

    [Fact]
    public void ImageTtl_ReturnsTimeSpanFromMinutes()
    {
        var config = new PluginConfiguration { ImageTokenTtlMinutes = 60 };
        var opts = CreateOptions(config);

        Assert.Equal(TimeSpan.FromMinutes(60), opts.ImageTtl);
    }

    [Fact]
    public void ConfigOverrides_AreApplied()
    {
        var config = new PluginConfiguration
        {
            EnableRelayTokenSecurity = false,
            StreamTokenTtlSeconds = 300,
            StreamTokenMaxUses = 10,
            ImageTokenTtlMinutes = 60,
            ImageTokenMaxUses = 100,
            EnableTokenReuse = false,
            StrictScopeValidation = false,
            CleanupExpiredTokensIntervalMinutes = 120,
            TokenValidationClockSkewSeconds = 10,
        };
        var opts = CreateOptions(config);

        Assert.False(opts.Enabled);
        Assert.Equal(300, opts.StreamTtlSeconds);
        Assert.Equal(10, opts.StreamMaxUses);
        Assert.Equal(60, opts.ImageTtlMinutes);
        Assert.Equal(100, opts.ImageMaxUses);
        Assert.False(opts.TokenReuse);
        Assert.False(opts.StrictScope);
        Assert.Equal(120, opts.CleanupIntervalMinutes);
        Assert.Equal(10, opts.ClockSkewSeconds);
    }

    [Fact]
    public void NullConfig_FallsBackToDefaults()
    {
        var provider = new ConfigurationProvider(() => null);
        var opts = new RelayTokenOptions(provider);

        Assert.True(opts.Enabled);
        Assert.Equal(120, opts.StreamTtlSeconds);
    }

    [Fact]
    public void Constructor_ThrowsOnNullProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayTokenOptions(null!));
    }
}
