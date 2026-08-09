// Lightweight in-memory map of TVHeadend channel UUID -> display name.

using System.Collections.Concurrent;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Guide;

/// <summary>
/// A small, thread-safe cache mapping a TVHeadend channel UUID to its display name. It is populated
/// whenever the guide is fetched (the channel grid carries both fields) and read by components that
/// only have a channel UUID — notably the streaming-session tracker and the relay metrics — so the
/// dashboard can show channel names instead of raw UUIDs.
/// </summary>
public sealed class ChannelNameCache
{
    private readonly ConcurrentDictionary<string, string> _names = new(System.StringComparer.Ordinal);

    /// <summary>Gets the number of cached channel names.</summary>
    public int Count => _names.Count;

    /// <summary>Records (or updates) the display name for a channel UUID.</summary>
    /// <param name="channelUuid">The TVHeadend channel UUID.</param>
    /// <param name="name">The channel display name.</param>
    public void Set(string? channelUuid, string? name)
    {
        if (string.IsNullOrWhiteSpace(channelUuid) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _names[channelUuid] = name;
    }

    /// <summary>
    /// Returns the display name for a channel UUID, or the UUID itself when the name is not yet known
    /// (e.g. before the first guide fetch). Never returns null/empty for a non-empty input.
    /// </summary>
    /// <param name="channelUuid">The TVHeadend channel UUID.</param>
    /// <returns>The channel name, or the UUID as a fallback.</returns>
    public string GetName(string? channelUuid)
    {
        if (string.IsNullOrWhiteSpace(channelUuid))
        {
            return string.Empty;
        }

        return _names.TryGetValue(channelUuid, out var name) ? name : channelUuid;
    }
}
