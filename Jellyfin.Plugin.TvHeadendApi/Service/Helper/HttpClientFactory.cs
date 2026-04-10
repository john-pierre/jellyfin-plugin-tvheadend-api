using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Creates HTTP clients configured for TVHeadend API communication.
/// </summary>
internal static class HttpClientFactory
{
    public static HttpClient Create(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var handler = new HttpClientHandler { CheckCertificateRevocationList = true };
        if (config.UseSSL && config.IgnoreCertificateErrors)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }

        var client = new HttpClient(handler);
        if (!config.AllowAnonymousAccess && !string.IsNullOrWhiteSpace(config.Username))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        return client;
    }
}
