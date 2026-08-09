using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// A cached image: the raw bytes plus the original content type (if known).
/// </summary>
/// <param name="Bytes">The image bytes.</param>
/// <param name="ContentType">The original content type, or <c>null</c> if unknown.</param>
internal sealed record CachedImage(byte[] Bytes, string? ContentType);
