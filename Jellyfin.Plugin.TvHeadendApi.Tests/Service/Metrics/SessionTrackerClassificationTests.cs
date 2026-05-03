// Unit tests for SessionTracker outcome classification and end-reason detection.

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Metrics;

/// <summary>
/// Tests for the streaming session lifecycle classification logic.
/// Verifies correct outcome assignment for Live TV disconnect patterns.
/// </summary>
public sealed class SessionTrackerClassificationTests
{
    // ═══════════════════════════════════════════════════════════════════
    // 1. Client disconnect after first byte → NormalDisconnect
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyEndReason_OperationCancelled_AfterFirstByte_ReturnsClientDisconnect()
    {
        var exception = new OperationCanceledException();
        var result = SessionTracker.ClassifyEndReason(exception, firstByteSent: true, upstreamStatusCode: null);
        Assert.Equal(StreamEndedBy.ClientDisconnectAfterFirstByte, result);
    }

    [Fact]
    public void ClassifyOutcome_ClientDisconnectAfterFirstByte_ReturnsNormalDisconnect()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.ClientDisconnectAfterFirstByte, firstByteSent: true);
        Assert.Equal(StreamFinalOutcome.NormalDisconnect, outcome);
    }

    [Fact]
    public void ClientDisconnectAfterFirstByte_IsNotAFailure()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.ClientDisconnectAfterFirstByte, firstByteSent: true);
        Assert.NotEqual(StreamFinalOutcome.Failed, outcome);
        Assert.NotEqual(StreamFinalOutcome.StartupFailed, outcome);
        Assert.NotEqual(StreamFinalOutcome.UpstreamFailed, outcome);
        Assert.NotEqual(StreamFinalOutcome.DownstreamFailed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. Client cancellation before first byte → StartupFailed
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyEndReason_OperationCancelled_BeforeFirstByte_ReturnsStartupCancelled()
    {
        var exception = new OperationCanceledException();
        var result = SessionTracker.ClassifyEndReason(exception, firstByteSent: false, upstreamStatusCode: null);
        Assert.Equal(StreamEndedBy.StartupCancelledBeforeFirstByte, result);
    }

    [Fact]
    public void ClassifyOutcome_StartupCancelled_ReturnsStartupFailed()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.StartupCancelledBeforeFirstByte, firstByteSent: false);
        Assert.Equal(StreamFinalOutcome.StartupFailed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. Upstream HTTP errors (401, 403, 404, 500) → UpstreamFailed
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void ClassifyEndReason_UpstreamHttpError_ReturnsUpstreamHttpError(int statusCode)
    {
        var result = SessionTracker.ClassifyEndReason(exception: null, firstByteSent: false, upstreamStatusCode: statusCode);
        Assert.Equal(StreamEndedBy.UpstreamHttpError, result);
    }

    [Fact]
    public void ClassifyOutcome_UpstreamHttpError_ReturnsUpstreamFailed()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.UpstreamHttpError, firstByteSent: false);
        Assert.Equal(StreamFinalOutcome.UpstreamFailed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. Upstream timeout → UpstreamFailed
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyEndReason_HttpRequestException_ReturnsUpstreamTimeout()
    {
        var exception = new HttpRequestException("Connection refused");
        var result = SessionTracker.ClassifyEndReason(exception, firstByteSent: false, upstreamStatusCode: null);
        Assert.Equal(StreamEndedBy.UpstreamTimeout, result);
    }

    [Fact]
    public void ClassifyOutcome_UpstreamTimeout_ReturnsUpstreamFailed()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.UpstreamTimeout, firstByteSent: false);
        Assert.Equal(StreamFinalOutcome.UpstreamFailed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. Downstream write error → DownstreamFailed
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyEndReason_IOException_ReturnsDownstreamWriteError()
    {
        var exception = new IOException("Connection reset by peer");
        var result = SessionTracker.ClassifyEndReason(exception, firstByteSent: true, upstreamStatusCode: null);
        Assert.Equal(StreamEndedBy.DownstreamWriteError, result);
    }

    [Fact]
    public void ClassifyOutcome_DownstreamWriteError_ReturnsDownstreamFailed()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.DownstreamWriteError, firstByteSent: true);
        Assert.Equal(StreamFinalOutcome.DownstreamFailed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. Upstream EOF → Completed
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyEndReason_NoException_NoErrorCode_ReturnsUpstreamEof()
    {
        var result = SessionTracker.ClassifyEndReason(exception: null, firstByteSent: true, upstreamStatusCode: null);
        Assert.Equal(StreamEndedBy.UpstreamEof, result);
    }

    [Fact]
    public void ClassifyOutcome_UpstreamEof_ReturnsCompleted()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.UpstreamEof, firstByteSent: true);
        Assert.Equal(StreamFinalOutcome.Completed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. Unexpected exception → Failed
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyEndReason_UnexpectedException_ReturnsUnexpected()
    {
        var exception = new InvalidOperationException("Something went wrong");
        var result = SessionTracker.ClassifyEndReason(exception, firstByteSent: true, upstreamStatusCode: null);
        Assert.Equal(StreamEndedBy.UnexpectedException, result);
    }

    [Fact]
    public void ClassifyOutcome_UnexpectedException_ReturnsFailed()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.UnexpectedException, firstByteSent: true);
        Assert.Equal(StreamFinalOutcome.Failed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. Plugin shutdown with sent bytes → NormalDisconnect
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyOutcome_PluginShutdown_AfterFirstByte_ReturnsNormalDisconnect()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.PluginShutdown, firstByteSent: true);
        Assert.Equal(StreamFinalOutcome.NormalDisconnect, outcome);
    }

    [Fact]
    public void ClassifyOutcome_PluginShutdown_BeforeFirstByte_ReturnsFailed()
    {
        var outcome = SessionTracker.ClassifyOutcome(StreamEndedBy.PluginShutdown, firstByteSent: false);
        Assert.Equal(StreamFinalOutcome.Failed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Client name derivation
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Infuse/7.6.8 (iPhone; iOS 17.4)", "Infuse")]
    [InlineData("Swiftfin iOS/1.0", "Swiftfin")]
    [InlineData("Jellyfin Mobile iOS", "Jellyfin Mobile")]
    [InlineData("Jellyfin Web/10.8.0", "Jellyfin Web")]
    [InlineData("ExoPlayer/2.18.1", "Android Client")]
    [InlineData("AppleCoreMedia/1.0.0.21E236", "Apple AVPlayer")]
    [InlineData("VLC/3.0.18 LibVLC/3.0.18", "VLC")]
    [InlineData("mpv 0.36.0", "mpv")]
    [InlineData("Kodi/20.0", "Kodi")]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData("SomeRandomClient/1.0", "Unknown")]
    public void DeriveClientName_CorrectlyIdentifiesClients(string? userAgent, string expectedName)
    {
        var result = SessionTracker.DeriveClientName(userAgent);
        Assert.Equal(expectedName, result);
    }
}

