// Unit tests for StreamEndedBy and StreamFinalOutcome enum coverage and edge cases.

using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Metrics;

/// <summary>
/// Tests verifying the complete coverage of stream lifecycle model enums
/// and edge cases in outcome classification (merged vocabulary in Model/Relay).
/// </summary>
public sealed class StreamLifecycleModelTests
{
    // ═══════════════════════════════════════════════════════════════════
    // HEAD request behavior model
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void HeadRequest_NoException_NoUpstreamError_ClassifiesAsUpstreamEof()
    {
        // HEAD requests complete without long-running stream.
        // When no exception and no error code, it's a clean completion.
        var endReason = SessionTracker.ClassifyEndReason(
            exception: null, firstByteSent: false, upstreamStatusCode: null);
        Assert.Equal(StreamEndedBy.UpstreamEof, endReason);

        var outcome = SessionTracker.ClassifyOutcome(endReason, firstByteSent: false);
        Assert.Equal(StreamFinalOutcome.Completed, outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Range request tracking
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ActiveStreamSession_RangeFields_DefaultToFalse()
    {
        var session = new ActiveStreamSession();
        Assert.False(session.RangeRequested);
        Assert.False(session.RangeSupported);
        Assert.False(session.ContentRangePresent);
        Assert.False(session.AcceptRangesPresent);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Bytes persistence model
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void CompletedSession_TotalBytes_CanBeSetFromAnyPath()
    {
        // Success path
        var successSession = new CompletedStreamSession { TotalBytes = 1_000_000 };
        Assert.Equal(1_000_000, successSession.TotalBytes);

        // Cancellation path — bytes still counted
        var cancelSession = new CompletedStreamSession
        {
            TotalBytes = 500_000,
            EndedBy = StreamEndedBy.ClientDisconnectAfterFirstByte,
            FinalOutcome = StreamFinalOutcome.NormalDisconnect,
            NormalDisconnect = true,
        };
        Assert.True(cancelSession.TotalBytes > 0);
        Assert.True(cancelSession.NormalDisconnect);

        // Exception path — bytes still counted
        var errorSession = new CompletedStreamSession
        {
            TotalBytes = 250_000,
            EndedBy = StreamEndedBy.DownstreamWriteError,
            FinalOutcome = StreamFinalOutcome.DownstreamFailed,
        };
        Assert.True(errorSession.TotalBytes > 0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Lifecycle state transitions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void StreamLifecycleState_HasExpectedValues()
    {
        Assert.Equal(0, (int)StreamLifecycleState.Starting);
        Assert.Equal(1, (int)StreamLifecycleState.Active);
        Assert.Equal(2, (int)StreamLifecycleState.Ending);
        Assert.Equal(3, (int)StreamLifecycleState.Completed);
        Assert.Equal(4, (int)StreamLifecycleState.Failed);
    }

    // ═══════════════════════════════════════════════════════════════════
    // End reason → Outcome exhaustive mapping (all merged enum values)
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(StreamEndedBy.UpstreamEof, true, StreamFinalOutcome.Completed)]
    [InlineData(StreamEndedBy.ClientDisconnectAfterFirstByte, true, StreamFinalOutcome.NormalDisconnect)]
    [InlineData(StreamEndedBy.StartupCancelledBeforeFirstByte, false, StreamFinalOutcome.StartupFailed)]
    [InlineData(StreamEndedBy.UpstreamTimeout, false, StreamFinalOutcome.UpstreamFailed)]
    [InlineData(StreamEndedBy.UpstreamHttpError, false, StreamFinalOutcome.UpstreamFailed)]
    [InlineData(StreamEndedBy.DownstreamWriteError, true, StreamFinalOutcome.DownstreamFailed)]
    [InlineData(StreamEndedBy.UnexpectedException, true, StreamFinalOutcome.Failed)]
    [InlineData(StreamEndedBy.Unknown, true, StreamFinalOutcome.Failed)]
    public void ClassifyOutcome_ExhaustiveMapping(StreamEndedBy endedBy, bool firstByteSent, StreamFinalOutcome expected)
    {
        var result = StreamOutcomeClassifier.ClassifyOutcome(endedBy, firstByteSent);
        Assert.Equal(expected, result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Normal disconnect detection
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(StreamFinalOutcome.Completed, true)]
    [InlineData(StreamFinalOutcome.NormalDisconnect, true)]
    [InlineData(StreamFinalOutcome.StartupFailed, false)]
    [InlineData(StreamFinalOutcome.UpstreamFailed, false)]
    [InlineData(StreamFinalOutcome.DownstreamFailed, false)]
    [InlineData(StreamFinalOutcome.Failed, false)]
    public void NormalDisconnect_CorrectlyClassified(StreamFinalOutcome outcome, bool expectedNormal)
    {
        Assert.Equal(expectedNormal, StreamOutcomeClassifier.IsNormalDisconnect(outcome));
    }
}
