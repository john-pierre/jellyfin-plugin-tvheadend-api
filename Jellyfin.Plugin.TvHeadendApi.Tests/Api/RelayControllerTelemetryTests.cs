// Tests for the consolidated stream telemetry pipeline in RelayController:
// ONE persisted metric per stream request, stamped with the session identity,
// the token-carried zapping telemetry, honest bitrates, and real startup latency.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

/// <summary>
/// Verifies the single measurement point: the relay timing context is the only
/// persisted record per stream request and carries session identity, token telemetry,
/// end reason, and honest bitrate/latency values.
/// </summary>
public sealed class RelayControllerTelemetryTests
{
    private readonly Mock<IRelayService> _relayServiceMock = new();
    private readonly Mock<IRelayMetricsService> _metricsMock = new();
    private readonly Mock<IRelayUrlBuilder> _urlBuilderMock = new();
    private readonly Mock<IRelayTokenValidator> _tokenValidatorMock = new();
    private readonly Mock<IHealthService> _healthServiceMock = new();
    private readonly PluginConfiguration _config = new();
    private readonly ActiveSessionStore _activeSessionStore = new();
    private readonly RelayController _controller;

    public RelayControllerTelemetryTests()
    {
        _healthServiceMock.Setup(x => x.GetSnapshot()).Returns(new HealthSnapshot());
        var configProvider = new ConfigurationProvider(() => _config);
        var sessionTracker = new SessionTracker(_activeSessionStore, NullLogger<SessionTracker>.Instance);

        _controller = new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            new RelayActivityTracker(),
            sessionTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            new RelayTokenOptions(configProvider),
            _healthServiceMock.Object,
            configProvider);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Headers.UserAgent = "VLC/3.0.18 LibVLC/3.0.18";
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    [Fact]
    public async Task GetStream_PersistsExactlyOneMetric_WithSessionIdentityAndOutcome()
    {
        _config.EnableRelayTokenSecurity = false;
        RelayRequestMetric? recorded = null;
        _metricsMock.Setup(x => x.RecordMetric(It.IsAny<RelayRequestMetric>()))
            .Callback<RelayRequestMetric>(m => recorded = m);

        var timing = new RelayTimingContext { RelayType = RelayType.Stream, ChannelId = "ch-1" };
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new MemoryStream(new byte[4096]),
            TimingContext = timing,
        };
        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-1", null, CancellationToken.None);

        _metricsMock.Verify(x => x.RecordMetric(It.IsAny<RelayRequestMetric>()), Times.Once, "exactly ONE metric per stream request");
        recorded.Should().NotBeNull();
        recorded!.SessionId.Should().NotBeNullOrEmpty("the in-memory session and the metric row share one identity");
        recorded.ClientName.Should().Be("VLC");
        recorded.UserAgent.Should().Contain("VLC");
        recorded.EndedBy.Should().Be(nameof(StreamEndedBy.UpstreamEof));
        recorded.StreamFinalOutcome.Should().Be(nameof(StreamFinalOutcome.Completed));
        recorded.NormalDisconnect.Should().BeTrue();
        recorded.BytesSent.Should().Be(4096);
        recorded.StartupLatencyMs.Should().BeGreaterThan(0);
        recorded.PeakBitrate.Should().NotBeNull("a final rolling sample must yield an honest peak");
        recorded.MediaInfoCacheStatus.Should().Be("unknown", "no token carried setup telemetry");

        _activeSessionStore.Count.Should().Be(0, "the in-memory session must be finalized");
    }

    [Fact]
    public async Task GetTokenSecuredStream_CopiesTokenTelemetryIntoMetric()
    {
        _config.EnableRelayTokenSecurity = true;
        RelayRequestMetric? recorded = null;
        _metricsMock.Setup(x => x.RecordMetric(It.IsAny<RelayRequestMetric>()))
            .Callback<RelayRequestMetric>(m => recorded = m);

        var tokenRecord = new RelayTokenRecord
        {
            SelectedProfile = "jellyfin",
            ResolutionSource = "ClientRule",
            MediaInfoCacheStatus = "hit",
            StreamSetupMs = 42.5,
        };
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayTokenValidationResult.Success(tokenRecord));

        var timing = new RelayTimingContext { RelayType = RelayType.Stream, ChannelId = "ch-2" };
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new MemoryStream(new byte[128]),
            TimingContext = timing,
        };
        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-2", "jellyfin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetTokenSecuredStream("ch-2", null, CancellationToken.None);

        recorded.Should().NotBeNull();
        recorded!.EffectiveProfile.Should().Be("jellyfin");
        recorded.ResolutionSource.Should().Be("ClientRule");
        recorded.MediaInfoCacheStatus.Should().Be("hit");
        recorded.StreamSetupMs.Should().Be(42.5);
    }

    [Fact]
    public async Task GetStream_ClientCancelAfterFirstByte_PersistsRealStartupLatency()
    {
        // Regression guard for the audit finding: a normal zap/stop used to persist
        // startup latency 0 because the measured value was scoped inside the try block.
        _config.EnableRelayTokenSecurity = false;
        RelayRequestMetric? recorded = null;
        _metricsMock.Setup(x => x.RecordMetric(It.IsAny<RelayRequestMetric>()))
            .Callback<RelayRequestMetric>(m => recorded = m);

        var timing = new RelayTimingContext { RelayType = RelayType.Stream, ChannelId = "ch-3" };
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new ChunkThenCancelStream(new byte[1024]),
            TimingContext = timing,
        };
        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-3", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-3", null, CancellationToken.None);

        recorded.Should().NotBeNull();
        recorded!.EndedBy.Should().Be(nameof(StreamEndedBy.ClientDisconnectAfterFirstByte));
        recorded.StreamFinalOutcome.Should().Be(nameof(StreamFinalOutcome.NormalDisconnect));
        recorded.NormalDisconnect.Should().BeTrue();
        recorded.FinalOutcome.Should().Be("cancelled");
        recorded.StartupLatencyMs.Should().BeGreaterThan(0, "the measured first-byte latency must survive the cancel path");
        recorded.BytesSent.Should().Be(1024);
    }

    [Fact]
    public async Task GetStream_UpstreamFailureBeforeBody_ClassifiesUpstreamError()
    {
        _config.EnableRelayTokenSecurity = false;
        RelayRequestMetric? recorded = null;
        _metricsMock.Setup(x => x.RecordMetric(It.IsAny<RelayRequestMetric>()))
            .Callback<RelayRequestMetric>(m => recorded = m);

        var timing = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            ChannelId = "ch-4",
            UpstreamStatusCode = 404,
            ClientStatusCode = 404,
            FailureReason = RelayFailureReason.Upstream404,
        };
        var relayResult = new RelayResult { StatusCode = 404, Body = null, TimingContext = timing };
        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-4", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-4", null, CancellationToken.None);

        recorded.Should().NotBeNull();
        recorded!.EndedBy.Should().Be(nameof(StreamEndedBy.UpstreamHttpError));
        recorded.StreamFinalOutcome.Should().Be(nameof(StreamFinalOutcome.UpstreamFailed));
        recorded.StartupFailedWithin5Seconds.Should().BeFalse("a fast upstream 404 is not a startup-latency failure");
        _activeSessionStore.Count.Should().Be(0);
    }

    /// <summary>Stream that yields one chunk, then simulates a client disconnect.</summary>
    private sealed class ChunkThenCancelStream : MemoryStream
    {
        private bool _chunkDelivered;

        public ChunkThenCancelStream(byte[] chunk)
            : base(chunk)
        {
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_chunkDelivered)
            {
                throw new OperationCanceledException();
            }

            _chunkDelivered = true;
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
