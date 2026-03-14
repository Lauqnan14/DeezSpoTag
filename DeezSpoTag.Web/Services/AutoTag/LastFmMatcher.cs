using System.Globalization;
using System.Net.Http.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LastFmMatcher
{
    private static readonly HashSet<string> JunkTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "seen live"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LastFmMatcher> _logger;

    public LastFmMatcher(IHttpClientFactory httpClientFactory, ILogger<LastFmMatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, LastFmConfig config, CancellationToken cancellationToken)
    {
        var apiKey = (config.ApiKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var artist = FirstNonEmpty(info.Artist, info.Artists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
        var title = OneTaggerMatching.CleanTitle(info.Title);
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var maxTags = Math.Clamp(config.MaxTags, 1, 50);
        var url =
            $"https://ws.audioscrobbler.com/2.0/?method=track.gettoptags&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";

        LastFmTopTagsResponse? response;
        try
        {
            var client = _httpClientFactory.CreateClient();
            response = await client.GetFromJsonAsync<LastFmTopTagsResponse>(url, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Last.fm tag lookup failed for {Artist} - {Title}.", artist, title);
            return null;
        }

        if (response is null)
        {
            return null;
        }

        if (response.Error.HasValue)
        {
            // Do not treat API errors as a match.
            _logger.LogDebug("Last.fm tag lookup error {Error}: {Message} for {Artist} - {Title}.", response.Error, response.Message, artist, title);
            return null;
        }

        var tags = response.Toptags?.Tag
            ?.Select(tag => new { Name = NormalizeValue(tag.Name), Count = ParseCount(tag.Count) })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Where(entry => !IsJunkTag(entry.Name!))
            .OrderByDescending(entry => entry.Count ?? 0)
            .Select(entry => entry.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxTags)
            .ToList();

        if (tags is not { Count: > 0 })
        {
            return null;
        }

        // This is an enrichment-only lookup, so we must not "blank out" core tags
        // when the runner writes global enabled tags for every platform pass.
        var passthroughArtists = info.Artists
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (passthroughArtists.Count == 0 && !string.IsNullOrWhiteSpace(info.Artist))
        {
            passthroughArtists.Add(info.Artist.Trim());
        }

        return new AutoTagMatchResult
        {
            Accuracy = 1.0,
            Track = new AutoTagTrack
            {
                Title = info.Title.Trim(),
                Artists = passthroughArtists,
                AlbumArtists = passthroughArtists.ToList(),
                Album = string.IsNullOrWhiteSpace(info.Album) ? null : info.Album.Trim(),
                Duration = info.DurationSeconds.HasValue ? TimeSpan.FromSeconds(info.DurationSeconds.Value) : null,
                Isrc = string.IsNullOrWhiteSpace(info.Isrc) ? null : info.Isrc.Trim(),
                TrackNumber = info.TrackNumber,
                Genres = tags
            }
        };
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates
            .Select(static candidate => candidate?.Trim())
            .FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate))
            ?? string.Empty;

    private static bool IsJunkTag(string tag)
    {
        return JunkTags.Contains(tag.Trim());
    }

    private static string NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int? ParseCount(string? count)
    {
        if (string.IsNullOrWhiteSpace(count))
        {
            return null;
        }

        return int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
