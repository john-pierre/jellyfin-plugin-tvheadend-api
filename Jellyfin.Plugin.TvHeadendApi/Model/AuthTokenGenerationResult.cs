namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents the result of an authentication token generation request.
/// </summary>
public class AuthTokenGenerationResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the token generation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the generated authentication token.
    /// </summary>
    public string AuthToken { get; set; } = string.Empty;
}
