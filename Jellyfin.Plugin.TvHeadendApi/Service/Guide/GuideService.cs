using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Common;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Guide;

/// <summary>
/// Fetches and maps TVHeadend channel/EPG metadata for Jellyfin.
/// </summary>
internal sealed class GuideService : IGuideService
{
    private static readonly Dictionary<int, string> EtsiGenreMapping = new()
    {
        { 0, "Undefined" },
        { 16, "Movie/Drama" },
        { 17, "Detective/Thriller" },
        { 18, "Adventure/Western/War" },
        { 19, "Science Fiction/Fantasy/Horror" },
        { 20, "Comedy" },
        { 21, "Soap/Melodrama/Drama" },
        { 22, "Romance" },
        { 23, "Serious/Classical/Religious/Historical Movie/Drama" },
        { 24, "Adult Movie/Drama" },
        { 32, "News/Current Affairs" },
        { 33, "News/Weather Report" },
        { 34, "News Magazine" },
        { 35, "Documentary" },
        { 36, "Discussion/Interview/Debate" },
        { 48, "Show/Game Show" },
        { 49, "Game Show/Quiz/Contest" },
        { 50, "Variety Show" },
        { 51, "Talk Show" },
        { 64, "Sports" },
        { 65, "Special Event" },
        { 66, "Sports Magazine" },
        { 67, "Football/Soccer" },
        { 68, "Tennis/Squash" },
        { 69, "Team Sports" },
        { 70, "Athletics" },
        { 71, "Motor Sport" },
        { 72, "Water Sport" },
        { 73, "Winter Sport" },
        { 74, "Equestrian" },
        { 75, "Martial Sports" },
        { 80, "Children's/Youth Programs" },
        { 81, "Pre-school Children's Programs" },
        { 82, "Entertainment/Cartoons" },
        { 83, "Educational/School Programs" },
        { 96, "Music/Ballet/Dance" },
        { 97, "Rock/Pop" },
        { 98, "Classical Music" },
        { 99, "Folk/Traditional Music" },
        { 100, "Jazz" },
        { 101, "Opera" },
        { 102, "Ballet" },
        { 112, "Arts/Culture (without music)" },
        { 113, "Performing Arts" },
        { 114, "Fine Arts" },
        { 115, "Religion" },
        { 116, "Popular Culture/Tradition" },
        { 117, "Literature" },
        { 118, "Film/Cinema" },
        { 119, "Experimental Film/Video" },
        { 120, "Broadcasting/Press" },
        { 121, "New Media" },
        { 122, "Arts/Culture Magazine" },
        { 123, "Fashion" },
        { 128, "Social/Political/Economic" },
        { 129, "Magazines/Reports/Documentary" },
        { 130, "Economics/Social Advisory" },
        { 131, "Remarkable People" },
        { 144, "Education/Science/Factual" },
        { 145, "Nature/Animals/Environment" },
        { 146, "Technology/Medical" },
        { 147, "Foreign Countries/Expeditions" },
        { 148, "Social/Spiritual Sciences" },
        { 149, "Further Education" },
        { 150, "Languages" },
        { 160, "Leisure Hobbies" },
        { 161, "Tourism/Travel" },
        { 162, "Handicraft" },
        { 163, "Gardening" },
        { 164, "Motors" },
        { 165, "Fitness/Health" },
        { 166, "Cooking" },
        { 167, "Advertisement/Shopping" },
        { 168, "Community" },
        { 240, "Reserved for future use" },
    };

    private readonly ILogger<GuideService> _logger;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;
    private readonly Relay.IRelayUrlBuilder _relayUrlBuilder;
    private readonly IHealthService _healthService;
    private readonly ChannelNameCache? _channelNameCache;

    public GuideService(
        ILogger<GuideService> logger,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder,
        Relay.IRelayUrlBuilder relayUrlBuilder,
        IHealthService healthService,
        ChannelNameCache? channelNameCache = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
        _relayUrlBuilder = relayUrlBuilder ?? throw new ArgumentNullException(nameof(relayUrlBuilder));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _channelNameCache = channelNameCache;
    }

    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        if (_healthService.ShouldBlockRequest())
        {
            _logger.LogWarning("Circuit breaker open — skipping channel fetch");
            return Enumerable.Empty<ChannelInfo>();
        }

        try
        {
            var config = GetConfig();
            var url = _tvheadendUrlBuilder.BuildApiUrl(config, "api/channel/grid");
            _logger.LogDebug("Fetching channels from TVHeadEnd at {Url}...", _tvheadendUrlBuilder.MaskSensitiveData(url, config));

            using var httpClient = _tvheadendApiClient.CreateApiHttpClient(config);
            var result = await GridFetcher.FetchAllAsync<ChannelGridResponse>(
                httpClient,
                url,
                r => r.Total,
                _logger,
                cancellationToken).ConfigureAwait(false);
            if (result?.Entries == null || !result.Entries.Any())
            {
                _logger.LogWarning("No channels retrieved from TVHeadEnd.");
                return Enumerable.Empty<ChannelInfo>();
            }

            var channelTagNames = await GetChannelTagsAsync(cancellationToken).ConfigureAwait(false);

            // Bouquet names are fetched lazily: TVHeadend's channel grid only carries the
            // bouquet's opaque idnode UUID, so an api/bouquet/grid lookup is required to get
            // a display name — but most setups have no bouquets at all, so skip the extra
            // round trip unless an enabled channel actually references one.
            Dictionary<string, string>? bouquetNames = null;

            var channels = new List<ChannelInfo>();
            foreach (var channel in result.Entries.Where(ch => ch.Enabled))
            {
                var resolvedTags = (channel.Tags ?? Array.Empty<string>())
                    .Where(tagId => !string.IsNullOrWhiteSpace(tagId))
                    .Select(tagId => channelTagNames.TryGetValue(tagId, out var tagName) && !string.IsNullOrWhiteSpace(tagName)
                        ? tagName
                        : tagId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                string? channelGroup;
                if (!string.IsNullOrWhiteSpace(channel.Bouquet))
                {
                    // Resolve the bouquet UUID to its human-readable name. When the lookup
                    // fails, fall back to the first tag name instead of surfacing a raw UUID —
                    // the value is user-visible and matched against admin-configured channel
                    // group overrides, so an opaque UUID would be useless in both places.
                    bouquetNames ??= await GetBouquetNamesAsync(cancellationToken).ConfigureAwait(false);
                    channelGroup = bouquetNames.TryGetValue(channel.Bouquet, out var bouquetName) && !string.IsNullOrWhiteSpace(bouquetName)
                        ? bouquetName
                        : resolvedTags.FirstOrDefault();
                }
                else
                {
                    channelGroup = resolvedTags.FirstOrDefault();
                }

                string? imageUrl = null;
                if (!string.IsNullOrWhiteSpace(channel.IconPublicUrl))
                {
                    // Shares the EPG-image logic: external absolute URLs pass through unchanged
                    // (relaying them through the TVHeadend base URL produces a broken address),
                    // TVHeadend-relative paths go through the token-secured relay.
                    imageUrl = await ResolveTvhImageUrlAsync(
                        config,
                        channel.IconPublicUrl,
                        cancellationToken,
                        Model.Relay.MediaKind.Logo).ConfigureAwait(false);
                }

                _channelNameCache?.Set(channel.Uuid, channel.Name);
                channels.Add(new ChannelInfo
                {
                    Id = channel.Uuid,
                    Name = channel.Name,
                    Number = FormatChannelNumber(channel.Number),
                    ImageUrl = imageUrl,
                    HasImage = !string.IsNullOrWhiteSpace(channel.IconPublicUrl),
                    Tags = resolvedTags,
                    ChannelGroup = channelGroup,
                });
            }

            return channels;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching channels from TVHeadEnd.");
            return Enumerable.Empty<ChannelInfo>();
        }
    }

    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        if (_healthService.ShouldBlockRequest())
        {
            _logger.LogWarning("Circuit breaker open — skipping EPG fetch for channel {ChannelId}", channelId);
            return Enumerable.Empty<ProgramInfo>();
        }

        try
        {
            var config = GetConfig();
            var encodedChannelId = Uri.EscapeDataString(channelId);

            // Pass server-side time-window filter to TVHeadend to reduce payload size.
            // TVH filter: stop > startDateUtc AND start < endDateUtc (overlap condition).
            var startUnix = new DateTimeOffset(startDateUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            var endUnix = new DateTimeOffset(endDateUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            var filterJson = "[{\"field\":\"stop\",\"type\":\"numeric\",\"value\":" + startUnix
                + ",\"comparison\":\"gt\"},{\"field\":\"start\",\"type\":\"numeric\",\"value\":" + endUnix
                + ",\"comparison\":\"lt\"}]";
            var encodedFilter = Uri.EscapeDataString(filterJson);
            var url = _tvheadendUrlBuilder.BuildApiUrl(config, $"api/epg/events/grid?channel={encodedChannelId}&filter={encodedFilter}");
            using var httpClient = _tvheadendApiClient.CreateApiHttpClient(config);
            var result = await GridFetcher.FetchAllAsync<EpgEventsGridResponse>(
                httpClient,
                url,
                r => r.TotalCount,
                _logger,
                cancellationToken).ConfigureAwait(false);
            var filteredEntries = result?.Entries?
                .Where(entry =>
                {
                    var programStart = DateTimeOffset.FromUnixTimeSeconds(entry.Start).UtcDateTime;
                    var programEnd = DateTimeOffset.FromUnixTimeSeconds(entry.Stop).UtcDateTime;
                    return entry.ChannelUuid == channelId && programStart < endDateUtc && programEnd > startDateUtc;
                })
                .ToList();

            if (filteredEntries == null || filteredEntries.Count == 0)
            {
                return Enumerable.Empty<ProgramInfo>();
            }

            var programs = new List<ProgramInfo>(filteredEntries.Count);
            foreach (var entry in filteredEntries)
            {
                string? imageUrl = null;
                if (!string.IsNullOrWhiteSpace(entry.Image))
                {
                    imageUrl = await ResolveTvhImageUrlAsync(config, entry.Image, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(entry.ChannelIcon))
                {
                    imageUrl = await ResolveTvhImageUrlAsync(config, entry.ChannelIcon, cancellationToken).ConfigureAwait(false);
                }

                var ratingLabelIconUrl = string.IsNullOrWhiteSpace(entry.RatingLabelIcon)
                    ? null
                    : await ResolveTvhImageUrlAsync(config, entry.RatingLabelIcon, cancellationToken).ConfigureAwait(false);

                var providerHints = config.EnableJellyfinMetadataEnrichment
                    ? BuildProviderHints(entry)
                    : null;

                var categories = entry.Category ?? Array.Empty<string>();
                var isPremiere = HasCategoryFlag(categories, "premiere", "first run", "new");
                var isLive = HasCategoryFlag(categories, "live");
                var isRepeat = !isPremiere && (entry.IsNew == 0 || HasCategoryFlag(categories, "repeat", "rerun", "wiederholung"));

                DateTime? originalAirDate = null;
                if (entry.FirstAired.HasValue && entry.FirstAired.Value > 0)
                {
                    originalAirDate = DateTimeOffset.FromUnixTimeSeconds(entry.FirstAired.Value).UtcDateTime;
                }

                programs.Add(new ProgramInfo
                {
                    Id = entry.EventId.ToString(CultureInfo.InvariantCulture),
                    ChannelId = entry.ChannelUuid,
                    Name = entry.Title,
                    Overview = entry.Description,
                    ShortOverview = entry.Summary,
                    StartDate = DateTimeOffset.FromUnixTimeSeconds(entry.Start).UtcDateTime,
                    EndDate = DateTimeOffset.FromUnixTimeSeconds(entry.Stop).UtcDateTime,
                    Genres = entry.Genre?.Select(genreId => EtsiGenreMapping.TryGetValue(genreId, out var genreDescription) ? genreDescription : $"Unknown ({genreId})").ToList() ?? new List<string>(),
                    IsHD = entry.IsHD == 1,
                    EpisodeTitle = entry.Subtitle,
                    OfficialRating = entry.RatingLabel,
                    ImageUrl = imageUrl,
                    LogoImageUrl = ratingLabelIconUrl,
                    HasImage = !string.IsNullOrEmpty(imageUrl),
                    IsMovie = entry.Genre?.Any(genreId => genreId >= 16 && genreId <= 24) ?? false,
                    IsSports = entry.Genre?.Any(genreId => genreId >= 64 && genreId <= 75) ?? false,
                    IsNews = entry.Genre?.Any(genreId => genreId >= 32 && genreId <= 36) ?? false,
                    IsKids = entry.Genre?.Any(genreId => genreId >= 80 && genreId <= 83) ?? false,
                    IsEducational = entry.Genre?.Any(genreId => genreId >= 144 && genreId <= 150) ?? false,
                    IsLive = isLive,
                    IsPremiere = isPremiere,
                    IsRepeat = isRepeat,
                    OriginalAirDate = originalAirDate,
                    Audio = MapProgramAudio(entry.Stereo),
                    IsSeries = !string.IsNullOrWhiteSpace(entry.SerieslinkUri)
                        || (entry.Genre?.Any(genreId => genreId >= 48 && genreId <= 51) ?? false),
                    SeriesId = string.IsNullOrWhiteSpace(entry.SerieslinkUri) ? null : entry.SerieslinkUri,
                    ShowId = string.IsNullOrWhiteSpace(entry.SerieslinkUri) ? null : entry.SerieslinkUri,
                    HomePageUrl = TryGetHttpUrl(entry.EpisodeUri),
                    ProviderIds = providerHints?.ProviderIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    SeriesProviderIds = providerHints?.SeriesProviderIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ProductionYear = entry.CopyrightYear > 0 ? entry.CopyrightYear : null,
                });
            }

            return programs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching EPG data for channel ID: {ChannelId}.", channelId);
            return Enumerable.Empty<ProgramInfo>();
        }
    }

    public async Task<Dictionary<int, string>> GetContentTypesAsync(CancellationToken cancellationToken)
    {
        if (_healthService.ShouldBlockRequest())
        {
            _logger.LogWarning("Circuit breaker open — skipping content types fetch");
            return new Dictionary<int, string>();
        }

        try
        {
            var config = GetConfig();
            var url = _tvheadendUrlBuilder.BuildApiUrl(config, "api/epg/content_type/list");
            using var httpClient = _tvheadendApiClient.CreateApiHttpClient(config);
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<int, string>();
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<EpgContentTypeListResponse>(stream, JsonDefaults.Api, cancellationToken).ConfigureAwait(false);
            return result?.Entries?.ToDictionary(entry => entry.Key, entry => entry.Val) ?? new Dictionary<int, string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching content types from TVHeadend.");
            return new Dictionary<int, string>();
        }
    }

    public async Task<Dictionary<string, string>> GetChannelTagsAsync(CancellationToken cancellationToken)
    {
        if (_healthService.ShouldBlockRequest())
        {
            _logger.LogWarning("Circuit breaker open — skipping channel tags fetch");
            return new Dictionary<string, string>();
        }

        try
        {
            var config = GetConfig();
            var url = _tvheadendUrlBuilder.BuildApiUrl(config, "api/channeltag/list");
            using var httpClient = _tvheadendApiClient.CreateApiHttpClient(config);
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<string, string>();
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<ChannelTagResponse>(stream, JsonDefaults.Api, cancellationToken).ConfigureAwait(false);
            return result?.Entries?.ToDictionary(entry => entry.Key, entry => entry.Val) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching channel tags from TVHeadend.");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Fetches the bouquet UUID → display-name mapping from TVHeadend's
    /// <c>api/bouquet/grid</c> endpoint. The channel grid's <c>bouquet</c> field only
    /// carries the bouquet's idnode UUID, so this lookup is needed to present a
    /// human-readable channel group. Failures degrade to an empty mapping.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping bouquet UUIDs to their display names.</returns>
    private async Task<Dictionary<string, string>> GetBouquetNamesAsync(CancellationToken cancellationToken)
    {
        var bouquetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var config = GetConfig();
            var url = _tvheadendUrlBuilder.BuildApiUrl(config, "api/bouquet/grid");
            using var httpClient = _tvheadendApiClient.CreateApiHttpClient(config);
            var result = await GridFetcher.FetchAllAsync<BouquetGridResponse>(
                httpClient,
                url,
                r => r.Total,
                _logger,
                cancellationToken).ConfigureAwait(false);
            if (result?.Entries == null)
            {
                return bouquetNames;
            }

            foreach (var entry in result.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Uuid) && !string.IsNullOrWhiteSpace(entry.Name))
                {
                    bouquetNames[entry.Uuid] = entry.Name;
                }
            }

            return bouquetNames;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error occurred while fetching bouquets from TVHeadend — channel groups fall back to tag names.");
            return bouquetNames;
        }
    }

    private PluginConfiguration GetConfig()
    {
        return _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }

    /// <summary>
    /// Formats the TVHeadend channel number for Jellyfin.
    /// <para>
    /// TVHeadend serializes channel numbers in two ways:
    /// <list type="bullet">
    ///   <item>Whole numbers → JSON integer (e.g. <c>10</c>)</item>
    ///   <item>Major.minor → JSON string (e.g. <c>"7.1"</c>)</item>
    /// </list>
    /// Both are passed through as-is since TVHeadend already formats them correctly.
    /// </para>
    /// </summary>
    /// <param name="numberElement">The raw JSON element from the TVHeadend channel grid <c>number</c> field.</param>
    /// <returns>The formatted channel number string (e.g. <c>"10"</c> or <c>"7.1"</c>), or <c>"0"</c> if unavailable.</returns>
    internal static string FormatChannelNumber(JsonElement numberElement)
    {
        if (numberElement.ValueKind == JsonValueKind.Number)
        {
            return numberElement.GetInt64().ToString(CultureInfo.InvariantCulture);
        }

        if (numberElement.ValueKind == JsonValueKind.String)
        {
            var str = numberElement.GetString();
            return !string.IsNullOrWhiteSpace(str) ? str : "0";
        }

        return "0";
    }

    private async Task<string> ResolveTvhImageUrlAsync(PluginConfiguration config, string rawImagePath, CancellationToken cancellationToken, Model.Relay.MediaKind? mediaKind = null)
    {
        var raw = rawImagePath.Trim();
        var normalized = raw.TrimStart('/');

        // External absolute HTTP(S) URLs are passed through unchanged — they don't
        // need relaying because they don't point to TVHeadend's internal API.
        // Note: We check for http/https scheme explicitly instead of using UriKind.Absolute
        // because on Linux, paths starting with "/" (e.g. "/imagecache/123") are treated as
        // absolute file URIs by Uri.TryCreate, which would incorrectly skip the relay.
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return raw;
        }

        // All TVHeadend-relative image paths are routed through the token-secured relay endpoint
        // so that TVHeadend credentials stay server-side and Jellyfin's ProviderManager can
        // fetch the image without Jellyfin auth headers (the token provides authorization).
        return await _relayUrlBuilder.BuildTokenizedImageRelayUrlAsync(
            normalized,
            mediaKind,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private static ProviderHintSet BuildProviderHints(EpgEventsGridEntry entry)
    {
        var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seriesProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        TryAddKnownProviderIds(providerIds, entry.EpisodeUri);
        TryAddKnownProviderIds(seriesProviderIds, entry.SerieslinkUri);

        return new ProviderHintSet(providerIds, seriesProviderIds);
    }

    private static void TryAddKnownProviderIds(Dictionary<string, string> target, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var text = source.Trim();

        // IMDb ID (tt1234567+)
        var imdbIdx = text.IndexOf("tt", StringComparison.OrdinalIgnoreCase);
        if (imdbIdx >= 0)
        {
            var end = imdbIdx + 2;
            while (end < text.Length && char.IsDigit(text[end]))
            {
                end++;
            }

            var candidate = text.Substring(imdbIdx, end - imdbIdx);
            if (candidate.Length >= 9)
            {
                target["Imdb"] = candidate;
            }
        }

        // TMDb hints: tmdb://movie/123 or tmdb://tv/123
        const string tmdbMoviePrefix = "tmdb://movie/";
        const string tmdbTvPrefix = "tmdb://tv/";
        if (text.StartsWith(tmdbMoviePrefix, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(tmdbTvPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var idPart = text.Substring(text.LastIndexOf('/') + 1);
            if (idPart.All(char.IsDigit))
            {
                target["Tmdb"] = idPart;
            }
        }

        // TVDb hints: tvdb://series/123 or thetvdb.com/.../123
        const string tvdbPrefix = "tvdb://";
        if (text.StartsWith(tvdbPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var idPart = text.Substring(text.LastIndexOf('/') + 1);
            if (idPart.All(char.IsDigit))
            {
                target["Tvdb"] = idPart;
            }
        }
    }

    private static ProgramAudio? MapProgramAudio(int? stereoMode)
    {
        return stereoMode switch
        {
            1 => ProgramAudio.Mono,
            2 or 3 => ProgramAudio.Stereo,
            >= 4 => ProgramAudio.DolbyDigital,
            _ => null,
        };
    }

    /// <summary>
    /// Returns the candidate when it is an absolute http/https URL; otherwise <c>null</c>.
    /// EPG providers frequently populate episode URIs with DVB CRIDs
    /// (e.g. <c>crid://www.channel4.com/41408/013</c>) which parse as absolute URIs but
    /// have no browser protocol handler — surfacing them as a homepage link would give
    /// users a dead link instead of no link at all.
    /// </summary>
    /// <param name="candidate">Raw URI string from TVHeadend, may be <c>null</c>.</param>
    /// <returns>The normalized http/https URL, or <c>null</c> when not a web URL.</returns>
    private static string? TryGetHttpUrl(string? candidate)
    {
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    /// <summary>
    /// Tests whether any category contains one of the patterns as a whole word.
    /// Whole-word matching matters: a raw substring test flagged the standard
    /// category "News" as a premiere because it contains "new".
    /// Internal for unit testing.
    /// </summary>
    /// <param name="categories">The EPG category strings of the programme.</param>
    /// <param name="patterns">The whole-word patterns to look for.</param>
    /// <returns><c>true</c> when any category contains any pattern as a whole word.</returns>
    internal static bool HasCategoryFlag(IEnumerable<string> categories, params string[] patterns)
    {
        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            var normalized = category.Trim();
            foreach (var pattern in patterns)
            {
                if (ContainsWholeWord(normalized, pattern))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Case-insensitive whole-word search: the match must not be bordered by letters or digits.
    /// </summary>
    private static bool ContainsWholeWord(string text, string pattern)
    {
        var index = 0;
        while (index <= text.Length - pattern.Length
               && (index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var startsAtBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var endIndex = index + pattern.Length;
            var endsAtBoundary = endIndex >= text.Length || !char.IsLetterOrDigit(text[endIndex]);
            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private sealed record ProviderHintSet(
        Dictionary<string, string> ProviderIds,
        Dictionary<string, string> SeriesProviderIds);

    /// <summary>
    /// Response model for the TVHeadend <c>api/bouquet/grid</c> endpoint.
    /// Kept private to this service — it is only used to resolve bouquet display names.
    /// </summary>
    private sealed class BouquetGridResponse
    {
        /// <summary>
        /// Gets the bouquet entries returned by the grid endpoint.
        /// </summary>
        public IReadOnlyList<BouquetGridEntry> Entries { get; init; } = new List<BouquetGridEntry>();

        /// <summary>
        /// Gets the total number of bouquets available on the server.
        /// </summary>
        public int Total { get; init; }
    }

    /// <summary>
    /// A single bouquet entry from the TVHeadend <c>api/bouquet/grid</c> endpoint.
    /// </summary>
    private sealed class BouquetGridEntry
    {
        /// <summary>
        /// Gets the bouquet's idnode UUID (matches the channel grid's <c>bouquet</c> field).
        /// </summary>
        public string Uuid { get; init; } = string.Empty;

        /// <summary>
        /// Gets the bouquet's human-readable display name.
        /// </summary>
        public string Name { get; init; } = string.Empty;
    }
}
