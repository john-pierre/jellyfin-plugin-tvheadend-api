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
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
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

    public GuideService(
        ILogger<GuideService> logger,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
    }

    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = GetConfig();
            var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/channel/grid");
            _logger.LogDebug("Fetching channels from TVHeadEnd at {Url}...", _tvheadendUrlBuilder.MaskSensitiveData(url, config));

            using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
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

            return result.Entries
                .Where(channel => channel.Enabled)
                .Select(channel =>
                {
                    var resolvedTags = (channel.Tags ?? Array.Empty<string>())
                        .Where(tagId => !string.IsNullOrWhiteSpace(tagId))
                        .Select(tagId => channelTagNames.TryGetValue(tagId, out var tagName) && !string.IsNullOrWhiteSpace(tagName)
                            ? tagName
                            : tagId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    var channelGroup = !string.IsNullOrWhiteSpace(channel.Bouquet)
                        ? channel.Bouquet
                        : resolvedTags.FirstOrDefault();

                    return new ChannelInfo
                    {
                        Id = channel.Uuid,
                        Name = channel.Name,
                        Number = FormatChannelNumber(channel.Number),
                        ImageUrl = !string.IsNullOrWhiteSpace(channel.IconPublicUrl)
                            ? _tvheadendUrlBuilder.BuildUrlWithParameterAuth(config, channel.IconPublicUrl.TrimStart('/'))
                            : null,
                        HasImage = !string.IsNullOrWhiteSpace(channel.IconPublicUrl),
                        Tags = resolvedTags,
                        ChannelGroup = channelGroup,
                    };
                })
                .ToList();
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
            var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, $"api/epg/events/grid?channel={encodedChannelId}&filter={encodedFilter}");
            using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
            var result = await GridFetcher.FetchAllAsync<EpgEventsGridResponse>(
                httpClient,
                url,
                r => r.TotalCount,
                _logger,
                cancellationToken).ConfigureAwait(false);
            return result?.Entries?
                .Where(entry =>
                {
                    var programStart = DateTimeOffset.FromUnixTimeSeconds(entry.Start).UtcDateTime;
                    var programEnd = DateTimeOffset.FromUnixTimeSeconds(entry.Stop).UtcDateTime;
                    return entry.ChannelUuid == channelId && programStart < endDateUtc && programEnd > startDateUtc;
                })
                .Select(entry =>
                {
                    string? imageUrl = null;
                    if (!string.IsNullOrWhiteSpace(entry.Image))
                    {
                        imageUrl = ResolveTvhImageUrl(config, entry.Image);
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.ChannelIcon))
                    {
                        imageUrl = ResolveTvhImageUrl(config, entry.ChannelIcon);
                    }

                    var ratingLabelIconUrl = string.IsNullOrWhiteSpace(entry.RatingLabelIcon)
                        ? null
                        : ResolveTvhImageUrl(config, entry.RatingLabelIcon);

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

                    return new ProgramInfo
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
                        // IsSeries: prefer SerieslinkUri presence (definitive), fall back to genre 48-51 (Show/Game Show)
                        IsSeries = !string.IsNullOrWhiteSpace(entry.SerieslinkUri)
                            || (entry.Genre?.Any(genreId => genreId >= 48 && genreId <= 51) ?? false),
                        // SeriesId: use SerieslinkUri as a stable identifier for series-timer linking
                        SeriesId = string.IsNullOrWhiteSpace(entry.SerieslinkUri) ? null : entry.SerieslinkUri,
                        ShowId = string.IsNullOrWhiteSpace(entry.SerieslinkUri) ? null : entry.SerieslinkUri,
                        HomePageUrl = Uri.TryCreate(entry.EpisodeUri, UriKind.Absolute, out var episodeUri) ? episodeUri.ToString() : null,
                        ProviderIds = providerHints?.ProviderIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        SeriesProviderIds = providerHints?.SeriesProviderIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        // ProductionYear from TVH copyright_year (0 = unknown)
                        ProductionYear = entry.CopyrightYear > 0 ? entry.CopyrightYear : null,
                    };
                })
                .ToList() ?? Enumerable.Empty<ProgramInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching EPG data for channel ID: {ChannelId}.", channelId);
            return Enumerable.Empty<ProgramInfo>();
        }
    }

    public async Task<Dictionary<int, string>> GetContentTypesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = GetConfig();
            var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/epg/content_type/list");
            using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
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
        try
        {
            var config = GetConfig();
            var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/channeltag/list");
            using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
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

    private PluginConfiguration GetConfig()
    {
        return _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }

    /// <summary>
    /// Converts a TVHeadend channel number (encoded as major * 1000000 + minor) to a display string.
    /// Examples: 101000000 â†’ "101", 7001000 â†’ "7.1", 0 â†’ "0".
    /// </summary>
    private static string FormatChannelNumber(long tvhNumber)
    {
        const long channelSplit = 1000000;
        if (tvhNumber <= 0)
        {
            return "0";
        }

        var major = tvhNumber / channelSplit;
        var minor = tvhNumber % channelSplit;
        return minor > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}")
            : major.ToString(CultureInfo.InvariantCulture);
    }

    private string ResolveTvhImageUrl(PluginConfiguration config, string rawImagePath)
    {
        var raw = rawImagePath.Trim();
        var normalized = raw.TrimStart('/');

        if (normalized.StartsWith("imagecache/", StringComparison.OrdinalIgnoreCase))
        {
            return _tvheadendUrlBuilder.BuildUrlWithParameterAuth(config, normalized);
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out _))
        {
            return raw;
        }

        return _tvheadendUrlBuilder.BuildUrlWithParameterAuth(config, normalized);
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

    private static bool HasCategoryFlag(IEnumerable<string> categories, params string[] patterns)
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
                if (normalized.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record ProviderHintSet(
        Dictionary<string, string> ProviderIds,
        Dictionary<string, string> SeriesProviderIds);
}
