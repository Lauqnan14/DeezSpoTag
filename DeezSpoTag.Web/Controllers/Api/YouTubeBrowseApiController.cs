using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Services.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/youtube/browse")]
public sealed class YouTubeBrowseApiController(YouTubeMusicSearchClient client, DeezSpoTagSettingsService settingsService, IMemoryCache cache, ILogger<YouTubeBrowseApiController> logger, IWebHostEnvironment? environment = null) : ControllerBase
{
    [HttpGet("categories")]
    public Task<IActionResult> Categories(CancellationToken cancellationToken) => RespondAsync("categories", null, cancellationToken);
    [HttpGet("sections")]
    public Task<IActionResult> Sections(CancellationToken cancellationToken) => RespondAsync("sections", null, cancellationToken);
    [HttpGet("page")]
    public Task<IActionResult> Page([FromQuery] string id, [FromQuery] string? parameters, CancellationToken cancellationToken) => RespondAsync(id, parameters, cancellationToken);

    [HttpGet("metadata")]
    public async Task<IActionResult> Metadata([FromQuery] string id, CancellationToken cancellationToken)
    {
        try {
            var settings = settingsService.LoadSettings();
            var country = (string.IsNullOrWhiteSpace(settings.YouTubeMusicCountry) ? settings.DeezerCountry : settings.YouTubeMusicCountry).Trim().ToUpperInvariant();
            return Ok(new { success = true, track = await client.GetQueueSongMetadataAsync(id, country, settings.DeezerLanguage.Trim(), cancellationToken) });
        }
        catch (ArgumentException ex) { return BadRequest(new { success = false, error = ex.Message }); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return StatusCode(502, new { success = false, error = "YouTube recording metadata unavailable." }); }
    }

    [HttpGet("segments")]
    public async Task<IActionResult> Segments([FromQuery] string id, CancellationToken cancellationToken)
    {
        try { return Ok(new { segments = await client.GetNonMusicSegmentsAsync(id, cancellationToken) }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "YouTube non-music segments unavailable for {VideoId}", id);
            return Ok(new { segments = Array.Empty<YouTubeNonMusicSegment>() });
        }
    }

    [HttpGet("playback")]
    public async Task<IActionResult> Playback([FromQuery] string id, [FromServices] DeezSpoTag.Web.Services.YouTubePlaybackResolver resolver, CancellationToken cancellationToken)
    {
        try
        {
            await resolver.ResolveAsync(id, cancellationToken);
            return Ok(new { success = true, url = "/api/youtube/browse/stream?id=" + Uri.EscapeDataString(id) });
        }
        catch (ArgumentException ex) { return BadRequest(new { success = false, error = ex.Message }); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return StatusCode(504, new { success = false, error = "YouTube streaming resolution timed out." }); }
        catch (InvalidOperationException ex) { return StatusCode(502, new { success = false, error = ex.Message }); }
    }

    [HttpGet("stream")]
    public async Task<IActionResult> Stream([FromQuery] string id, [FromServices] DeezSpoTag.Web.Services.YouTubePlaybackResolver resolver, [FromServices] IHttpClientFactory httpFactory, CancellationToken cancellationToken)
    {
        try
        {
            var range = Request.Headers.Range.ToString();
            if (range.Length > 80 || (range.Length > 0 && !Regex.IsMatch(range, "^bytes=(?:[0-9]{1,16}-[0-9]{0,16}|-[0-9]{1,16})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))) return BadRequest("Unsupported media range.");
            var media = await resolver.ResolveAsync(id, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, media.Url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            if (range.Length > 0) request.Headers.TryAddWithoutValidation("Range", range);
            using var http = httpFactory.CreateClient("YouTubeStreaming");
            using var upstream = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)upstream.StatusCode is not 200 and not 206 and not 416) return StatusCode(502, new { success = false, error = "YouTube stream unavailable." });
            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType = media.ContentType;
            Response.Headers.CacheControl = "private, no-store";
            Response.Headers.AcceptRanges = "bytes";
            if (upstream.Content.Headers.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange.ToString();
            if (upstream.Content.Headers.ContentLength is { } contentLength) Response.ContentLength = contentLength;
            await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            await body.CopyToAsync(Response.Body, 64 * 1024, cancellationToken);
            return new EmptyResult();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { if (Response.HasStarted) throw; return StatusCode(504); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        { if (Response.HasStarted) throw; return StatusCode(502, new { success = false, error = "YouTube stream unavailable." }); }
    }

    public async Task<JsonObject> LoadAsync(string page, string? parameters, CancellationToken cancellationToken, bool refresh = false)
    {
        var settings = settingsService.LoadSettings();
        var country = (string.IsNullOrWhiteSpace(settings.YouTubeMusicCountry) ? settings.DeezerCountry : settings.YouTubeMusicCountry).Trim().ToUpperInvariant();
        var language = settings.DeezerLanguage.Trim();
        if (!Regex.IsMatch(country, "^[A-Z]{2}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) || !Regex.IsMatch(language, "^[a-zA-Z]{2,3}(?:-[a-zA-Z]{2,4})?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
            throw new ArgumentException("Set a valid YouTube Music country and content language.");
        var key = $"youtube:browse:market-v5:{country}:{language}:{page}:{parameters}";
        if (!refresh && cache.TryGetValue(key, out JsonObject? cached) && cached is not null) return (JsonObject)cached.DeepClone();
        using var document = await client.BrowseAsync(page == "categories" ? "FEmusic_moods_and_genres" : page == "sections" ? "FEmusic_explore" : page, country, language, parameters, cancellationToken);
        var sections = YouTubeMusicSearchClient.MapBrowseSections(document.RootElement, page == "categories");
        if (page == "sections")
        {
            // Explore's trending shelf is global. Use the provider's explicitly selected country chart.
            foreach (var section in sections.Where(s => s?["role"]?.GetValue<string>() == "trending").ToArray()) sections.Remove(section);
            using var charts = await client.BrowseAsync("FEmusic_charts", country, language, null, cancellationToken);
            var chartSections = YouTubeMusicSearchClient.MapBrowseSections(charts.RootElement);
            var globalSelection = YouTubeMusicSearchClient.FindGlobalChartSelection(charts.RootElement);
            var globalCharts = new JsonArray();
            if (globalSelection is not null)
            {
                using var global = await client.BrowseAsync("FEmusic_charts", country, language, null, cancellationToken, globalSelection);
                globalCharts = YouTubeMusicSearchClient.MapBrowseSections(global.RootElement);
            }
            var featured = await LoadFeaturedPlaylistsAsync(country, language, chartSections, globalCharts, globalSelection, cancellationToken);
            if (featured.Count > 0) sections.Add(new JsonObject { ["title"] = "Featured playlists", ["role"] = "featured-playlists", ["items"] = featured });
            var trending = chartSections.SelectMany(s => (s?["items"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .FirstOrDefault(i => i["title"]?.GetValue<string>().StartsWith("Trending 20", StringComparison.OrdinalIgnoreCase) == true);
            if (trending?["browseId"]?.GetValue<string>() is { } browseId)
            {
                using var playlist = await client.BrowseAsync(browseId, country, language, null, cancellationToken);
                var mapped = YouTubeMusicSearchClient.MapBrowseSections(playlist.RootElement);
                var tracks = new JsonArray();
                foreach (var item in mapped.SelectMany(s => (s?["items"] as JsonArray) ?? [])) tracks.Add(item!.DeepClone());
                if (tracks.Count > 0) sections.Add(new JsonObject { ["title"] = trending["title"]!.DeepClone(), ["role"] = "trending", ["items"] = tracks });
            }
        }
        var categories = new JsonArray();
        if (page == "categories") foreach (var item in sections.SelectMany(s => (s?["items"] as JsonArray) ?? [])) categories.Add(item!.DeepClone());
        var payload = new JsonObject { ["success"] = true, ["country"] = country, ["language"] = language, ["sections"] = sections, ["categories"] = categories };
        cache.Set(key, payload.DeepClone(), page == "categories" ? TimeSpan.FromHours(24) : TimeSpan.FromHours(4));
        return payload;
    }

    private string[] MarketRegions(string country)
    {
        if (environment is null) return [];
        var map = System.Xml.Linq.XDocument.Load(Path.Combine(environment.WebRootPath, "images", "world", "world-regions.svg"));
        var slugs = map.Descendants().FirstOrDefault(node => (string?)node.Attribute("data-country") == country)?
            .Attribute("data-regions")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return DeezSpoTag.Services.Genre.PersonalGenreRegions.All.Where(region => slugs.Contains(region.Slug)).Select(region => region.Name).ToArray();
    }

    private static int MarketRank(string text, string country, string[] regions)
    {
        var info = new System.Globalization.RegionInfo(country);
        bool Has(string label) => label.Length > 2 && Regex.IsMatch(text, @"\b" + Regex.Escape(label) + @"\w*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        if (Has(info.EnglishName) || Has(info.NativeName)) return 0;
        if (regions.Any(Has)) return 1;
        return 2;
    }

    private static IEnumerable<JsonObject> Interleave(IEnumerable<IEnumerable<JsonObject>> sources)
    {
        var queues = sources.Select(source => new Queue<JsonObject>(source)).ToArray();
        while (queues.Any(queue => queue.Count > 0))
            foreach (var queue in queues) if (queue.Count > 0) yield return queue.Dequeue();
    }

    private async Task<JsonArray> LoadFeaturedPlaylistsAsync(string country, string language, JsonArray countryCharts, JsonArray globalCharts, string? globalSelection, CancellationToken cancellationToken)
    {
        var key = $"youtube:market-featured:v2:{country}:{language}:{globalSelection}";
        if (cache.TryGetValue(key, out JsonArray? cached) && cached is not null) return (JsonArray)cached.DeepClone();
        var regions = MarketRegions(country);
        var categories = await LoadAsync("categories", null, cancellationToken);
        var groups = (categories["sections"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(group => (group["items"] as JsonArray ?? []).OfType<JsonObject>().ToArray()).ToArray();
        var all = groups.SelectMany(group => group).ToArray();
        var geographic = all.Where(item => MarketRank(item["title"]?.GetValue<string>() ?? "", country, regions) < 2)
            .OrderBy(item => MarketRank(item["title"]?.GetValue<string>() ?? "", country, regions));
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var selections = geographic.Concat(Interleave(groups)).Where(item => selected.Add(item["browseId"] + ":" + item["params"])).Take(8).ToArray();
        using var limit = new SemaphoreSlim(4);
        var pages = await Task.WhenAll(selections.Select(async category =>
        {
            await limit.WaitAsync(cancellationToken);
            try { return await LoadAsync(category["browseId"]!.GetValue<string>(), category["params"]?.GetValue<string>(), cancellationToken); }
            finally { limit.Release(); }
        }));
        static IEnumerable<JsonObject> Items(JsonArray shelves) => shelves.OfType<JsonObject>().SelectMany(shelf => (shelf["items"] as JsonArray ?? []).OfType<JsonObject>()).Where(item => item["type"]?.GetValue<string>() == "playlist" && item["title"]?.GetValue<string>().StartsWith("Trending 20", StringComparison.OrdinalIgnoreCase) != true);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var playlists = new JsonArray();
        void Add(JsonObject item)
        {
            var id = item["browseId"]?.GetValue<string>() ?? item["id"]?.GetValue<string>();
            if (playlists.Count < 20 && !string.IsNullOrWhiteSpace(id) && seen.Add(id)) playlists.Add(item.DeepClone());
        }
        foreach (var chart in Items(countryCharts).Take(19)) Add(chart);
        var globalItem = globalCharts.OfType<JsonObject>().OrderBy(shelf => shelf["title"]?.GetValue<string>().Contains("song", StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
            .SelectMany(shelf => Items(new JsonArray(shelf.DeepClone()))).FirstOrDefault();
        if (globalItem is not null) Add(globalItem);
        var globalIds = Items(globalCharts).Select(item => item["browseId"]?.GetValue<string>() ?? item["id"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var sources = pages.SelectMany((page, index) => (page["sections"] as JsonArray ?? []).OfType<JsonObject>().Select(shelf => new
        {
            Category = selections[index]["title"]?.GetValue<string>() ?? "",
            Shelf = shelf
        })).ToArray();
        for (var rank = 0; rank < 3; rank++)
        {
            var tier = rank;
            foreach (var item in Interleave(sources.Select(source => Items(new JsonArray(source.Shelf.DeepClone())).Where(item => MarketRank(
                source.Category + " " + source.Shelf["title"] + " " + item["title"] + " " + item["subtitle"], country, regions) == tier))))
                if (!globalIds.Contains(item["browseId"]?.GetValue<string>() ?? item["id"]?.GetValue<string>())) Add(item);
        }
        if (playlists.Count > 0) cache.Set(key, playlists.DeepClone(), TimeSpan.FromHours(4));
        return playlists;
    }

    private async Task<IActionResult> RespondAsync(string page, string? parameters, CancellationToken cancellationToken)
    {
        try { return Ok(await LoadAsync(page, parameters, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(new { success = false, error = exception.Message }); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "YouTube Music browse unavailable.");
            return StatusCode(502, new { success = false, error = "YouTube Music browse unavailable." });
        }
    }
}
