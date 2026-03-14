using HtmlAgilityPack;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

#pragma warning disable CA1822
namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class JunoDownloadClient
{
    private const string JunoHost = "www.junodownload.com";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private sealed record ReleaseParseContext(
        List<string> Artists,
        string Title,
        string Url,
        string ReleaseId,
        string? Label,
        string? CatalogNumber,
        DateTime? ReleaseDate,
        List<string> Genres,
        string? ImageFull,
        HtmlNodeCollection TrackNodes);
    private readonly HttpClient _httpClient;
    private readonly ILogger<JunoDownloadClient> _logger;

    public JunoDownloadClient(HttpClient httpClient, ILogger<JunoDownloadClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:85.0) Gecko/20100101 Firefox/85.0");
        }
    }

    public async Task<List<JunoDownloadTrackInfo>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var queryString = "q%5Ball%5D%5B%5D=" + Uri.EscapeDataString(query)
            + "&solrorder=relevancy&items_per_page=50";
        var url = new UriBuilder(Uri.UriSchemeHttps, JunoHost)
        {
            Path = "search/",
            Query = queryString
        }.Uri;
        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("JunoDownload rate limit hit; waiting 2s.");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return await SearchAsync(query, cancellationToken);
        }
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var releases = doc.DocumentNode.SelectNodes("//div[contains(@class,'jd-listing-item')]")
            ?? new HtmlNodeCollection(doc.DocumentNode);
        var results = new List<JunoDownloadTrackInfo>();
        for (var i = 0; i < releases.Count; i++)
        {
            var tracks = ParseRelease(releases[i]);
            if (tracks == null)
            {
                if (i < 50)
                {
                    _logger.LogWarning("Failed parsing JunoDownload release at index {Index} for query {Query}.", i, query);
                }
                continue;
            }
            results.AddRange(tracks);
        }

        return results;
    }

    private static List<JunoDownloadTrackInfo>? ParseRelease(HtmlNode release) // NOSONAR
    {
        if (!TryParseReleaseContext(release, out var context))
        {
            return null;
        }
        var output = new List<JunoDownloadTrackInfo>();
        var trackTotal = context.TrackNodes.Count;
        for (var i = 0; i < context.TrackNodes.Count; i++)
        {
            var track = ParseTrackNode(context, context.TrackNodes[i], i, trackTotal);
            if (track == null)
            {
                continue;
            }
            output.Add(track);
        }

        return output;
    }

    private static bool TryParseReleaseContext(HtmlNode release, out ReleaseParseContext context)
    {
        context = default!;
        var artistsNode = release.SelectSingleNode(".//div[contains(@class,'juno-artist')]");
        var titleNode = release.SelectSingleNode(".//a[contains(@class,'juno-title')]");
        var infoNode = release.SelectSingleNode(".//div[contains(@class,'col') and contains(@class,'text-right')]//div[contains(@class,'text-sm')]");
        if (artistsNode == null || titleNode == null || infoNode == null)
        {
            return false;
        }

        var artists = artistsNode.InnerText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(artist => artist != "/")
            .ToList();
        var url = titleNode.GetAttributeValue("href", string.Empty);
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var releaseId = url.Split('/').Reverse().Skip(1).FirstOrDefault() ?? string.Empty;
        var label = release.SelectSingleNode(".//a[contains(@class,'juno-label')]")?.InnerText.Trim();
        var (catalogNumber, releaseDate, genres) = ParseReleaseInfo(infoNode.InnerText);

        var imageFull = ResolveReleaseImage(release);
        var trackNodes = release.SelectNodes(".//div[contains(@class,'jd-listing-tracklist')]//div[contains(@class,'col')]")
            ?? new HtmlNodeCollection(release);

        context = new ReleaseParseContext(
            artists,
            titleNode.InnerText.Trim(),
            url,
            releaseId,
            label,
            catalogNumber,
            releaseDate,
            genres,
            imageFull,
            trackNodes);
        return true;
    }

    private static (string? CatalogNumber, DateTime? ReleaseDate, List<string> Genres) ParseReleaseInfo(string rawInfo)
    {
        var infoParts = rawInfo.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        string? catalogNumber = null;
        if (infoParts.Count >= 3)
        {
            catalogNumber = infoParts[0];
            infoParts = infoParts.Skip(1).ToList();
        }

        if (infoParts.Count < 2)
        {
            return (catalogNumber, null, new List<string>());
        }

        var releaseDate = DateTime.TryParseExact(
            infoParts[0] + " " + infoParts[1],
            "dd MMM yy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedDate)
            ? parsedDate
            : (DateTime?)null;
        var genres = infoParts.Count >= 3
            ? infoParts[2].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();

        return (catalogNumber, releaseDate, genres);
    }

    private static string? ResolveReleaseImage(HtmlNode release)
    {
        var imageNode = release.SelectSingleNode(".//div[contains(@class,'col')]//img");
        var imageSmall = imageNode?.GetAttributeValue("src", string.Empty) ?? string.Empty;
        if (imageSmall.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            imageSmall = imageNode?.GetAttributeValue("data-src", string.Empty) ?? string.Empty;
        }

        var imageKey = imageSmall.Split('/').LastOrDefault()?.Replace(".jpg", string.Empty) ?? string.Empty;
        return string.IsNullOrWhiteSpace(imageKey)
            ? null
            : $"https://imagescdn.junodownload.com/full/{imageKey}-BIG.jpg";
    }

    private static JunoDownloadTrackInfo? ParseTrackNode(ReleaseParseContext context, HtmlNode trackNode, int index, int trackTotal)
    {
        var text = HtmlEntity.DeEntitize(trackNode.InnerText).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var durationMatch = Regex.Match(text, " - \\((\\d+:\\d\\d)\\) ?$", RegexOptions.None, RegexTimeout);
        var duration = durationMatch.Success ? ParseDuration(durationMatch.Groups[1].Value) : TimeSpan.Zero;
        var noDuration = durationMatch.Success
            ? Regex.Replace(text, " - \\((\\d+:\\d\\d)\\) ?$", string.Empty, RegexOptions.None, RegexTimeout)
            : text;
        var (trackTitle, trackArtists) = ParseTrackArtistAndTitle(noDuration, context.Artists);
        var bpm = ParseBpm(text);

        return new JunoDownloadTrackInfo
        {
            Title = trackTitle,
            Artists = trackArtists.ToList(),
            AlbumArtists = context.Artists.ToList(),
            Album = context.Title,
            Bpm = bpm,
            Genres = context.Genres.ToList(),
            Label = context.Label,
            ReleaseDate = context.ReleaseDate,
            Art = context.ImageFull,
            Url = $"https://www.junodownload.com{context.Url}",
            CatalogNumber = context.CatalogNumber,
            ReleaseId = context.ReleaseId,
            Duration = duration,
            TrackNumber = index + 1,
            TrackTotal = trackTotal
        };
    }

    private static (string TrackTitle, List<string> TrackArtists) ParseTrackArtistAndTitle(string text, List<string> releaseArtists)
    {
        var split = text.Split(" - \"", StringSplitOptions.None);
        if (split.Length == 1)
        {
            return (split[0], releaseArtists);
        }

        var trackArtists = split[0]
            .Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var trackTitle = split[1].Replace("\"", string.Empty);
        return (trackTitle, trackArtists);
    }

    private static long? ParseBpm(string text)
    {
        var bpmMatch = Regex.Match(text, "(\\d+)\\u00A0?BPM", RegexOptions.None, RegexTimeout);
        if (bpmMatch.Success && long.TryParse(bpmMatch.Groups[1].Value, out var bpmParsed))
        {
            return bpmParsed;
        }

        return null;
    }

    private static TimeSpan ParseDuration(string text)
    {
        var parts = text.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var minutes) && int.TryParse(parts[1], out var seconds))
        {
            return TimeSpan.FromSeconds((minutes * 60d) + seconds);
        }
        return TimeSpan.Zero;
    }
}
