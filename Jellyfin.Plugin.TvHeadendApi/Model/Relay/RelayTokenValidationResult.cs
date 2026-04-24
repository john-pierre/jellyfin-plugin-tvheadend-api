// Typed result of relay token validation — carries success state, failure reason, and the matched record.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Represents the outcome of a relay token validation attempt.
/// </summary>
public sealed class RelayTokenValidationResult
{
    /// <summary>Gets a value indicating whether the token is valid and the request should proceed.</summary>
    public bool IsValid { get; init; }

    /// <summary>Gets the failure reason when validation fails. <see cref="RelayTokenFailureReason.None"/> on success.</summary>
    public RelayTokenFailureReason FailureReason { get; init; }

    /// <summary>Gets the matched token record when validation succeeds. Null on failure.</summary>
    public RelayTokenRecord? TokenRecord { get; init; }

    /// <summary>Creates a successful validation result.</summary>
    /// <param name="record">The matched token record.</param>
    /// <returns>A valid result.</returns>
    public static RelayTokenValidationResult Success(RelayTokenRecord record) => new()
    {
        IsValid = true,
        FailureReason = RelayTokenFailureReason.None,
        TokenRecord = record,
    };

    /// <summary>Creates a failed validation result.</summary>
    /// <param name="reason">The failure reason.</param>
    /// <returns>An invalid result.</returns>
    public static RelayTokenValidationResult Failure(RelayTokenFailureReason reason) => new()
    {
        IsValid = false,
        FailureReason = reason,
    };

    /// <summary>Creates a result indicating security is disabled — request should proceed without token.</summary>
    /// <returns>A valid-but-skipped result.</returns>
    public static RelayTokenValidationResult SecurityDisabledResult() => new()
    {
        IsValid = true,
        FailureReason = RelayTokenFailureReason.SecurityDisabled,
    };
}
