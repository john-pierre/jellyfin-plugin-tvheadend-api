using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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

    [Fact]
    public void BuildHttpClient_WithValidConfig_ReturnsHttpClient()
    {
        // Arrange
        var client = new ApiClient(CreateMockFactory());

        // Act
        var result = client.BuildHttpClient(_testConfig);

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
        var client = new ApiClient(factoryMock.Object);

        // Act
        var result = client.BuildHttpClient(_testConfig);

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
        var client = new ApiClient(factoryMock.Object);
        var config = new PluginConfiguration { UseSSL = true, IgnoreCertificateErrors = true, AllowAnonymousAccess = true };

        // Act
        client.BuildHttpClient(config);

        // Assert
        factoryMock.Verify(f => f.CreateClient(ApiClient.HttpClientUnsafeName), Times.Once);
    }

    [Fact]
    public void BuildHttpClient_WithCredentials_SetsAuthorizationHeader()
    {
        // Arrange
        var client = new ApiClient(CreateMockFactory());
        var config = new PluginConfiguration
        {
            AllowAnonymousAccess = false,
            Username = "admin",
            Password = "secret"
        };

        // Act
        var httpClient = client.BuildHttpClient(config);

        // Assert
        Assert.NotNull(httpClient.DefaultRequestHeaders.Authorization);
        Assert.Equal("Basic", httpClient.DefaultRequestHeaders.Authorization.Scheme);
    }

    [Fact]
    public void GetBaseUrl_WithValidConfig_ReturnsUrl()
    {
        // Arrange
        var client = new ApiClient(CreateMockFactory());

        // Act
        var url = client.GetBaseUrl(_testConfig);

        // Assert
        Assert.NotNull(url);
        Assert.NotEmpty(url);
        Assert.StartsWith("http://", url);
    }

    [Fact]
    public void GetBaseUrl_IncludesHostAndPort()
    {
        // Arrange
        var client = new ApiClient(CreateMockFactory());

        // Act
        var url = client.GetBaseUrl(_testConfig);

        // Assert
        Assert.Contains("tvheadend.test.local", url);
        Assert.Contains("9981", url);
    }

    [Fact]
    public void GetWebRoot_ReturnsNormalizedWebroot()
    {
        // Arrange
        var client = new ApiClient(CreateMockFactory());

        // Act
        var webroot = client.GetWebRoot(_testConfig);

        // Assert
        Assert.NotNull(webroot);
        Assert.NotEmpty(webroot);
    }

    [Fact]
    public void BuildUrl_WithEndpoint_ConstructsFullUrl()
    {
        // Arrange
        var client = new ApiClient(CreateMockFactory());

        // Act
        var url = client.BuildUrl(_testConfig, "/api/channel/grid");

        // Assert
        Assert.NotNull(url);
        Assert.Contains("tvheadend.test.local", url);
        Assert.Contains("api/channel/grid", url);
    }

    [Fact]
    public async Task GetStringAsync_WithMockedHttpClient_ReturnsString()
    {
        // Arrange
        var client = new ApiClient(CreateMockFactory());
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
        var client = new ApiClient(CreateMockFactory());
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
        var client = new ApiClient(CreateMockFactory());
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
        var client = new ApiClient(CreateMockFactory());
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
        var client = new ApiClient(CreateMockFactory());
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
        var client = new ApiClient(CreateMockFactory());
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
        var client = new ApiClient(CreateMockFactory());

        // Act
        var url1 = client.BuildUrl(_testConfig, "/api/channel/grid");
        var url2 = client.BuildUrl(_testConfig, "/api/dvr/autorec/grid");

        // Assert
        Assert.NotEqual(url1, url2);
        Assert.Contains("channel/grid", url1);
        Assert.Contains("dvr/autorec/grid", url2);
    }

    [Fact]
    public async Task GetStringAsync_MultipleRequests_BothSucceed()
    {
        // Arrange
        var client = new ApiClient(CreateMockFactory());
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
