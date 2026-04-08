using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Images;

/// <summary>
/// Implements image proxy behavior by fetching TVHeadend images through Jellyfin-authenticated endpoints.
/// </summary>
internal sealed class ImageProxyService : IImageProxyService
{
    private readonly ILogger<ImageProxyService> _logger;
    private readonly ITvheadendImageGateway _imageGateway;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProxyService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="imageGateway">Gateway used to fetch image payloads from TVHeadend.</param>
    public ImageProxyService(ILogger<ImageProxyService> logger, ITvheadendImageGateway imageGateway)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _imageGateway = imageGateway ?? throw new ArgumentNullException(nameof(imageGateway));
    }

    /// <inheritdoc />
    public async Task<IActionResult> ProxyImageAsync(string? imagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            _logger.LogWarning("Image proxy called without a valid imagePath.");
            return new BadRequestObjectResult(new { error = "imagePath parameter is required" });
        }

        try
        {
            using var response = await _imageGateway.FetchImageAsync(imagePath, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TVHeadend returned HTTP {Status} for image {ImagePath}.", response.StatusCode, imagePath);
                return new ObjectResult(new { error = $"TVHeadend returned {response.StatusCode}" }) { StatusCode = (int)response.StatusCode };
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new MemoryStream();
            await sourceStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            return new FileStreamResult(buffer, contentType);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid image path: {ImagePath}", imagePath);
            return new BadRequestObjectResult(new { error = $"Invalid image path: {ex.Message}" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch image from TVHeadend for path {ImagePath}.", imagePath);
            return new ObjectResult(new { error = $"Failed to connect to TVHeadend: {ex.Message}" }) { StatusCode = 502 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in image proxy for path {ImagePath}.", imagePath);
            return new ObjectResult(new { error = $"Internal server error: {ex.Message}" }) { StatusCode = 500 };
        }
    }
}
