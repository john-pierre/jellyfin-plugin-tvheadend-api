namespace Jellyfin.Plugin.TvHeadendApi.Model.Auth;

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

    /// <summary>
    /// Gets or sets how many attempts were needed until the operation finished.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a refresh call was used.
    /// </summary>
    public bool UsedRefresh { get; set; }
}
