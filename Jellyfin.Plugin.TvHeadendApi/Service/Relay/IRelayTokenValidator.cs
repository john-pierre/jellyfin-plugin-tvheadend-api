// Validator interface for relay token validation at public endpoints.

using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Validates relay tokens presented at public relay endpoints.
/// </summary>
public interface IRelayTokenValidator
{
    /// <summary>
    /// Validates a raw relay token against the expected relay type and resource identifier.
    /// Increments use count on success. Returns a typed result with failure reason on failure.
    /// </summary>
    /// <param name="rawToken">The raw token from the request query string.</param>
    /// <param name="expectedType">The expected relay type (stream or image).</param>
    /// <param name="expectedResourceId">The expected resource — channel ID for streams, image path for images.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A validation result indicating success or failure with reason.</returns>
    Task<RelayTokenValidationResult> ValidateAsync(
        string? rawToken,
        RelayType expectedType,
        string? expectedResourceId,
        CancellationToken cancellationToken);
}
