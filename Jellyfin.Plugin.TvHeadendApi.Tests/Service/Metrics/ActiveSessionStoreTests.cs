// Unit tests for ActiveSessionStore thread-safe operations and queries.

using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Metrics;

/// <summary>
/// Tests for the active session store operations: add, get, remove, queries.
/// </summary>
public sealed class ActiveSessionStoreTests
{
    [Fact]
    public void Add_IncreasesCount()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1", ChannelId = "ch1" });
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Get_ReturnsCorrectSession()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1", ChannelId = "ch1", ClientName = "TestClient" });
        var session = store.Get("s1");
        Assert.NotNull(session);
        Assert.Equal("TestClient", session.ClientName);
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        var store = new ActiveSessionStore();
        Assert.Null(store.Get("nonexistent"));
    }

    [Fact]
    public void Remove_DecreasesCount()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1" });
        store.Add(new ActiveStreamSession { SessionId = "s2" });
        store.Remove("s1");
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Remove_ReturnsRemovedSession()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1", ChannelId = "ch1" });
        var removed = store.Remove("s1");
        Assert.NotNull(removed);
        Assert.Equal("ch1", removed.ChannelId);
    }

    [Fact]
    public void GetAll_ReturnsAllSessions()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1" });
        store.Add(new ActiveStreamSession { SessionId = "s2" });
        store.Add(new ActiveStreamSession { SessionId = "s3" });
        var all = store.GetAll();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void GetClientDistribution_GroupsByClientName()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1", ClientName = "Infuse" });
        store.Add(new ActiveStreamSession { SessionId = "s2", ClientName = "Infuse" });
        store.Add(new ActiveStreamSession { SessionId = "s3", ClientName = "VLC" });

        var dist = store.GetClientDistribution();
        Assert.Equal(2, dist["Infuse"]);
        Assert.Equal(1, dist["VLC"]);
    }

    [Fact]
    public void GetActiveChannels_ReturnsDistinctChannels()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1", ChannelName = "BBC One", ChannelId = "ch1" });
        store.Add(new ActiveStreamSession { SessionId = "s2", ChannelName = "BBC One", ChannelId = "ch1" });
        store.Add(new ActiveStreamSession { SessionId = "s3", ChannelName = "ITV", ChannelId = "ch2" });

        var channels = store.GetActiveChannels();
        Assert.Equal(2, channels.Count);
        Assert.Contains("BBC One", channels);
        Assert.Contains("ITV", channels);
    }

    [Fact]
    public void GetTotalBandwidth_SumsRollingBitrates()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1", RollingBitrate = 5_000_000 });
        store.Add(new ActiveStreamSession { SessionId = "s2", RollingBitrate = 3_000_000 });

        Assert.Equal(8_000_000, store.GetTotalBandwidth());
    }

    [Fact]
    public void CountByState_FiltersCorrectly()
    {
        var store = new ActiveSessionStore();
        store.Add(new ActiveStreamSession { SessionId = "s1", StreamState = StreamLifecycleState.Starting });
        store.Add(new ActiveStreamSession { SessionId = "s2", StreamState = StreamLifecycleState.Active });
        store.Add(new ActiveStreamSession { SessionId = "s3", StreamState = StreamLifecycleState.Active });

        Assert.Equal(1, store.CountByState(StreamLifecycleState.Starting));
        Assert.Equal(2, store.CountByState(StreamLifecycleState.Active));
        Assert.Equal(0, store.CountByState(StreamLifecycleState.Completed));
    }

    [Fact]
    public void ConcurrentAccess_DoesNotThrow()
    {
        var store = new ActiveSessionStore();
        Parallel.For(0, 100, i =>
        {
            store.Add(new ActiveStreamSession { SessionId = $"s{i}", ClientName = "Test" });
        });

        Assert.Equal(100, store.Count);

        Parallel.For(0, 100, i =>
        {
            store.Remove($"s{i}");
        });

        Assert.Equal(0, store.Count);
    }
}

