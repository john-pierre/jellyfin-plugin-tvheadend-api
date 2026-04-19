using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Infrastructure Tests for ApiClient and HTTP operations.
/// Tests the HTTP abstraction layer and basic client operations.
/// </summary>
public class ApiClientTests
{
    private readonly PluginConfiguration _testConfig = new()
    {
        Host = "tvheadend.test.local",
        Port = 9981,
        UseSSL = false,
        Webroot = "/",
        AllowAnonymousAccess = true
    };

    private static IHttpClientFactory CreateMockFactory(HttpMessageHandler? handler = null)
    {
        var mock = new Mock<IHttpClientFactory>();
        mock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => handler != null ? new HttpClient(handler) : new HttpClient());
        return mock.Object;
    }

    private static PluginConfigurationProvider CreateConfigProvider(PluginConfiguration? config = null)
    {
        return new PluginConfigurationProvider(() => config);
    }

    private static ApiClient CreateApiClient(IHttpClientFactory? factory = null, PluginConfigurationProvider? configProvider = null)
    {
        return new ApiClient(factory ?? CreateMockFactory(), configProvider ?? CreateConfigProvider());
    }

    [Fact]
    public void BuildHttpClient_WithValidConfig_ReturnsHttpClient()
    {
        // Arrange
        var client = CreateApiClient();

        // Act
        var result = client.CreateApiHttpClient(_testConfig);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<HttpClient>(result);
    }

    [Fact]
    public void BuildHttpClient_UsesHttpClientFactory()
    {
        // Arrange
        var factoryMock = new Mock<IHttpClientFactory>();
        var expectedClient = new HttpClient();
        factoryMock.Setup(f => f.CreateClient(ApiClient.HttpClientName)).Returns(expectedClient);
        var client = CreateApiClient(factoryMock.Object);

        // Act
        var result = client.CreateApiHttpClient(_testConfig);

        // Assert
        Assert.Same(expectedClient, result);
        factoryMock.Verify(f => f.CreateClient(ApiClient.HttpClientName), Times.Once);
    }

    [Fact]
    public void BuildHttpClient_WithIgnoreCertificateErrors_UsesUnsafeClient()
    {
        // Arrange
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var client = CreateApiClient(factoryMock.Object);
        var config = new PluginConfiguration { UseSSL = true, IgnoreCertificateErrors = true, AllowAnonymousAccess = true };

        // Act
        client.CreateApiHttpClient(config);

        // Assert
        factoryMock.Verify(f => f.CreateClient(ApiClient.HttpClientUnsafeName), Times.Once);
    }

    [Fact]
    public void BuildHttpClient_WithCredentials_DoesNotSetProactiveAuthHeader()
    {
        // Arrange
        var client = CreateApiClient();
        var config = new PluginConfiguration
        {
            AllowAnonymousAccess = false,
            Username = "admin",
            Password = "secret"
        };

        // Act
        var httpClient = client.CreateApiHttpClient(config);

        // Assert — No proactive Authorization header is set because TVHeadend
        // uses Digest auth by default. A proactive Basic header would prevent
        // HttpClientHandler from responding to the Digest challenge.
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void BuildHttpClient_WithCredentials_ReturnsDistinctClientFromFactory()
    {
        // Arrange — verify authenticated path does NOT use factory
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var client = CreateApiClient(factoryMock.Object);
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            AllowAnonymousAccess = false,
            Username = "admin",
            Password = "secret"
        };

        // Act
        client.CreateApiHttpClient(config);

        // Assert — factory was not called, a dedicated handler was created
        factoryMock.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void BuildHttpClient_WithCredentials_NoProactiveHeader()
    {
        // Arrange
        var client = CreateApiClient();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            AllowAnonymousAccess = false,
            Username = "admin",
            Password = "secret"
        };

        // Act
        var httpClient = client.CreateApiHttpClient(config);

        // Assert — CredentialCache handles auth via challenge-response, no default header
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void BuildHttpClient_WithEmptyUsername_UsesFactoryClient()
    {
        // Arrange — AllowAnonymousAccess=false but username is empty → should fall through to factory
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(ApiClient.HttpClientName)).Returns(new HttpClient());
        var client = CreateApiClient(factoryMock.Object);
        var config = new PluginConfiguration
        {
            AllowAnonymousAccess = false,
            Username = "",
            Password = "secret"
        };

        // Act
        var httpClient = client.CreateApiHttpClient(config);

        // Assert — no proactive auth header, factory was used
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
        factoryMock.Verify(f => f.CreateClient(ApiClient.HttpClientName), Times.Once);
    }

    [Fact]
    public void GetBaseUrl_WithValidConfig_ReturnsUrl()
    {
        // Arrange
        var urlBuilder = new UrlBuilder();

        // Act
        var url = urlBuilder.GetBaseUrl(_testConfig);

        // Assert
        Assert.NotNull(url);
        Assert.NotEmpty(url);
        Assert.StartsWith("http://", url);
    }

    [Fact]
    public void GetBaseUrl_IncludesHostAndPort()
    {
        // Arrange
        var urlBuilder = new UrlBuilder();

        // Act
        var url = urlBuilder.GetBaseUrl(_testConfig);

        // Assert
        Assert.Contains("tvheadend.test.local", url);
        Assert.Contains("9981", url);
    }

    [Fact]
    public void GetWebRoot_ReturnsNormalizedWebroot()
    {
        // Arrange
        var urlBuilder = new UrlBuilder();

        // Act
        var webroot = urlBuilder.GetWebRoot(_testConfig);

        // Assert
        Assert.NotNull(webroot);
        Assert.NotEmpty(webroot);
    }

    [Fact]
    public void BuildUrl_WithEndpoint_ConstructsFullUrl()
    {
        // Arrange
        var urlBuilder = new UrlBuilder();

        // Act
        var url = urlBuilder.BuildApiUrl(_testConfig, "/api/channel/grid");

        // Assert
        Assert.NotNull(url);
        Assert.Contains("tvheadend.test.local", url);
        Assert.Contains("api/channel/grid", url);
    }

    [Fact]
    public async Task GetStringAsync_WithMockedHttpClient_ReturnsString()
    {
        // Arrange
        var client = CreateApiClient();
        var mockHandler = new MockHttpMessageHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"success\"}")
            }
        };
        var httpClient = new HttpClient(mockHandler);
        var cts = new CancellationTokenSource();

        // Act
        var result = await client.GetStringAsync(httpClient, "http://localhost:9981/api/test", cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("result", result);
    }

    [Fact]
    public async Task GetStringAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var client = CreateApiClient();
        var mockHandler = new MockHttpMessageHandler
        {
            Delay = 1000
        };
        var httpClient = new HttpClient(mockHandler);
        var cts = new CancellationTokenSource(100);

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetStringAsync(httpClient, "http://localhost:9981/api/test", cts.Token)
        );
    }

    [Fact]
    public async Task PostFormAsync_WithValidData_ReturnsHttpResponse()
    {
        // Arrange
        var client = CreateApiClient();
        var mockHandler = new MockHttpMessageHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
        };
        var httpClient = new HttpClient(mockHandler);
        var formData = new Dictionary<string, string>
        {
            { "username", "admin" },
            { "password", "secret" }
        };
        var cts = new CancellationTokenSource();

        // Act
        var result = await client.PostFormAsync(httpClient, "http://localhost:9981/api/login", formData, cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task PostFormAsync_WithFormContent_IncludesFormData()
    {
        // Arrange
        var client = CreateApiClient();
        var capturedRequest = false;
        var mockHandler = new MockHttpMessageHandler
        {
            OnRequest = msg =>
            {
                if (msg.Content is FormUrlEncodedContent)
                {
                    capturedRequest = true;
                }
            },
            Response = new HttpResponseMessage(HttpStatusCode.OK)
        };
        var httpClient = new HttpClient(mockHandler);
        var formData = new Dictionary<string, string> { { "key", "value" } };
        var cts = new CancellationTokenSource();

        // Act
        await client.PostFormAsync(httpClient, "http://localhost:9981/api/test", formData, cts.Token);

        // Assert
        Assert.True(capturedRequest);
    }

    [Fact]
    public async Task GetStreamAsync_WithValidResponse_ReturnsStream()
    {
        // Arrange
        var client = CreateApiClient();
        var mockHandler = new MockHttpMessageHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 })
            }
        };
        var httpClient = new HttpClient(mockHandler);
        var cts = new CancellationTokenSource();

        // Act
        var result = await client.GetStreamAsync(httpClient, "http://localhost:9981/api/stream", cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.CanRead);
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public async Task GetStreamAsync_WithFailedResponse_ThrowsHttpRequestException()
    {
        // Arrange
        var client = CreateApiClient();
        var mockHandler = new MockHttpMessageHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        var httpClient = new HttpClient(mockHandler);
        var cts = new CancellationTokenSource();

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetStreamAsync(httpClient, "http://localhost:9981/api/notfound", cts.Token)
        );
    }

    [Fact]
    public void BuildUrl_WithDifferentEndpoints_ReturnsDifferentUrls()
    {
        // Arrange
        var urlBuilder = new UrlBuilder();

        // Act
        var url1 = urlBuilder.BuildApiUrl(_testConfig, "/api/channel/grid");
        var url2 = urlBuilder.BuildApiUrl(_testConfig, "/api/dvr/autorec/grid");

        // Assert
        Assert.NotEqual(url1, url2);
        Assert.Contains("channel/grid", url1);
        Assert.Contains("dvr/autorec/grid", url2);
    }

    [Fact]
    public async Task GetStringAsync_MultipleRequests_BothSucceed()
    {
        // Arrange
        var client = CreateApiClient();
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":\"test\"}")
            }
        };
        var httpClient = new HttpClient(mockHandler);
        var cts = new CancellationTokenSource();

        // Act
        var result1 = await client.GetStringAsync(httpClient, "http://localhost:9981/api/1", cts.Token);
        var result2 = await client.GetStringAsync(httpClient, "http://localhost:9981/api/2", cts.Token);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
    }

    [Fact]
    public void Constructor_NullHttpClientFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ApiClient(null!, CreateConfigProvider()));
    }

    [Fact]
    public void Constructor_NullConfigProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ApiClient(CreateMockFactory(), null!));
    }

    [Fact]
    public void CreateApiHttpClient_NullConfig_Throws()
    {
        var client = CreateApiClient();
        Assert.Throws<ArgumentNullException>(() => client.CreateApiHttpClient(null!));
    }

    [Fact]
    public void GetCurrentConfiguration_ReturnsProviderValue()
    {
        var config = new PluginConfiguration { Host = "test" };
        var client = CreateApiClient(configProvider: CreateConfigProvider(config));
        Assert.Same(config, client.GetCurrentConfiguration());
    }

    [Fact]
    public void GetCurrentConfiguration_WhenNull_ReturnsNull()
    {
        var client = CreateApiClient(configProvider: CreateConfigProvider(null));
        Assert.Null(client.GetCurrentConfiguration());
    }

    [Fact]
    public void CreateApiHttpClient_AuthenticatedWithSslAndIgnoreCerts_ReturnsClient()
    {
        var client = CreateApiClient();
        var config = new PluginConfiguration
        {
            AllowAnonymousAccess = false,
            Username = "admin",
            Password = "secret",
            UseSSL = true,
            IgnoreCertificateErrors = true,
        };

        var httpClient = client.CreateApiHttpClient(config);
        Assert.NotNull(httpClient);
    }

    [Fact]
    public void CreateApiHttpClient_AuthenticatedWithSslNoCertIgnore_ReturnsClient()
    {
        var client = CreateApiClient();
        var config = new PluginConfiguration
        {
            AllowAnonymousAccess = false,
            Username = "admin",
            Password = "secret",
            UseSSL = true,
            IgnoreCertificateErrors = false,
        };

        var httpClient = client.CreateApiHttpClient(config);
        Assert.NotNull(httpClient);
    }

    [Fact]
    public void CreateApiHttpClient_AnonymousWithSslNoIgnore_UsesStandardFactory()
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var client = CreateApiClient(factoryMock.Object);
        var config = new PluginConfiguration { AllowAnonymousAccess = true, UseSSL = true, IgnoreCertificateErrors = false };

        client.CreateApiHttpClient(config);

        factoryMock.Verify(f => f.CreateClient(ApiClient.HttpClientName), Times.Once);
    }

    /// <summary>
    /// Mock HTTP message handler for testing HTTP operations.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage? Response { get; set; }
        public Func<HttpResponseMessage>? ResponseFactory { get; set; }
        public Action<HttpRequestMessage>? OnRequest { get; set; }
        public int Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OnRequest?.Invoke(request);

            if (Delay > 0)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            return ResponseFactory?.Invoke() ?? Response ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
