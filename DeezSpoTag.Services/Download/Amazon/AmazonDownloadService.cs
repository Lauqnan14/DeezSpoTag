using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Linq;
using IOFile = System.IO.File;
using DeezSpoTag.Services.Download;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download.Utils;
using System.Diagnostics;
using DeezSpoTag.Integrations.Amazon;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Services.Download.Amazon;

public sealed class AmazonDownloadService : IAmazonDownloadService
{
    private const string ZarzBaseUrl = "https://api.zarz.moe/v2";
    private const string ZarzDownloadPath = "/dl/amazeamazeamaze";
    private const string ZarzTicketPath = "/tickets";
    private const string ZarzBootstrapPath = "/bootstrap";
    private const string ZarzChallengePath = "/challenge";
    private const string ZarzExchangePath = "/session/exchange";
    private const string ZarzAppVersion = "amzn@2.2.0";
    private const string ZarzPlatform = ZarzSignedSessionContract.Platform;
    private const string ZarzScheme = ZarzSignedSessionContract.SchemeLabel;
    private const string ZarzRefreshPath = ZarzSignedSessionContract.RefreshPath;
    private const int ZarzTimeWindowSeconds = ZarzSignedSessionContract.TimeWindowSeconds;
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
    private readonly ZarzSignedSessionCoordinator _zarzSessions;
    public AmazonDownloadService(
        ILogger<AmazonDownloadService> logger,
        IAmazonPublicProviderRegistry publicProviderRegistry,
        ZarzSignedSessionCoordinator zarzSessions)
    {
        _logger = logger;
        _publicProviderRegistry = publicProviderRegistry;
        _zarzSessions = zarzSessions;
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
                using var response = await SendZarzDownloadRequestAsync(asin, codec, cancellationToken);
                stopwatch.Stop();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var category = ClassifyDownloadFailure(response.StatusCode);
                    await _publicProviderRegistry.RecordFailureAsync(provider.Id, category, stopwatch.ElapsedMilliseconds, cancellationToken);
                    lastError = new InvalidOperationException(BuildZarzFailure("Amazon download API", response, body));
                    continue;
                }

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

    private async Task<HttpResponseMessage> SendZarzDownloadRequestAsync(
        string asin,
        string codec,
        CancellationToken cancellationToken)
    {
        var ticket = await RequestZarzTicketAsync(asin, cancellationToken);
        return await SendZarzSignedRequestAsync(
            HttpMethod.Post,
            ZarzDownloadPath,
            JsonSerializer.SerializeToUtf8Bytes(new { asin, codec }),
            new Dictionary<string, string> { ["X-Zarz-Ticket"] = ticket },
            cancellationToken);
    }

    private async Task<string> RequestZarzTicketAsync(string asin, CancellationToken cancellationToken)
    {
        var resourceHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"amazeamazeamaze:track:{asin.ToLowerInvariant()}")))
            .ToLowerInvariant();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            capability = "download_ticket",
            provider = "amazeamazeamaze",
            resource_hash = resourceHash
        });
        using var response = await SendZarzSignedRequestAsync(
            HttpMethod.Post,
            ZarzTicketPath,
            payload,
            null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildZarzFailure("Amazon ticket provider", response, body));
        }

        var ticket = JsonSerializer.Deserialize<ZarzTicketResponse>(body, SerializerOptions);
        var value = !string.IsNullOrWhiteSpace(ticket?.TicketId) ? ticket.TicketId : ticket?.Ticket;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("Amazon ticket provider did not return a ticket.");
    }

    private async Task<HttpResponseMessage> SendZarzSignedRequestAsync(
        HttpMethod method,
        string path,
        byte[] body,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken,
        bool allowSessionRetry = true,
        bool allowSessionRefresh = true)
    {
        var session = await _zarzSessions.EnsureSessionAsync(
            "amazon",
            BootstrapZarzSessionAsync,
            allowSessionRefresh ? RefreshZarzSessionAsync : null,
            cancellationToken);
        var uri = BuildZarzUri(path);
        var timestamp = DateTimeOffset.UtcNow;
        var timestampValue = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var window = timestamp.ToUnixTimeSeconds() / ZarzTimeWindowSeconds;
        var rollingInput = $"{window}:{session.SessionId}";
        var rollingKey = Base64UrlNoPadding(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(session.SessionSecret),
            Encoding.UTF8.GetBytes(rollingInput)));
        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var canonical = string.Join('\n',
            ZarzScheme,
            method.Method.ToUpperInvariant(),
            "/" + escapedPath.TrimStart('/'),
            string.Empty,
            bodyHash,
            timestampValue,
            nonce,
            session.SessionId,
            ZarzAppVersion,
            ZarzPlatform);
        var signature = Base64UrlNoPadding(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(rollingKey),
            Encoding.UTF8.GetBytes(canonical)));

        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{ZarzAppVersion}");
        request.Headers.TryAddWithoutValidation("X-Zarz-Session", session.SessionId);
        request.Headers.TryAddWithoutValidation("X-Zarz-Timestamp", timestampValue);
        request.Headers.TryAddWithoutValidation("X-Zarz-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Zarz-Body-SHA256", bodyHash);
        request.Headers.TryAddWithoutValidation("X-Zarz-Signature", signature);
        request.Headers.TryAddWithoutValidation("X-Zarz-App-Version", ZarzAppVersion);
        request.Headers.TryAddWithoutValidation("X-Zarz-Platform", ZarzPlatform);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var disposition = await _zarzSessions.ProcessResponseAsync(
                "amazon",
                session,
                response.StatusCode,
                responseBody,
                cancellationToken);
            if (allowSessionRetry && disposition is (ZarzResponseDisposition.SessionInvalid or ZarzResponseDisposition.RetryWithCurrentSession))
            {
                response.Dispose();
                return await SendZarzSignedRequestAsync(
                    method,
                    path,
                    body,
                    headers,
                    cancellationToken,
                    allowSessionRetry: false,
                    allowSessionRefresh: allowSessionRefresh);
            }
            if (disposition == ZarzResponseDisposition.VerificationRequired)
            {
                response.Dispose();
                throw new InvalidOperationException("Amazon public download verification is required.");
            }
            if (disposition == ZarzResponseDisposition.SessionInvalid)
            {
                response.Dispose();
                throw new InvalidOperationException("Amazon public download session is invalid and must be renewed.");
            }
        }
        return response;
    }

    public Task<bool> HasPublicDownloadSessionAsync(CancellationToken cancellationToken)
        => _zarzSessions.HasUsableSessionAsync("amazon", cancellationToken);

    public Task<bool> PeekPublicDownloadSessionAsync(CancellationToken cancellationToken)
        => _zarzSessions.PeekUsableSessionAsync("amazon", cancellationToken);

    public Task<string?> BeginPublicDownloadVerificationAsync(
        CancellationToken cancellationToken,
        string? publicAppBaseUrl = null)
        => _zarzSessions.BeginVerificationAsync(
            "amazon",
            (current, token) => BootstrapZarzSessionAsync(current, publicAppBaseUrl, token),
            cancellationToken);

    public async Task CompletePublicDownloadVerificationAsync(string grant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grant))
        {
            throw new ArgumentException("Amazon public download grant is required.", nameof(grant));
        }

        await _zarzSessions.CompleteVerificationAsync(
            "amazon",
            grant.Trim(),
            async (record, verificationGrant, token) =>
            {
                var body = await ZarzSignedSessionContract.ExchangeGrantAsync(
                    _client,
                    BuildZarzUri(ZarzExchangePath),
                    $"SpotiFLAC-Mobile/{ZarzAppVersion}",
                    verificationGrant,
                    record.InstallId,
                    ZarzAppVersion,
                    token);
                var exchanged = JsonSerializer.Deserialize<ZarzBootstrapResponse>(body, SerializerOptions);
                return new ZarzSignedSession
                {
                    InstallId = record.InstallId,
                    SessionId = exchanged?.SessionId ?? string.Empty,
                    SessionSecret = exchanged?.SessionSecret ?? string.Empty,
                    ExpiresAt = ParseZarzExpiry(exchanged?.ExpiresAt)
                };
            },
            cancellationToken);
    }

    private Task<ZarzSessionBootstrapResult> BootstrapZarzSessionAsync(
        ZarzSignedSession? current,
        CancellationToken cancellationToken)
        => BootstrapZarzSessionAsync(current, publicAppBaseUrl: null, cancellationToken);

    private async Task<ZarzSessionBootstrapResult> BootstrapZarzSessionAsync(
        ZarzSignedSession? current,
        string? publicAppBaseUrl,
        CancellationToken cancellationToken)
    {
        var installId = string.IsNullOrWhiteSpace(current?.InstallId)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
            : current.InstallId;
        var builder = new UriBuilder(BuildZarzUri(ZarzBootstrapPath))
        {
            Query = ZarzSignedSessionContract.BuildBootstrapQuery(installId, ZarzAppVersion)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{ZarzAppVersion}");
        using var response = await _client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var rateLimit = ZarzSessionRateLimitException.TryCreate(
                "Amazon session bootstrap",
                response.StatusCode,
                body,
                response);
            if (rateLimit is not null)
            {
                throw rateLimit;
            }

            throw new InvalidOperationException(BuildZarzFailure("Amazon session bootstrap", response, body));
        }

        var payload = JsonSerializer.Deserialize<ZarzBootstrapResponse>(body, SerializerOptions);
        var record = new ZarzSignedSession
        {
            InstallId = installId,
            SessionId = payload?.SessionId ?? string.Empty,
            SessionSecret = payload?.SessionSecret ?? string.Empty,
            ExpiresAt = ParseZarzExpiry(payload?.ExpiresAt)
        };
        var verificationUrl = ZarzSignedSessionContract.ResolveVerificationUrl(
            payload?.AuthUrl,
            payload?.ChallengeUrl,
            payload?.ChallengeId,
            ZarzBaseUrl,
            ZarzChallengePath,
            installId,
            publicAppBaseUrl);
        if (!record.IsUsable && string.IsNullOrWhiteSpace(verificationUrl))
        {
            throw new InvalidOperationException("Amazon session bootstrap did not return a verification challenge.");
        }
        return new(record, string.IsNullOrWhiteSpace(verificationUrl) ? null : verificationUrl);
    }

    private async Task<ZarzSignedSession> RefreshZarzSessionAsync(
        ZarzSignedSession current,
        CancellationToken cancellationToken)
    {
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(new { install_id = current.InstallId });
        using var response = await SendZarzSignedRequestAsync(
            HttpMethod.Post,
            ZarzRefreshPath,
            bodyBytes,
            null,
            cancellationToken,
            allowSessionRetry: false,
            allowSessionRefresh: false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildZarzFailure("Amazon session refresh", response, body));
        }

        var refreshed = JsonSerializer.Deserialize<ZarzBootstrapResponse>(body, SerializerOptions);
        return new ZarzSignedSession
        {
            InstallId = current.InstallId,
            SessionId = string.IsNullOrWhiteSpace(refreshed?.SessionId) ? current.SessionId : refreshed.SessionId,
            SessionSecret = string.IsNullOrWhiteSpace(refreshed?.SessionSecret) ? current.SessionSecret : refreshed.SessionSecret,
            ExpiresAt = ParseZarzExpiry(refreshed?.ExpiresAt) ?? current.ExpiresAt
        };
    }

    private static Uri BuildZarzUri(string path) => new($"{ZarzBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");

    private static string BuildZarzFailure(string name, HttpResponseMessage response, string body)
        => $"{name} failed with HTTP {(int)response.StatusCode}: {body.Trim()}";

    private static DateTimeOffset? ParseZarzExpiry(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string Base64UrlNoPadding(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

    private sealed class ZarzBootstrapResponse
    {
        [JsonPropertyName("auth_url")]
        public string? AuthUrl { get; set; }

        [JsonPropertyName("challenge_url")]
        public string? ChallengeUrl { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("session_secret")]
        public string? SessionSecret { get; set; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }

        [JsonPropertyName("challenge_id")]
        public string? ChallengeId { get; set; }
    }

    private sealed class ZarzTicketResponse
    {
        [JsonPropertyName("ticket_id")]
        public string? TicketId { get; set; }

        [JsonPropertyName("ticket")]
        public string? Ticket { get; set; }
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
