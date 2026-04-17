using System.Text.Json;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Contract tests verifying that TVHeadend JSON responses deserialize correctly into model types.
/// </summary>
public class StatusModelContractTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void ActivityStatus_Deserializes_AllFields()
    {
        const string json = "{\"current_time\":1700000000,\"next_activity\":1700003600,\"subscription_count\":3,\"connection_count\":7}";
        var result = JsonSerializer.Deserialize<ActivityStatus>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(1700000000, result!.CurrentTime);
        Assert.Equal(1700003600, result.NextActivity);
        Assert.Equal(3, result.SubscriptionCount);
        Assert.Equal(7, result.ConnectionCount);
    }

    [Fact]
    public void ConnectionEntry_Deserializes_AllFields()
    {
        const string json = "{\"id\":1,\"server\":\"0.0.0.0\",\"server_port\":9981,\"peer\":\"10.0.0.1\",\"peer_port\":54321,\"started\":1700000000,\"streaming\":1,\"type\":\"HTTP\",\"user\":\"admin\"}";
        var result = JsonSerializer.Deserialize<ConnectionEntry>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
        Assert.Equal("0.0.0.0", result.Server);
        Assert.Equal(9981, result.ServerPort);
        Assert.Equal("10.0.0.1", result.Peer);
        Assert.Equal(54321, result.PeerPort);
        Assert.Equal("admin", result.User);
    }

    [Fact]
    public void ConnectionGridResponse_Deserializes_WithEntries()
    {
        const string json = "{\"entries\":[{\"id\":1,\"type\":\"HTSP\"}],\"totalCount\":1}";
        var result = JsonSerializer.Deserialize<ConnectionGridResponse>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public void InputStatusEntry_Deserializes_AllFields()
    {
        const string json = "{\"uuid\":\"abc\",\"input\":\"DVB-T\",\"stream\":\"690MHz\",\"subs\":2,\"weight\":100,\"signal\":-450,\"signal_scale\":2,\"ber\":5,\"snr\":280,\"snr_scale\":2,\"unc\":1,\"bps\":15000000,\"te\":0,\"cc\":3,\"ec_bit\":10,\"tc_bit\":1000,\"ec_block\":2,\"tc_block\":50}";
        var result = JsonSerializer.Deserialize<InputStatusEntry>(json, Options);

        Assert.NotNull(result);
        Assert.Equal("abc", result!.Uuid);
        Assert.Equal(-450, result.Signal);
        Assert.Equal(2, result.SignalScale);
        Assert.Equal(280, result.Snr);
        Assert.Equal(15000000, result.Bps);
        Assert.Equal(3, result.Cc);
    }

    [Fact]
    public void InputGridResponse_Deserializes_WithEntries()
    {
        const string json = "{\"entries\":[{\"uuid\":\"x\"}],\"totalCount\":1}";
        var result = JsonSerializer.Deserialize<InputGridResponse>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result!.Entries);
    }

    [Fact]
    public void SubscriptionEntry_Deserializes_AllFields()
    {
        const string json = "{\"id\":42,\"start\":1700000000,\"errors\":0,\"state\":\"Running\",\"hostname\":\"10.0.0.1\",\"username\":\"admin\",\"client\":\"Jellyfin\",\"title\":\"epg\",\"channel\":\"BBC One\",\"service\":\"DVB-T\",\"profile\":\"pass\",\"in\":1500000,\"out\":1500000,\"total_in\":75000000,\"total_out\":75000000}";
        var result = JsonSerializer.Deserialize<SubscriptionEntry>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(42, result!.Id);
        Assert.Equal("Running", result.State);
        Assert.Equal("BBC One", result.Channel);
        Assert.Equal("pass", result.Profile);
        Assert.Equal(75000000, result.TotalIn);
    }

    [Fact]
    public void SubscriptionGridResponse_Deserializes_WithEntries()
    {
        const string json = "{\"entries\":[{\"id\":1}],\"totalCount\":1}";
        var result = JsonSerializer.Deserialize<SubscriptionGridResponse>(json, Options);

        Assert.NotNull(result);
        Assert.Single(result!.Entries);
    }

    [Fact]
    public void ActivityStatus_UnknownFields_AreIgnored()
    {
        const string json = "{\"current_time\":100,\"next_activity\":200,\"subscription_count\":1,\"connection_count\":2,\"unknown_field\":\"value\"}";
        var result = JsonSerializer.Deserialize<ActivityStatus>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(100, result!.CurrentTime);
    }

    [Fact]
    public void InputStatusEntry_Defaults_WhenFieldsMissing()
    {
        const string json = "{\"uuid\":\"test\"}";
        var result = JsonSerializer.Deserialize<InputStatusEntry>(json, Options);

        Assert.NotNull(result);
        Assert.Equal("test", result!.Uuid);
        Assert.Equal(0, result.Signal);
        Assert.Equal(0, result.Bps);
        Assert.Equal(string.Empty, result.Input);
    }
}

