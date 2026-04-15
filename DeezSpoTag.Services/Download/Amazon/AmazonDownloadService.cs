using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using System.Linq;
using IOFile = System.IO.File;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared.Utils;
using System.Diagnostics;

namespace DeezSpoTag.Services.Download.Amazon;

public sealed class AmazonDownloadService : IAmazonDownloadService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private const string FlacExtension = ".flac";
    private const string ErrorLogLevel = "error";
    private const string ErrorStatus = "error";
    private const string TrackFlacFileName = "track.flac";
    private const string LucidaLoadApi = "https://lucida.to/api/load?url=/api/fetch/stream/v2";
    private static readonly string[] FfmpegExecutableNamesWindows = ["ffmpeg.exe", "ffmpeg"];
    private static readonly string[] FfmpegExecutableNamesUnix = ["ffmpeg"];
    private static readonly string[] FfprobeExecutableNamesWindows = ["ffprobe.exe", "ffprobe"];
    private static readonly string[] FfprobeExecutableNamesUnix = ["ffprobe"];
    private static readonly string[] LucidaTokenPatterns =
    [
        "token:\"([^\"]+)\"",
        "\"token\"\\s*:\\s*\"([^\"]+)\""
    ];
    private static readonly string[] LucidaStreamUrlPatterns =
    [
        "\"url\":\"([^\"]+)\"",
        "url:\"([^\"]+)\""
    ];
    private static readonly string[] LucidaTokenExpiryPatterns =
    [
        "tokenExpiry:(\\d+)",
        "\"tokenExpiry\"\\s*:\\s*(\\d+)"
    ];
    private static readonly string[] LucidaErrorPatterns =
    [
        "error:\"([^\"]+)\"",
        "\"error\"\\s*:\\s*\"([^\"]+)\""
    ];
    private static readonly char[] ContentDispositionTrimChars = ['\"', '\''];
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<AmazonDownloadService> _logger;
    private readonly HttpClient _client;
    private DateTimeOffset _lastApiCall = DateTimeOffset.MinValue;
    private int _apiCallCount;
    private DateTimeOffset _apiCallReset = DateTimeOffset.UtcNow;

    public AmazonDownloadService(ILogger<AmazonDownloadService> logger)
    {
        _logger = logger;
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
    }

    public async Task<string> DownloadAsync(
        AmazonDownloadRequest request,
        bool embedMaxQualityCover,
        DeezSpoTag.Core.Models.Settings.TagSettings? tagSettings,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDir);

        var amazonUrl = EngineLinkParser.TryNormalizeAmazonUrl(request.ServiceUrl);
        var resolvedSpotifyId = string.IsNullOrWhiteSpace(request.SpotifyId)
            ? EngineLinkParser.TryExtractSpotifyTrackId(request.ServiceUrl, RegexTimeout)
            : request.SpotifyId;
        if (string.IsNullOrWhiteSpace(amazonUrl))
        {
            if (string.IsNullOrWhiteSpace(resolvedSpotifyId))
            {
                throw new InvalidOperationException("Amazon download requires a service URL or Spotify ID");
            }

            amazonUrl = await GetAmazonUrlFromSpotifyAsync(resolvedSpotifyId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(amazonUrl))
        {
            throw new InvalidOperationException("Amazon Music URL not available");
        }

        var expectedPathContext = new AudioFilePathHelper.AudioPathContext
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
            UseAlbumTrackNumber = false,
            Sanitize = value => DownloadFileUtilities.SanitizeFilename(value, "Unknown")
        };
        var expectedPaths = AudioFilePathHelper.BuildExpectedPaths(expectedPathContext, FlacExtension, ".m4a");
        var existingPath = expectedPaths.FirstOrDefault(IOFile.Exists);
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            return existingPath;
        }

        var filePath = await DownloadFromServiceAsync(amazonUrl, request.OutputDir, progressCallback, cancellationToken);
        var renamedPath = await TryRenameAndTagAsync(
            new RenameAndTagRequest(
                FilePath: filePath,
                OutputDir: request.OutputDir,
                FilenameFormat: request.FilenameFormat,
                IncludeTrackNumber: request.IncludeTrackNumber,
                Position: request.Position,
                TrackTitle: request.TrackName,
                ArtistName: request.ArtistName,
                AlbumTitle: request.AlbumName,
                AlbumArtist: request.AlbumArtist,
                ReleaseDate: request.ReleaseDate,
                CoverUrl: request.CoverUrl,
                Isrc: request.Isrc,
                SpotifyTrackNumber: request.SpotifyTrackNumber,
                SpotifyDiscNumber: request.SpotifyDiscNumber,
                SpotifyTotalTracks: request.SpotifyTotalTracks,
                EmbedMaxQualityCover: embedMaxQualityCover,
                TagSettings: tagSettings),
            cancellationToken);

        return renamedPath ?? filePath;
    }

    private async Task<string> GetAmazonUrlFromSpotifyAsync(string spotifyId, CancellationToken cancellationToken)
    {
        await ThrottleSongLinkAsync(cancellationToken);
        _client.DefaultRequestHeaders.UserAgent.Clear();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(GetRandomUserAgent());
        var amazonUrl = await SongLinkClient.ResolvePlatformUrlAsync(_client, spotifyId, "amazonMusic", cancellationToken);
        if (amazonUrl.Contains("trackAsin=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = amazonUrl.Split("trackAsin=", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                var asin = parts[1].Split('&')[0];
                amazonUrl = $"https://music.amazon.com/tracks/{asin}?musicTerritory=US";
            }
        }

        return amazonUrl;
    }

    private async Task<string> DownloadFromServiceAsync(string amazonUrl, string outputDir, Func<double, double, Task>? progressCallback, CancellationToken cancellationToken)
    {
        var regions = new[] { "us", "eu" };
        Exception? lastError = null;

        try
        {
            var afkarPath = await DownloadFromAfkarAsync(amazonUrl, outputDir, progressCallback, cancellationToken);
            if (!string.IsNullOrWhiteSpace(afkarPath))
            {
                return afkarPath;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Amazon afkarxyz download failed; falling back to Lucida/DoubleDouble");
            lastError = ex;
        }

        try
        {
            var lucidaPath = await DownloadFromLucidaAsync(amazonUrl, outputDir, cancellationToken);
            if (!string.IsNullOrWhiteSpace(lucidaPath))
            {
                return lucidaPath;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Lucida download failed; falling back to DoubleDouble");
            lastError = ex;
        }

        foreach (var region in regions)
        {
            try
            {
                var outputPath = await TryDownloadFromDoubleDoubleRegionAsync(region, amazonUrl, outputDir, progressCallback, cancellationToken);
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    return outputPath;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Amazon download failed for region {Region}", region);
                lastError = ex;
            }
        }

        throw new InvalidOperationException("Amazon download failed", lastError);
    }

    private async Task<string?> TryDownloadFromDoubleDoubleRegionAsync(
        string region,
        string amazonUrl,
        string outputDir,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var baseUrl = $"https://{region}.doubledouble.top";
        var submitUrl = $"{baseUrl}/dl?url={WebUtility.UrlEncode(amazonUrl)}";

        using var submitReq = CreateGetRequestWithRandomUserAgent(submitUrl);
        using var submitResp = await _client.SendAsync(submitReq, cancellationToken);
        if (!submitResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Amazon submit failed ({Status}) for region {Region}", (int)submitResp.StatusCode, region);
            return null;
        }

        var submitBody = await submitResp.Content.ReadAsStringAsync(cancellationToken);
        var submit = JsonSerializer.Deserialize<DoubleDoubleSubmitResponse>(submitBody, SerializerOptions);
        if (submit == null || !submit.Success || string.IsNullOrWhiteSpace(submit.Id))
        {
            _logger.LogWarning("Amazon submit payload invalid for region {Region}: {Body}", region, submitBody);
            return null;
        }

        var statusUrl = $"{baseUrl}/dl/{submit.Id}";
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Amazon status URL: {StatusUrl}", statusUrl);        }
        var fileUrl = await PollStatusAsync(statusUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(fileUrl))
        {
            _logger.LogWarning("Amazon status polling returned empty file URL for region {Region}", region);
            return null;
        }

        fileUrl = NormalizeDoubleDoubleFileUrl(fileUrl, baseUrl);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Amazon download URL resolved: {FileUrl}", fileUrl);        }

        var filename = $"{Guid.NewGuid():N}{FlacExtension}";
        var outputPath = Path.Join(outputDir, filename);
        await DownloadFileAsync(fileUrl, outputPath, progressCallback, cancellationToken);
        return outputPath;
    }

    private static string NormalizeDoubleDoubleFileUrl(string fileUrl, string baseUrl)
    {
        if (fileUrl.StartsWith("./", StringComparison.Ordinal))
        {
            return $"{baseUrl}/{fileUrl.TrimStart('.', '/')}";
        }
        if (fileUrl.StartsWith('/'))
        {
            return $"{baseUrl}/{fileUrl.TrimStart('/')}";
        }
        return fileUrl;
    }

    private async Task<string?> DownloadFromAfkarAsync(
        string amazonUrl,
        string outputDir,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var asin = ExtractAmazonAsin(amazonUrl);
        if (string.IsNullOrWhiteSpace(asin))
        {
            return null;
        }

        var apiUrl = $"https://amazon.afkarxyz.fun/api/track/{asin}";
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Afkar API failed ({(int)response.StatusCode})");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Afkar API returned empty response.");
        }

        var payload = JsonSerializer.Deserialize<AfkarStreamResponse>(body, SerializerOptions);
        if (string.IsNullOrWhiteSpace(payload?.StreamUrl))
        {
            throw new InvalidOperationException("Afkar response missing stream URL.");
        }

        var sourceExtension = InferAudioExtension(payload.StreamUrl, ".m4a");
        var encryptedPath = Path.Join(outputDir, $"{Guid.NewGuid():N}{sourceExtension}");
        await DownloadFileAsync(payload.StreamUrl, encryptedPath, progressCallback, cancellationToken);

        if (string.IsNullOrWhiteSpace(payload.DecryptionKey))
        {
            return encryptedPath;
        }

        return await DecryptAmazonMediaAsync(encryptedPath, payload.DecryptionKey, outputDir, cancellationToken);
    }

    private static async Task<string> DecryptAmazonMediaAsync(
        string encryptedPath,
        string decryptionKey,
        string outputDir,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new InvalidOperationException("ffmpeg not available for Amazon decryption.");
        }

        var codec = await TryDetectAudioCodecAsync(encryptedPath, cancellationToken);
        var extension = string.Equals(codec, "flac", StringComparison.OrdinalIgnoreCase) ? FlacExtension : ".m4a";
        var outputPath = Path.Join(outputDir, $"{Guid.NewGuid():N}{extension}");

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add(ErrorLogLevel);
        startInfo.ArgumentList.Add("-decryption_key");
        startInfo.ArgumentList.Add(decryptionKey.Trim());
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(encryptedPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg decryption failed: {DownloadFileUtilities.TruncateForLog(stderr)}");
        }

        if (!IOFile.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
        {
            throw new InvalidOperationException("ffmpeg decryption produced no output.");
        }

        DownloadFileUtilities.TryDeleteFile(encryptedPath);
        return outputPath;
    }

    private static async Task<string?> TryDetectAudioCodecAsync(string filePath, CancellationToken cancellationToken)
    {
        var ffprobePath = ResolveFfprobePath();
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add(ErrorLogLevel);
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("a:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=codec_name");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(filePath);

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        if (process.ExitCode != 0)
        {
            return null;
        }

        var codec = stdout.Trim();
        return string.IsNullOrWhiteSpace(codec) ? null : codec;
    }

    private static string? ResolveFfmpegPath()
    {
        return DownloadFileUtilities.ResolveExecutablePath(
            OperatingSystem.IsWindows() ? FfmpegExecutableNamesWindows : FfmpegExecutableNamesUnix);
    }

    private static string? ResolveFfprobePath()
    {
        return DownloadFileUtilities.ResolveExecutablePath(
            OperatingSystem.IsWindows() ? FfprobeExecutableNamesWindows : FfprobeExecutableNamesUnix);
    }

    private static async Task<string?> DownloadFromLucidaAsync(string amazonUrl, string outputDir, CancellationToken cancellationToken)
    {
        using var handler = CreateLucidaHttpHandler();
        using var client = CreateLucidaHttpClient(handler);
        var userAgent = GetRandomUserAgent();

        var sessionData = await InitializeLucidaSessionAsync(client, userAgent, amazonUrl, cancellationToken);
        var loadData = await SubmitLucidaLoadAsync(client, handler.CookieContainer, userAgent, sessionData, cancellationToken);

        var completionUrl = $"https://{loadData.Server}.lucida.to/api/fetch/request/{loadData.Handoff}";
        await WaitForLucidaCompletionAsync(client, userAgent, completionUrl, cancellationToken);

        var downloadUrl = $"{completionUrl}/download";
        return await DownloadLucidaFileAsync(client, userAgent, downloadUrl, outputDir, cancellationToken);
    }

    private static HttpClientHandler CreateLucidaHttpHandler()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer()
        };
        TlsPolicy.ApplyIfAllowed(handler, configuration: null);
        return handler;
    }

    private static HttpClient CreateLucidaHttpClient(HttpClientHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    private static async Task<LucidaSessionData> InitializeLucidaSessionAsync(
        HttpClient client,
        string userAgent,
        string amazonUrl,
        CancellationToken cancellationToken)
    {
        var lucidaUrl = $"https://lucida.to/?url={WebUtility.UrlEncode(amazonUrl)}&country=auto";
        using var initialReq = new HttpRequestMessage(HttpMethod.Get, lucidaUrl);
        initialReq.Headers.UserAgent.ParseAdd(userAgent);
        using var initialResp = await client.SendAsync(initialReq, cancellationToken);
        if (!initialResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Lucida init failed ({(int)initialResp.StatusCode})");
        }

        var html = await initialResp.Content.ReadAsStringAsync(cancellationToken);
        var token = ExtractLucidaData(html, LucidaTokenPatterns);
        var streamUrl = ExtractLucidaData(html, LucidaStreamUrlPatterns);
        var expiry = ExtractLucidaData(html, LucidaTokenExpiryPatterns);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(streamUrl))
        {
            var errorMsg = ExtractLucidaData(html, LucidaErrorPatterns);
            throw !string.IsNullOrWhiteSpace(errorMsg)
                ? new InvalidOperationException($"Lucida error: {errorMsg}")
                : new InvalidOperationException("Lucida init missing token or stream URL");
        }

        return new LucidaSessionData(
            DecodedToken: DecodeLucidaToken(token),
            StreamUrl: streamUrl.Replace("\\/", "/", StringComparison.Ordinal),
            Expiry: expiry);
    }

    private static string DecodeLucidaToken(string token)
    {
        try
        {
            var second = Convert.FromBase64String(token);
            var first = Convert.FromBase64String(Encoding.UTF8.GetString(second));
            return Encoding.UTF8.GetString(first);
        }
        catch
        {
            return token;
        }
    }

    private static async Task<LucidaLoadResponse> SubmitLucidaLoadAsync(
        HttpClient client,
        CookieContainer cookieContainer,
        string userAgent,
        LucidaSessionData sessionData,
        CancellationToken cancellationToken)
    {
        var loadPayload = new
        {
            account = new { id = "auto", type = "country" },
            compat = "false",
            downscale = "original",
            handoff = true,
            metadata = true,
            @private = true,
            token = new { primary = sessionData.DecodedToken, expiry = sessionData.Expiry },
            upload = new { enabled = false },
            url = sessionData.StreamUrl
        };

        using var loadReq = new HttpRequestMessage(HttpMethod.Post, LucidaLoadApi);
        loadReq.Headers.UserAgent.ParseAdd(userAgent);
        loadReq.Content = new StringContent(JsonSerializer.Serialize(loadPayload), Encoding.UTF8, "application/json");
        AddLucidaCsrfHeaders(loadReq, cookieContainer);

        using var loadResp = await client.SendAsync(loadReq, cancellationToken);
        if (!loadResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Lucida load failed ({(int)loadResp.StatusCode})");
        }

        var loadData = await loadResp.Content.ReadFromJsonAsync<LucidaLoadResponse>(SerializerOptions, cancellationToken);
        if (loadData == null || !loadData.Success)
        {
            throw new InvalidOperationException($"Lucida load request failed: {loadData?.Error ?? "unknown"}");
        }

        return loadData;
    }

    private static void AddLucidaCsrfHeaders(HttpRequestMessage request, CookieContainer cookieContainer)
    {
        foreach (Cookie cookie in cookieContainer
                     .GetCookies(new Uri(LucidaLoadApi))
                     .Where(static cookie => string.Equals(cookie.Name, "csrf_token", StringComparison.OrdinalIgnoreCase)))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-Token", cookie.Value);
        }
    }

    private static async Task WaitForLucidaCompletionAsync(
        HttpClient client,
        string userAgent,
        string completionUrl,
        CancellationToken cancellationToken)
    {
        var maxWait = TimeSpan.FromSeconds(300);
        var elapsed = TimeSpan.Zero;
        var pollInterval = TimeSpan.FromSeconds(2);

        while (elapsed < maxWait)
        {
            await Task.Delay(pollInterval, cancellationToken);
            elapsed += pollInterval;

            using var statusReq = new HttpRequestMessage(HttpMethod.Get, completionUrl);
            statusReq.Headers.UserAgent.ParseAdd(userAgent);
            using var statusResp = await client.SendAsync(statusReq, cancellationToken);
            if (!statusResp.IsSuccessStatusCode)
            {
                continue;
            }

            var status = await statusResp.Content.ReadFromJsonAsync<LucidaStatusResponse>(SerializerOptions, cancellationToken);
            if (status == null)
            {
                continue;
            }

            if (string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(status.Status, ErrorStatus, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Lucida processing failed: {status.Message}");
            }
        }

        throw new InvalidOperationException("Lucida processing timeout");
    }

    private static async Task<string> DownloadLucidaFileAsync(
        HttpClient client,
        string userAgent,
        string downloadUrl,
        string outputDir,
        CancellationToken cancellationToken)
    {
        using var downloadReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        downloadReq.Headers.UserAgent.ParseAdd(userAgent);
        using var downloadResp = await client.SendAsync(downloadReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!downloadResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Lucida download failed ({(int)downloadResp.StatusCode})");
        }

        var filePath = Path.Join(outputDir, ResolveLucidaFileName(downloadResp));
        await using var responseStream = await downloadResp.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = IOFile.Create(filePath);
        await DownloadStreamHelper.CopyToAsyncWithProgress(
            responseStream,
            output,
            downloadResp.Content.Headers.ContentLength,
            null,
            cancellationToken);
        return filePath;
    }

    private static string ResolveLucidaFileName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        if (disposition == null)
        {
            return TrackFlacFileName;
        }

        var rawName = disposition.FileNameStar ?? disposition.FileName;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return TrackFlacFileName;
        }

        rawName = rawName.Trim(ContentDispositionTrimChars);
        if (rawName.StartsWith("UTF-8''", StringComparison.OrdinalIgnoreCase))
        {
            rawName = Uri.UnescapeDataString(rawName[7..]);
        }

        var sanitized = Regex.Replace(rawName, "[<>:\"/\\\\|?*]", string.Empty, RegexOptions.None, RegexTimeout);
        return string.IsNullOrWhiteSpace(sanitized) ? TrackFlacFileName : sanitized;
    }

    private static string ExtractLucidaData(string html, IEnumerable<string> patterns)
    {
        var match = patterns
            .Select(pattern => Regex.Match(html, pattern, RegexOptions.None, RegexTimeout))
            .FirstOrDefault(static candidate => candidate.Success && candidate.Groups.Count > 1);

        return match?.Groups[1].Value ?? string.Empty;
    }

    private async Task<string?> PollStatusAsync(string statusUrl, CancellationToken cancellationToken)
    {
        var maxWait = TimeSpan.FromMinutes(5);
        var pollInterval = TimeSpan.FromSeconds(3);
        var elapsed = TimeSpan.Zero;

        while (elapsed < maxWait)
        {
            await Task.Delay(pollInterval, cancellationToken);
            elapsed += pollInterval;

            using var statusReq = CreateGetRequestWithRandomUserAgent(statusUrl);
            using var statusResp = await _client.SendAsync(statusReq, cancellationToken);
            if (!statusResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Amazon status polling failed ({Status})", (int)statusResp.StatusCode);
                continue;
            }

            var statusBody = await statusResp.Content.ReadAsStringAsync(cancellationToken);
            var status = JsonSerializer.Deserialize<DoubleDoubleStatusResponse>(statusBody, SerializerOptions);
            if (status == null)
            {
                _logger.LogWarning("Amazon status polling returned invalid JSON: {Body}", statusBody);
                continue;
            }

            if (string.Equals(status.Status, "done", StringComparison.OrdinalIgnoreCase))
            {
                return status.Url;
            }

            if (string.Equals(status.Status, ErrorStatus, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(status.FriendlyStatus ?? "Amazon processing failed");
            }

            if (!string.IsNullOrWhiteSpace(status.Status) && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Amazon status: {Status}", status.Status);
            }
        }

        throw new InvalidOperationException("Amazon download timed out");
    }

    private async Task DownloadFileAsync(string url, string outputPath, Func<double, double, Task>? progressCallback, CancellationToken cancellationToken)
    {
        using var request = CreateGetRequestWithRandomUserAgent(url);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = IOFile.Create(outputPath);
        await DownloadStreamHelper.CopyToAsyncWithProgress(stream, file, response.Content.Headers.ContentLength, progressCallback, cancellationToken);
    }

    private static HttpRequestMessage CreateGetRequestWithRandomUserAgent(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(GetRandomUserAgent());
        return request;
    }

    private async Task<string?> TryRenameAndTagAsync(RenameAndTagRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || string.IsNullOrWhiteSpace(request.ArtistName))
        {
            return request.FilePath;
        }

        var extension = Path.GetExtension(request.FilePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = FlacExtension;
        }

        var outputPathContext = new AudioFilePathHelper.AudioPathContext
        {
            OutputDir = request.OutputDir,
            Title = request.TrackTitle,
            Artist = request.ArtistName,
            Album = request.AlbumTitle,
            AlbumArtist = request.AlbumArtist,
            ReleaseDate = request.ReleaseDate,
            TrackNumber = request.SpotifyTrackNumber,
            DiscNumber = request.SpotifyDiscNumber,
            FilenameFormat = request.FilenameFormat,
            IncludeTrackNumber = request.IncludeTrackNumber,
            Position = request.Position,
            UseAlbumTrackNumber = false,
            Sanitize = value => DownloadFileUtilities.SanitizeFilename(value, "Unknown")
        };
        var newPath = AudioFilePathHelper.BuildOutputPath(outputPathContext, extension);

        try
        {
            if (!string.Equals(request.FilePath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                IOFile.Move(request.FilePath, newPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to rename Amazon download");
            newPath = request.FilePath;
        }

        await AudioFileTaggingHelper.TryTagAsync(
            new AudioFileTaggingHelper.AudioTaggingRequest(
                Logger: _logger,
                EngineName: "Amazon",
                HttpClient: _client,
                FilePath: newPath,
                TagData: AudioFileTaggingHelper.CreateTagData(
                    new AudioFileTaggingHelper.AudioTagDataInput(
                        Title: request.TrackTitle,
                        Artist: request.ArtistName,
                        Album: request.AlbumTitle,
                        AlbumArtist: request.AlbumArtist,
                        ReleaseDate: request.ReleaseDate,
                        TrackNumber: request.SpotifyTrackNumber,
                        DiscNumber: request.SpotifyDiscNumber,
                        TotalTracks: request.SpotifyTotalTracks,
                        Isrc: request.Isrc)),
                CoverUrl: request.CoverUrl,
                EmbedMaxQualityCover: request.EmbedMaxQualityCover,
                TagSettings: request.TagSettings),
            cancellationToken);

        AudioFilePathHelper.EnsureIsrcMatchOrThrow(newPath, request.Isrc);

        return newPath;
    }

    private static string InferAudioExtension(string sourceUrl, string fallback)
    {
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext))
            {
                return AudioFilePathHelper.NormalizeAudioExtension(ext, FlacExtension);
            }
        }

        return AudioFilePathHelper.NormalizeAudioExtension(fallback, FlacExtension);
    }

    private static string? ExtractAmazonAsin(string amazonUrl)
    {
        if (string.IsNullOrWhiteSpace(amazonUrl))
        {
            return null;
        }

        var match = Regex.Match(amazonUrl, "(B[0-9A-Z]{9})", RegexOptions.IgnoreCase, RegexTimeout);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private async Task ThrottleSongLinkAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _apiCallReset >= TimeSpan.FromMinutes(1))
        {
            _apiCallCount = 0;
            _apiCallReset = now;
        }

        if (_apiCallCount >= 9)
        {
            var waitTime = TimeSpan.FromMinutes(1) - (now - _apiCallReset);
            if (waitTime > TimeSpan.Zero)
            {
                await Task.Delay(waitTime, cancellationToken);
            }
            _apiCallCount = 0;
            _apiCallReset = DateTimeOffset.UtcNow;
        }

        if (_lastApiCall != DateTimeOffset.MinValue)
        {
            var since = DateTimeOffset.UtcNow - _lastApiCall;
            var minDelay = TimeSpan.FromSeconds(7);
            if (since < minDelay)
            {
                await Task.Delay(minDelay - since, cancellationToken);
            }
        }

        _lastApiCall = DateTimeOffset.UtcNow;
        _apiCallCount++;
    }

    private static string GetRandomUserAgent()
    {
        var rand = Random.Shared;
        return $"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_{rand.Next(11, 15)}_{rand.Next(4, 9)}) " +
               $"AppleWebKit/{rand.Next(530, 537)}.{rand.Next(30, 37)} (KHTML, like Gecko) " +
               $"Chrome/{rand.Next(80, 105)}.0.{rand.Next(3000, 4500)}.{rand.Next(60, 130)} Safari/{rand.Next(530, 537)}.{rand.Next(30, 37)}";
    }

    private sealed record RenameAndTagRequest(
        string FilePath,
        string OutputDir,
        string FilenameFormat,
        bool IncludeTrackNumber,
        int Position,
        string TrackTitle,
        string ArtistName,
        string AlbumTitle,
        string AlbumArtist,
        string ReleaseDate,
        string CoverUrl,
        string Isrc,
        int SpotifyTrackNumber,
        int SpotifyDiscNumber,
        int SpotifyTotalTracks,
        bool EmbedMaxQualityCover,
        DeezSpoTag.Core.Models.Settings.TagSettings? TagSettings);

    private sealed record LucidaSessionData(string DecodedToken, string StreamUrl, string Expiry);

    private sealed class AfkarStreamResponse
    {
        [JsonPropertyName("streamUrl")]
        public string StreamUrl { get; set; } = "";

        [JsonPropertyName("decryptionKey")]
        public string DecryptionKey { get; set; } = "";
    }

    private sealed class DoubleDoubleSubmitResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
    }

    private sealed class DoubleDoubleStatusResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("friendlyStatus")]
        public string? FriendlyStatus { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    private sealed class LucidaLoadResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("server")]
        public string Server { get; set; } = "";

        [JsonPropertyName("handoff")]
        public string Handoff { get; set; } = "";

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class LucidaStatusResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("progress")]
        public LucidaProgress Progress { get; set; } = new();
    }

    private sealed class LucidaProgress
    {
        [JsonPropertyName("current")]
        public long Current { get; set; }

        [JsonPropertyName("total")]
        public long Total { get; set; }
    }
}
