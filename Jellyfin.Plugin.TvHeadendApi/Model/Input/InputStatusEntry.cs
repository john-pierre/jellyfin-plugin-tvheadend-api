using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Input;

/// <summary>
/// Represents a single TV input/tuner stream from the TVHeadend <c>/api/status/inputs</c> endpoint.
/// </summary>
public sealed class InputStatusEntry
{
    /// <summary>Gets the input instance UUID.</summary>
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    /// <summary>Gets the input adapter name.</summary>
    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;

    /// <summary>Gets the mux/stream name currently tuned.</summary>
    [JsonPropertyName("stream")]
    public string Stream { get; init; } = string.Empty;

    /// <summary>Gets the number of active subscriptions on this input.</summary>
    [JsonPropertyName("subs")]
    public int Subs { get; init; }

    /// <summary>Gets the maximum subscription weight.</summary>
    [JsonPropertyName("weight")]
    public int Weight { get; init; }

    /// <summary>Gets the signal strength value.</summary>
    [JsonPropertyName("signal")]
    public int Signal { get; init; }

    /// <summary>Gets the signal scale indicator (0 = unknown, 1 = relative, 2 = dBm).</summary>
    [JsonPropertyName("signal_scale")]
    public int SignalScale { get; init; }

    /// <summary>Gets the bit error rate.</summary>
    [JsonPropertyName("ber")]
    public long Ber { get; init; }

    /// <summary>Gets the signal-to-noise ratio.</summary>
    [JsonPropertyName("snr")]
    public int Snr { get; init; }

    /// <summary>Gets the SNR scale indicator (0 = unknown, 1 = relative, 2 = dB).</summary>
    [JsonPropertyName("snr_scale")]
    public int SnrScale { get; init; }

    /// <summary>Gets the uncorrectable error count.</summary>
    [JsonPropertyName("unc")]
    public long Unc { get; init; }

    /// <summary>Gets the current bitrate in bits per second.</summary>
    [JsonPropertyName("bps")]
    public long Bps { get; init; }

    /// <summary>Gets the transport error count.</summary>
    [JsonPropertyName("te")]
    public long Te { get; init; }

    /// <summary>Gets the continuity error count.</summary>
    [JsonPropertyName("cc")]
    public long Cc { get; init; }

    /// <summary>Gets the number of corrected bit errors (FEC).</summary>
    [JsonPropertyName("ec_bit")]
    public long EcBit { get; init; }

    /// <summary>Gets the total bit errors before FEC.</summary>
    [JsonPropertyName("tc_bit")]
    public long TcBit { get; init; }

    /// <summary>Gets the number of corrected block errors.</summary>
    [JsonPropertyName("ec_block")]
    public long EcBlock { get; init; }

    /// <summary>Gets the total block errors.</summary>
    [JsonPropertyName("tc_block")]
    public long TcBlock { get; init; }
}
