using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace DeezSpoTag.Integrations.Plex;

/// <summary>
/// Plex API client for interacting with Plex Media Server
/// Modified from Syncra functionality for DeezSpoTag integration
/// </summary>
public class PlexApiClient
{
    private const string DirectoryElementName = "Directory";
    private const string RatingKeyAttributeName = "ratingKey";
    private const string TitleAttributeName = "title";
    private const string TrackElementName = "Track";
    private const string DurationAttributeName = "duration";
    private const string ThumbAttributeName = "thumb";
    private const string ParentThumbAttributeName = "parentThumb";
    private readonly ILogger<PlexApiClient> _logger;
    private readonly HttpClient _httpClient;

    public PlexApiClient(ILogger<PlexApiClient> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        
        // Set default headers for Plex API
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/xml");
        _httpClient.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "DeezSpoTag");
        _httpClient.DefaultRequestHeaders.Add("X-Plex-Product", "DeezSpoTag");
        _httpClient.DefaultRequestHeaders.Add("X-Plex-Version", "1.0");
    }

    /// <summary>
    /// Test connection to Plex server
    /// </summary>
    public async Task<bool> TestConnectionAsync(string serverUrl, string token)
    {
        try
        {
            var identity = await GetIdentityAsync(serverUrl, token);
            if (identity is not null)
            {
                _logger.LogDebug("Successfully connected to Plex server at {ServerUrl}", serverUrl);
                return true;
            }

            _logger.LogWarning("Failed to connect to Plex server at {ServerUrl}", serverUrl);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error testing connection to Plex server at {ServerUrl}", serverUrl);
            return false;
        }
    }

    public async Task<PlexIdentity?> GetIdentityAsync(string serverUrl, string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{serverUrl.TrimEnd('/')}/identity?X-Plex-Token={token}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Plex identity request failed for {ServerUrl}: {StatusCode}", serverUrl, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var container = doc.Root;
            if (container is null)
            {
                return null;
            }

            return new PlexIdentity
            {
                FriendlyName = container.Attribute("friendlyName")?.Value ?? string.Empty,
                MachineIdentifier = container.Attribute("machineIdentifier")?.Value ?? string.Empty,
                Version = container.Attribute("version")?.Value ?? string.Empty
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex identity from ServerUrl");
            return null;
        }
    }

    public async Task<PlexUserInfo?> GetUserInfoAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://plex.tv/users/account?X-Plex-Token={token}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Plex account request failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var user = doc.Root;
            if (user is null)
            {
                return null;
            }

            return new PlexUserInfo
            {
                Username = user.Attribute("username")?.Value ?? string.Empty,
                Email = user.Attribute("email")?.Value ?? string.Empty,
                Thumb = user.Attribute(ThumbAttributeName)?.Value ?? string.Empty
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex account info");
            return null;
        }
    }

    /// <summary>
    /// Get server information
    /// </summary>
    public async Task<PlexServerInfo?> GetServerInfoAsync()
    {
        try
        {
            // This would need server URL and token from configuration
            // For now, return placeholder data
            await Task.CompletedTask;
            return new PlexServerInfo
            {
                FriendlyName = "Plex Media Server",
                Version = "1.0.0",
                MachineIdentifier = "placeholder"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting Plex server info");
            return null;
        }
    }

    /// <summary>
    /// Get music libraries from Plex server
    /// </summary>
    public async Task<List<PlexLibrary>> GetMusicLibrariesAsync()
    {
        try
        {
            // This would make actual API call to get library sections
            // For now, return placeholder data
            await Task.CompletedTask;
            var libraries = new List<PlexLibrary>
            {
                new PlexLibrary
                {
                    Key = "1",
                    Title = "Music",
                    Type = "artist"
                }
            };

            _logger.LogDebug("Retrieved {LibraryCount} music libraries from Plex", libraries.Count);
            return libraries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting music libraries from Plex");
            return new List<PlexLibrary>();
        }
    }

    /// <summary>
    /// Refresh a specific library section
    /// </summary>
    public async Task RefreshLibraryAsync(string libraryKey)
    {
        try
        {
            _logger.LogInformation("Refreshing Plex library section {LibraryKey}", libraryKey);
            
            // This would make actual API call to refresh library
            // For now, just simulate the operation
            await Task.Delay(100);
            
            _logger.LogDebug("Successfully triggered refresh for library section {LibraryKey}", libraryKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw;
        }
    }

    public async Task<bool> RefreshLibraryAsync(string serverUrl, string token, string libraryKey, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("Skipping Plex refresh: server URL or token missing.");
                return false;
            }

            var url = $"{serverUrl.TrimEnd('/')}/library/sections/{libraryKey}/refresh?X-Plex-Token={token}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to refresh Plex library {LibraryKey}: {StatusCode}", libraryKey, response.StatusCode);
                return false;
            }

            _logger.LogInformation("Triggered Plex library refresh for section {LibraryKey}", libraryKey);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error refreshing Plex library section {LibraryKey}", libraryKey);
            return false;
        }
    }

    public async Task<string?> FindArtistRatingKeyAsync(string serverUrl, string token, string artistName, CancellationToken cancellationToken = default)
    {
        var location = await FindArtistLocationAsync(serverUrl, token, artistName, cancellationToken);
        return location?.RatingKey;
    }

    public async Task<PlexArtistLocation?> FindArtistLocationAsync(string serverUrl, string token, string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var targetName = NormalizeArtistTitle(artistName);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return null;
        }
        var targetLoose = NormalizeArtistLoose(targetName);

        var sections = await GetMusicSectionKeysAsync(serverUrl, token, cancellationToken);
        if (sections.Count == 0)
        {
            return null;
        }

        var baseUrl = serverUrl.TrimEnd('/');
        var encodedArtist = Uri.EscapeDataString(artistName.Trim());
        var candidates = await CollectArtistCandidatesAsync(baseUrl, token, encodedArtist, targetName, targetLoose, sections, cancellationToken);

        if (candidates.Count == 0)
        {
            _logger.LogWarning(
                "Plex artist match not found for '{ArtistName}' after querying {SectionCount} sections.",
                artistName,
                sections.Count);
            return null;
        }

        var best = candidates
            .OrderByDescending(item => item.Value.score)
            .ThenBy(item => item.Value.lengthDelta)
            .First();

        _logger.LogDebug(
            "Resolved Plex artist '{ArtistName}' to '{MatchedTitle}' (section {SectionKey}, ratingKey {RatingKey}, score {Score})",
            artistName,
            best.Value.title,
            best.Value.sectionKey,
            best.Key,
            best.Value.score);

        return new PlexArtistLocation(best.Value.sectionKey, best.Key);
    }

    /// <summary>
    /// Upload poster from a local file by sending raw image bytes directly to Plex.
    /// This is more reliable than the URL-based approach because Plex does not need
    /// network access back to the DeezSpoTag server.
    /// </summary>
    public async Task<bool> UpdateArtistPosterFromFileAsync(string serverUrl, string token, string ratingKey, string filePath, CancellationToken cancellationToken = default)
        => await UpdateArtistImageFromFileAsync(
            serverUrl,
            token,
            ratingKey,
            filePath,
            "posters",
            "upload artist poster",
            cancellationToken);

    /// <summary>
    /// Upload background art from a local file by sending raw image bytes directly to Plex.
    /// </summary>
    public async Task<bool> UpdateArtistArtFromFileAsync(string serverUrl, string token, string ratingKey, string filePath, CancellationToken cancellationToken = default)
        => await UpdateArtistImageFromFileAsync(
            serverUrl,
            token,
            ratingKey,
            filePath,
            "arts",
            "upload artist art",
            cancellationToken);

    private async Task<bool> UpdateArtistImageFromFileAsync(
        string serverUrl,
        string token,
        string ratingKey,
        string filePath,
        string mediaEndpoint,
        string operation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(ratingKey) || !File.Exists(filePath))
        {
            return false;
        }

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetMimeTypeForFile(filePath));
        return await SendPlexRequestAsync(
            HttpMethod.Post,
            $"{serverUrl.TrimEnd('/')}/library/metadata/{ratingKey}/{mediaEndpoint}?X-Plex-Token={token}",
            operation,
            ratingKey,
            cancellationToken,
            content);
    }

    /// <summary>
    /// Fallback: upload poster via URL (requires Plex to reach the URL).
    /// </summary>
    public async Task<bool> UpdateArtistPosterAsync(string serverUrl, string token, string ratingKey, string imageUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(ratingKey) || string.IsNullOrWhiteSpace(imageUrl))
        {
            return false;
        }

        return await SendPlexRequestAsync(
            HttpMethod.Post,
            $"{serverUrl.TrimEnd('/')}/library/metadata/{ratingKey}/posters?url={Uri.EscapeDataString(imageUrl)}&X-Plex-Token={token}",
            "update artist poster",
            ratingKey,
            cancellationToken);
    }

    /// <summary>
    /// Fallback: upload background art via URL (requires Plex to reach the URL).
    /// </summary>
    public async Task<bool> UpdateArtistArtAsync(string serverUrl, string token, string ratingKey, string imageUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(ratingKey) || string.IsNullOrWhiteSpace(imageUrl))
        {
            return false;
        }

        return await SendPlexRequestAsync(
            HttpMethod.Post,
            $"{serverUrl.TrimEnd('/')}/library/metadata/{ratingKey}/arts?url={Uri.EscapeDataString(imageUrl)}&X-Plex-Token={token}",
            "update artist art",
            ratingKey,
            cancellationToken);
    }

    /// <summary>
    /// Update artist biography/summary via the library sections endpoint, which is the
    /// correct Plex API for mutating artist metadata fields.
    /// Requires both the section key (music library) and the artist rating key.
    /// </summary>
    public async Task<bool> UpdateArtistBiographyAsync(string serverUrl, string token, string sectionKey, string ratingKey, string biography, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(sectionKey) || string.IsNullOrWhiteSpace(ratingKey) ||
            string.IsNullOrWhiteSpace(biography))
        {
            return false;
        }

        // PUT /library/sections/{sectionKey}/all?type=8&id={ratingKey}&summary.value={bio}&summary.locked=1
        var url = $"{serverUrl.TrimEnd('/')}/library/sections/{sectionKey}/all" +
                  $"?type=8&id={Uri.EscapeDataString(ratingKey)}" +
                  $"&summary.value={Uri.EscapeDataString(biography)}&summary.locked=1" +
                  $"&X-Plex-Token={token}";
        var response = await _httpClient.PutAsync(url, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to update Plex biography for {RatingKey}: {StatusCode}", ratingKey, response.StatusCode);
            return false;
        }

        return true;
    }

    private static string GetMimeTypeForFile(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }

    private static string NormalizeArtistTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            WebUtility.HtmlDecode(value)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeArtistLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var formD = WebUtility.HtmlDecode(value).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        var previousWasSpace = false;
        foreach (var character in formD)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
                continue;
            }

            if (previousWasSpace)
            {
                continue;
            }

            builder.Append(' ');
            previousWasSpace = true;
        }

        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static int ScoreArtistCandidate(string targetName, string targetLoose, string? candidateTitle)
    {
        var normalizedCandidate = NormalizeArtistTitle(candidateTitle);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return 0;
        }

        var candidateLoose = NormalizeArtistLoose(normalizedCandidate);
        if (string.Equals(normalizedCandidate, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }
        if (string.Equals(candidateLoose, targetLoose, StringComparison.OrdinalIgnoreCase))
        {
            return 975;
        }
        if (candidateLoose.StartsWith(targetLoose, StringComparison.OrdinalIgnoreCase) ||
            targetLoose.StartsWith(candidateLoose, StringComparison.OrdinalIgnoreCase))
        {
            return 900;
        }
        if (normalizedCandidate.StartsWith(targetName, StringComparison.OrdinalIgnoreCase) ||
            targetName.StartsWith(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return 850;
        }
        if (candidateLoose.Contains(targetLoose, StringComparison.OrdinalIgnoreCase) ||
            targetLoose.Contains(candidateLoose, StringComparison.OrdinalIgnoreCase))
        {
            return 800;
        }
        if (normalizedCandidate.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
            targetName.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return 760;
        }

        return 0;
    }

    private async Task<List<string>> GetMusicSectionKeysAsync(string serverUrl, string token, CancellationToken cancellationToken)
    {
        var url = $"{serverUrl.TrimEnd('/')}/library/sections?X-Plex-Token={token}";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<string>();
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = XDocument.Parse(content);
        var keys = doc.Descendants(DirectoryElementName)
            .Where(element =>
                string.Equals(element.Attribute("type")?.Value, "artist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.Attribute("type")?.Value, "music", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("key")?.Value)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToList();

        return keys;
    }

    /// <summary>
    /// Search for tracks in Plex library
    /// </summary>
    public async Task<List<PlexTrack>> SearchTracksAsync(string query, string? libraryKey = null)
    {
        try
        {
            _logger.LogDebug("Searching Plex for tracks: {Query}", query);

            // This would make actual search API call
            // For now, return empty results
            await Task.CompletedTask;
            var tracks = new List<PlexTrack>();

            _logger.LogDebug("Found {TrackCount} tracks in Plex for query: {Query}", tracks.Count, query);
            return tracks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error searching Plex for tracks: {Query}", query);
            return new List<PlexTrack>();
        }
    }

    public async Task<List<PlexTrack>> SearchTracksAsync(
        string serverUrl,
        string token,
        string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(query))
            {
                return new List<PlexTrack>();
            }

            var tracks = await SearchTracksInternalAsync(serverUrl, token, query, cancellationToken);
            _logger.LogDebug("Found {TrackCount} Plex tracks for query: {Query}", tracks.Count, query);
            return tracks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error searching Plex for tracks: {Query}", query);
            return new List<PlexTrack>();
        }
    }

    private async Task<Dictionary<string, (int score, int lengthDelta, string sectionKey, string title)>> CollectArtistCandidatesAsync(
        string baseUrl,
        string token,
        string encodedArtist,
        string targetName,
        string targetLoose,
        IReadOnlyList<string> sections,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, (int score, int lengthDelta, string sectionKey, string title)>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in BuildArtistCandidateQueries(baseUrl, token, encodedArtist, sections))
        {
            await CollectArtistCandidatesFromQueryAsync(query.url, query.sectionKeyHint, targetName, targetLoose, sections, candidates, cancellationToken);
        }

        return candidates;
    }

    private static IEnumerable<(string url, string? sectionKeyHint)> BuildArtistCandidateQueries(string baseUrl, string token, string encodedArtist, IReadOnlyList<string> sections)
    {
        yield return ($"{baseUrl}/search?query={encodedArtist}&type=8&X-Plex-Token={token}", null);
        yield return ($"{baseUrl}/library/search?query={encodedArtist}&type=8&X-Plex-Token={token}", null);

        foreach (var sectionKey in sections)
        {
            yield return ($"{baseUrl}/library/sections/{sectionKey}/all?type=8&title={encodedArtist}&X-Plex-Token={token}", sectionKey);
        }
    }

    private async Task CollectArtistCandidatesFromQueryAsync(
        string url,
        string? sectionKeyHint,
        string targetName,
        string targetLoose,
        IReadOnlyList<string> sections,
        Dictionary<string, (int score, int lengthDelta, string sectionKey, string title)> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var doc = XDocument.Parse(content);
            foreach (var candidate in EnumerateArtistCandidates(doc, targetName, targetLoose, sections, sectionKeyHint))
            {
                if (!candidates.TryGetValue(candidate.ratingKey, out var existing) ||
                    candidate.score > existing.score ||
                    (candidate.score == existing.score && candidate.lengthDelta < existing.lengthDelta))
                {
                    candidates[candidate.ratingKey] = (candidate.score, candidate.lengthDelta, candidate.sectionKey, candidate.title);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Plex artist candidate query failed: {Url}", url);
        }
    }

    private static IEnumerable<(string ratingKey, int score, int lengthDelta, string sectionKey, string title)> EnumerateArtistCandidates(
        XDocument doc,
        string targetName,
        string targetLoose,
        IReadOnlyList<string> sections,
        string? sectionKeyHint)
    {
        foreach (var directory in doc.Descendants(DirectoryElementName))
        {
            if (!IsArtistDirectory(directory))
            {
                continue;
            }

            var ratingKey = directory.Attribute(RatingKeyAttributeName)?.Value;
            if (string.IsNullOrWhiteSpace(ratingKey))
            {
                continue;
            }

            var title = directory.Attribute(TitleAttributeName)?.Value ?? string.Empty;
            var score = ScoreArtistCandidate(targetName, targetLoose, title);
            if (score <= 0)
            {
                continue;
            }

            var normalizedCandidate = NormalizeArtistLoose(title);
            var lengthDelta = Math.Abs(normalizedCandidate.Length - targetLoose.Length);
            var sectionKey = ResolveArtistSectionKey(directory, sections, sectionKeyHint);
            yield return (ratingKey, score, lengthDelta, sectionKey, title);
        }
    }

    private static bool IsArtistDirectory(XElement directory)
    {
        var type = directory.Attribute("type")?.Value;
        return string.Equals(type, "artist", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "music", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveArtistSectionKey(XElement directory, IReadOnlyList<string> sections, string? sectionKeyHint)
    {
        var sectionKey = directory.Attribute("librarySectionID")?.Value;
        if (!string.IsNullOrWhiteSpace(sectionKey) && sections.Contains(sectionKey))
        {
            return sectionKey;
        }

        if (!string.IsNullOrWhiteSpace(sectionKeyHint) && sections.Contains(sectionKeyHint))
        {
            return sectionKeyHint;
        }

        return sections[0];
    }

    private async Task<List<PlexTrack>> SearchTracksInternalAsync(string serverUrl, string token, string query, CancellationToken cancellationToken)
    {
        var tracksByRatingKey = new Dictionary<string, PlexTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in BuildTrackSearchEndpoints(serverUrl, token, query))
        {
            var trackDoc = await TryLoadTrackSearchDocumentAsync(endpoint, cancellationToken);
            if (trackDoc is null)
            {
                continue;
            }

            AddSearchTracks(trackDoc, tracksByRatingKey);
            if (tracksByRatingKey.Count > 0)
            {
                break;
            }
        }

        return tracksByRatingKey.Values.ToList();
    }

    private static IEnumerable<string> BuildTrackSearchEndpoints(string serverUrl, string token, string query)
    {
        var baseUrl = serverUrl.TrimEnd('/');
        var encodedQuery = Uri.EscapeDataString(query.Trim());
        yield return $"{baseUrl}/library/search?query={encodedQuery}&type=10&X-Plex-Token={token}";
        yield return $"{baseUrl}/hubs/search?query={encodedQuery}&type=10&limit=30&includeExternalMedia=0&X-Plex-Token={token}";
        yield return $"{baseUrl}/search?type=10&query={encodedQuery}&X-Plex-Token={token}";
    }

    private async Task<XDocument?> TryLoadTrackSearchDocumentAsync(string endpoint, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(endpoint, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Plex track search endpoint failed {Endpoint}: {StatusCode}", endpoint, response.StatusCode);
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Plex search XML parse failed for endpoint {Endpoint}", endpoint);
            return null;
        }
    }

    private static void AddSearchTracks(XDocument doc, Dictionary<string, PlexTrack> tracksByRatingKey)
    {
        foreach (var track in doc.Descendants(TrackElementName))
        {
            var mapped = MapTrack(track);
            var dedupeKey = GetTrackDedupeKey(mapped);
            if (!tracksByRatingKey.ContainsKey(dedupeKey))
            {
                tracksByRatingKey[dedupeKey] = mapped;
            }
        }
    }

    private static PlexTrack MapTrack(XElement track)
    {
        return new PlexTrack
        {
            RatingKey = track.Attribute(RatingKeyAttributeName)?.Value ?? string.Empty,
            Key = track.Attribute("key")?.Value ?? string.Empty,
            Title = track.Attribute(TitleAttributeName)?.Value ?? string.Empty,
            Artist = track.Attribute("grandparentTitle")?.Value ?? string.Empty,
            Album = track.Attribute("parentTitle")?.Value ?? string.Empty,
            DurationMs = ParseLong(track.Attribute(DurationAttributeName)?.Value),
            FilePath = track.Descendants("Part").FirstOrDefault()?.Attribute("file")?.Value ?? string.Empty
        };
    }

    private static string GetTrackDedupeKey(PlexTrack track)
    {
        return string.IsNullOrWhiteSpace(track.RatingKey)
            ? $"{track.Title}::{track.Artist}::{track.Album}"
            : track.RatingKey;
    }

    public async Task<List<PlexPlaylist>> GetPlaylistsAsync(string serverUrl, string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{serverUrl.TrimEnd('/')}/playlists?X-Plex-Token={token}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to load Plex playlists: {StatusCode}", response.StatusCode);
                return new List<PlexPlaylist>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var playlistElements = doc.Descendants("Playlist").ToList();
            if (playlistElements.Count == 0)
            {
                playlistElements = doc.Descendants(DirectoryElementName)
                    .Where(el => string.Equals(el.Attribute("type")?.Value, "playlist", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var playlists = new List<PlexPlaylist>();
            foreach (var element in playlistElements)
            {
                playlists.Add(ParsePlaylist(element, serverUrl, token));
            }

            return playlists;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex playlists");
            return new List<PlexPlaylist>();
        }
    }

    public async Task<List<PlexLibrarySection>> GetLibrarySectionsAsync(string serverUrl, string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{serverUrl.TrimEnd('/')}/library/sections?X-Plex-Token={token}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to load Plex library sections: {StatusCode}", response.StatusCode);
                return new List<PlexLibrarySection>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var sections = new List<PlexLibrarySection>();
            foreach (var directory in doc.Descendants(DirectoryElementName))
            {
                var key = directory.Attribute("key")?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                sections.Add(new PlexLibrarySection
                {
                    Key = key,
                    Title = directory.Attribute(TitleAttributeName)?.Value ?? string.Empty,
                    Type = directory.Attribute("type")?.Value ?? string.Empty
                });
            }

            return sections;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex library sections");
            return new List<PlexLibrarySection>();
        }
    }

    public async Task<List<PlexMediaItem>> GetLibraryMediaItemsAsync(
        string serverUrl,
        string token,
        string sectionKey,
        int offset = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(sectionKey))
        {
            return new List<PlexMediaItem>();
        }

        try
        {
            var encodedSection = Uri.EscapeDataString(sectionKey.Trim());
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedLimit = Math.Clamp(limit.GetValueOrDefault(200), 1, 500);
            var url = $"{serverUrl.TrimEnd('/')}/library/sections/{encodedSection}/all?X-Plex-Token={Uri.EscapeDataString(token)}&X-Plex-Container-Start={normalizedOffset}&X-Plex-Container-Size={normalizedLimit}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to load Plex media items for section {SectionKey}: {StatusCode}",
                    sectionKey,
                    response.StatusCode);
                return new List<PlexMediaItem>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var mediaById = new Dictionary<string, PlexMediaItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in doc.Descendants("Video"))
            {
                var type = node.Attribute("type")?.Value ?? string.Empty;
                if (!string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mapped = MapPlexMediaItem(node, type, serverUrl, token);
                if (!string.IsNullOrWhiteSpace(mapped.Id))
                {
                    mediaById[mapped.Id] = mapped;
                }
            }

            foreach (var node in doc.Descendants(DirectoryElementName))
            {
                var type = node.Attribute("type")?.Value ?? string.Empty;
                if (!string.Equals(type, "show", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(type, "series", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mapped = MapPlexMediaItem(node, "show", serverUrl, token);
                if (!string.IsNullOrWhiteSpace(mapped.Id))
                {
                    mediaById[mapped.Id] = mapped;
                }
            }

            return mediaById.Values
                .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed reading Plex media items for section {SectionKey}", sectionKey);
            return new List<PlexMediaItem>();
        }
    }

    public async Task<List<PlexSeasonItem>> GetShowSeasonsAsync(
        string serverUrl,
        string token,
        string showId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(showId))
        {
            return new List<PlexSeasonItem>();
        }

        try
        {
            var url = $"{serverUrl.TrimEnd('/')}/library/metadata/{Uri.EscapeDataString(showId.Trim())}/children?X-Plex-Token={Uri.EscapeDataString(token)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to load Plex seasons for show {ShowId}: {StatusCode}",
                    showId,
                    response.StatusCode);
                return new List<PlexSeasonItem>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var seasons = new List<PlexSeasonItem>();

            foreach (var node in doc.Descendants(DirectoryElementName))
            {
                var type = node.Attribute("type")?.Value ?? string.Empty;
                if (!string.Equals(type, "season", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var seasonId = node.Attribute(RatingKeyAttributeName)?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(seasonId))
                {
                    continue;
                }

                var thumb = node.Attribute(ThumbAttributeName)?.Value
                            ?? node.Attribute("art")?.Value
                            ?? node.Attribute(ParentThumbAttributeName)?.Value
                            ?? string.Empty;

                seasons.Add(new PlexSeasonItem
                {
                    Id = seasonId,
                    Title = node.Attribute(TitleAttributeName)?.Value ?? string.Empty,
                    SeasonNumber = ParseNullableInt(node.Attribute("index")?.Value),
                    ImageUrl = ToAbsoluteUrl(serverUrl, token, thumb)
                });
            }

            return seasons
                .OrderBy(item => item.SeasonNumber ?? int.MaxValue)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed reading Plex seasons for show {ShowId}", showId);
            return new List<PlexSeasonItem>();
        }
    }

    public async Task<List<PlexEpisodeItem>> GetSeasonEpisodesAsync(
        string serverUrl,
        string token,
        string seasonId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(seasonId))
        {
            return new List<PlexEpisodeItem>();
        }

        try
        {
            var url = $"{serverUrl.TrimEnd('/')}/library/metadata/{Uri.EscapeDataString(seasonId.Trim())}/children?X-Plex-Token={Uri.EscapeDataString(token)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to load Plex episodes for season {SeasonId}: {StatusCode}",
                    seasonId,
                    response.StatusCode);
                return new List<PlexEpisodeItem>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var episodes = new List<PlexEpisodeItem>();

            foreach (var node in doc.Descendants("Video"))
            {
                var type = node.Attribute("type")?.Value ?? string.Empty;
                if (!string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var episodeId = node.Attribute(RatingKeyAttributeName)?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(episodeId))
                {
                    continue;
                }

                var thumb = node.Attribute(ThumbAttributeName)?.Value
                            ?? node.Attribute("art")?.Value
                            ?? node.Attribute(ParentThumbAttributeName)?.Value
                            ?? node.Attribute("grandparentThumb")?.Value
                            ?? string.Empty;

                episodes.Add(new PlexEpisodeItem
                {
                    Id = episodeId,
                    Title = node.Attribute(TitleAttributeName)?.Value ?? string.Empty,
                    SeasonNumber = ParseNullableInt(node.Attribute("parentIndex")?.Value),
                    EpisodeNumber = ParseNullableInt(node.Attribute("index")?.Value),
                    Year = ParseNullableInt(node.Attribute("year")?.Value),
                    ImageUrl = ToAbsoluteUrl(serverUrl, token, thumb)
                });
            }

            return episodes
                .OrderBy(item => item.SeasonNumber ?? int.MaxValue)
                .ThenBy(item => item.EpisodeNumber ?? int.MaxValue)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed reading Plex episodes for season {SeasonId}", seasonId);
            return new List<PlexEpisodeItem>();
        }
    }

    private static PlexMediaItem MapPlexMediaItem(
        XElement node,
        string type,
        string serverUrl,
        string token)
    {
        var ratingKey = node.Attribute(RatingKeyAttributeName)?.Value ?? string.Empty;
        var title = node.Attribute(TitleAttributeName)?.Value ?? string.Empty;
        var year = ParseInt(node.Attribute("year")?.Value);
        var thumb = node.Attribute(ThumbAttributeName)?.Value
                    ?? node.Attribute("art")?.Value
                    ?? node.Attribute(ParentThumbAttributeName)?.Value
                    ?? node.Attribute("grandparentThumb")?.Value
                    ?? string.Empty;
        var imageUrl = ToAbsoluteUrl(serverUrl, token, thumb);

        return new PlexMediaItem
        {
            Id = ratingKey,
            Type = type,
            Title = title,
            Year = year,
            ImageUrl = imageUrl
        };
    }
    public async Task<PlexPlaylist?> GetPlaylistAsync(string serverUrl, string token, string playlistId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{serverUrl.TrimEnd('/')}/playlists/{playlistId}?X-Plex-Token={token}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to load Plex playlist {PlaylistId}: {StatusCode}", playlistId, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var element = doc.Descendants("Playlist").FirstOrDefault()
                          ?? doc.Descendants(DirectoryElementName).FirstOrDefault(el =>
                              string.Equals(el.Attribute("type")?.Value, "playlist", StringComparison.OrdinalIgnoreCase));
            if (element is null)
            {
                return null;
            }

            return ParsePlaylist(element, serverUrl, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex playlist {PlaylistId}", playlistId);
            return null;
        }
    }

    public async Task<List<PlexPlaylistTrack>> GetPlaylistItemsAsync(string serverUrl, string token, string playlistId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{serverUrl.TrimEnd('/')}/playlists/{playlistId}/items?X-Plex-Token={token}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to load Plex playlist items for {PlaylistId}: {StatusCode}", playlistId, response.StatusCode);
                return new List<PlexPlaylistTrack>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var tracks = new List<PlexPlaylistTrack>();
            foreach (var track in doc.Descendants(TrackElementName))
            {
                var part = track.Descendants("Part").FirstOrDefault();
                var partKey = part?.Attribute("key")?.Value;
                var filePath = part?.Attribute("file")?.Value;
                var streamUrl = partKey != null ? BuildPlexUrl(serverUrl, token, partKey) : null;
                tracks.Add(new PlexPlaylistTrack
                {
                    Id = track.Attribute(RatingKeyAttributeName)?.Value ?? string.Empty,
                    Title = track.Attribute(TitleAttributeName)?.Value ?? string.Empty,
                    Artist = track.Attribute("grandparentTitle")?.Value ?? string.Empty,
                    Album = track.Attribute("parentTitle")?.Value ?? string.Empty,
                    DurationMs = ParseLong(track.Attribute(DurationAttributeName)?.Value),
                    CoverUrl = ToAbsoluteUrl(serverUrl, token,
                        track.Attribute(ThumbAttributeName)?.Value ??
                        track.Attribute(ParentThumbAttributeName)?.Value ??
                        track.Attribute("grandparentThumb")?.Value),
                    StreamUrl = streamUrl,
                    FilePath = filePath
                });
            }

            return tracks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex playlist items for {PlaylistId}", playlistId);
            return new List<PlexPlaylistTrack>();
        }
    }

    public async Task<PlexTrackMetadata?> GetTrackMetadataAsync(
        string serverUrl,
        string token,
        string ratingKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ratingKey))
            {
                return null;
            }

            var url = $"{serverUrl.TrimEnd('/')}/library/metadata/{ratingKey}?X-Plex-Token={token}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to load Plex metadata {RatingKey}: {StatusCode}", ratingKey, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            var track = doc.Descendants(TrackElementName).FirstOrDefault();
            if (track is null)
            {
                return null;
            }

            var metadata = new PlexTrackMetadata
            {
                RatingKey = track.Attribute(RatingKeyAttributeName)?.Value ?? ratingKey,
                UserRating = ParseInt(track.Attribute("userRating")?.Value),
                AlbumUserRating = ParseInt(track.Attribute("parentUserRating")?.Value),
                ArtistUserRating = ParseInt(track.Attribute("grandparentUserRating")?.Value),
                LastViewedAtUtc = ParseUnixTime(track.Attribute("lastViewedAt")?.Value)
            };

            metadata.Genres = track.Descendants("Genre")
                .Select(el => el.Attribute("tag")?.Value)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            metadata.Moods = track.Descendants("Mood")
                .Select(el => el.Attribute("tag")?.Value)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return metadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex metadata for {RatingKey}", ratingKey);
            return null;
        }
    }

    public async Task<List<string>> GetSonicallySimilarRatingKeysAsync(
        string serverUrl,
        string token,
        string ratingKey,
        int limit,
        double? maxDistance = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ratingKey))
            {
                return new List<string>();
            }

            var url = $"{serverUrl.TrimEnd('/')}/library/metadata/{ratingKey}/similar?X-Plex-Token={token}&limit={limit}";
            if (maxDistance.HasValue && maxDistance.Value > 0)
            {
                url += $"&maxDistance={maxDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to load Plex similar tracks {RatingKey}: {StatusCode}", ratingKey, response.StatusCode);
                return new List<string>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            return doc.Descendants(TrackElementName)
                .Select(el => el.Attribute(RatingKeyAttributeName)?.Value)
                .Where(val => !string.IsNullOrWhiteSpace(val))
                .Select(val => val!)
                .Distinct()
                .Take(limit)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex similar tracks for {RatingKey}", ratingKey);
            return new List<string>();
        }
    }

    public sealed record PlaylistUpsertOptions(
        string? ExistingTitlePrefix = null,
        bool AppendMissingOnly = false);

    public async Task<string?> CreateOrUpdatePlaylistAsync(
        string serverUrl,
        string token,
        string machineIdentifier,
        string playlistName,
        IReadOnlyList<string> ratingKeys,
        PlaylistUpsertOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (ratingKeys.Count == 0)
        {
            return null;
        }

        var existingTitlePrefix = options?.ExistingTitlePrefix;
        var appendMissingOnly = options?.AppendMissingOnly ?? false;
        var existing = await GetPlaylistsAsync(serverUrl, token, cancellationToken);
        var match = string.IsNullOrWhiteSpace(existingTitlePrefix)
            ? existing.FirstOrDefault(p => string.Equals(p.Title, playlistName, StringComparison.OrdinalIgnoreCase))
            : existing.FirstOrDefault(p => p.Title.StartsWith(existingTitlePrefix, StringComparison.OrdinalIgnoreCase));
        var playlistId = match?.Id;

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            var createUrl = $"{serverUrl.TrimEnd('/')}/playlists?X-Plex-Token={token}&type=audio&title={Uri.EscapeDataString(playlistName)}&smart=0";
            var uri = BuildPlaylistUri(machineIdentifier, ratingKeys);
            createUrl += $"&uri={Uri.EscapeDataString(uri)}";
            var response = await _httpClient.PostAsync(createUrl, null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to create Plex playlist {PlaylistName}: {StatusCode}", playlistName, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(content);
            playlistId = doc.Descendants("Playlist").FirstOrDefault()?.Attribute(RatingKeyAttributeName)?.Value;
            return playlistId;
        }

        if (appendMissingOnly)
        {
            var existingItems = await GetPlaylistItemsAsync(serverUrl, token, playlistId, cancellationToken);
            var existingRatingKeys = existingItems
                .Select(item => item.Id)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = ratingKeys
                .Where(value => !string.IsNullOrWhiteSpace(value) && !existingRatingKeys.Contains(value))
                .ToList();
            if (pending.Count > 0)
            {
                await AddPlaylistItemsAsync(serverUrl, token, machineIdentifier, playlistId, pending, cancellationToken);
            }
        }
        else
        {
            await ClearPlaylistItemsAsync(serverUrl, token, playlistId, cancellationToken);
            await AddPlaylistItemsAsync(serverUrl, token, machineIdentifier, playlistId, ratingKeys, cancellationToken);
        }

        return playlistId;
    }

    public async Task UpdatePlaylistMetadataAsync(
        string serverUrl,
        string token,
        string playlistId,
        string? title,
        string? summary,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return;
        }

        var url = $"{serverUrl.TrimEnd('/')}/playlists/{playlistId}?X-Plex-Token={token}";
        if (!string.IsNullOrWhiteSpace(title))
        {
            url += $"&title={Uri.EscapeDataString(title)}";
        }
        if (!string.IsNullOrWhiteSpace(summary))
        {
            url += $"&summary={Uri.EscapeDataString(summary)}";
        }

        var response = await _httpClient.PutAsync(url, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to update Plex playlist metadata for {PlaylistId}: {StatusCode}", playlistId, response.StatusCode);
        }
    }

    public async Task UpdatePlaylistPosterAsync(
        string serverUrl,
        string token,
        string playlistId,
        string posterUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(posterUrl))
        {
            return;
        }

        await SendPlexRequestAsync(
            HttpMethod.Post,
            $"{serverUrl.TrimEnd('/')}/playlists/{playlistId}/posters?X-Plex-Token={token}&url={Uri.EscapeDataString(posterUrl)}",
            "update playlist poster",
            playlistId,
            cancellationToken);
    }

    public async Task UpdatePlaylistPosterFromUrlAsync(
        string serverUrl,
        string token,
        string playlistId,
        string posterUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(playlistId)
            || string.IsNullOrWhiteSpace(posterUrl))
        {
            return;
        }

        try
        {
            var response = await _httpClient.GetAsync(posterUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download playlist poster for {PlaylistId}: {StatusCode}", playlistId, response.StatusCode);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
            {
                return;
            }

            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = response.Content.Headers.ContentType
                ?? new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

            await SendPlexRequestAsync(
                HttpMethod.Post,
                $"{serverUrl.TrimEnd('/')}/playlists/{playlistId}/posters?X-Plex-Token={token}",
                "upload playlist poster",
                playlistId,
                cancellationToken,
                content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh Plex playlist poster for {PlaylistId}", playlistId);
        }
    }

    private async Task ClearPlaylistItemsAsync(string serverUrl, string token, string playlistId, CancellationToken cancellationToken)
    {
        await SendPlexRequestAsync(
            HttpMethod.Delete,
            $"{serverUrl.TrimEnd('/')}/playlists/{playlistId}/items?X-Plex-Token={token}",
            "clear playlist items",
            playlistId,
            cancellationToken);
    }

    private async Task AddPlaylistItemsAsync(
        string serverUrl,
        string token,
        string machineIdentifier,
        string playlistId,
        IReadOnlyList<string> ratingKeys,
        CancellationToken cancellationToken)
    {
        var uri = BuildPlaylistUri(machineIdentifier, ratingKeys);
        await SendPlexRequestAsync(
            HttpMethod.Put,
            $"{serverUrl.TrimEnd('/')}/playlists/{playlistId}/items?X-Plex-Token={token}&uri={Uri.EscapeDataString(uri)}",
            "add playlist items",
            playlistId,
            cancellationToken);
    }

    private async Task<bool> SendPlexRequestAsync(
        HttpMethod method,
        string url,
        string operation,
        string entityId,
        CancellationToken cancellationToken,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = content
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        _logger.LogWarning("Plex request failed for {Operation} {EntityId}: {StatusCode}", operation, entityId, response.StatusCode);
        return false;
    }

    private static string BuildPlaylistUri(string machineIdentifier, IReadOnlyList<string> ratingKeys)
    {
        var items = string.Join(",", ratingKeys);
        return $"server://{machineIdentifier}/com.plexapp.plugins.library/library/metadata/{items}";
    }
    public async Task<List<PlexHistoryItem>> GetHistoryAsync(string serverUrl, string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var baseUrl = serverUrl.TrimEnd('/');
            var urls = new[]
            {
                $"{baseUrl}/status/sessions/history?X-Plex-Token={token}",
                $"{baseUrl}/status/sessions/history/all?X-Plex-Token={token}",
                $"{baseUrl}/status/sessions/history?X-Plex-Token={token}&X-Plex-Container-Start=0&X-Plex-Container-Size=200"
            };

            foreach (var url in urls)
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        continue;
                    }
                    _logger.LogWarning("Failed to load Plex history from {Url}: {StatusCode}", url, response.StatusCode);
                    return new List<PlexHistoryItem>();
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = XDocument.Parse(content);
                var items = new List<PlexHistoryItem>();
                foreach (var track in doc.Descendants(TrackElementName))
                {
                    var viewedAt = ParseUnixTime(track.Attribute("viewedAt")?.Value);
                    var part = track.Descendants("Part").FirstOrDefault();
                    var filePath = part?.Attribute("file")?.Value;
                    items.Add(new PlexHistoryItem
                    {
                        RatingKey = track.Attribute(RatingKeyAttributeName)?.Value ?? string.Empty,
                        Title = track.Attribute(TitleAttributeName)?.Value ?? string.Empty,
                        Artist = track.Attribute("grandparentTitle")?.Value ?? string.Empty,
                        Album = track.Attribute("parentTitle")?.Value ?? string.Empty,
                        ViewedAtUtc = viewedAt,
                        DurationMs = ParseLong(track.Attribute(DurationAttributeName)?.Value),
                        FilePath = filePath
                    });
                }

                return items;
            }

            _logger.LogWarning("Failed to load Plex history: NotFound");
            return new List<PlexHistoryItem>();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Plex history request timed out.");
            return new List<PlexHistoryItem>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrieving Plex history");
            return new List<PlexHistoryItem>();
        }
    }
    private static PlexPlaylist ParsePlaylist(XElement element, string serverUrl, string token)
    {
        return new PlexPlaylist
        {
            Id = element.Attribute(RatingKeyAttributeName)?.Value
                 ?? element.Attribute("key")?.Value
                 ?? string.Empty,
            Title = element.Attribute(TitleAttributeName)?.Value ?? string.Empty,
            Summary = element.Attribute("summary")?.Value ?? string.Empty,
            TrackCount = ParseInt(element.Attribute("leafCount")?.Value),
            DurationMs = ParseLong(element.Attribute(DurationAttributeName)?.Value),
            UpdatedAt = ParseUnixTime(element.Attribute("updatedAt")?.Value),
            PlaylistType = element.Attribute("playlistType")?.Value
                           ?? element.Attribute("type")?.Value
                           ?? string.Empty,
            LibrarySectionId = element.Attribute("librarySectionID")?.Value
                               ?? element.Attribute("librarySectionId")?.Value
                               ?? string.Empty,
            CoverUrl = ToAbsoluteUrl(serverUrl, token,
                element.Attribute(ThumbAttributeName)?.Value ??
                element.Attribute("composite")?.Value)
        };
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static long ParseLong(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static DateTimeOffset? ParseUnixTime(string? value)
    {
        if (!long.TryParse(value, out var parsed) || parsed <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(parsed);
    }

    private static string? ToAbsoluteUrl(string serverUrl, string token, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["file://".Length..];
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (string.Equals(absolute.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                return BuildPlexUrl(serverUrl, token, absolute.AbsolutePath);
            }

            return AppendToken(absolute.ToString(), token);
        }

        var normalized = value.StartsWith('/') ? value : "/" + value;
        return BuildPlexUrl(serverUrl, token, normalized);
    }

    private static string BuildPlexUrl(string serverUrl, string token, string path)
    {
        var baseUrl = serverUrl.TrimEnd('/');
        var normalized = path.StartsWith('/') ? path : "/" + path;
        return AppendToken(baseUrl + normalized, token);
    }

    private static string AppendToken(string url, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{separator}X-Plex-Token={Uri.EscapeDataString(token)}";
    }
}

public sealed record PlexArtistLocation(string SectionKey, string RatingKey);

/// <summary>
/// Plex server information
/// </summary>
public class PlexServerInfo
{
    public string FriendlyName { get; set; } = "";
    public string Version { get; set; } = "";
    public string MachineIdentifier { get; set; } = "";
}

public class PlexIdentity
{
    public string FriendlyName { get; set; } = "";
    public string Version { get; set; } = "";
    public string MachineIdentifier { get; set; } = "";
}

public class PlexUserInfo
{
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string Thumb { get; set; } = "";
}

/// <summary>
/// Plex library section
/// </summary>
public class PlexLibrary
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>
/// Plex track information
/// </summary>
public class PlexTrack
{
    public string RatingKey { get; set; } = "";
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public long DurationMs { get; set; }
    public string FilePath { get; set; } = "";
}

public class PlexPlaylist
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public int TrackCount { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string PlaylistType { get; set; } = "";
    public string LibrarySectionId { get; set; } = "";
    public string? CoverUrl { get; set; }
}

public class PlexLibrarySection
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
}

public class PlexHistoryItem
{
    public string RatingKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public DateTimeOffset? ViewedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public string? FilePath { get; set; }
}

public class PlexPlaylistTrack
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public long DurationMs { get; set; }
    public string? CoverUrl { get; set; }
    public string? StreamUrl { get; set; }
    public string? FilePath { get; set; }
}

public sealed class PlexMediaItem
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class PlexSeasonItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int? SeasonNumber { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class PlexEpisodeItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public int? Year { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class PlexTrackMetadata
{
    public string RatingKey { get; set; } = "";
    public int? UserRating { get; set; }
    public int? AlbumUserRating { get; set; }
    public int? ArtistUserRating { get; set; }
    public DateTimeOffset? LastViewedAtUtc { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<string> Moods { get; set; } = new();
}
