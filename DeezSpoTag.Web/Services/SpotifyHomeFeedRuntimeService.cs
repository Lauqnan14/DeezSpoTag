using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyHomeFeedRuntimeService
{
    private const string TrendingSectionTitle = "Trending Songs";
    private const string TrendingMatchToken = "spotify:section:home-trending-songs";
    private static readonly TimeSpan TrendingMatchWaitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TrendingMatchPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly SpotifyHomeFeedCollaborators _collaborators;
    private readonly SpotifyTracklistService _tracklistService;
    private readonly ISpotifyTracklistMatchStore _matchStore;
    private readonly IWebHostEnvironment _hostEnvironment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SpotifyHomeFeedRuntimeService> _logger;

    public SpotifyHomeFeedRuntimeService(
        SpotifyHomeFeedCollaborators collaborators,
        SpotifyTracklistService tracklistService,
        ISpotifyTracklistMatchStore matchStore,
        IWebHostEnvironment hostEnvironment,
        ILoggerFactory loggerFactory,
        ILogger<SpotifyHomeFeedRuntimeService> logger)
    {
        _collaborators = collaborators;
        _tracklistService = tracklistService;
        _matchStore = matchStore;
        _hostEnvironment = hostEnvironment;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<object>> GetMappedSectionsAsync(
        string? timeZone,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (refresh)
        {
            SpotifyHomeFeedApiController.ClearRuntimeAndPersistedCaches();
        }

        var controller = CreateController();
        var result = await controller.GetHomeFeedSections(timeZone, cancellationToken);
        var sections = ExtractSections(result);
        return await EnrichTrendingSectionAsync(sections, waitForCompletion: refresh, cancellationToken);
    }

    public async Task<int> RefreshAsync(string? timeZone, CancellationToken cancellationToken)
    {
        var sections = await GetMappedSectionsAsync(timeZone, refresh: true, cancellationToken);
        if (sections.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Spotify home feed runtime cache refreshed. sections={SectionCount}", sections.Count);
            }
        }
        else
        {
            _logger.LogWarning("Spotify home feed runtime refresh completed with no sections.");
        }

        return sections.Count;
    }

    public async Task<IReadOnlyList<object>> GetBrowseCategoriesAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        var controller = CreateController();
        var result = await controller.GetBrowseCategories(refresh, debug: false, cancellationToken);
        return ExtractNamedList(result, "categories");
    }

    private SpotifyHomeFeedApiController CreateController()
        => new(_collaborators, _loggerFactory.CreateLogger<SpotifyHomeFeedApiController>(), _hostEnvironment);

    private async Task<IReadOnlyList<object>> EnrichTrendingSectionAsync(
        IReadOnlyList<object> sections,
        bool waitForCompletion,
        CancellationToken cancellationToken)
    {
        var mutableSections = sections
            .Select(section => JsonSerializer.SerializeToNode(section) as JsonObject)
            .Where(static section => section is not null)
            .Cast<JsonObject>()
            .ToList();
        var trendingSection = mutableSections.FirstOrDefault(section =>
            string.Equals(ReadString(section, "title"), TrendingSectionTitle, StringComparison.OrdinalIgnoreCase));
        if (trendingSection?["items"] is not JsonArray items)
        {
            return mutableSections.Cast<object>().ToList();
        }

        var sourceTracks = BuildTrendingTrackSummaries(items);
        if (sourceTracks.Count == 0)
        {
            return mutableSections.Cast<object>().ToList();
        }

        var settings = _collaborators.SettingsService.LoadSettings();
        var matched = await _tracklistService.BuildMatchedTracksAsync(
            "section",
            "home-trending-songs",
            sourceTracks,
            allowFallbackSearch: !settings.StrictSpotifyDeezerMode,
            cancellationToken,
            immediateResolveLimit: waitForCompletion ? sourceTracks.Count : 0);

        if (waitForCompletion && matched.PendingCount > 0)
        {
            await WaitForTrendingMatchesAsync(cancellationToken);
        }

        var resolvedTracks = _tracklistService.ApplyStoredMatchesToTracks(
            TrendingMatchToken,
            matched.Tracks);
        ApplyTrendingMatches(items, resolvedTracks);
        return mutableSections.Cast<object>().ToList();
    }

    private async Task WaitForTrendingMatchesAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TrendingMatchWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _matchStore.GetSnapshot(TrendingMatchToken);
            if (snapshot is null || snapshot.Pending <= 0)
            {
                return;
            }

            await Task.Delay(TrendingMatchPollInterval, cancellationToken);
        }

        _logger.LogWarning(
            "Spotify Trending Songs background matching did not finish within {Timeout}.",
            TrendingMatchWaitTimeout);
    }

    private static List<SpotifyTrackSummary> BuildTrendingTrackSummaries(JsonArray items)
    {
        var tracks = new List<SpotifyTrackSummary>(items.Count);
        foreach (var node in items)
        {
            if (node is not JsonObject item
                || !string.Equals(ReadString(item, "source"), "spotify", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ReadString(item, "type"), "track", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = ReadString(item, "id");
            var uri = ReadString(item, "uri");
            var sourceUrl = ReadString(item, "sourceUrl");
            if (string.IsNullOrWhiteSpace(sourceUrl)
                && uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            {
                sourceUrl = $"https://open.spotify.com/track/{uri["spotify:track:".Length..]}";
            }

            tracks.Add(new SpotifyTrackSummary(
                Id: id,
                Name: FirstNonEmpty(ReadString(item, "name"), ReadString(item, "title")),
                Artists: ReadString(item, "artists"),
                Album: ReadString(item, "albumName"),
                DurationMs: ReadInt(item, "durationMs"),
                SourceUrl: sourceUrl,
                ImageUrl: FirstNonEmpty(ReadString(item, "coverUrl"), ReadString(item, "image")),
                Isrc: ReadString(item, "isrc")));
        }

        return tracks;
    }

    private static void ApplyTrendingMatches(JsonArray items, IReadOnlyList<SpotifyTracklistTrack> tracks)
    {
        var bySpotifyId = tracks
            .Where(static track => !string.IsNullOrWhiteSpace(track.SpotifyId)
                                   && !string.IsNullOrWhiteSpace(track.Id)
                                   && track.Id.All(char.IsDigit))
            .GroupBy(static track => track.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        foreach (var node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var spotifyId = ReadString(item, "id");
            if (!string.IsNullOrWhiteSpace(spotifyId)
                && bySpotifyId.TryGetValue(spotifyId, out var deezerId))
            {
                item["deezerId"] = deezerId;
            }
        }
    }

    private static string ReadString(JsonObject node, string propertyName)
        => node[propertyName]?.ToString().Trim() ?? string.Empty;

    private static int? ReadInt(JsonObject node, string propertyName)
        => int.TryParse(node[propertyName]?.ToString(), out var value) && value > 0 ? value : null;

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static IReadOnlyList<object> ExtractSections(IActionResult result)
    {
        if (result is not ObjectResult objectResult || objectResult.Value == null)
        {
            return Array.Empty<object>();
        }

        return ExtractNamedList(objectResult.Value, "sections");
    }

    private static IReadOnlyList<object> ExtractNamedList(IActionResult result, string propertyName)
    {
        if (result is not ObjectResult objectResult || objectResult.Value == null)
        {
            return Array.Empty<object>();
        }

        return ExtractNamedList(objectResult.Value, propertyName);
    }

    private static IReadOnlyList<object> ExtractNamedList(object value, string propertyName)
    {
        var sectionsValue = value.GetType()
            .GetProperty(propertyName)
            ?.GetValue(value);

        return sectionsValue switch
        {
            IReadOnlyList<object> list => list,
            IEnumerable<object> enumerable => enumerable.ToList(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>().ToList(),
            _ => Array.Empty<object>()
        };
    }
}
