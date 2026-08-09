using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;

/// <summary>
/// Represents a channel option for configuration dropdowns.
/// </summary>
/// <param name="Id">TVHeadend channel UUID.</param>
/// <param name="Name">Channel display name.</param>
public sealed record ChannelOption(string Id, string Name);
