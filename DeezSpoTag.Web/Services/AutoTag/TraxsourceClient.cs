using HtmlAgilityPack;
using System.Globalization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class TraxsourceClient
{
    private readonly HttpClient _httpClient;

    public TraxsourceClient(HttpClient httpClient, ILogger<TraxsourceClient> logger)
    {
        _httpClient = httpClient;
        _ = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:85.0) Gecko/20100101 Firefox/85.0");
        }
    }

    public async Task<List<TraxsourceTrackInfo>> SearchTracksAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"https://www.traxsource.com/search/tracks?term={Uri.EscapeDataString(query)}";
        var html = await _httpClient.GetStringAsync(url, cancellationToken);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var trackList = doc.DocumentNode.SelectSingleNode("//div[@id='searchTrackList']");
        if (trackList == null)
        {
            return new List<TraxsourceTrackInfo>();
        }

        var rows = trackList.SelectNodes(".//div[contains(@class,'trk-row')]")
            ?? new HtmlNodeCollection(trackList);
        var tracks = new List<TraxsourceTrackInfo>();
        foreach (var row in rows)
        {
            if (TryParseTrackRow(row, out var track))
            {
                tracks.Add(track);
            }
        }

        return tracks;
    }

    public async Task ExtendTrackAsync(TraxsourceTrackInfo track, bool albumMeta, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Url))
        {
            return;
        }

        var html = await _httpClient.GetStringAsync(track.Url, cancellationToken);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var albumLink = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'ttl-info') and contains(@class,'ellip')]//a");
        if (albumLink == null)
        {
            return;
        }
        var albumUrl = albumLink.GetAttributeValue("href", "");
        var albumTitle = albumLink.InnerText.Trim();
        track.Album = albumTitle;

        if (!string.IsNullOrWhiteSpace(albumUrl))
        {
            var releaseId = albumUrl.Replace("/title/", "");
            var slashIndex = releaseId.IndexOf('/');
            if (slashIndex > 0)
            {
                releaseId = releaseId[..slashIndex];
            }
            track.ReleaseId = releaseId;
        }

        if (!albumMeta)
        {
            return;
        }

        var albumHtml = await _httpClient.GetStringAsync($"https://www.traxsource.com{albumUrl}", cancellationToken);
        var albumDoc = new HtmlDocument();
        albumDoc.LoadHtml(albumHtml);

        var catNode = albumDoc.DocumentNode.SelectSingleNode("//div[contains(@class,'cat-rdate')]");
        var catText = catNode?.InnerText ?? string.Empty;
        var parts = catText.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            track.CatalogNumber = parts[0];
        }

        var artistsNode = albumDoc.DocumentNode.SelectSingleNode("//h1[contains(@class,'artists')]");
        if (artistsNode != null)
        {
            var artistText = artistsNode.InnerText.Trim();
            track.AlbumArtists = artistText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (!string.IsNullOrWhiteSpace(track.TrackId))
        {
            var trackNode = albumDoc.DocumentNode.SelectSingleNode($"//div[contains(@class,'trk-row') and contains(@class,'ptk-{track.TrackId}')]");
            var numberText = trackNode?.SelectSingleNode(".//div[contains(@class,'tnum')]")?.InnerText.Trim();
            if (int.TryParse(numberText, out var trackNum))
            {
                track.TrackNumber = trackNum;
            }
        }

        var total = albumDoc.DocumentNode.SelectNodes("//div[contains(@class,'trk-row') and contains(@class,'play-trk')]")?.Count ?? 0;
        if (total > 0)
        {
            track.TrackTotal = total;
        }

        var artNode = albumDoc.DocumentNode.SelectSingleNode("//div[contains(@class,'t-image')]//img");
        var artUrl = artNode?.GetAttributeValue("src", string.Empty);
        if (!string.IsNullOrWhiteSpace(artUrl))
        {
            track.Art = artUrl;
        }
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

    private static bool TryParseTrackRow(HtmlNode row, out TraxsourceTrackInfo track)
    {
        track = new TraxsourceTrackInfo();
        var titleNode = row.SelectSingleNode(".//div[contains(@class,'title')]");
        if (titleNode == null)
        {
            return false;
        }

        var titleParts = titleNode.InnerText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (titleParts.Count == 0)
        {
            return false;
        }

        var title = titleParts[0];
        var (version, duration) = ParseTitleParts(titleParts);
        var link = titleNode.SelectSingleNode(".//a");
        var href = link?.GetAttributeValue("href", "") ?? string.Empty;
        var trackId = ExtractTrackId(href);
        var artists = row.SelectNodes(".//div[contains(@class,'artists')]//a")?.Select(a => a.InnerText.Trim()).ToList() ?? new List<string>();
        var label = row.SelectSingleNode(".//div[contains(@class,'label')]")?.InnerText.Trim();
        var (key, bpm) = ParseKeyBpm(row);
        var genre = row.SelectSingleNode(".//div[contains(@class,'genre')]")?.InnerText.Trim();
        var releaseDate = ParseReleaseDate(row.SelectSingleNode(".//div[contains(@class,'r-date')]")?.InnerText);

        track = new TraxsourceTrackInfo
        {
            Title = title,
            Version = version,
            Artists = artists,
            Bpm = bpm,
            Key = key,
            Url = string.IsNullOrWhiteSpace(href) ? string.Empty : $"https://www.traxsource.com{href}",
            Label = label,
            ReleaseDate = releaseDate,
            Genres = genre != null ? new List<string> { genre } : new List<string>(),
            TrackId = trackId,
            Duration = duration
        };
        return true;
    }

    private static (string? Version, TimeSpan Duration) ParseTitleParts(IReadOnlyList<string> titleParts)
    {
        if (titleParts.Count >= 3)
        {
            return (titleParts[1].Trim(), ParseDuration(titleParts[2]));
        }

        if (titleParts.Count == 2)
        {
            return (null, ParseDuration(titleParts[1]));
        }

        return (null, TimeSpan.Zero);
    }

    private static string ExtractTrackId(string href)
    {
        var trackId = href.Replace("/track/", "");
        var slashIndex = trackId.IndexOf('/');
        if (slashIndex > 0)
        {
            trackId = trackId[..slashIndex];
        }

        return trackId;
    }

    private static (string? Key, long? Bpm) ParseKeyBpm(HtmlNode row)
    {
        var keyBpmParts = row.SelectSingleNode(".//div[contains(@class,'key-bpm')]")?
            .InnerText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList() ?? new List<string>();
        if (keyBpmParts.Count != 2)
        {
            return (null, null);
        }

        var key = keyBpmParts[0]
            .Replace("maj", "", StringComparison.OrdinalIgnoreCase)
            .Replace("min", "m", StringComparison.OrdinalIgnoreCase)
            .Trim();
        var bpm = long.TryParse(keyBpmParts[1], out var parsed) ? (long?)parsed : null;
        return (key, bpm);
    }

    private static DateTime? ParseReleaseDate(string? releaseDateRaw)
    {
        var normalized = (releaseDateRaw ?? string.Empty)
            .Trim()
            .Replace("Pre-order for ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return DateTime.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
            ? parsedDate
            : null;
    }
}
