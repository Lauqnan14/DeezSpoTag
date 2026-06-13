using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed record ShazamTrackCard(
    string Id,
    string Title,
    string Artist,
    string? Album,
    string? Genre,
    string? Label,
    string? ReleaseDate,
    string? ArtworkUrl,
    string? Url,
    string? AppleMusicUrl,
    string? SpotifyUrl,
    string? Isrc,
    int? DurationMs,
    string? Key,
    string? Language,
    string? Composer,
    string? Lyricist,
    string? Publisher,
    int? TrackNumber,
    int? DiscNumber,
    bool? Explicit,
    string? AlbumAdamId,
    List<string> ArtistIds,
    List<string> ArtistAdamIds,
    Dictionary<string, List<string>> Tags);

public sealed partial class ShazamDiscoveryService
{
    private const string Language = "en-US";
    private const string Country = "US";
    private const string AppleMusicIdPrefix = "am:";
    private const string ToolsDirectory = "Tools";
    private const string ShazamPortDirectory = "shazam_port";
    private const string DiscoverScriptName = "discover.py";
    private const string UnknownArtist = "Unknown Artist";
    private const string AttributesPropertyName = "attributes";
    private const string TrackType = "track";
    private const int MaxTrackLimit = 20;

    private readonly HttpClient _httpClient;
    private readonly ILogger<ShazamDiscoveryService> _logger;
    private readonly IWebHostEnvironment _environment;
    private static readonly ConcurrentDictionary<string, ShazamTrackCard> SessionCardCache = new(StringComparer.OrdinalIgnoreCase);

    public ShazamDiscoveryService(
        HttpClient httpClient,
        ILogger<ShazamDiscoveryService> logger,
        IWebHostEnvironment environment)
    {
        _httpClient = httpClient;
        _logger = logger;
        _environment = environment;
        if (_httpClient.Timeout <= TimeSpan.Zero)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }
    }

    public async Task<ShazamTrackCard?> GetTrackAsync(string trackId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        trackId = trackId.Trim();
        if (SessionCardCache.TryGetValue(trackId, out var cached))
        {
            return cached;
        }

        if (trackId.StartsWith(AppleMusicIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parsed = await GetTrackViaPortedDiscoveryAsync(trackId, cancellationToken);
        if (parsed != null)
        {
            CacheCard(parsed);
            return parsed;
        }
        return null;
    }

    public async Task<IReadOnlyList<ShazamTrackCard>> GetRelatedTracksAsync(
        string trackId,
        int limit = MaxTrackLimit,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return Array.Empty<ShazamTrackCard>();
        }

        trackId = trackId.Trim();
        if (trackId.StartsWith(AppleMusicIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<ShazamTrackCard>();
        }

        limit = Math.Clamp(limit, 1, MaxTrackLimit);
        offset = Math.Max(0, offset);

        return await GetRelatedTracksViaPortedDiscoveryAsync(trackId, limit, offset, cancellationToken);
    }

    public async Task<IReadOnlyList<ShazamTrackCard>> SearchTracksAsync(
        string query,
        int limit = MaxTrackLimit,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ShazamTrackCard>();
        }

        limit = Math.Clamp(limit, 1, MaxTrackLimit);
        offset = Math.Max(0, offset);

        var discovered = await SearchTracksViaPortedDiscoveryAsync(query, limit, offset, cancellationToken);
        return discovered;
    }

    private async Task<IReadOnlyList<ShazamTrackCard>> SearchTracksViaPortedDiscoveryAsync(
        string query,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        using var doc = await RunPortedDiscoverAsync("search", trackId: null, query, limit, offset, cancellationToken);
        if (doc == null)
        {
            return Array.Empty<ShazamTrackCard>();
        }

        var root = doc.RootElement;
        if (!TryGetBool(root, "ok", out var ok) || !ok)
        {
            return Array.Empty<ShazamTrackCard>();
        }

        var parsed = ParseTrackList(root, limit);
        foreach (var card in parsed)
        {
            CacheCard(card);
        }

        return parsed;
    }

    private static string ResolveShazamPortPython(string scriptPath)
    {
        var explicitPython = Environment.GetEnvironmentVariable("SHAZAM_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitPython) && File.Exists(explicitPython))
        {
            return explicitPython;
        }

        var scriptDirectory = Path.GetDirectoryName(scriptPath) ?? string.Empty;
        var venvPython = Path.Join(scriptDirectory, ".venv", "bin", "python");
        if (File.Exists(venvPython))
        {
            return venvPython;
        }

        var venvPython3 = Path.Join(scriptDirectory, ".venv", "bin", "python3");
        if (File.Exists(venvPython3))
        {
            return venvPython3;
        }

        var vibePython = Environment.GetEnvironmentVariable("VIBE_ANALYZER_PYTHON");
        if (!string.IsNullOrWhiteSpace(vibePython) && File.Exists(vibePython))
        {
            return vibePython;
        }

        return "python3";
    }

    private static string? ResolveShazamPortDiscoverScriptPath(string? contentRootPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(contentRootPath))
        {
            candidates.Add(Path.Join(contentRootPath, ToolsDirectory, ShazamPortDirectory, DiscoverScriptName));
            candidates.Add(Path.Join(contentRootPath, "..", "DeezSpoTag.Web", ToolsDirectory, ShazamPortDirectory, DiscoverScriptName));
        }

        var current = Directory.GetCurrentDirectory();
        candidates.Add(Path.Join(current, ToolsDirectory, ShazamPortDirectory, DiscoverScriptName));
        candidates.Add(Path.Join(current, "DeezSpoTag.Web", ToolsDirectory, ShazamPortDirectory, DiscoverScriptName));

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Join(baseDir, ToolsDirectory, ShazamPortDirectory, DiscoverScriptName));
        candidates.Add(Path.Join(baseDir, "..", "..", "..", "..", ToolsDirectory, ShazamPortDirectory, DiscoverScriptName));

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    private async Task<JsonDocument?> RunPortedDiscoverAsync(
        string mode,
        string? trackId,
        string? query,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        try
        {
            var projectRoot = _environment?.ContentRootPath;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                var baseDir = AppContext.BaseDirectory;
                projectRoot = Path.GetFullPath(Path.Join(baseDir, "..", "..", "..", ".."));
            }
            var scriptPath = ResolveShazamPortDiscoverScriptPath(projectRoot);
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                _logger.LogWarning("Shazam port discovery script not found. contentRoot={ContentRoot}", projectRoot);
                return null;
            }

            var python = ResolveShazamPortPython(scriptPath);
            var start = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add(mode);
            if (!string.IsNullOrWhiteSpace(trackId))
            {
                start.ArgumentList.Add("--track-id");
                start.ArgumentList.Add(trackId);
            }
            if (!string.IsNullOrWhiteSpace(query))
            {
                start.ArgumentList.Add("--query");
                start.ArgumentList.Add(query);
            }
            start.ArgumentList.Add("--limit");
            start.ArgumentList.Add(limit.ToString());
            start.ArgumentList.Add("--offset");
            start.ArgumentList.Add(offset.ToString());
            start.ArgumentList.Add("--language");
            start.ArgumentList.Add(Language);
            start.ArgumentList.Add("--country");
            start.ArgumentList.Add(Country);
            start.ArgumentList.Add("--timeout");
            start.ArgumentList.Add("20");
            start.Environment["PYTHONUNBUFFERED"] = "1";

            using var process = new Process { StartInfo = start };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogWarning(
                    "Shazam port discovery failed: mode={Mode} code={Code} python={Python} script={Script} stderr={Stderr}",
                    mode,
                    process.ExitCode,
                    python,
                    scriptPath,
                    string.IsNullOrWhiteSpace(stderr) ? "none" : stderr);
                return null;
            }

            return JsonDocument.Parse(stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Shazam port discovery bridge failed for mode {Mode}.", mode);
            return null;
        }
    }

    private async Task<ShazamTrackCard?> GetTrackViaPortedDiscoveryAsync(string trackId, CancellationToken cancellationToken)
    {
        using var doc = await RunPortedDiscoverAsync(TrackType, trackId, query: null, limit: 1, offset: 0, cancellationToken);
        if (doc == null)
        {
            return null;
        }

        var root = doc.RootElement;
        if (!TryGetBool(root, "ok", out var ok) || !ok)
        {
            return null;
        }

        if (!TryGetObject(root, TrackType, out var trackObject))
        {
            return null;
        }

        var parsed = ParseTrack(trackObject);
        if (parsed != null)
        {
            CacheCard(parsed);
        }

        return parsed;
    }

    private async Task<IReadOnlyList<ShazamTrackCard>> GetRelatedTracksViaPortedDiscoveryAsync(
        string trackId,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        using var doc = await RunPortedDiscoverAsync("related", trackId, query: null, limit, offset, cancellationToken);
        if (doc == null)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("Shazam port related lookup returned null document for trackId {TrackId}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackId));
            }
            return Array.Empty<ShazamTrackCard>();
        }

        var root = doc.RootElement;
        if (!TryGetBool(root, "ok", out var ok) || !ok)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("Shazam port related lookup returned non-ok payload for trackId {TrackId}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackId));
            }
            return Array.Empty<ShazamTrackCard>();
        }

        var parsed = ParseTrackList(root, limit);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Shazam port related lookup produced {Count} tracks for trackId {TrackId}.", parsed.Count, DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackId));
        }
        foreach (var card in parsed)
        {
            CacheCard(card);
        }

        return parsed;
    }

    private static bool TryGetBool(JsonElement source, string propertyName, out bool value)
    {
        value = false;
        if (!source.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static List<ShazamTrackCard> ParseTrackList(JsonElement root, int limit)
    {
        var cards = new List<ShazamTrackCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in EnumerateTrackCandidates(root)
            .Select(ParseTrack)
            .Where(card => card is not null)
            .Select(card => card!))
        {
            var key = !string.IsNullOrWhiteSpace(card.Id)
                ? card.Id
                : $"{card.Title}|{card.Artist}";
            if (!seen.Add(key))
            {
                continue;
            }

            cards.Add(card);
            if (cards.Count >= limit)
            {
                break;
            }
        }

        return cards;
    }

    private static IEnumerable<JsonElement> EnumerateTrackCandidates(JsonElement root)
    {
        var queue = new Queue<JsonElement>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var candidate in GetTrackCandidatesFromElement(current))
            {
                yield return candidate;
            }

            EnqueueChildElements(current, queue);
        }
    }

    private static IEnumerable<JsonElement> GetTrackCandidatesFromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (TryGetObject(element, TrackType, out var trackObject))
        {
            yield return trackObject;
        }

        if (LooksLikeTrack(element))
        {
            yield return element;
        }
    }

    private static void EnqueueChildElements(JsonElement element, Queue<JsonElement> queue)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                queue.Enqueue(prop.Value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                queue.Enqueue(item);
            }
        }
    }

    private static bool LooksLikeTrack(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasTitle = TryGetString(element, "title") is not null || TryGetString(element, "name") is not null;
        var hasArtist = TryGetString(element, "subtitle") is not null || TryGetString(element, "artist") is not null;
        var hasId = TryGetString(element, "key") is not null || TryGetString(element, "id") is not null;

        return hasTitle && (hasArtist || hasId);
    }

    private static ShazamTrackCard? ParseTrack(JsonElement source)
    {
        var track = source;
        if (TryGetObject(source, TrackType, out var nestedTrack))
        {
            track = nestedTrack;
        }

        var metadata = ExtractMetadataMap(track);

        var id = FirstNonEmpty(
            TryGetString(track, "key"),
            TryGetString(track, "id"),
            TryGetString(track, "track_id"),
            TryGetString(track, "trackId"));

        var title = FirstNonEmpty(
            TryGetString(track, "title"),
            TryGetString(track, "name"));

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artist = FirstNonEmpty(
            TryGetString(track, "subtitle"),
            TryGetString(track, "artist"),
            TryGetString(track, "artists"),
            ExtractFirstArtist(track),
            UnknownArtist);

        var album = FirstNonEmpty(
            TryGetString(track, "album"),
            GetMetadataValue(metadata, "Album"));

        var genre = FirstNonEmpty(
            TryGetNestedString(track, "genres", "primary"),
            GetMetadataValue(metadata, "Genre"));

        var label = GetMetadataValue(metadata, "Label");
        var releaseDate = FirstNonEmpty(
            TryGetString(track, "releasedate"),
            TryGetString(track, "releaseDate"),
            GetMetadataValue(metadata, "Released"),
            GetMetadataValue(metadata, "Release Date"),
            GetMetadataValue(metadata, "Release"),
            GetMetadataValue(metadata, "Year"));

        var artwork = FirstNonEmpty(
            TryGetNestedString(track, "images", "coverart"),
            TryGetNestedString(track, "images", "coverarthq"),
            TryGetNestedString(track, "images", "background"));

        var shazamUrl = FirstNonEmpty(
            TryGetString(track, "url"),
            TryGetNestedString(track, "share", "href"));

        var appleMusicUrl = FindFirstUri(track, uri => uri.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase));
        var spotifyUrl = FindFirstUri(track, uri => uri.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase));
        var deezerUrls = FindUris(track, IsDeezerUriCandidate, limit: 8);
        var deezerUrl = deezerUrls.FirstOrDefault();

        var isrc = FirstNonEmpty(
            TryGetString(track, "isrc"),
            GetMetadataValue(metadata, "ISRC"));

        var durationMs = ParseDurationMs(FirstNonEmpty(
            TryGetString(track, "duration_ms"),
            TryGetString(track, "durationMs"),
            TryGetString(track, "duration"),
            GetMetadataValue(metadata, "Duration"),
            GetMetadataValue(metadata, "Time"),
            GetMetadataValue(metadata, "Length")));

        var key = FirstNonEmpty(
            TryGetString(track, "musical_key"),
            GetMetadataValue(metadata, "Key"));
        var language = FirstNonEmpty(
            GetMetadataValue(metadata, "Language"),
            GetMetadataValue(metadata, "Lang"));
        var composer = FirstNonEmpty(
            GetMetadataValue(metadata, "Composer"),
            GetMetadataValue(metadata, "Songwriter(s)"),
            GetMetadataValue(metadata, "Songwriter"),
            GetMetadataValue(metadata, "Writers"),
            GetMetadataValue(metadata, "Written By"));
        var lyricist = FirstNonEmpty(
            GetMetadataValue(metadata, "Lyricist"),
            GetMetadataValue(metadata, "Lyrics By"));
        var publisher = GetMetadataValue(metadata, "Publisher");
        var trackNumber = ParsePositiveNumber(FirstNonEmpty(
            GetMetadataValue(metadata, "Track Number"),
            GetMetadataValue(metadata, "Track")));
        var discNumber = ParsePositiveNumber(FirstNonEmpty(
            GetMetadataValue(metadata, "Disc Number"),
            GetMetadataValue(metadata, "Disc")));
        var explicitFlag = ParseExplicitFlag(FirstNonEmpty(
            GetMetadataValue(metadata, "Explicit"),
            GetMetadataValue(metadata, "Content Rating")));
        var albumAdamId = FirstNonEmpty(
            TryGetString(track, "albumadamid"),
            TryGetString(track, "albumAdamId"));
        var (artistIds, artistAdamIds) = ExtractArtistIds(track);
        var tags = BuildShazamTagMap(track, new ShazamTagContext
        {
            Id = id,
            Title = title,
            Artist = artist,
            Album = album,
            Genre = genre,
            Label = label,
            ReleaseDate = releaseDate,
            Artwork = artwork,
            ShazamUrl = shazamUrl,
            AppleMusicUrl = appleMusicUrl,
            SpotifyUrl = spotifyUrl,
            DeezerUrl = deezerUrl,
            DeezerUrls = deezerUrls,
            Isrc = isrc,
            DurationMs = durationMs,
            Key = key,
            TrackLanguage = language,
            Composer = composer,
            Lyricist = lyricist,
            Publisher = publisher,
            TrackNumber = trackNumber,
            DiscNumber = discNumber,
            ExplicitFlag = explicitFlag,
            AlbumAdamId = albumAdamId,
            ArtistIds = artistIds,
            ArtistAdamIds = artistAdamIds,
            Metadata = metadata
        });

        return new ShazamTrackCard(
            id ?? string.Empty,
            title,
            artist ?? UnknownArtist,
            album,
            genre,
            label,
            releaseDate,
            artwork,
            shazamUrl,
            appleMusicUrl,
            spotifyUrl,
            isrc,
            durationMs,
            key,
            language,
            composer,
            lyricist,
            publisher,
            trackNumber,
            discNumber,
            explicitFlag,
            albumAdamId,
            artistIds,
            artistAdamIds,
            tags);
    }

    private static void CacheCard(ShazamTrackCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.Id))
        {
            SessionCardCache[card.Id] = card;
            if (card.Id.StartsWith(AppleMusicIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rawId = card.Id[AppleMusicIdPrefix.Length..];
                if (!string.IsNullOrWhiteSpace(rawId))
                {
                    SessionCardCache[rawId] = card;
                }
            }
        }
    }

    private static int? ParseDurationMs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Contains(':'))
        {
            var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var totalSeconds = 0;
            foreach (var part in parts)
            {
                if (!int.TryParse(part.Trim(), out var value))
                {
                    return null;
                }
                totalSeconds = (totalSeconds * 60) + value;
            }
            return totalSeconds > 0 ? totalSeconds * 1000 : null;
        }

        if (!int.TryParse(trimmed, out var numeric))
        {
            return null;
        }

        if (numeric <= 0)
        {
            return null;
        }

        return numeric <= 1000 ? numeric * 1000 : numeric;
    }

    private static string? ExtractFirstArtist(JsonElement track)
    {
        if (TryGetArray(track, "artists", out var artists))
        {
            return artists.EnumerateArray()
                .Select(artist => FirstNonEmpty(
                    TryGetString(artist, "name"),
                    TryGetNestedString(artist, AttributesPropertyName, "name")))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        }

        return null;
    }

    private static Dictionary<string, List<string>> ExtractMetadataMap(JsonElement track)
    {
        var metadata = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetArray(track, "sections", out var sections))
        {
            return metadata;
        }

        foreach (var section in sections.EnumerateArray())
        {
            if (!TryGetArray(section, "metadata", out var items))
            {
                continue;
            }

            AddMetadataItems(metadata, items);
        }

        return metadata;
    }

    private static void AddMetadataItems(Dictionary<string, List<string>> metadata, JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            var title = TryGetString(item, "title");
            var value = TryGetString(item, "text");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var key = title.Trim();
            var cleanValue = value.Trim();
            if (!metadata.TryGetValue(key, out var values))
            {
                values = new List<string>();
                metadata[key] = values;
            }

            if (!values.Contains(cleanValue, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(cleanValue);
            }
        }
    }

    private static string? GetMetadataValue(Dictionary<string, List<string>> metadata, params string[] titles)
    {
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title) || !metadata.TryGetValue(title, out var values) || values.Count == 0)
            {
                continue;
            }

            var value = values.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static (List<string> artistIds, List<string> artistAdamIds) ExtractArtistIds(JsonElement track)
    {
        var artistIds = new List<string>();
        var artistAdamIds = new List<string>();
        if (!TryGetArray(track, "artists", out var artists))
        {
            return (artistIds, artistAdamIds);
        }

        foreach (var artist in artists.EnumerateArray())
        {
            var id = FirstNonEmpty(
                TryGetString(artist, "id"),
                TryGetNestedString(artist, AttributesPropertyName, "id"));
            var adamId = FirstNonEmpty(
                TryGetString(artist, "adamid"),
                TryGetString(artist, "adamId"),
                TryGetNestedString(artist, AttributesPropertyName, "adamid"),
                TryGetNestedString(artist, AttributesPropertyName, "adamId"));

            if (!string.IsNullOrWhiteSpace(id) && !artistIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                artistIds.Add(id.Trim());
            }

            if (!string.IsNullOrWhiteSpace(adamId) && !artistAdamIds.Contains(adamId, StringComparer.OrdinalIgnoreCase))
            {
                artistAdamIds.Add(adamId.Trim());
            }
        }

        return (artistIds, artistAdamIds);
    }

    private static int? ParsePositiveNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Trim()
            .SkipWhile(ch => !char.IsDigit(ch))
            .TakeWhile(char.IsDigit)
            .ToArray());
        return int.TryParse(digits, out var value) && value > 0 ? value : null;
    }

    private static bool? ParseExplicitFlag(string? raw) => ShazamSharedParsing.ParseExplicitFlag(raw);

    private static string NormalizeMetaKey(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var lastSeparator = false;
        foreach (var ch in title.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
                lastSeparator = false;
            }
            else if (!lastSeparator)
            {
                builder.Append('_');
                lastSeparator = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    private static void AddTagValue(Dictionary<string, List<string>> tags, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalizedKey = key.Trim();
        var normalizedValue = value.Trim();
        if (!tags.TryGetValue(normalizedKey, out var values))
        {
            values = new List<string>();
            tags[normalizedKey] = values;
        }

        if (!values.Contains(normalizedValue, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(normalizedValue);
        }
    }

    private static void AddTagValues(Dictionary<string, List<string>> tags, string key, IEnumerable<string>? values)
    {
        if (values == null)
        {
            return;
        }

        foreach (var value in values)
        {
            AddTagValue(tags, key, value);
        }
    }

    private static Dictionary<string, List<string>> BuildShazamTagMap(
        JsonElement track,
        ShazamTagContext context)
    {
        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        AddTagValue(tags, "SHAZAM_TRACK_ID", context.Id);
        AddTagValue(tags, "SHAZAM_TRACK_KEY", TryGetString(track, "key"));
        AddTagValue(tags, "SHAZAM_KEY", TryGetString(track, "key"));
        AddTagValue(tags, "SHAZAM_TITLE", context.Title);
        AddTagValue(tags, "SHAZAM_ARTIST", context.Artist);
        AddTagValue(tags, "SHAZAM_URL", context.ShazamUrl);
        AddTagValue(tags, "SHAZAM_APPLE_MUSIC_URL", context.AppleMusicUrl);
        AddTagValue(tags, "SHAZAM_SPOTIFY_URL", context.SpotifyUrl);
        AddTagValues(tags, "SHAZAM_DEEZER_URL", context.DeezerUrls);
        AddTagValue(tags, "SHAZAM_DEEZER_URL", context.DeezerUrl);
        AddTagValue(tags, "SHAZAM_ALBUM", context.Album);
        AddTagValue(tags, "SHAZAM_GENRE", context.Genre);
        AddTagValue(tags, "SHAZAM_LABEL", context.Label);
        AddTagValue(tags, "SHAZAM_RELEASE_DATE", context.ReleaseDate);
        AddTagValue(tags, "SHAZAM_ARTWORK", context.Artwork);
        AddTagValue(tags, "SHAZAM_ISRC", context.Isrc);
        AddTagValue(tags, "SHAZAM_MUSICAL_KEY", context.Key);
        AddTagValue(tags, "SHAZAM_LANGUAGE", context.TrackLanguage);
        AddTagValue(tags, "SHAZAM_COMPOSER", context.Composer);
        AddTagValue(tags, "SHAZAM_LYRICIST", context.Lyricist);
        AddTagValue(tags, "SHAZAM_PUBLISHER", context.Publisher);
        AddTagValue(tags, "SHAZAM_ALBUM_ADAM_ID", context.AlbumAdamId);
        AddTagValues(tags, "SHAZAM_ARTIST_IDS", context.ArtistIds);
        AddTagValues(tags, "SHAZAM_ARTIST_ADAM_IDS", context.ArtistAdamIds);
        if (context.DurationMs.HasValue && context.DurationMs.Value > 0)
        {
            AddTagValue(tags, "SHAZAM_DURATION_MS", context.DurationMs.Value.ToString());
        }

        if (context.TrackNumber.HasValue && context.TrackNumber.Value > 0)
        {
            AddTagValue(tags, "SHAZAM_TRACK_NUMBER", context.TrackNumber.Value.ToString());
        }

        if (context.DiscNumber.HasValue && context.DiscNumber.Value > 0)
        {
            AddTagValue(tags, "SHAZAM_DISC_NUMBER", context.DiscNumber.Value.ToString());
        }

        if (context.ExplicitFlag.HasValue)
        {
            AddTagValue(tags, "SHAZAM_EXPLICIT", context.ExplicitFlag.Value ? "true" : "false");
        }

        foreach (var (metaTitle, metaValues) in context.Metadata)
        {
            var normalized = NormalizeMetaKey(metaTitle);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            foreach (var value in metaValues)
            {
                AddTagValue(tags, $"SHAZAM_META_{normalized}", value);
            }
        }

        return tags;
    }

    private static string? FindFirstUri(JsonElement element, Func<string, bool> predicate)
    {
        var queue = new Queue<JsonElement>();
        queue.Enqueue(element);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (TryFindUriOnElement(current, predicate, out var match))
            {
                return match;
            }

            EnqueueNestedElements(current, queue);
        }

        return null;
    }

    private static List<string> FindUris(JsonElement element, Func<string, bool> predicate, int limit)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (limit <= 0)
        {
            return results;
        }

        var queue = new Queue<JsonElement>();
        queue.Enqueue(element);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (TryFindUriOnElement(current, predicate, out var match)
                && !string.IsNullOrWhiteSpace(match)
                && seen.Add(match))
            {
                results.Add(match);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            EnqueueNestedElements(current, queue);
        }

        return results;
    }

    private static bool IsDeezerUriCandidate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Contains("deezer.com/track/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("deezer-query://", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("deezer://", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("deezer:track:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindUriOnElement(JsonElement element, Func<string, bool> predicate, out string? match)
    {
        match = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var isUriField = prop.Name.Equals("uri", StringComparison.OrdinalIgnoreCase)
                || prop.Name.Equals("href", StringComparison.OrdinalIgnoreCase)
                || prop.Name.Equals("url", StringComparison.OrdinalIgnoreCase);
            if (!isUriField || !predicate(value))
            {
                continue;
            }

            match = value.Trim();
            return true;
        }

        return false;
    }

    private static void EnqueueNestedElements(JsonElement element, Queue<JsonElement> queue)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var value in element.EnumerateObject()
                .Select(prop => prop.Value)
                .Where(static value => value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array))
            {
                queue.Enqueue(value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                queue.Enqueue(item);
            }
        }
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (TryGetProperty(element, propertyName, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
    {
        if (TryGetProperty(element, propertyName, out value) && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static string? TryGetNestedString(JsonElement element, params string[] propertyPath)
    {
        var current = element;
        var resolved = propertyPath.Aggregate(
            (Found: true, Current: current),
            static (state, property) => state.Found && TryGetProperty(state.Current, property, out var next)
                ? (true, next)
                : (false, default));
        if (!resolved.Found)
        {
            return null;
        }

        current = resolved.Current;

        if (current.ValueKind == JsonValueKind.String)
        {
            return current.GetString()?.Trim();
        }

        return current.ValueKind == JsonValueKind.Number
            ? current.ToString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        try
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = property.Value;
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // Upstream schema shifts can produce invalid JsonElement instances.
            // Treat as missing property instead of failing the entire request.
        }

        value = default;
        return false;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private sealed class ShazamTagContext
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public string? Genre { get; init; }
        public string? Label { get; init; }
        public string? ReleaseDate { get; init; }
        public string? Artwork { get; init; }
        public string? ShazamUrl { get; init; }
        public string? AppleMusicUrl { get; init; }
        public string? SpotifyUrl { get; init; }
        public string? DeezerUrl { get; init; }
        public IEnumerable<string> DeezerUrls { get; init; } = Array.Empty<string>();
        public string? Isrc { get; init; }
        public int? DurationMs { get; init; }
        public string? Key { get; init; }
        public string? TrackLanguage { get; init; }
        public string? Composer { get; init; }
        public string? Lyricist { get; init; }
        public string? Publisher { get; init; }
        public int? TrackNumber { get; init; }
        public int? DiscNumber { get; init; }
        public bool? ExplicitFlag { get; init; }
        public string? AlbumAdamId { get; init; }
        public IEnumerable<string> ArtistIds { get; init; } = Array.Empty<string>();
        public IEnumerable<string> ArtistAdamIds { get; init; } = Array.Empty<string>();
        public Dictionary<string, List<string>> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
