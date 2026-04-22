// High-performance relay service — proxies TVHeadend images and streams with zero-copy passthrough.

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Production relay that forwards requests to TVHeadend and streams
/// the response body directly to the caller without buffering.
/// <para>
/// Uses a dedicated <see cref="SocketsHttpHandler"/> tuned for maximum
/// connection reuse, keep-alive, and low allocation overhead.
/// The socket-level handler (TCP pool, TLS sessions) is kept alive across
/// credential changes — only the auth wrapper is rebuilt. A full socket
/// rebuild only occurs when host, port, or SSL settings change.
/// No Jellyfin restart needed for any configuration change.
/// </para>
/// </summary>
internal sealed class RelayService : IRelayService, IDisposable
{
    /// <summary>
    /// Number of TCP connections to pre-establish when the socket pool is created or rebuilt.
    /// Eliminates cold-start latency for the first N concurrent client requests.
    /// </summary>
    private const int WarmupConnectionCount = 5;

    /// <summary>
    /// Timeout for image relay requests (channel logos, EPG images).
    /// </summary>
    private static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for stream relay requests — effectively infinite for live streams.
    /// Using 24 hours as a practical upper bound.
    /// </summary>
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromHours(24);

    /// <summary>
    /// Timeout for individual warmup probe requests. Must be short — warmup is best-effort.
    /// </summary>
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(3);

    private readonly object _clientLock = new();
    private readonly IUrlBuilder _urlBuilder;
    private readonly PluginConfigurationProvider _configProvider;
    private readonly ILogger<RelayService> _logger;

    /// <summary>
    /// Fingerprint of socket-level config (host, port, SSL, cert errors, webroot).
    /// Changes here require a full socket handler rebuild (TCP pool + TLS sessions lost).
    /// </summary>
    private string _socketFingerprint = string.Empty;

    /// <summary>
    /// Fingerprint of auth-level config (anonymous, username, password).
    /// Changes here only rebuild the auth wrapper — the socket pool survives.
    /// </summary>
    private string _authFingerprint = string.Empty;

    private SocketsHttpHandler? _socketHandler;
    private HttpMessageHandler? _outerHandler;
    private HttpClient? _imageClient;
    private HttpClient? _streamClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayService"/> class.
    /// </summary>
    /// <param name="urlBuilder">URL builder for constructing TVHeadend URLs.</param>
    /// <param name="configProvider">Provider for the current plugin configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public RelayService(
        IUrlBuilder urlBuilder,
        PluginConfigurationProvider configProvider,
        ILogger<RelayService> logger)
    {
        ArgumentNullException.ThrowIfNull(urlBuilder);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _urlBuilder = urlBuilder;
        _configProvider = configProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RelayResult> RelayImageAsync(string upstreamPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamPath);
        var (imageClient, _) = GetOrRebuildClients();
        return await RelayRequestAsync(imageClient, upstreamPath, "image", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RelayResult> RelayStreamAsync(string channelId, string? profile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var config = GetConfigOrThrow();
        var endpoint = $"stream/channel/{Uri.EscapeDataString(channelId)}";
        if (!string.IsNullOrWhiteSpace(profile))
        {
            endpoint += $"?profile={Uri.EscapeDataString(profile)}";
        }
        else if (!string.IsNullOrWhiteSpace(config.StreamingProfile))
        {
            endpoint += $"?profile={Uri.EscapeDataString(config.StreamingProfile)}";
        }

        var (_, streamClient) = GetOrRebuildClients();
        return await RelayRequestAsync(streamClient, endpoint, "stream", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public void Dispose()
    {
        lock (_clientLock)
        {
            DisposeClients();

            // On full disposal we dispose everything — cascade from outer to socket is fine here.
            if (_outerHandler is not null && !ReferenceEquals(_outerHandler, _socketHandler))
            {
                _outerHandler.Dispose(); // cascades to _socketHandler via InnerHandler
                _outerHandler = null;
                _socketHandler = null;
            }
            else
            {
                _outerHandler = null;
                DisposeSocketHandler();
            }
        }
    }

    /// <summary>
    /// Returns the current image and stream clients, rebuilding only what's necessary.
    /// <list type="bullet">
    ///   <item>Socket config changed (host/port/SSL) → full rebuild (TCP pool lost).</item>
    ///   <item>Auth config changed (username/password) → only auth wrapper + clients rebuilt (TCP pool survives).</item>
    ///   <item>Nothing changed → fast path, no lock, return cached clients.</item>
    /// </list>
    /// </summary>
    private (HttpClient ImageClient, HttpClient StreamClient) GetOrRebuildClients()
    {
        var config = GetConfigOrThrow();
        var socketFp = BuildSocketFingerprint(config);
        var authFp = BuildAuthFingerprint(config);

        // Fast path — no lock when nothing has changed.
        if (string.Equals(_socketFingerprint, socketFp, StringComparison.Ordinal)
            && string.Equals(_authFingerprint, authFp, StringComparison.Ordinal)
            && _imageClient is not null && _streamClient is not null)
        {
            return (_imageClient, _streamClient);
        }

        lock (_clientLock)
        {
            // Double-check after acquiring lock.
            if (string.Equals(_socketFingerprint, socketFp, StringComparison.Ordinal)
                && string.Equals(_authFingerprint, authFp, StringComparison.Ordinal)
                && _imageClient is not null && _streamClient is not null)
            {
                return (_imageClient, _streamClient);
            }

            var socketChanged = !string.Equals(_socketFingerprint, socketFp, StringComparison.Ordinal);
            var authChanged = !string.Equals(_authFingerprint, authFp, StringComparison.Ordinal);

            if (socketChanged)
            {
                // Host/port/SSL changed — must rebuild everything, TCP pool is invalid.
                _logger.LogInformation(
                    "Relay full pipeline rebuild — socket configuration changed (host/port/SSL)");

                DisposeClients();
                DisposeAuthHandler();
                DisposeSocketHandler();

                _socketHandler = CreateSocketHandler(config);
                _socketFingerprint = socketFp;
            }

            if (socketChanged || authChanged)
            {
                // Auth changed — rebuild auth wrapper on top of existing socket handler.
                // Socket pool (TCP connections, TLS sessions) survives.
                if (!socketChanged)
                {
                    _logger.LogInformation(
                        "Relay auth pipeline rebuild — credentials changed (socket pool preserved)");
                }

                DisposeClients();
                DisposeAuthHandler();

                _outerHandler = WrapWithAuth(_socketHandler!, config);
                _authFingerprint = authFp;

                _imageClient = new HttpClient(_outerHandler, disposeHandler: false) { Timeout = ImageTimeout };
                _streamClient = new HttpClient(_outerHandler, disposeHandler: false) { Timeout = StreamTimeout };
            }

            // Warm the connection pool in the background after any rebuild.
            // Fire-and-forget — does not block the current request.
            if (socketChanged || authChanged)
            {
                _ = WarmConnectionPoolAsync(_imageClient!, config);
            }

            return (_imageClient!, _streamClient!);
        }
    }

    /// <summary>
    /// Disposes only the HttpClient instances. Caller must hold <see cref="_clientLock"/>.
    /// </summary>
    private void DisposeClients()
    {
        _imageClient?.Dispose();
        _streamClient?.Dispose();
        _imageClient = null;
        _streamClient = null;
    }

    /// <summary>
    /// Disposes the outer auth handler (DigestAuthHandler) without touching the socket handler.
    /// Only relevant if the outer handler is a separate wrapper (not the socket handler itself).
    /// <para>
    /// We do NOT call Dispose() on the DigestAuthHandler because DelegatingHandler.Dispose()
    /// cascades to InnerHandler, which would destroy our socket pool. The DigestAuthHandler
    /// holds no unmanaged resources — it only has username/password strings and a nonce counter,
    /// so skipping disposal is safe. The socket handler is disposed separately in
    /// <see cref="DisposeSocketHandler"/>.
    /// </para>
    /// Caller must hold <see cref="_clientLock"/>.
    /// </summary>
    private void DisposeAuthHandler()
    {
        // Simply drop the reference — do NOT dispose, as that would cascade to the socket handler.
        _outerHandler = null;
    }

    /// <summary>
    /// Disposes the socket handler (and its TCP connection pool). Caller must hold <see cref="_clientLock"/>.
    /// </summary>
    private void DisposeSocketHandler()
    {
        _socketHandler?.Dispose();
        _socketHandler = null;
    }

    /// <summary>
    /// Builds a fingerprint for socket-level configuration.
    /// Changes here invalidate the TCP connection pool and TLS sessions.
    /// </summary>
    private static string BuildSocketFingerprint(PluginConfiguration config)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{config.Host}|{config.Port}|{config.UseSSL}|{config.IgnoreCertificateErrors}|{config.Webroot}");
    }

    /// <summary>
    /// Builds a fingerprint for auth-level configuration.
    /// Changes here only require rebuilding the auth wrapper — the socket pool survives.
    /// </summary>
    private static string BuildAuthFingerprint(PluginConfiguration config)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{config.AllowAnonymousAccess}|{config.Username}|{config.Password}");
    }

    /// <summary>
    /// Core relay method — sends a GET to TVHeadend with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// so the body is never buffered in memory. Returns the raw stream for direct passthrough.
    /// </summary>
    private async Task<RelayResult> RelayRequestAsync(
        HttpClient client,
        string relativeEndpoint,
        string kind,
        CancellationToken cancellationToken)
    {
        var config = GetConfigOrThrow();
        var url = _urlBuilder.BuildResourceUrl(config, relativeEndpoint);
        var safeUrl = _urlBuilder.MaskSensitiveData(url, config);

        _logger.LogDebug("Relay {Kind} request started: {Url}", kind, safeUrl);
        var sw = Stopwatch.StartNew();

        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            _logger.LogDebug(
                "Relay {Kind} upstream responded {StatusCode} in {ElapsedMs}ms: {Url}",
                kind,
                (int)response.StatusCode,
                sw.ElapsedMilliseconds,
                safeUrl);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = MapUpstreamStatus(response.StatusCode);
                response.Dispose();
                return new RelayResult { StatusCode = statusCode };
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return new RelayResult
            {
                StatusCode = (int)response.StatusCode,
                ContentType = response.Content.Headers.ContentType?.ToString(),
                ContentLength = response.Content.Headers.ContentLength,
                ETag = response.Headers.ETag?.ToString(),
                LastModified = response.Content.Headers.LastModified?.ToString("R"),
                CacheControl = response.Headers.CacheControl?.ToString(),
                Body = body,
                ResponseMessage = response,
            };
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Relay {Kind} cancelled by client: {Url}", kind, safeUrl);
            response?.Dispose();
            throw;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Relay {Kind} timed out: {Url}", kind, safeUrl);
            response?.Dispose();
            return new RelayResult { StatusCode = 504 };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Relay {Kind} upstream unreachable: {Url}", kind, safeUrl);
            response?.Dispose();
            return new RelayResult { StatusCode = 502 };
        }
    }

    /// <summary>
    /// Maps non-success upstream status codes to appropriate gateway error codes.
    /// </summary>
    private static int MapUpstreamStatus(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.NotFound => 404,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => 502,
            _ when (int)statusCode >= 500 => 502,
            _ => (int)statusCode,
        };
    }

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> tuned for maximum connection reuse.
    /// <para>
    /// TVHeadend is typically a LAN server — long pool lifetimes and generous idle
    /// timeouts maximize TCP/TLS session reuse across bursts of image and stream requests.
    /// </para>
    /// </summary>
    private static SocketsHttpHandler CreateSocketHandler(PluginConfiguration config)
    {
        var handler = new SocketsHttpHandler
        {
            // Long lifetime — TVHeadend is a LAN server, no DNS rotation concerns.
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),

            // Generous idle timeout — logo bursts happen in waves (EPG page open → pause → next page).
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),

            // Allow enough parallel connections for multiple concurrent clients.
            MaxConnectionsPerServer = 20,

            // Keep-alive pings to detect dead connections early on long-running streams.
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),

            // Raw passthrough — no decompression overhead.
            AutomaticDecompression = DecompressionMethods.None,

            // Follow redirects from TVHeadend image proxies.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,

            // No cookie jar needed for TVHeadend.
            UseCookies = false,

            // Pre-connect: enable HTTP/2 multiplexing if TVHeadend supports it.
            EnableMultipleHttp2Connections = true,

            // TCP connect timeout — fail fast if TVHeadend host is unreachable.
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        if (config is { UseSSL: true, IgnoreCertificateErrors: true })
        {
#pragma warning disable CA5359 // User explicitly opted into ignoring certificate errors via plugin config
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            };
#pragma warning restore CA5359
        }

        return handler;
    }

    /// <summary>
    /// Wraps the socket handler with a <see cref="DigestAuthHandler"/> when credentials
    /// are configured. Returns the socket handler directly for anonymous access.
    /// </summary>
    private static HttpMessageHandler WrapWithAuth(SocketsHttpHandler socketHandler, PluginConfiguration config)
    {
        if (config is { AllowAnonymousAccess: false } && !string.IsNullOrWhiteSpace(config.Username))
        {
            return new DigestAuthHandler(config.Username, config.Password ?? string.Empty)
            {
                InnerHandler = socketHandler,
            };
        }

        return socketHandler;
    }

    /// <summary>
    /// Pre-establishes <see cref="WarmupConnectionCount"/> TCP connections to TVHeadend
    /// by sending parallel lightweight GET requests to <c>/api/serverinfo</c>.
    /// <para>
    /// This is fire-and-forget — failures are silently ignored since warmup is best-effort.
    /// Each request opens a separate TCP connection (and TLS session if HTTPS), which then
    /// returns to the <see cref="SocketsHttpHandler"/> pool for reuse by real client requests.
    /// </para>
    /// </summary>
    private async Task WarmConnectionPoolAsync(HttpClient client, PluginConfiguration config)
    {
        var warmupUrl = _urlBuilder.BuildApiUrl(config, "api/serverinfo");

        try
        {
            var tasks = Enumerable.Range(0, WarmupConnectionCount).Select(async _ =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(WarmupTimeout);
                    using var request = new HttpRequestMessage(HttpMethod.Get, warmupUrl);
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort — warmup failures are expected when TVHeadend is not yet reachable.
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            _logger.LogDebug(
                "Connection pool warmup completed — {Count} connections pre-established",
                WarmupConnectionCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection pool warmup failed (best-effort, non-critical)");
        }
    }

    private PluginConfiguration GetConfigOrThrow()
    {
        return _configProvider.Configuration
               ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }
}
