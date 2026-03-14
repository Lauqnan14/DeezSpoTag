using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Text.RegularExpressions;
using System.Diagnostics;
using IOFile = System.IO.File;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared.Utils;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalDownloadService
{
    private const string AudioKeyword = "audio";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static Match MatchWithTimeout(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Match(input, pattern, options, RegexTimeout);
    private static MatchCollection MatchesWithTimeout(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Matches(input, pattern, options, RegexTimeout);

    private readonly ILogger<TidalDownloadService> _logger;
    private readonly HttpClient _client;

    public TidalDownloadService(ILogger<TidalDownloadService> logger)
    {
        _logger = logger;
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<string> DownloadAsync(
        TidalDownloadRequest request,
        bool embedMaxQualityCover,
        DeezSpoTag.Core.Models.Settings.TagSettings? tagSettings,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDir);

        if (!string.IsNullOrWhiteSpace(request.Isrc) &&
            AudioFilePathHelper.TryFindExistingByIsrc(request.OutputDir, request.Isrc, out var existingPath, ".flac"))
        {
            return existingPath;
        }

        string? tidalUrl = request.ServiceUrl;
        if (string.IsNullOrWhiteSpace(tidalUrl) && !string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            tidalUrl = await GetTidalUrlFromSpotifyAsync(request.SpotifyId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(tidalUrl))
        {
            try
            {
                return await DownloadByUrlAsync(
                    request,
                    tidalUrl,
                    embedMaxQualityCover,
                    tagSettings,
                    progressCallback,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is InvalidOperationException)
            {
                _logger.LogWarning(ex, "Tidal URL download failed. Url={Url}", tidalUrl);
            }
        }

        throw new InvalidOperationException("Tidal download requires a valid service URL or Spotify ID for song.link resolution.");
    }

    public async Task<string?> ResolveTrackUrlAsync(
        string trackTitle,
        string artistName,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var trackInfo = await SearchTrackByMetadataWithIsrcAsync(
                trackTitle,
                artistName,
                isrc,
                expectedDuration,
                cancellationToken);
            if (trackInfo == null || trackInfo.Id <= 0)
            {
                return null;
            }

            return $"https://listen.tidal.com/track/{trackInfo.Id}";
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Tidal metadata resolution failed for {Title} - {Artist}", trackTitle, artistName);
            return null;
        }
    }

    private async Task<string> DownloadByUrlAsync(
        TidalDownloadRequest request,
        string tidalUrl,
        bool embedMaxQualityCover,
        DeezSpoTag.Core.Models.Settings.TagSettings? tagSettings,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var trackId = GetTrackIdFromUrl(tidalUrl);
        var trackInfo = await GetTrackInfoByIdAsync(trackId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.Isrc)
            && !string.Equals(trackInfo.Isrc, request.Isrc, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "ISRC mismatch for Tidal URL download: expected {ExpectedIsrc}, got {ActualIsrc}. Proceeding with URL-specified track.",
                request.Isrc,
                trackInfo.Isrc);
        }

        var downloadUrl = await GetDownloadUrlAsync(trackInfo.Id, request.Quality, cancellationToken);
        var outputPathContext = new AudioFilePathHelper.AudioPathContext
        {
            OutputDir = request.OutputDir,
            Title = request.TrackName,
            Artist = request.ArtistName,
            Album = request.AlbumName,
            AlbumArtist = request.AlbumArtist,
            ReleaseDate = request.ReleaseDate,
            TrackNumber = request.SpotifyTrackNumber,
            DiscNumber = request.SpotifyDiscNumber,
            FilenameFormat = request.FilenameFormat,
            IncludeTrackNumber = request.IncludeTrackNumber,
            Position = request.Position,
            UseAlbumTrackNumber = request.UseAlbumTrackNumber,
            Sanitize = value => DownloadFileUtilities.SanitizeFilename(value)
        };
        var outputPath = AudioFilePathHelper.BuildOutputPath(outputPathContext, ".flac");

        await DownloadFileAsync(downloadUrl, outputPath, progressCallback, cancellationToken);
        await AudioFileTaggingHelper.TryTagAsync(
            new AudioFileTaggingHelper.AudioTaggingRequest(
                Logger: _logger,
                EngineName: "Tidal",
                HttpClient: _client,
                FilePath: outputPath,
                TagData: AudioFileTaggingHelper.CreateTagData(
                    new AudioFileTaggingHelper.AudioTagDataInput(
                        Title: request.TrackName,
                        Artist: request.ArtistName,
                        Album: request.AlbumName,
                        AlbumArtist: request.AlbumArtist,
                        ReleaseDate: request.ReleaseDate,
                        TrackNumber: request.SpotifyTrackNumber,
                        DiscNumber: request.SpotifyDiscNumber,
                        TotalTracks: request.SpotifyTotalTracks,
                        Isrc: request.Isrc)),
                CoverUrl: request.CoverUrl,
                EmbedMaxQualityCover: embedMaxQualityCover,
                TagSettings: tagSettings),
            cancellationToken);
        return outputPath;
    }

    private async Task<string> GetTidalUrlFromSpotifyAsync(string spotifyId, CancellationToken cancellationToken)
    {
        return await SongLinkClient.ResolvePlatformUrlAsync(_client, spotifyId, "tidal", cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var clientId = Encoding.UTF8.GetString(Convert.FromBase64String("NkJEU1JkcEs5aHFFQlRnVQ=="));
        var clientSecret = Encoding.UTF8.GetString(Convert.FromBase64String("eGV1UG1ZN25icFo5SUliTEFjUTkzc2hrYTFWTmhlVUFxTjZJY3N6alRHOD0="));

        var data = $"client_id={WebUtility.UrlEncode(clientId)}&grant_type=client_credentials";
        var authUrl = Encoding.UTF8.GetString(Convert.FromBase64String("aHR0cHM6Ly9hdXRoLnRpZGFsLmNvbS92MS9vYXV0aDIvdG9rZW4="));

        using var request = new HttpRequestMessage(HttpMethod.Post, authUrl)
        {
            Content = new StringContent(data, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);

        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Tidal auth failed");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var token = JsonSerializer.Deserialize<TidalTokenResponse>(body, SerializerOptions);
        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Tidal access token missing");
        }

        return token.AccessToken;
    }

    private async Task<TidalTrack> SearchTrackByMetadataWithIsrcAsync(string trackName, string artistName, string isrc, int expectedDuration, CancellationToken cancellationToken)
    {
        var queries = BuildSearchQueries(trackName, artistName);
        var allTracks = new List<TidalTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            if (!seen.Add(query))
            {
                continue;
            }
            var result = await SearchTracksAsync(query, 100, cancellationToken);
            if (result.Count > 0)
            {
                allTracks.AddRange(result);
            }
        }

        if (allTracks.Count == 0)
        {
            throw new InvalidOperationException("No tracks found");
        }

        var isrcMatch = FindIsrcMatch(allTracks, isrc);
        if (isrcMatch != null)
        {
            return isrcMatch;
        }

        var durationMatch = FindDurationMatch(allTracks, expectedDuration);
        if (durationMatch != null)
        {
            return durationMatch;
        }

        return allTracks[0];
    }

    private static List<string> BuildSearchQueries(string trackName, string artistName)
    {
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(trackName))
        {
            queries.Add($"{artistName} {trackName}");
        }

        if (!string.IsNullOrWhiteSpace(trackName))
        {
            queries.Add(trackName);
        }

        if (!string.IsNullOrWhiteSpace(artistName))
        {
            queries.Add(artistName);
        }

        return queries;
    }

    private TidalTrack? FindIsrcMatch(List<TidalTrack> allTracks, string isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var match = allTracks.FirstOrDefault(track => string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            _logger.LogDebug("No ISRC match for {Isrc}, falling back to duration/title matching", isrc);
        }

        return match;
    }

    private static TidalTrack? FindDurationMatch(List<TidalTrack> allTracks, int expectedDuration)
    {
        if (expectedDuration <= 0)
        {
            return null;
        }

        const int tolerance = 3;
        return allTracks.FirstOrDefault(track => Math.Abs(track.Duration - expectedDuration) <= tolerance);
    }

    private async Task<List<TidalTrack>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var baseUrl = Encoding.UTF8.GetString(Convert.FromBase64String("aHR0cHM6Ly9hcGkudGlkYWwuY29tL3YxL3NlYXJjaC90cmFja3M/cXVlcnk9"));
        var url = $"{baseUrl}{WebUtility.UrlEncode(query)}&limit={limit}&offset=0&countryCode=US";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<TidalTrack>();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = JsonSerializer.Deserialize<TidalSearchResponse>(body, SerializerOptions);
        return payload?.Items ?? new List<TidalTrack>();
    }

    private static long GetTrackIdFromUrl(string tidalUrl)
    {
        var match = MatchWithTimeout(tidalUrl, @"\/track\/(?<id>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException("Invalid Tidal URL");
        }

        if (!long.TryParse(match.Groups["id"].Value, out var trackId))
        {
            throw new InvalidOperationException("Invalid Tidal track ID");
        }

        return trackId;
    }

    private async Task<TidalTrack> GetTrackInfoByIdAsync(long trackId, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var baseUrl = Encoding.UTF8.GetString(Convert.FromBase64String("aHR0cHM6Ly9hcGkudGlkYWwuY29tL3YxL3RyYWNrcy8="));
        var url = $"{baseUrl}{trackId}?countryCode=US";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Tidal track lookup failed: {Status} for {TrackId} ({Url})",
                (int)response.StatusCode, trackId, url);
            response.EnsureSuccessStatusCode();
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var track = JsonSerializer.Deserialize<TidalTrack>(body, SerializerOptions);
        if (track == null)
        {
            throw new InvalidOperationException("Tidal track not found");
        }
        return track;
    }

    private async Task<string> GetDownloadUrlAsync(long trackId, string quality, CancellationToken cancellationToken)
    {
        var apis = GetAvailableApis();
        if (apis.Count == 0)
        {
            throw new InvalidOperationException("Tidal API pool is empty");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = apis
            .Select(api => FetchManifestFromApiAsync(api, trackId, quality, linkedCts.Token))
            .ToList();

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            var manifest = await completed;
            if (!string.IsNullOrWhiteSpace(manifest))
            {
                await linkedCts.CancelAsync();
                return manifest;
            }
        }

        throw new InvalidOperationException("Tidal download URL not available");
    }

    private async Task<string?> FetchManifestFromApiAsync(
        string apiBase,
        long trackId,
        string quality,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{apiBase}/track/?id={trackId}&quality={quality}";
            using var response = await _client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return TryParseManifest(body, out var manifest) ? manifest : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Tidal API {Api} failed for track {TrackId}", apiBase, trackId);
            return null;
        }
    }

    private async Task DownloadFileAsync(string url, string outputPath, Func<double, double, Task>? progressCallback, CancellationToken cancellationToken)
    {
        if (url.StartsWith("MANIFEST:", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadFromManifestAsync(url.Substring("MANIFEST:".Length), outputPath, progressCallback, cancellationToken);
            return;
        }

        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = IOFile.Create(outputPath);
        await DownloadStreamHelper.CopyToAsyncWithProgress(stream, file, response.Content.Headers.ContentLength, progressCallback, cancellationToken);
    }

    private async Task DownloadFromManifestAsync(string manifestB64, string outputPath, Func<double, double, Task>? progressCallback, CancellationToken cancellationToken)
    {
        var (directUrl, initUrl, mediaUrls, mimeType) = ParseManifest(manifestB64);
        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            if (IsLikelyFlacMimeType(mimeType) || string.IsNullOrWhiteSpace(mimeType))
            {
                await DownloadFileAsync(directUrl, outputPath, progressCallback, cancellationToken);
                return;
            }

            var directTempPath = outputPath + ".m4a.tmp";
            await DownloadFileAsync(directUrl, directTempPath, progressCallback, cancellationToken);
            await ConvertTempToFlacAsync(directTempPath, outputPath, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(initUrl) || mediaUrls.Count == 0)
        {
            throw new InvalidOperationException("Invalid manifest");
        }

        var tempPath = outputPath + ".m4a.tmp";
        await using var output = IOFile.Create(tempPath);
        var totalSegments = 1 + mediaUrls.Count;
        var completed = 0;

        if (progressCallback != null)
        {
            await progressCallback(0, 0);
        }

        await DownloadSegmentAsync(initUrl, output, cancellationToken);
        completed++;
        if (progressCallback != null)
        {
            await progressCallback(completed * 100d / totalSegments, 0);
        }

        foreach (var media in mediaUrls)
        {
            await DownloadSegmentAsync(media, output, cancellationToken);
            completed++;
            if (progressCallback != null)
            {
                await progressCallback(completed * 100d / totalSegments, 0);
            }
        }

        output.Close();
        await ConvertTempToFlacAsync(tempPath, outputPath, cancellationToken);
    }

    private async Task DownloadSegmentAsync(string url, Stream output, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(output, cancellationToken);
    }

    private static (string DirectUrl, string InitUrl, List<string> MediaUrls, string MimeType) ParseManifest(string manifestPayload)
    {
        var manifestStr = TryDecodeManifest(manifestPayload);
        if (TryParseBtsManifest(manifestStr, out var btsManifest))
        {
            return btsManifest;
        }

        if (TryParseDashTemplate(manifestStr, out var initUrl, out var mediaTemplate, out var startNumber, out var segmentCount, out var dashMimeType))
        {
            return ("", initUrl, BuildDashMediaUrls(mediaTemplate, startNumber, segmentCount), dashMimeType);
        }

        return ParseDashFallbackManifest(manifestStr);
    }

    private static bool TryParseBtsManifest(
        string manifestStr,
        out (string DirectUrl, string InitUrl, List<string> MediaUrls, string MimeType) manifest)
    {
        manifest = default;
        if (!manifestStr.StartsWith('{'))
        {
            return false;
        }

        var bts = JsonSerializer.Deserialize<TidalBtsManifest>(manifestStr, SerializerOptions);
        if (bts?.Urls == null || bts.Urls.Count == 0)
        {
            throw new InvalidOperationException("No URLs in manifest");
        }

        manifest = (bts.Urls[0], "", new List<string>(), bts.MimeType ?? "");
        return true;
    }

    private static List<string> BuildDashMediaUrls(string mediaTemplate, int startNumber, int segmentCount)
    {
        var mediaUrls = new List<string>(segmentCount);
        for (var i = 0; i < segmentCount; i++)
        {
            var segmentNumber = startNumber + i;
            mediaUrls.Add(ReplaceNumberPlaceholder(mediaTemplate, segmentNumber));
        }

        return mediaUrls;
    }

    private static (string DirectUrl, string InitUrl, List<string> MediaUrls, string MimeType) ParseDashFallbackManifest(string manifestStr)
    {
        var initRe = MatchWithTimeout(manifestStr, "initialization=\"([^\"]+)\"");
        var mediaRe = MatchWithTimeout(manifestStr, "media=\"([^\"]+)\"");
        var initFallback = initRe.Success ? DecodeXmlUrl(initRe.Groups[1].Value) : "";
        var mediaFallback = mediaRe.Success ? DecodeXmlUrl(mediaRe.Groups[1].Value) : "";
        if (string.IsNullOrWhiteSpace(initFallback) || string.IsNullOrWhiteSpace(mediaFallback))
        {
            throw new InvalidOperationException("Invalid DASH manifest");
        }

        var countFallback = CountFallbackSegments(manifestStr);
        var mediaUrlsFallback = new List<string>(countFallback);
        for (var i = 0; i < countFallback; i++)
        {
            mediaUrlsFallback.Add(ReplaceNumberPlaceholder(mediaFallback, i + 1));
        }

        return ("", initFallback, mediaUrlsFallback, ExtractDashMimeType(manifestStr));
    }

    private static int CountFallbackSegments(string manifestStr)
    {
        var count = 0;
        var segmentTags = MatchesWithTimeout(manifestStr, "<S\\b[^>]*>", RegexOptions.IgnoreCase);
        foreach (Match tag in segmentTags)
        {
            if (!tag.Success)
            {
                continue;
            }

            var repeatMatch = MatchWithTimeout(tag.Value, "\\br=\"(-?\\d+)\"", RegexOptions.IgnoreCase);
            var repeat = repeatMatch.Success && int.TryParse(repeatMatch.Groups[1].Value, out var parsedRepeat)
                ? Math.Max(0, parsedRepeat)
                : 0;
            count += repeat + 1;
        }

        return count <= 0 ? 1 : count;
    }

    private static bool TryParseDashTemplate(
        string manifestXml,
        out string initUrl,
        out string mediaTemplate,
        out int startNumber,
        out int segmentCount,
        out string mimeType)
    {
        initUrl = string.Empty;
        mediaTemplate = string.Empty;
        startNumber = 1;
        segmentCount = 0;
        mimeType = string.Empty;

        try
        {
            var doc = new XmlDocument
            {
                XmlResolver = null
            };
            doc.LoadXml(manifestXml);

            var (selectedTemplate, selectedMime) = SelectBestAudioSegmentTemplate(doc);

            if (selectedTemplate == null)
            {
                selectedTemplate = doc.SelectSingleNode("//*[local-name()='SegmentTemplate']");
            }

            if (selectedTemplate == null)
            {
                return false;
            }

            (initUrl, mediaTemplate, startNumber, segmentCount, mimeType) = BuildDashTemplateMetadata(
                selectedTemplate,
                selectedMime,
                manifestXml);
            return !string.IsNullOrWhiteSpace(initUrl) && !string.IsNullOrWhiteSpace(mediaTemplate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }
    }

    private static (XmlNode? Template, string? MimeType) SelectBestAudioSegmentTemplate(XmlDocument doc)
    {
        XmlNode? selectedTemplate = null;
        var selectedBandwidth = int.MinValue;
        string? selectedMime = null;
        var adaptationSets = doc.SelectNodes("//*[local-name()='AdaptationSet']");
        if (adaptationSets == null)
        {
            return (null, null);
        }

        foreach (XmlNode adaptationSet in adaptationSets)
        {
            TrySelectAudioTemplateFromAdaptationSet(
                adaptationSet,
                ref selectedTemplate,
                ref selectedBandwidth,
                ref selectedMime);
        }

        return (selectedTemplate, selectedMime);
    }

    private static void TrySelectAudioTemplateFromAdaptationSet(
        XmlNode adaptationSet,
        ref XmlNode? selectedTemplate,
        ref int selectedBandwidth,
        ref string? selectedMime)
    {
        var adaptationMime = adaptationSet.Attributes?["mimeType"]?.Value ?? string.Empty;
        var adaptationContentType = adaptationSet.Attributes?["contentType"]?.Value ?? string.Empty;
        var adaptationLooksAudio = adaptationContentType.Equals(AudioKeyword, StringComparison.OrdinalIgnoreCase)
                                   || adaptationMime.Contains(AudioKeyword, StringComparison.OrdinalIgnoreCase);

        var representations = adaptationSet.SelectNodes("./*[local-name()='Representation']");
        if (representations != null)
        {
            foreach (XmlNode representation in representations)
            {
                TrySelectAudioRepresentationTemplate(
                    representation,
                    adaptationMime,
                    adaptationLooksAudio,
                    ref selectedTemplate,
                    ref selectedBandwidth,
                    ref selectedMime);
            }
        }

        if (selectedTemplate != null || !adaptationLooksAudio)
        {
            return;
        }

        var adaptationTemplate = adaptationSet.SelectSingleNode("./*[local-name()='SegmentTemplate']");
        if (adaptationTemplate == null)
        {
            return;
        }

        selectedTemplate = adaptationTemplate;
        selectedBandwidth = 0;
        selectedMime = adaptationMime;
    }

    private static void TrySelectAudioRepresentationTemplate(
        XmlNode representation,
        string adaptationMime,
        bool adaptationLooksAudio,
        ref XmlNode? selectedTemplate,
        ref int selectedBandwidth,
        ref string? selectedMime)
    {
        var templateNode = representation.SelectSingleNode("./*[local-name()='SegmentTemplate']");
        if (templateNode == null)
        {
            return;
        }

        var representationMime = representation.Attributes?["mimeType"]?.Value;
        var representationCodecs = representation.Attributes?["codecs"]?.Value;
        var mimeCandidate = !string.IsNullOrWhiteSpace(representationMime)
            ? representationMime
            : adaptationMime;
        var representationLooksAudio = adaptationLooksAudio
            || (!string.IsNullOrWhiteSpace(mimeCandidate)
                && mimeCandidate.Contains(AudioKeyword, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(representationCodecs)
                && representationCodecs.Contains("flac", StringComparison.OrdinalIgnoreCase));
        if (!representationLooksAudio)
        {
            return;
        }

        var bandwidth = ParseIntAttribute(representation.Attributes?["bandwidth"]?.Value, 0);
        if (selectedTemplate != null && bandwidth <= selectedBandwidth)
        {
            return;
        }

        selectedTemplate = templateNode;
        selectedBandwidth = bandwidth;
        selectedMime = mimeCandidate;
    }

    private static (string InitUrl, string MediaTemplate, int StartNumber, int SegmentCount, string MimeType) BuildDashTemplateMetadata(
        XmlNode selectedTemplate,
        string? selectedMime,
        string manifestXml)
    {
        var initUrl = DecodeXmlUrl(selectedTemplate.Attributes?["initialization"]?.Value ?? string.Empty);
        var mediaTemplate = DecodeXmlUrl(selectedTemplate.Attributes?["media"]?.Value ?? string.Empty);
        var startNumber = ParseIntAttribute(selectedTemplate.Attributes?["startNumber"]?.Value, 1);
        if (startNumber <= 0)
        {
            startNumber = 1;
        }

        var segmentCount = CountDashSegments(selectedTemplate);
        if (segmentCount <= 0 && selectedTemplate.ParentNode != null)
        {
            segmentCount = CountDashSegments(selectedTemplate.ParentNode);
        }

        if (segmentCount <= 0)
        {
            segmentCount = 1;
        }

        var mimeType = !string.IsNullOrWhiteSpace(selectedMime)
            ? selectedMime
            : ExtractDashMimeType(manifestXml);

        return (initUrl, mediaTemplate, startNumber, segmentCount, mimeType);
    }

    private static int CountDashSegments(XmlNode node)
    {
        var timeline = node.SelectSingleNode("./*[local-name()='SegmentTimeline']");
        if (timeline == null)
        {
            return 0;
        }

        var count = 0;
        var segments = timeline.SelectNodes("./*[local-name()='S']");
        if (segments == null || segments.Count == 0)
        {
            return 0;
        }

        foreach (XmlNode segment in segments)
        {
            var repeat = ParseIntAttribute(segment.Attributes?["r"]?.Value, 0);
            if (repeat < 0)
            {
                repeat = 0;
            }

            count += repeat + 1;
        }

        return count;
    }

    private static int ParseIntAttribute(string? rawValue, int fallback)
    {
        return int.TryParse(rawValue, out var parsed) ? parsed : fallback;
    }

    private static string DecodeXmlUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static string ReplaceNumberPlaceholder(string template, int number)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        if (template.Contains("$Number$", StringComparison.OrdinalIgnoreCase))
        {
            return template.Replace("$Number$", number.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        var paddedMatch = MatchWithTimeout(template, "\\$Number%0(?<width>\\d+)d\\$", RegexOptions.IgnoreCase);
        if (paddedMatch.Success && int.TryParse(paddedMatch.Groups["width"].Value, out var width) && width > 0)
        {
            var padded = number.ToString().PadLeft(width, '0');
            return template.Replace(paddedMatch.Value, padded, StringComparison.OrdinalIgnoreCase);
        }

        return template;
    }

    private static string ExtractDashMimeType(string manifestXml)
    {
        if (string.IsNullOrWhiteSpace(manifestXml))
        {
            return string.Empty;
        }

        var mimeMatches = MatchesWithTimeout(manifestXml, "mimeType=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        var audioCandidate = mimeMatches
            .Select(static match => match.Groups.Count > 1 ? match.Groups[1].Value : string.Empty)
            .FirstOrDefault(candidate => candidate.Contains(AudioKeyword, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(audioCandidate))
        {
            return audioCandidate;
        }

        return mimeMatches.Count > 0 && mimeMatches[0].Groups.Count > 1
            ? mimeMatches[0].Groups[1].Value
            : string.Empty;
    }

    private static bool IsLikelyFlacMimeType(string mimeType)
    {
        return !string.IsNullOrWhiteSpace(mimeType)
            && mimeType.Contains("flac", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveFfmpegPath() => ExternalToolResolver.ResolveFfmpegPath();

    private static async Task ConvertTempToFlacAsync(
        string tempPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!IOFile.Exists(tempPath))
        {
            throw new InvalidOperationException("Temporary audio file is missing.");
        }

        var ffmpegPath = ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            var fallbackPath = Path.ChangeExtension(outputPath, ".m4a");
            IOFile.Move(tempPath, fallbackPath, overwrite: true);
            throw new InvalidOperationException($"ffmpeg not available; source kept as {fallbackPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -i \"{tempPath}\" -vn -c:a flac \"{outputPath}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0 || !IOFile.Exists(outputPath))
        {
            var fallbackPath = Path.ChangeExtension(outputPath, ".m4a");
            IOFile.Move(tempPath, fallbackPath, overwrite: true);
            throw new InvalidOperationException($"ffmpeg conversion failed; source kept as {fallbackPath}. Error: {stderr}");
        }

        IOFile.Delete(tempPath);
    }

    private static bool TryParseManifest(string body, out string manifest)
    {
        manifest = "";
        try
        {
            var v2 = JsonSerializer.Deserialize<TidalApiResponseV2>(body, SerializerOptions);
            if (!string.IsNullOrWhiteSpace(v2?.Data?.Manifest))
            {
                manifest = "MANIFEST:" + v2.Data.Manifest;
                return true;
            }
        }
        catch (JsonException)
        {
            manifest = "";
        }

        try
        {
            var v1 = JsonSerializer.Deserialize<List<TidalApiResponse>>(body, SerializerOptions);
            var direct = v1?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.OriginalTrackUrl));
            if (direct != null)
            {
                manifest = direct.OriginalTrackUrl;
                return true;
            }
        }
        catch (JsonException)
        {
            manifest = "";
        }

        return false;
    }

    private static string TryDecodeManifest(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "";
        }

        try
        {
            var decoded = Convert.FromBase64String(payload);
            return Encoding.UTF8.GetString(decoded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return payload;
        }
    }

    private static List<string> GetAvailableApis()
    {
        var encoded = new[]
        {
            "dm9nZWwucXFkbC5zaXRl",
            "bWF1cy5xcWRsLnNpdGU=",
            "aHVuZC5xcWRsLnNpdGU=",
            "a2F0emUucXFkbC5zaXRl",
            "d29sZi5xcWRsLnNpdGU=",
            "dGlkYWwua2lub3BsdXMub25saW5l",
            "dGlkYWwtYXBpLmJpbmltdW0ub3Jn",
            "dHJpdG9uLnNxdWlkLnd0Zg=="
        };

        return encoded
            .Select(value => Encoding.UTF8.GetString(Convert.FromBase64String(value)))
            .Select(decoded => $"https://{decoded}")
            .ToList();
    }

    private sealed class TidalTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";
    }

    private sealed class TidalSearchResponse
    {
        [JsonPropertyName("items")]
        public List<TidalTrack> Items { get; set; } = new();
    }

    private sealed class TidalTrack
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("isrc")]
        public string Isrc { get; set; } = "";

        [JsonPropertyName("duration")]
        public int Duration { get; set; }
    }

    private sealed class TidalApiResponse
    {
        [JsonPropertyName("OriginalTrackUrl")]
        public string OriginalTrackUrl { get; set; } = "";
    }

    private sealed class TidalApiResponseV2
    {
        [JsonPropertyName("data")]
        public TidalApiResponseV2Data? Data { get; set; }
    }

    private sealed class TidalApiResponseV2Data
    {
        [JsonPropertyName("manifest")]
        public string Manifest { get; set; } = "";

        [JsonPropertyName("manifestMimeType")]
        public string ManifestMimeType { get; set; } = "";
    }

    private sealed class TidalBtsManifest
    {
        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = "";

        [JsonPropertyName("codecs")]
        public string Codecs { get; set; } = "";

        [JsonPropertyName("urls")]
        public List<string> Urls { get; set; } = new();
    }
}
