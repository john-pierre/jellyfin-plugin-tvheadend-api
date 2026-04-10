using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class LifecycleServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LifecycleService(null!));
    }

    [Fact]
    public async Task CloseLiveStreamAsync_WithEmptyId_Completes()
    {
        var sut = new LifecycleService(NullLogger<LifecycleService>.Instance);

        await sut.CloseLiveStreamAsync(string.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task CloseLiveStreamAsync_WithValidId_Completes()
    {
        var sut = new LifecycleService(NullLogger<LifecycleService>.Instance);

        await sut.CloseLiveStreamAsync("stream-1", CancellationToken.None);
    }

    [Fact]
    public async Task ResetTunerAsync_WithEmptyId_Completes()
    {
        var sut = new LifecycleService(NullLogger<LifecycleService>.Instance);

        await sut.ResetTunerAsync(string.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task ResetTunerAsync_WithValidId_Completes()
    {
        var sut = new LifecycleService(NullLogger<LifecycleService>.Instance);

        await sut.ResetTunerAsync("tuner-1", CancellationToken.None);
    }
}
