using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Backend;

/// <summary>
/// Provides centralized HTTP client creation and request execution for TVHeadend API calls.
/// <para>
/// Anonymous requests use factory-managed clients (connection pooling, resilience).
/// Authenticated requests share a cached handler chain (<see cref="ResilienceHandler"/> →
/// <see cref="DigestAuthHandler"/> → <see cref="HttpClientHandler"/>) keyed by the connection
/// settings, so the TCP connection pool, the Digest session, and the circuit-breaker state
/// survive across the per-operation <see cref="HttpClient"/> instances that callers dispose.
/// The handler is created intentionally — no casting of factory-internal handlers —
/// so this remains safe under .NET 9 where <c>SocketsHttpHandler</c> is the default.
/// </para>
/// </summary>
internal sealed class ApiClient : IApiClient
{
    /// <summary>
    /// Named HTTP client for standard TVHeadend connections with certificate revocation checks.
    /// </summary>
    internal const string HttpClientName = "TvHeadend";

    /// <summary>
    /// Named HTTP client for TVHeadend connections that skip certificate validation (self-signed certs).
    /// </summary>
    internal const string HttpClientUnsafeName = "TvHeadendUnsafe";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigurationProvider _configProvider;
    private readonly Func<IHealthService?>? _healthServiceFactory;
    private readonly object _authChainLock = new();
    private ResilienceHandler? _authChain;
    private string? _authChainFingerprint;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating managed client instances.</param>
    /// <param name="configProvider">Provider for the current plugin configuration.</param>
    /// <param name="healthServiceFactory">
    /// Deferred accessor for the central health service (deferred because <see cref="IHealthService"/>
    /// itself depends on <see cref="IApiClient"/>). Optional for tests.
    /// </param>
    public ApiClient(
        IHttpClientFactory httpClientFactory,
        ConfigurationProvider configProvider,
        Func<IHealthService?>? healthServiceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(configProvider);
        _httpClientFactory = httpClientFactory;
        _configProvider = configProvider;
        _healthServiceFactory = healthServiceFactory;
    }

    /// <inheritdoc />
    public PluginConfiguration? GetCurrentConfiguration()
    {
        return _configProvider.Configuration;
    }

    /// <inheritdoc />
    public HttpClient CreateApiHttpClient(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // When credentials are configured, use the shared authenticated handler chain with
        // DigestAuthHandler so .NET-unsupported Digest auth works against TVHeadend.
        // This bypasses IHttpClientFactory because the factory-registered handlers
        // cannot accept per-request credentials, and casting factory handlers to
        // HttpClientHandler is unsafe in .NET 9 where SocketsHttpHandler is the default.
        if (!config.AllowAnonymousAccess && !string.IsNullOrWhiteSpace(config.Username))
        {
            return CreateAuthenticatedClient(config);
        }

        // Anonymous access — use the factory-managed client (connection pooling, resilience handler).
        var clientName = config.UseSSL && config.IgnoreCertificateErrors
            ? HttpClientUnsafeName
            : HttpClientName;

        return _httpClientFactory.CreateClient(clientName);
    }

    /// <inheritdoc />
    public Task<string> GetStringAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        return httpClient.GetStringAsync(url, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> PostFormAsync(
        HttpClient httpClient,
        string url,
        IEnumerable<KeyValuePair<string, string>> formValues,
        CancellationToken cancellationToken)
    {
        // FormUrlEncodedContent is IDisposable — dispose after the request completes.
        using var content = new FormUrlEncodedContent(formValues);
        return await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns an authenticated <see cref="HttpClient"/> backed by the shared handler chain.
    /// <para>
    /// .NET's <see cref="SocketsHttpHandler"/> (default since .NET Core) does not support
    /// HTTP Digest Authentication. TVHeadend defaults to Digest auth, so a custom
    /// <see cref="DigestAuthHandler"/> intercepts 401 challenges and computes the Digest
    /// response. The handler chain is: <c>ResilienceHandler → DigestAuthHandler → HttpClientHandler</c>.
    /// The chain is cached and shared across calls: callers dispose their per-operation
    /// <see cref="HttpClient"/> (created with <c>disposeHandler: false</c>) while the pooled
    /// connections, the Digest session, and the circuit-breaker state stay alive. A new chain
    /// is only built when the connection-relevant configuration changes.
    /// </para>
    /// </summary>
    private HttpClient CreateAuthenticatedClient(PluginConfiguration config)
    {
        const char Sep = '\u001f';
        var fingerprint = FormattableString.Invariant(
            $"{config.Host}{Sep}{config.Port}{Sep}{config.UseSSL}{Sep}{config.IgnoreCertificateErrors}{Sep}{config.Username}{Sep}{config.Password}");

        ResilienceHandler chain;
        lock (_authChainLock)
        {
            if (_authChain is null || !string.Equals(_authChainFingerprint, fingerprint, StringComparison.Ordinal))
            {
                // The previous chain (if any) is intentionally not disposed: in-flight requests
                // may still be using it. Replaced chains are reclaimed once their pooled
                // connections idle out; chains only change when connection settings change.
                _authChain = BuildAuthenticatedChain(config);
                _authChainFingerprint = fingerprint;
            }

            chain = _authChain;
        }

        return new HttpClient(chain, disposeHandler: false);
    }

    private ResilienceHandler BuildAuthenticatedChain(PluginConfiguration config)
    {
#pragma warning disable CA5400 // User explicitly opted into ignoring certificate errors via config
        var innerHandler = new HttpClientHandler
        {
            CheckCertificateRevocationList = true,
        };

        if (config.UseSSL && config.IgnoreCertificateErrors)
        {
            innerHandler.CheckCertificateRevocationList = false;
            innerHandler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }
#pragma warning restore CA5400

        // DigestAuthHandler handles 401 Digest challenges from TVHeadend.
        var digestHandler = new DigestAuthHandler(config.Username ?? string.Empty, config.Password ?? string.Empty)
        {
            InnerHandler = innerHandler,
        };

        // Shared ResilienceHandler: cumulative circuit-breaker state and central health reporting,
        // matching the factory-managed clients.
        return new ResilienceHandler
        {
            InnerHandler = digestHandler,
            HealthService = _healthServiceFactory?.Invoke(),
        };
    }
}
