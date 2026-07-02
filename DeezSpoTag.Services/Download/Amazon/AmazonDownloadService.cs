using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using IOFile = System.IO.File;
using DeezSpoTag.Services.Download;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download.Utils;
using System.Diagnostics;
using DeezSpoTag.Integrations.Amazon;
using DeezSpoTag.Services.Download.Shared.Utils;

namespace DeezSpoTag.Services.Download.Amazon;

public sealed class AmazonDownloadService : IAmazonDownloadService
{
    private const string FlacExtension = ".flac";
    private const string ErrorLogLevel = "error";
    private static readonly string[] FfmpegExecutableNamesWindows = ["ffmpeg.exe", "ffmpeg"];
    private static readonly string[] FfmpegExecutableNamesUnix = ["ffmpeg"];
    private static readonly string[] FfprobeExecutableNamesWindows = ["ffprobe.exe", "ffprobe"];
    private static readonly string[] FfprobeExecutableNamesUnix = ["ffprobe"];
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<AmazonDownloadService> _logger;
    private readonly IAmazonPublicProviderRegistry _publicProviderRegistry;
    private readonly HttpClient _client;
    public AmazonDownloadService(
        ILogger<AmazonDownloadService> logger,
        IAmazonPublicProviderRegistry publicProviderRegistry)
    {
        _logger = logger;
        _publicProviderRegistry = publicProviderRegistry;
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
        if (string.IsNullOrWhiteSpace(amazonUrl))
        {
            if (string.IsNullOrWhiteSpace(request.AmazonId))
            {
                throw new InvalidOperationException("Amazon download requires a valid Amazon ID or service URL.");
            }

            amazonUrl = $"https://music.amazon.com/tracks/{Uri.EscapeDataString(request.AmazonId.Trim())}";
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
        await EnsureExpectedFinalDestinationsAllowedAsync(request, expectedPaths, cancellationToken);

        var filePath = await DownloadFromServiceAsync(amazonUrl, request.Quality, request.OutputDir, progressCallback, cancellationToken);
        var durationValidation = AudioDurationGuard.ValidateAgainstPreview(filePath, request.DurationSeconds);
        if (!durationValidation.Success)
        {
            DownloadFileUtilities.TryDeleteFile(filePath);
            throw new InvalidOperationException(durationValidation.Message);
        }

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
                TagSettings: tagSettings,
                RequestedLocalQualityRank: request.RequestedLocalQualityRank));

        return renamedPath ?? filePath;
    }

    private async Task<string> DownloadFromServiceAsync(
        string amazonUrl,
        string quality,
        string outputDir,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var asin = ExtractAmazonAsin(amazonUrl);
        if (string.IsNullOrWhiteSpace(asin))
        {
            throw new InvalidOperationException("Amazon download requires a valid Amazon ASIN.");
        }

        var codec = ResolveAmazonCodec(quality);
        var providers = (await _publicProviderRegistry.GetProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled)
            .ToArray();
        if (providers.Length == 0)
        {
            throw new InvalidOperationException("No enabled Amazon public download API provider.");
        }

        Exception? lastError = null;
        foreach (var provider in providers)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var apiUrl = BuildAmazonMediaApiUrl(provider.Endpoint, asin, codec);
                using var request = CreateGetRequestWithRandomUserAgent(apiUrl);
                using var response = await _client.SendAsync(request, cancellationToken);
                stopwatch.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    var category = ClassifyDownloadFailure(response.StatusCode);
                    await _publicProviderRegistry.RecordFailureAsync(provider.Id, category, stopwatch.ElapsedMilliseconds, cancellationToken);
                    lastError = new InvalidOperationException($"Amazon download API failed ({(int)response.StatusCode}).");
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var media = DeserializeZarzMedia(body);
                if (string.IsNullOrWhiteSpace(media?.Audio?.Url))
                {
                    await _publicProviderRegistry.RecordFailureAsync(provider.Id, "empty_response", stopwatch.ElapsedMilliseconds, cancellationToken);
                    lastError = new InvalidOperationException("Amazon download URL not available.");
                    continue;
                }

                await _publicProviderRegistry.RecordSuccessAsync(provider.Id, stopwatch.ElapsedMilliseconds, cancellationToken);
                var extension = ResolveAmazonExtension(codec, media.Audio.Codec, media.Audio.Url);
                var encryptedPath = Path.Join(outputDir, $"{Guid.NewGuid():N}{extension}");
                await DownloadFileAsync(media.Audio.Url, encryptedPath, progressCallback, cancellationToken);
                return string.IsNullOrWhiteSpace(media.Audio.Key)
                    ? encryptedPath
                    : await DecryptAmazonMediaAsync(encryptedPath, media.Audio.Key, outputDir, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                _logger.LogDebug(ex, "Amazon public provider download failed for {ProviderId}.", provider.Id);
                await _publicProviderRegistry.RecordFailureAsync(provider.Id, "transient", stopwatch.ElapsedMilliseconds, cancellationToken);
                lastError = ex;
            }
            catch (JsonException ex)
            {
                stopwatch.Stop();
                _logger.LogDebug(ex, "Amazon public provider returned invalid JSON for {ProviderId}.", provider.Id);
                await _publicProviderRegistry.RecordFailureAsync(provider.Id, "empty_response", stopwatch.ElapsedMilliseconds, cancellationToken);
                lastError = new InvalidOperationException("Amazon download API returned invalid media metadata.", ex);
            }
            catch (IOException ex)
            {
                stopwatch.Stop();
                _logger.LogDebug(ex, "Amazon public provider download IO failed for {ProviderId}.", provider.Id);
                await _publicProviderRegistry.RecordFailureAsync(provider.Id, "transient", stopwatch.ElapsedMilliseconds, cancellationToken);
                lastError = ex;
            }
            catch (InvalidOperationException ex)
            {
                stopwatch.Stop();
                _logger.LogDebug(ex, "Amazon public provider download failed for {ProviderId}.", provider.Id);
                await _publicProviderRegistry.RecordFailureAsync(provider.Id, "transient", stopwatch.ElapsedMilliseconds, cancellationToken);
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Amazon download URL not available.");
    }

    private static string BuildAmazonMediaApiUrl(string endpoint, string asin, string codec)
    {
        var baseEndpoint = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseEndpoint))
        {
            throw new InvalidOperationException("Amazon public download API endpoint is not configured.");
        }

        return $"{baseEndpoint}/media?asin={Uri.EscapeDataString(asin)}&codec={Uri.EscapeDataString(codec)}";
    }

    private static string ClassifyDownloadFailure(System.Net.HttpStatusCode statusCode)
        => statusCode == System.Net.HttpStatusCode.TooManyRequests
            ? "rate_limited"
            : (int)statusCode >= 500
                ? "transient"
                : "offline";

    private static string ResolveAmazonCodec(string? quality)
        => (quality ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "DOLBY_ATMOS" => "eac3",
            "OPUS" => "opus",
            _ => "flac"
        };

    private static string ResolveAmazonExtension(string requestedCodec, string? actualCodec, string url)
    {
        var codec = string.IsNullOrWhiteSpace(actualCodec) ? requestedCodec : actualCodec.Trim().ToLowerInvariant();
        if (codec.Contains("opus", StringComparison.OrdinalIgnoreCase))
        {
            return ".opus";
        }

        if (codec.Contains("eac3", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("ec-3", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("atmos", StringComparison.OrdinalIgnoreCase))
        {
            return ".m4a";
        }

        return InferAudioExtension(url, FlacExtension);
    }

    private static ZarzMediaResponse? DeserializeZarzMedia(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            var first = root.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.Undefined
                ? null
                : first.Deserialize<ZarzMediaResponse>(SerializerOptions);
        }

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array)
            {
                var first = data.EnumerateArray().FirstOrDefault();
                return first.ValueKind == JsonValueKind.Undefined
                    ? null
                    : first.Deserialize<ZarzMediaResponse>(SerializerOptions);
            }

            if (data.ValueKind == JsonValueKind.Object)
            {
                return data.Deserialize<ZarzMediaResponse>(SerializerOptions);
            }
        }

        return root.Deserialize<ZarzMediaResponse>(SerializerOptions);
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

    private async Task<string?> TryRenameAndTagAsync(RenameAndTagRequest request)
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
        await EnsureFinalDestinationAllowedAsync(request, newPath);

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

        return newPath;
    }

    private static async Task EnsureExpectedFinalDestinationsAllowedAsync(
        AmazonDownloadRequest request,
        IReadOnlyList<string> expectedPaths,
        CancellationToken cancellationToken)
    {
        foreach (var expectedPath in expectedPaths)
        {
            var decision = await DownloadDedupeService.CheckFinalDestinationAsync(
                DownloadDedupeService.FromEngineDownloadRequest(request, expectedPath),
                cancellationToken);
            if (!decision.Allowed)
            {
                throw new InvalidOperationException(decision.Message ?? "Amazon final destination rejected by dedupe.");
            }
        }
    }

    private static async Task EnsureFinalDestinationAllowedAsync(
        RenameAndTagRequest request,
        string outputPath)
    {
        var decision = await DownloadDedupeService.CheckFinalDestinationAsync(
            new DownloadDedupeRequest
            {
                Isrc = request.Isrc,
                TrackTitle = request.TrackTitle,
                TrackArtist = request.ArtistName,
                TrackPrimaryArtist = NormalizePrimaryArtist(request.ArtistName),
                Album = request.AlbumTitle,
                ReleaseDate = request.ReleaseDate,
                RequestedLocalQualityRank = request.RequestedLocalQualityRank,
                FinalOutputPath = outputPath
            });
        if (!decision.Allowed)
        {
            throw new InvalidOperationException(decision.Message ?? "Amazon final destination rejected by dedupe.");
        }
    }

    private static string? NormalizePrimaryArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var normalized = artist.Split([',', '&'], 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
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
        => EngineLinkParser.NormalizeAmazonTrackId(amazonUrl)
           ?? EngineLinkParser.TryExtractAmazonTrackId(amazonUrl, TimeSpan.FromMilliseconds(250));

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
        DeezSpoTag.Core.Models.Settings.TagSettings? TagSettings,
        int? RequestedLocalQualityRank);

    private sealed class ZarzMediaResponse
    {
        [JsonPropertyName("audio")]
        public ZarzAudioResponse? Audio { get; set; }
    }

    private sealed class ZarzAudioResponse
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [JsonPropertyName("codec")]
        public string Codec { get; set; } = "";
    }
}
