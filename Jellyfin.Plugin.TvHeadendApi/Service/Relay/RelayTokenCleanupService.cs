// Background hosted service that periodically cleans up expired relay tokens.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Hosted service that periodically removes expired and revoked relay tokens
/// from the SQLite database to prevent unbounded growth.
/// </summary>
internal sealed class RelayTokenCleanupService : IHostedService, IDisposable
{
    /// <summary>
    /// Upper bound for the effective cleanup cadence. Expired tokens are worthless (validation
    /// always rejects them) and should disappear from the database — and the dashboard counts —
    /// within minutes, so configured intervals above this cap are clamped down to it. Shorter
    /// configured intervals are honored as-is.
    /// </summary>
    internal static readonly TimeSpan MaxCleanupInterval = TimeSpan.FromMinutes(5);

    /// <summary>Delay before the first cleanup run after startup.</summary>
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);

    private readonly ILogger<RelayTokenCleanupService> _logger;
    private readonly IRelayTokenService _tokenService;
    private readonly RelayTokenOptions _options;
    private Timer? _cleanupTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenCleanupService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tokenService">Token service for cleanup operations.</param>
    /// <param name="options">Token policy options (includes cleanup interval).</param>
    public RelayTokenCleanupService(
        ILogger<RelayTokenCleanupService> logger,
        IRelayTokenService tokenService,
        RelayTokenOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var interval = GetEffectiveInterval(_options.CleanupInterval);
        _cleanupTimer = new Timer(
            _ => RunCleanup(),
            null,
            FirstRunDelay,
            interval);

        _logger.LogInformation(
            "RelayTokenCleanupService started — cleanup runs every {IntervalMinutes} minutes (configured: {ConfiguredMinutes} minutes).",
            interval.TotalMinutes,
            _options.CleanupIntervalMinutes);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the effective cleanup interval: the configured value, clamped to
    /// <see cref="MaxCleanupInterval"/> so expired tokens never linger for hours.
    /// </summary>
    /// <param name="configured">The configured cleanup interval.</param>
    /// <returns>The interval actually used for the cleanup timer.</returns>
    internal static TimeSpan GetEffectiveInterval(TimeSpan configured)
        => configured > MaxCleanupInterval ? MaxCleanupInterval : configured;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("RelayTokenCleanupService stopped.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }

    private void RunCleanup()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _tokenService.CleanupExpiredTokensAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Relay token cleanup timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up expired relay tokens.");
        }
    }
}
