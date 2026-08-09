using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Result of finalizing an in-memory stream session: the removed session plus its
/// classified outcome, handed back to the relay controller for metric stamping.
/// </summary>
/// <param name="Session">The removed active session (identity and last known counters).</param>
/// <param name="EndedBy">How the stream ended.</param>
/// <param name="FinalOutcome">The classified final outcome.</param>
/// <param name="NormalDisconnect">Whether the end counts as normal Live TV behavior.</param>
public sealed record FinalizedStreamSession(
    ActiveStreamSession Session,
    StreamEndedBy EndedBy,
    StreamFinalOutcome FinalOutcome,
    bool NormalDisconnect);
