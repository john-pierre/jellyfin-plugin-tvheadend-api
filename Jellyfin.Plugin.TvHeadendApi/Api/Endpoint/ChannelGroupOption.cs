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
/// Represents a channel group/tag option for configuration dropdowns.
/// </summary>
/// <param name="Id">TVHeadend tag UUID.</param>
/// <param name="Name">Tag display name.</param>
public sealed record ChannelGroupOption(string Id, string Name);
