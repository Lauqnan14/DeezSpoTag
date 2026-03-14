using DeezSpoTag.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Services.Download.Utils;

public sealed class LrclibLyricsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LrclibLyricsService> _logger;

    public LrclibLyricsService(IHttpClientFactory httpClientFactory, ILogger<LrclibLyricsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<LyricsBase> ResolveLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track == null)
        {
            return CreateError("Track is required for LRCLIB lyrics.");
        }

        var title = track.Title ?? string.Empty;
        var artist = track.MainArtist?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return CreateError("Track title and artist are required for LRCLIB lyrics.");
        }

        var duration = track.Duration > 0 ? track.Duration : 0;

        var exact = await FetchLyricsWithMetadataAsync(title, artist, duration, cancellationToken);
        if (exact.IsLoaded())
        {
            return exact;
        }

        var search = await FetchLyricsFromSearchAsync(title, artist, cancellationToken);
        if (search.IsLoaded())
        {
            return search;
        }

        var simplifiedTitle = SimplifyTrackName(title);
        if (!string.Equals(simplifiedTitle, title, StringComparison.OrdinalIgnoreCase))
        {
            var simplifiedExact = await FetchLyricsWithMetadataAsync(simplifiedTitle, artist, duration, cancellationToken);
            if (simplifiedExact.IsLoaded())
            {
                return simplifiedExact;
            }

            var simplifiedSearch = await FetchLyricsFromSearchAsync(simplifiedTitle, artist, cancellationToken);
            if (simplifiedSearch.IsLoaded())
            {
                return simplifiedSearch;
            }
        }

        return CreateError("LRCLIB lyrics not found.");
    }

    private async Task<LyricsBase> FetchLyricsWithMetadataAsync(string trackName, string artistName, int duration, CancellationToken cancellationToken)
    {
        var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackName)}";
        if (duration > 0)
        {
            url += $"&duration={duration}";
        }

        return await FetchLyricsAsync(url, cancellationToken);
    }

    private async Task<LyricsBase> FetchLyricsFromSearchAsync(string trackName, string artistName, CancellationToken cancellationToken)
    {
        var query = $"{artistName} {trackName}";
        var url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";

        try
        {
            using var httpClient = _httpClientFactory.CreateClient("LyricsService");
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CreateError($"LRCLIB search failed with status {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var results = await JsonSerializer.DeserializeAsync<List<LrclibResponse>>(stream, cancellationToken: cancellationToken);
            if (results == null || results.Count == 0)
            {
                return CreateError("LRCLIB search returned no results.");
            }

            var best = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
                ?? results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PlainLyrics))
                ?? results[0];

            return ConvertToLyrics(best);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LRCLIB search request failed");
            return CreateError("LRCLIB search failed.");
        }
    }

    private async Task<LyricsBase> FetchLyricsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient("LyricsService");
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CreateError($"LRCLIB request failed with status {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<LrclibResponse>(stream, cancellationToken: cancellationToken);
            if (payload == null)
            {
                return CreateError("LRCLIB returned empty response.");
            }

            return ConvertToLyrics(payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LRCLIB request failed");
            return CreateError("LRCLIB request failed.");
        }
    }

    private static LyricsSource ConvertToLyrics(LrclibResponse payload)
    {
        var lyrics = new LyricsSource();
        var text = payload.SyncedLyrics;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = payload.PlainLyrics;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            lyrics.SetErrorMessage("LRCLIB response had no lyrics text.");
            return lyrics;
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines
            .Select(static rawLine => rawLine.Trim())
            .Where(static line => line.Length > 0))
        {
            if (TryParseTimestampedLine(line, out var timestamp, out var content))
            {
                lyrics.SyncedLyrics?.Add(new SynchronizedLyric(content, timestamp, TimestampToMilliseconds(timestamp)));
            }
            else
            {
                if (lyrics.UnsyncedLyrics == null)
                {
                    lyrics.UnsyncedLyrics = line;
                }
                else
                {
                    lyrics.UnsyncedLyrics += "\n" + line;
                }
            }
        }

        if (lyrics.SyncedLyrics?.Count == 0 && string.IsNullOrWhiteSpace(lyrics.UnsyncedLyrics))
        {
            lyrics.SetErrorMessage("LRCLIB response had no parsed lyrics.");
        }

        return lyrics;
    }

    private static bool TryParseTimestampedLine(string line, out string timestamp, out string text)
    {
        timestamp = string.Empty;
        text = string.Empty;

        if (line.Length < 10 || line[0] != '[')
        {
            return false;
        }

        var closeBracket = line.IndexOf(']');
        if (closeBracket <= 0)
        {
            return false;
        }

        timestamp = line[..(closeBracket + 1)];
        text = line[(closeBracket + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static int TimestampToMilliseconds(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return 0;
        }

        var trimmed = timestamp.Trim('[', ']');
        var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return 0;
        }

        if (!int.TryParse(parts[0], out var minutes))
        {
            return 0;
        }

        var secParts = parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(secParts[0], out var seconds))
        {
            return 0;
        }

        var centiseconds = 0;
        if (secParts.Length > 1)
        {
            _ = int.TryParse(secParts[1], out centiseconds);
        }

        return (minutes * 60 + seconds) * 1000 + centiseconds * 10;
    }

    private static string SimplifyTrackName(string name)
    {
        var simplified = name;
        var parenIndex = simplified.IndexOf('(');
        if (parenIndex > 0)
        {
            simplified = simplified[..parenIndex].Trim();
        }

        var dashIndex = simplified.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex > 0)
        {
            simplified = simplified[..dashIndex].Trim();
        }

        return simplified;
    }

    private static LyricsSource CreateError(string message)
    {
        var lyrics = new LyricsSource();
        lyrics.SetErrorMessage(message);
        return lyrics;
    }

    private sealed record LrclibResponse(
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);
}
