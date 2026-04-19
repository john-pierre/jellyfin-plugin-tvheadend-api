using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Provides centralized HTTP client creation and request execution for TVHeadend API calls.
/// <para>
/// Anonymous requests use factory-managed clients (connection pooling, resilience).
/// Authenticated requests create an explicit <see cref="HttpClientHandler"/> with
/// <see cref="CredentialCache"/> for Basic+Digest support, wrapped in a
/// <see cref="ResilienceHandler"/> for retry and circuit-breaker behavior.
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
    private readonly PluginConfigurationProvider _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating managed client instances.</param>
    /// <param name="configProvider">Provider for the current plugin configuration.</param>
    public ApiClient(IHttpClientFactory httpClientFactory, PluginConfigurationProvider configProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(configProvider);
        _httpClientFactory = httpClientFactory;
        _configProvider = configProvider;
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

        // When credentials are configured, create a dedicated HttpClientHandler with
        // CredentialCache so .NET automatically responds to the server's 401 challenge
        // with the correct auth scheme (Basic, Digest, or both).
        // The handler is wrapped in a ResilienceHandler for retry/circuit-breaker parity
        // with factory-managed clients.
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
    public Task<HttpResponseMessage> PostFormAsync(
        HttpClient httpClient,
        string url,
        IEnumerable<KeyValuePair<string, string>> formValues,
        CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(formValues);
        return httpClient.PostAsync(url, content, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<global::System.IO.Stream> GetStreamAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var target = new MemoryStream();
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        target.Position = 0;
        return target;
    }

    /// <summary>
    /// Creates an authenticated <see cref="HttpClient"/> with Digest and Basic auth support.
    /// <para>
    /// .NET's <see cref="SocketsHttpHandler"/> (default since .NET Core) does not support
    /// HTTP Digest Authentication. TVHeadend defaults to Digest auth, so a custom
    /// <see cref="DigestAuthHandler"/> intercepts 401 challenges and computes the Digest
    /// response. The handler chain is: <c>ResilienceHandler → DigestAuthHandler → HttpClientHandler</c>.
    /// </para>
    /// </summary>
    private static HttpClient CreateAuthenticatedClient(PluginConfiguration config)
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

        // Wrap in ResilienceHandler for retry + circuit-breaker parity with factory-managed clients.
        var resilienceHandler = new ResilienceHandler { InnerHandler = digestHandler };
        return new HttpClient(resilienceHandler);
    }
}
