// Pure classification helpers mapping stream end reasons to final outcomes.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Pure, dependency-free mapping from <see cref="StreamEndedBy"/> to <see cref="StreamFinalOutcome"/>.
/// Shared by the in-memory session tracker and the persisted metric conversion so both
/// always agree on what counts as a failure versus normal Live TV behavior.
/// </summary>
public static class StreamOutcomeClassifier
{
    /// <summary>
    /// Classifies the final outcome based on how the stream ended and whether data was sent.
    /// </summary>
    /// <param name="endedBy">How the stream ended.</param>
    /// <param name="firstByteSent">Whether the first byte was successfully sent to the client.</param>
    /// <returns>The classified final outcome.</returns>
    public static StreamFinalOutcome ClassifyOutcome(StreamEndedBy endedBy, bool firstByteSent)
    {
        return endedBy switch
        {
            StreamEndedBy.UpstreamEof => StreamFinalOutcome.Completed,
            StreamEndedBy.ClientDisconnectAfterFirstByte => StreamFinalOutcome.NormalDisconnect,
            StreamEndedBy.StartupCancelledBeforeFirstByte => StreamFinalOutcome.StartupFailed,
            StreamEndedBy.UpstreamTimeout => StreamFinalOutcome.UpstreamFailed,
            StreamEndedBy.UpstreamHttpError => StreamFinalOutcome.UpstreamFailed,
            StreamEndedBy.DownstreamWriteError => StreamFinalOutcome.DownstreamFailed,
            StreamEndedBy.UnexpectedException => StreamFinalOutcome.Failed,
            StreamEndedBy.Unknown => StreamFinalOutcome.Failed,
            _ => StreamFinalOutcome.Failed,
        };
    }

    /// <summary>
    /// Determines whether an outcome represents a normal (non-failure) stream end.
    /// </summary>
    /// <param name="outcome">The final outcome.</param>
    /// <returns><c>true</c> for completed streams and normal client disconnects.</returns>
    public static bool IsNormalDisconnect(StreamFinalOutcome outcome)
    {
        return outcome is StreamFinalOutcome.Completed or StreamFinalOutcome.NormalDisconnect;
    }
}
