using System.Net;
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
    Dictionary<string, List<string>> Tags,
    string? DeezerUrl = null)
{
    public DeezSpoTag.Web.Services.AutoTag.AutoTagTrack? SongDetails { get; init; }
    public double? AnalysisBpm { get; init; }
    public int DetailsVersion { get; init; }
    public bool DetailsAlgorithmicAnalysis { get; init; }
    public string? DetailsPageId { get; init; }
    public DateTimeOffset? DetailsRetrievedAt { get; init; }
}

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
    private const int MaxSessionCardCacheEntries = 2000;
    private static readonly TimeSpan PortedDiscoverTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SessionCardCacheTtl = TimeSpan.FromHours(12);
    private static readonly object SessionCardCacheTrimLock = new();

    private readonly HttpClient _httpClient;
    private readonly ShazamRelatedRuntimeService? _relatedRuntime;
    private readonly ILogger<ShazamDiscoveryService> _logger;
    private readonly IWebHostEnvironment _environment;
    private static readonly ConcurrentDictionary<string, SessionCardCacheEntry> SessionCardCache = new(StringComparer.OrdinalIgnoreCase);

    public ShazamDiscoveryService(
        HttpClient httpClient,
        ILogger<ShazamDiscoveryService> logger,
        IWebHostEnvironment environment,
        ShazamRelatedRuntimeService? relatedRuntime = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _environment = environment;
        _relatedRuntime = relatedRuntime;
        if (_httpClient.Timeout <= TimeSpan.Zero)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }
    }

    private const int SongDetailsVersion = 1;
    private const int MaxSongDetailsBytes = 8 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, Lazy<Task<ShazamTrackCard?>>> _detailRequests = new(StringComparer.Ordinal);

    public async Task<ShazamTrackCard?> GetSongDetailsAsync(ShazamTrackCard track,
        CancellationToken cancellationToken = default, bool refresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageId = SongDetailsPageId(track.AppleMusicUrl);
        if (pageId is null || !NumericDetailsId(track.Id)) return null;
        var key = $"{track.Id}-{pageId}-{Country}-{Language}";
        var pending = _detailRequests.GetOrAdd(key, _ => new Lazy<Task<ShazamTrackCard?>>(
            () => FetchSongDetailsAsync(track, pageId, key, refresh, cancellationToken)));
        try
        {
            var result = await pending.Value.WaitAsync(cancellationToken);
            return result is not null && SameDetailsRecording(track, result) ? result : null;
        }
        finally
        {
            if (pending.IsValueCreated)
            {
                if (pending.Value.IsCompleted) _detailRequests.TryRemove(new KeyValuePair<string, Lazy<Task<ShazamTrackCard?>>>(key, pending));
                else _ = pending.Value.ContinueWith(_ =>
                    _detailRequests.TryRemove(new KeyValuePair<string, Lazy<Task<ShazamTrackCard?>>>(key, pending)),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task<ShazamTrackCard?> FetchSongDetailsAsync(ShazamTrackCard expected, string pageId,
        string key, bool refresh, CancellationToken token)
    {
        var path = Path.Join(AppDataPaths.GetDataRoot(_environment), "shazam-song-details", key + ".json");
        ShazamTrackCard? cached = null;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= MaxSongDetailsBytes)
            {
                var value = JsonSerializer.Deserialize<ShazamTrackCard>(await File.ReadAllTextAsync(path, token));
                if (value is { DetailsVersion: SongDetailsVersion, SongDetails: not null }
                    && value.DetailsPageId == pageId && SameDetailsRecording(expected, value)) cached = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _logger.LogDebug(ex, "Cannot read optional Shazam details cache"); }
        if (!refresh && cached?.DetailsRetrievedAt > DateTimeOffset.UtcNow.AddDays(-7)) return cached;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var slug = string.Join("-", System.Text.RegularExpressions.Regex.Matches(expected.Title.ToLowerInvariant(), @"[\p{L}\p{N}]+")
                    .Select(match => match.Value));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.shazam.com/song/{pageId}/{Uri.EscapeDataString(slug)}");
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Sec-CH-UA", "\"Not:A-Brand\";v=\"8\", \"Chromium\";v=\"156\", \"Google Chrome\";v=\"156\"");
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/jxl,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
                request.Headers.TryAddWithoutValidation("Accept-Language", Language);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token);
                if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400), bounded.Token);
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxSongDetailsBytes) return cached;
                await using var rawStream = await response.Content.ReadAsStreamAsync(bounded.Token);
                Stream stream = rawStream;
                // Some injected HttpClients decompress automatically (and remove these headers),
                // others do not. Bound the decoded document in either case.
                foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
                {
                    stream = encoding.ToLowerInvariant() switch
                    {
                        "gzip" => new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress),
                        "deflate" => new System.IO.Compression.DeflateStream(stream, System.IO.Compression.CompressionMode.Decompress),
                        "br" => new System.IO.Compression.BrotliStream(stream, System.IO.Compression.CompressionMode.Decompress),
                        "identity" => stream,
                        _ => throw new HttpRequestException("Unsupported Shazam document encoding")
                    };
                }
                await using var decodedStream = stream;
                using var body = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await stream.ReadAsync(buffer, bounded.Token)) > 0)
                {
                    if (body.Length + count > MaxSongDetailsBytes) return cached;
                    body.Write(buffer, 0, count);
                }
                var accepted = ParseSongDetails(expected, Encoding.UTF8.GetString(body.ToArray()));
                if (accepted is null) return cached;
                if (cached?.SongDetails is { } previous && accepted.SongDetails is { } fresh)
                {
                    fresh.Bpm ??= previous.Bpm;
                    fresh.Melodicness ??= previous.Melodicness;
                    fresh.Danceability ??= previous.Danceability;
                    fresh.Energy ??= previous.Energy;
                    fresh.Valence ??= previous.Valence;
                    fresh.Acousticness ??= previous.Acousticness;
                    foreach (var entry in previous.Other) fresh.Other.TryAdd(entry.Key, entry.Value);
                    accepted = accepted with { AnalysisBpm = accepted.AnalysisBpm ?? cached.AnalysisBpm };
                }
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(accepted), bounded.Token);
                    File.Move(temporary, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _logger.LogDebug(ex, "Cannot save optional Shazam details cache"); }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug(ex, "Cannot remove optional Shazam details temporary file"); }
                }
                return accepted;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { _logger.LogDebug("Optional Shazam details timed out"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { _logger.LogDebug(ex, "Optional Shazam details request failed"); }
        return cached;
    }

    private static bool NumericDetailsId(string? value) => value is { Length: > 0 and <= 20 } && value.All(char.IsAsciiDigit);

    private static string? SongDetailsPageId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        if (uri.Host.Equals("music.apple.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            if (query.TryGetValue("i", out var id) && NumericDetailsId(id.ToString())) return id.ToString();
        }
        else if (!uri.Host.Equals("www.shazam.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.Equals("shazam.com", StringComparison.OrdinalIgnoreCase)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, @"/song/(\d+)(?:/|$)");
        return match.Success && NumericDetailsId(match.Groups[1].Value) ? match.Groups[1].Value : null;
    }

    private static string DetailsIdentityText(string? text) => string.Concat((text ?? "").Normalize()
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static bool SameDetailsRecording(ShazamTrackCard expected, ShazamTrackCard actual) => expected.Id == actual.Id
        && DetailsIdentityText(expected.Title) == DetailsIdentityText(actual.Title)
        && DetailsIdentityText(expected.Artist) == DetailsIdentityText(actual.Artist)
        && (string.IsNullOrWhiteSpace(expected.Isrc) || string.IsNullOrWhiteSpace(actual.Isrc)
            || DetailsIdentityText(expected.Isrc) == DetailsIdentityText(actual.Isrc))
        && (!expected.DurationMs.HasValue || !actual.DurationMs.HasValue
            || Math.Abs((long)expected.DurationMs.Value - actual.DurationMs.Value) <= 2000);

    private static ShazamTrackCard? ParseSongDetails(ShazamTrackCard expected, string html)
    {
        if (html.Length > MaxSongDetailsBytes || SongDetailsPageId(expected.AppleMusicUrl) is not { } pageId) return null;
        var options = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        var timeout = TimeSpan.FromSeconds(2);
        try
        {
            var canonicals = System.Text.RegularExpressions.Regex.Matches(html, @"<link\b[^>]*>", options, timeout)
                .Select(m => m.Value).Where(link => System.Text.RegularExpressions.Regex.IsMatch(link,
                    "\\brel=[\"']canonical[\"']", options, timeout)).ToList();
            if (canonicals.Count != 1) return null;
            var href = System.Text.RegularExpressions.Regex.Match(canonicals[0], "\\bhref=[\"']([^\"']+)[\"']", options, timeout);
            if (!href.Success || !Uri.TryCreate(System.Net.WebUtility.HtmlDecode(href.Groups[1].Value), UriKind.Absolute, out var canonical)
                || canonical.Host is not ("www.shazam.com" or "shazam.com") || SongDetailsPageId(canonical.AbsoluteUri) != pageId) return null;
            var flight = new StringBuilder();
            foreach (System.Text.RegularExpressions.Match script in System.Text.RegularExpressions.Regex.Matches(html,
                @"self\.__next_f\.push\((.*?)\)</script>", System.Text.RegularExpressions.RegexOptions.Singleline, timeout))
            {
                using var chunk = JsonDocument.Parse(script.Groups[1].Value);
                if (chunk.RootElement.ValueKind == JsonValueKind.Array && chunk.RootElement.GetArrayLength() == 2
                    && chunk.RootElement[0].ValueKind == JsonValueKind.Number && chunk.RootElement[0].GetInt32() == 1
                    && chunk.RootElement[1].ValueKind == JsonValueKind.String) flight.Append(chunk.RootElement[1].GetString());
            }
            var frames = ReadDetailsFrames(Encoding.UTF8.GetBytes(flight.ToString()));
            var nodes = frames.Values.SelectMany(WalkDetailsJson).ToList();
            var mains = nodes.Where(node => node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object
                && DetailsString(track, "type") == "shazam-songs" && DetailsString(track, "id") == expected.Id
                && node.TryGetProperty("audioAnalysis", out _)).ToList();
            if (mains.Count != 1) return null;
            var main = mains[0];
            var attributes = main.GetProperty("track").GetProperty("attributes");
            var actual = expected with { Title = DetailsString(attributes, "title") ?? "", Artist = DetailsString(attributes, "artist") ?? "",
                Isrc = DetailsString(attributes, "isrc"), DurationMs = attributes.TryGetProperty("durationInMillis", out var duration)
                    && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var ms) ? ms : null };
            if (!SameDetailsRecording(expected, actual)) return null;
            var analysis = main.GetProperty("audioAnalysis");
            double? Unit(string name) => DetailsNumber(analysis, name) is { } value && value is >= 0 and <= 1 ? value : null;
            var bpm = DetailsNumber(analysis, "bpm");
            if (bpm is < 0.5 or >= long.MaxValue) bpm = null;
            var details = new DeezSpoTag.Web.Services.AutoTag.AutoTagTrack {
                Title = actual.Title, Artists = [actual.Artist], Isrc = actual.Isrc ?? expected.Isrc,
                Duration = (actual.DurationMs ?? expected.DurationMs) is { } durationMs ? TimeSpan.FromMilliseconds(durationMs) : null,
                Bpm = bpm.HasValue ? (long)Math.Round(bpm.Value, MidpointRounding.AwayFromZero) : null,
                Melodicness = Unit("melodicness"), Acousticness = Unit("acousticness"), Valence = Unit("valence"),
                Danceability = Unit("danceability"), Energy = Unit("energy") };
            foreach (var props in nodes.Where(node => node.ValueKind == JsonValueKind.Object
                && DetailsString(node, "dataTestId") == "creditPerson" && DetailsString(node, "songId") == pageId
                && DetailsString(node, "providerName") == "credits"))
            {
                if (!props.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) continue;
                var labels = children.EnumerateArray().SelectMany(child => DetailsCreditLabels(child, frames, new HashSet<string>(), 0))
                    .Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
                if (labels.Count != 2) continue;
                foreach (var role in DeezSpoTag.Web.Services.AutoTag.SupportedTagCreditMappings.NormalizeRoles(labels[1]))
                {
                    var key = string.IsNullOrEmpty(role.Key) ? "involvedPeople" : role.Key;
                    if (!details.Other.TryGetValue(key, out var values)) details.Other[key] = values = [];
                    var value = key == "involvedPeople" ? $"{role.OriginalLabel}: {labels[0]}"
                        : key == "performer" && !string.Equals(role.OriginalLabel, "Performer", StringComparison.OrdinalIgnoreCase)
                            ? $"{labels[0]} [{role.OriginalLabel}]" : labels[0];
                    if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
                }
            }
            if (details.Bpm is null && details.Melodicness is null && details.Acousticness is null && details.Valence is null
                && details.Danceability is null && details.Energy is null && details.Other.Count == 0) return null;
            return expected with { SongDetails = details, AnalysisBpm = bpm, DetailsVersion = SongDetailsVersion,
                DetailsPageId = pageId, DetailsAlgorithmicAnalysis = true, DetailsRetrievedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException
            or KeyNotFoundException or System.Text.RegularExpressions.RegexMatchTimeoutException) { return null; }
    }

    private static double? DetailsNumber(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number
        && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static string? DetailsString(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static Dictionary<string, JsonElement> ReadDetailsFrames(byte[] bytes)
    {
        var frames = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < bytes.Length && frames.Count < 12000)
        {
            var colon = Array.IndexOf(bytes, (byte)':', offset);
            if (colon < 0) break;
            var id = colon - offset is > 0 and <= 8 ? Encoding.ASCII.GetString(bytes, offset, colon - offset) : "";
            if (id.Length == 0 || !id.All(char.IsAsciiHexDigit))
            {
                // React preload rows (e.g. :HL[...]) have no frame ID.
                var newline = Array.IndexOf(bytes, (byte)'\n', offset);
                if (newline < 0) break;
                offset = newline + 1;
                continue;
            }
            offset = colon + 1;
            if (offset < bytes.Length && bytes[offset] == 'T')
            {
                var comma = Array.IndexOf(bytes, (byte)',', offset);
                if (comma < 0 || comma - offset > 9 || !int.TryParse(Encoding.ASCII.GetString(bytes, offset + 1, comma - offset - 1),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var size)
                    || size < 0 || size > bytes.Length - comma - 1) break;
                frames[id] = JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(bytes, comma + 1, size));
                offset = comma + 1 + size;
            }
            else
            {
                try
                {
                    var reader = new Utf8JsonReader(bytes.AsSpan(offset));
                    using var document = JsonDocument.ParseValue(ref reader);
                    frames[id] = document.RootElement.Clone();
                    offset += (int)reader.BytesConsumed;
                }
                catch (JsonException)
                {
                    var newline = Array.IndexOf(bytes, (byte)'\n', offset);
                    if (newline < 0) break;
                    offset = newline;
                }
            }
            while (offset < bytes.Length && bytes[offset] is (byte)'\n' or (byte)'\r') offset++;
        }
        return frames;
    }

    private static IEnumerable<JsonElement> WalkDetailsJson(JsonElement node)
    {
        yield return node;
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject()) foreach (var child in WalkDetailsJson(property.Value)) yield return child;
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var value in node.EnumerateArray()) foreach (var child in WalkDetailsJson(value)) yield return child;
    }

    private static IEnumerable<string> DetailsCreditLabels(JsonElement node, Dictionary<string, JsonElement> frames,
        HashSet<string> seen, int depth)
    {
        if (depth > 24) yield break;
        if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString() ?? "";
            if (!text.StartsWith('$')) { yield return text.Trim(); yield break; }
            var reference = text[1..].TrimStart('L', '@');
            if (seen.Add(reference) && frames.TryGetValue(reference, out var target))
                foreach (var label in DetailsCreditLabels(target, frames, seen, depth + 1)) yield return label;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            if (node.GetArrayLength() == 4 && node[0].ValueKind == JsonValueKind.String && node[0].GetString() == "$")
            {
                foreach (var label in DetailsCreditLabels(node[3], frames, seen, depth + 1)) yield return label;
            }
            else foreach (var child in node.EnumerateArray())
                foreach (var label in DetailsCreditLabels(child, frames, seen, depth + 1)) yield return label;
        }
        else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("children", out var children))
            foreach (var label in DetailsCreditLabels(children, frames, seen, depth + 1)) yield return label;
    }

    public async Task<ShazamTrackCard?> GetTrackAsync(string trackId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        trackId = trackId.Trim();
        if (TryGetCachedCard(trackId, out var cached))
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
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(trackId) || trackId.Length > 20 || trackId.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("A numeric Shazam ID is required.", nameof(trackId));
        if (_relatedRuntime is null) throw new HttpRequestException("shazam_helper_unavailable");
        using var document = await _relatedRuntime.GetRelatedAsync(trackId, Language, Country, cancellationToken);
        var cards = ParseTrackList(document.RootElement, MaxTrackLimit);
        for (var index = 0; index < cards.Count; index++)
        {
            CacheCard(cards[index]);
            if (TryGetCachedCard(cards[index].Id, out var cached) && cached is not null) cards[index] = cached;
        }
        return cards.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxTrackLimit)).ToList();
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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(PortedDiscoverTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                _logger.LogWarning(
                    ex,
                    "Shazam port discovery timed out: mode={Mode} python={Python} script={Script}",
                    mode,
                    python,
                    scriptPath);
                return null;
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                // discover.py reports its failure as {"ok": false, "error": ...} on stdout and
                // exits 1, so logging stderr alone recorded "none" for every real failure and
                // hid the cause entirely. Surface both streams.
                _logger.LogWarning(
                    "Shazam port discovery failed: mode={Mode} code={Code} python={Python} script={Script} stdout={Stdout} stderr={Stderr}",
                    mode,
                    process.ExitCode,
                    python,
                    scriptPath,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(string.IsNullOrWhiteSpace(stdout) ? "none" : stdout),
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

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Shazam port process kill skipped: {ex.Message}");
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

        var appleMusicUrl = FirstNonEmpty(TryGetString(track, "appleMusicUrl"), FindFirstUri(track, uri => uri.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase)));
        var spotifyUrl = FirstNonEmpty(TryGetString(track, "spotifyUrl"), FindFirstUri(track, uri => uri.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase)));
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
            GetMetadataValue(metadata, "Composed By"),
            GetMetadataValue(metadata, "Music By"));
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
            tags,
            FirstNonEmpty(TryGetString(track, "deezerUrl"), deezerUrl));
    }

    private static void CacheCard(ShazamTrackCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.Id))
        {
            CacheCardAlias(card.Id, card);
            if (card.Id.StartsWith(AppleMusicIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rawId = card.Id[AppleMusicIdPrefix.Length..];
                if (!string.IsNullOrWhiteSpace(rawId))
                {
                    CacheCardAlias(rawId, card);
                }
            }
        }

        TrimSessionCardCacheIfNeeded();
    }

    private static bool TryGetCachedCard(string trackId, out ShazamTrackCard? card)
    {
        card = null;
        if (!SessionCardCache.TryGetValue(trackId, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            SessionCardCache.TryRemove(trackId, out _);
            return false;
        }

        card = entry.Card;
        return true;
    }

    private static void CacheCardAlias(string key, ShazamTrackCard card)
    {
        var expiry = DateTimeOffset.UtcNow.Add(SessionCardCacheTtl);
        SessionCardCache.AddOrUpdate(key, new SessionCardCacheEntry(card, expiry), (_, previous) =>
        {
            var older = previous.Card;
            if (previous.ExpiresAtUtc <= DateTimeOffset.UtcNow || !string.Equals(older.Id, card.Id, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(older.Isrc) && !string.IsNullOrWhiteSpace(card.Isrc)
                    && !string.Equals(older.Isrc.Trim(), card.Isrc.Trim(), StringComparison.OrdinalIgnoreCase)))
                return new SessionCardCacheEntry(card, expiry);
            // The v2 list can omit links supplied by an earlier detail lookup for this same recording.
            var tags = new Dictionary<string, List<string>>(card.Tags, StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "SHAZAM_DEEZER_URL", "SHAZAM_SPOTIFY_URL", "SHAZAM_APPLE_MUSIC_URL" })
                if (!tags.ContainsKey(name) && older.Tags.TryGetValue(name, out var values)) tags[name] = new List<string>(values);
            return new SessionCardCacheEntry(card with
            {
                Isrc = FirstNonEmpty(card.Isrc, older.Isrc),
                DurationMs = card.DurationMs ?? older.DurationMs,
                Url = FirstNonEmpty(card.Url, older.Url),
                AppleMusicUrl = FirstNonEmpty(card.AppleMusicUrl, older.AppleMusicUrl),
                SpotifyUrl = FirstNonEmpty(card.SpotifyUrl, older.SpotifyUrl),
                DeezerUrl = FirstNonEmpty(card.DeezerUrl, older.DeezerUrl),
                Tags = tags
            }, expiry);
        });
    }

    private static void TrimSessionCardCacheIfNeeded()
    {
        if (SessionCardCache.Count <= MaxSessionCardCacheEntries)
        {
            return;
        }

        lock (SessionCardCacheTrimLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in SessionCardCache.Where(candidate => candidate.Value.ExpiresAtUtc <= now))
            {
                SessionCardCache.TryRemove(pair.Key, out _);
            }

            var overflow = SessionCardCache.Count - MaxSessionCardCacheEntries;
            if (overflow <= 0)
            {
                return;
            }

            foreach (var key in SessionCardCache
                         .OrderBy(static pair => pair.Value.ExpiresAtUtc)
                         .Take(overflow)
                         .Select(static pair => pair.Key)
                         .ToList())
            {
                SessionCardCache.TryRemove(key, out _);
            }
        }
    }

    private sealed record SessionCardCacheEntry(ShazamTrackCard Card, DateTimeOffset ExpiresAtUtc);

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
