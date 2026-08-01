using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Security;
using DeezSpoTag.Services.Utils;
using Microsoft.AspNetCore.DataProtection;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyBlobService : IAsyncDisposable
{
    private const string DefaultWebPlayerUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string ProtobufRuntimeEnv = "PROTOCOL_BUFFERS_PYTHON_IMPLEMENTATION";
    private const string ProtobufRuntimeValue = "python";
    private const string ProjectWebFolder = "DeezSpoTag.Web";
    private const string ToolsFolder = "Tools";
    private const string PayloadField = "payload";
    private const string ErrorField = "error";
    private const string MissingBlobError = "missing_blob";
    private const string RequestFailedError = "request_failed";
    private const string UnknownError = "unknown_error";
    private const string MissingPayloadError = "missing_payload";
    private const string ExceptionError = "exception";
    private const string InvalidLibrespotBlobError = "invalid_librespot_blob";
    private const string CredentialsArg = "--credentials";
    private const string SpotifyCookieDomain = ".spotify.com";
    private const string SpotifyDcCookie = "sp_dc";
    private const string SpotifyLibrespotFolder = "spotify_librespot";
    private const string SpotizerrPhoenixFolder = "spotizerr-phoenix";
    private const string ZeroconfAuthScript = "spotify_zeroconf_auth.py";
    private const int SpotifyConnectListenerPort = 4070;
    private const string LibrespotWorkerScript = "spotify_librespot_worker.py";
    private const string SpotifyOpenHost = "open.spotify.com";
    private const string SpotifyOpenTokenPath = "/api/token";
    private const string AllRetriesFailedError = "all_retries_failed";
    private const string ProcessTimeoutError = "process_timeout";
    private const string WebPlayerProtectionPurpose = "DeezSpoTag.Spotify.WebPlayer";
    private const string LibrespotProtectionPurpose = "DeezSpoTag.Spotify.Librespot";
    private static readonly TimeSpan LibrespotMetadataRequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan[] WebApiRetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    };
    private static readonly HashSet<string> NonRetryableWebApiErrors = new(StringComparer.Ordinal)
    {
        MissingBlobError,
        InvalidLibrespotBlobError
    };
    private static readonly Uri SpotifyOpenReferrerUri = BuildSpotifyUri("/");
    private static readonly Regex SpotifyIdRegex = new(
        "^[A-Za-z0-9]{22}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SpotifyBlobService> _logger;
    private readonly ProtectedCredentialFileStore _webPlayerCredentialStore;
    private readonly ProtectedCredentialFileStore _librespotCredentialStore;
    private readonly SemaphoreSlim _librespotWorkerLock = new(1, 1);
    private readonly Dictionary<string, LibrespotWorkerProcess> _librespotWorkers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> BlobGenerationLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpotifyBlobGenerationStatus> BlobGenerationStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt, string CredentialSignature)> TokenCache = new();
    public sealed class SpotifyBlobGenerationInProgressException : InvalidOperationException
    {
        public SpotifyBlobGenerationInProgressException(string accountName)
            : base($"Spotify credentials generation already in progress for account '{accountName}'.")
        {
        }
    }

    public sealed record SpotifyBlobGenerationStatus(string Phase, string Message, string? DeviceName, DateTimeOffset UpdatedAt);

    public SpotifyBlobService(
        IWebHostEnvironment environment,
        ILogger<SpotifyBlobService> logger,
        IDataProtectionProvider dataProtectionProvider)
    {
        _environment = environment;
        _logger = logger;
        _webPlayerCredentialStore = new ProtectedCredentialFileStore(dataProtectionProvider, WebPlayerProtectionPurpose);
        _librespotCredentialStore = new ProtectedCredentialFileStore(dataProtectionProvider, LibrespotProtectionPurpose);
    }

    public bool BlobExists(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return false;
        }

        try
        {
            return File.Exists(blobPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to check Spotify blob existence for {BlobPath}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath));
            return false;
        }
    }

    public Task<SpotifyBlobResult> GenerateBlobAsync(string accountName, bool headless, CancellationToken cancellationToken)
    {
        var configRoot = GetConfigRoot();
        var blobDir = Path.Join(configRoot, "spotify", "blobs");
        return GenerateBlobAsync(accountName, headless, blobDir, removeExisting: true, cancellationToken);
    }

    public SpotifyBlobGenerationStatus? GetGenerationStatus(string accountName, string blobDir)
        => BlobGenerationStatuses.TryGetValue(NormalizeAccountLockKey(blobDir, accountName), out var status)
            ? status
            : null;

    public Task<SpotifyBlobResult> GenerateBlobAsync(
        string accountName,
        bool headless,
        string blobDir,
        CancellationToken cancellationToken)
        => GenerateBlobAsync(accountName, headless, blobDir, removeExisting: false, cancellationToken);

    private async Task<SpotifyBlobResult> GenerateBlobAsync(
        string accountName,
        bool headless,
        string blobDir,
        bool removeExisting,
        CancellationToken cancellationToken)
    {
        var lockKey = NormalizeAccountLockKey(blobDir, accountName);
        var generationLock = BlobGenerationLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        if (!await generationLock.WaitAsync(0, cancellationToken))
        {
            throw new SpotifyBlobGenerationInProgressException(accountName);
        }

        SetGenerationStatus(lockKey, "starting", "Starting the Spotify Connect receiver.", null);

        var configRoot = GetConfigRoot();
        Directory.CreateDirectory(blobDir);
        if (removeExisting)
        {
            RemoveExistingBlobs(blobDir);
        }
        var blobPath = Path.Join(blobDir, $"{accountName}.json");

        var repoRoot = ResolveRepoRoot();
        var authWorkingDir = CreateAuthWorkingDirectory(blobDir, configRoot);

        try
        {
            var helperPath = ResolveSpotifyAuthHelperPath(repoRoot);
            if (helperPath == null)
            {
                throw new FileNotFoundException(
                    "Spotify auth helper not found.",
                    Path.Join(repoRoot, ProjectWebFolder, ToolsFolder, ZeroconfAuthScript));
            }

            var pythonExecutable = await EnsureSpotifyAuthEnvironmentAsync(cancellationToken);
            if (headless)
            {
                _logger.LogInformation("Spotify auth now uses headless Zeroconf; browser automation is disabled.");
            }

            var timeoutSeconds = 180;
            var startInfo = CreatePythonScriptStartInfo(
                pythonExecutable,
                helperPath,
                authWorkingDir,
                "--output", blobPath,
                "--credentials-dir", authWorkingDir,
                "--device-name", "DeezSpoTag",
                "--listen-port", SpotifyConnectListenerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--timeout", timeoutSeconds.ToString());

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            SetGenerationStatus(lockKey, "listening", "Spotify Connect receiver is active. Transfer playback to DeezSpoTag in Spotify.", "DeezSpoTag");
            var processOutput = await WaitForSpotifyAuthProcessExitAsync(process, lockKey, cancellationToken);
            var stdout = processOutput.StandardOutput;
            var stderr = processOutput.StandardError;
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                foreach (var stderrLine in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _logger.LogWarning("Spotify credentials stderr: {Message}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(stderrLine));
                }
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogError(
                    "Spotify credentials generator exited without structured output. Exit code: {ExitCode}. Error: {Error}",
                    processOutput.ExitCode,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(stderr));
                throw new InvalidOperationException("Spotify Connect listener stopped before credential capture completed. Check the application log for the receiver error.");
            }

            if (!TryParseJsonFromStdout(stdout, out var doc, out var parseError))
            {
                throw new InvalidOperationException(
                    $"Spotify credentials generator returned malformed JSON output. {parseError}");
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
                {
                    var errorMessage = root.TryGetProperty(ErrorField, out var errorElement) ? errorElement.GetString() : "Unknown error";
                    SetGenerationStatus(lockKey, "failed", errorMessage ?? "Spotify Connect login failed.", null);
                    throw new InvalidOperationException($"Spotify credentials generator failed: {errorMessage}");
                }

                if (!File.Exists(blobPath))
                {
                    throw new InvalidOperationException("Spotify credentials generator did not create credentials.json.");
                }

                // A newly generated blob can reuse the same file path as a prior login.
                // Ensure stale token cache for that path is dropped immediately.
                InvalidateWebApiAccessToken(blobPath);
                await ProtectBlobFileByKindAsync(blobPath, cancellationToken);

                var deviceName = root.TryGetProperty("deviceName", out var deviceNameElement)
                    ? deviceNameElement.GetString()
                    : null;
                SetGenerationStatus(lockKey, "complete", "Spotify credentials were captured.", deviceName);

                return new SpotifyBlobResult
                {
                    BlobPath = blobPath,
                    CreatedAt = DateTimeOffset.UtcNow,
                    DeviceName = deviceName
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetGenerationStatus(lockKey, "failed", ex.Message, null);
            throw;
        }
        finally
        {
            TryDeleteDirectory(authWorkingDir);
            generationLock.Release();
            if (generationLock.CurrentCount == 1)
            {
                BlobGenerationLocks.TryRemove(lockKey, out _);
            }
        }
    }

    private static void SetGenerationStatus(string lockKey, string phase, string message, string? deviceName)
        => BlobGenerationStatuses[lockKey] = new SpotifyBlobGenerationStatus(phase, message, deviceName, DateTimeOffset.UtcNow);

    public async Task<string?> GetAccountProductAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        var tokenResult = await GetWebApiAccessTokenAsync(blobPath, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenResult.AccessToken))
        {
            return null;
        }

        var claims = SpotifyAccessTokenParser.TryParse(tokenResult.AccessToken);
        return claims?.Product;
    }

    public async Task<SpotifyAccessTokenResult> GetWebApiAccessTokenAsync(
        string blobPath,
        bool allowRetries = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return new SpotifyAccessTokenResult(null, null, MissingBlobError);
        }

        if (!await IsLibrespotBlobAsync(blobPath, cancellationToken))
        {
            return new SpotifyAccessTokenResult(null, null, InvalidLibrespotBlobError);
        }

        var credentialSignature = GetCredentialFileSignature(blobPath);
        if (TokenCache.TryGetValue(blobPath, out var cached)
            && DateTimeOffset.UtcNow < cached.ExpiresAt
            && string.Equals(cached.CredentialSignature, credentialSignature, StringComparison.Ordinal))
        {
            return new SpotifyAccessTokenResult(cached.Token, null, null);
        }

        var result = await RequestWebApiTokenWithRetriesAsync(blobPath, allowRetries, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return result;
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(45);
        if (result.ExpiresAtUnixMs.HasValue && result.ExpiresAtUnixMs.Value > 0)
        {
            var candidate = DateTimeOffset.FromUnixTimeMilliseconds(result.ExpiresAtUnixMs.Value);
            expiresAt = candidate.AddMinutes(-2);
        }

        TokenCache[blobPath] = (result.AccessToken!, expiresAt, credentialSignature);
        return result;
    }

    private async Task<SpotifyAccessTokenResult> RequestWebApiTokenWithRetriesAsync(
        string blobPath,
        bool allowRetries,
        CancellationToken cancellationToken)
    {
        var maxAttempts = allowRetries ? WebApiRetryDelays.Length + 1 : 1;
        SpotifyAccessTokenResult? lastResult = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            lastResult = await RequestLibrespotWebApiTokenAsync(blobPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(lastResult.AccessToken))
            {
                return lastResult;
            }

            if (lastResult.Error is { } error && NonRetryableWebApiErrors.Contains(error))
            {
                _logger.LogWarning("Librespot auth failed with non-retryable error: {Error}", error);
                break;
            }

            if (attempt >= maxAttempts - 1)
            {
                if (allowRetries)
                {
                    _logger.LogError("Librespot auth failed after {MaxAttempts} attempts. Last error: {Error}", maxAttempts, lastResult.Error);
                }
                break;
            }

            var delay = WebApiRetryDelays[Math.Min(attempt, WebApiRetryDelays.Length - 1)];
            _logger.LogWarning(
                "Librespot auth attempt {Attempt}/{MaxAttempts} failed: {Error}. Retrying in {DelayMs}ms...",
                attempt + 1,
                maxAttempts,
                lastResult.Error,
                (int)delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
        }

        return lastResult ?? new SpotifyAccessTokenResult(null, null, AllRetriesFailedError);
    }

    public void InvalidateWebApiAccessToken(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return;
        }

        if (TokenCache.TryRemove(blobPath, out _) && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Invalidated Spotify Web API token cache for {BlobPath}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath));
        }
    }

    public string? GetBlobPath(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return null;
        }

        var configRoot = GetConfigRoot();
        var blobPath = Path.Join(configRoot, "spotify", "blobs", $"{accountName}.json");
        return File.Exists(blobPath) ? blobPath : null;
    }

    public async Task<string?> GetWebPlayerAccessTokenAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        var payload = await TryLoadBlobPayloadAsync(blobPath, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        using var client = CreateCookieClient(payload);
        if (client is null)
        {
            return null;
        }

        return await GetWebPlayerAccessTokenAsync(client, cancellationToken);
    }

    public async Task<SpotifyWebPlayerTokenInfo?> GetWebPlayerTokenInfoAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        var payload = await TryLoadBlobPayloadAsync(blobPath, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        using var client = CreateCookieClient(payload);
        if (client is null)
        {
            return null;
        }

        var result = await RequestWebPlayerAccessTokenAsync(client, cancellationToken);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return new SpotifyWebPlayerTokenInfo(
                null,
                result.ExpiresAtUnixMs,
                result.IsAnonymous,
                result.Country,
                result.ClientId,
                result.ErrorSnippet ?? "web_player_token_failed");
        }

        return new SpotifyWebPlayerTokenInfo(
            result.AccessToken,
            result.ExpiresAtUnixMs,
            result.IsAnonymous,
            result.Country,
            result.ClientId,
            null);
    }

    public async Task<SpotifyLibrespotPlaylistResult> GetLibrespotPlaylistAsync(
        string blobPath,
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return new SpotifyLibrespotPlaylistResult(null, MissingBlobError);
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return new SpotifyLibrespotPlaylistResult(null, "missing_playlist_id");
        }
        if (!IsValidSpotifyId(playlistId))
        {
            return new SpotifyLibrespotPlaylistResult(null, "invalid_playlist_id");
        }

        var result = await RequestLibrespotPayloadAsync(
            blobPath,
            "playlist",
            new { playlist_id = playlistId },
            cancellationToken);
        return new SpotifyLibrespotPlaylistResult(result.PayloadJson, result.Error, result.IsPartial, result.Failures);
    }

    public async Task<SpotifyLibrespotTracksResult> GetLibrespotTracksAsync(
        string blobPath,
        IReadOnlyList<string> trackIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return new SpotifyLibrespotTracksResult(null, MissingBlobError);
        }

        if (trackIds.Count == 0)
        {
            return new SpotifyLibrespotTracksResult("[]", null);
        }
        if (trackIds.Any(trackId => !IsValidSpotifyId(trackId)))
        {
            return new SpotifyLibrespotTracksResult(null, "invalid_track_ids");
        }

        var result = await RequestLibrespotPayloadAsync(
            blobPath,
            "tracks",
            new { track_ids = trackIds },
            cancellationToken);
        return new SpotifyLibrespotTracksResult(result.PayloadJson, result.Error, result.IsPartial, result.Failures);
    }

    public async Task<SpotifyLibrespotSearchResult> SearchLibrespotTracksAsync(
        string blobPath,
        string query,
        int limit,
        string? country,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return new SpotifyLibrespotSearchResult(null, MissingBlobError);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new SpotifyLibrespotSearchResult(null, "missing_query");
        }

        var resolvedLimit = Math.Clamp(limit <= 0 ? 10 : limit, 1, 50);
        var result = await RequestLibrespotPayloadAsync(
            blobPath,
            "search",
            new { query = query.Trim(), limit = resolvedLimit, country = country?.Trim() },
            cancellationToken);
        return new SpotifyLibrespotSearchResult(result.PayloadJson, result.Error, result.IsPartial, result.Failures);
    }

    public async Task<SpotifyLibrespotAlbumResult> GetLibrespotAlbumAsync(
        string blobPath,
        string albumId,
        bool includeTracks = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return new SpotifyLibrespotAlbumResult(null, MissingBlobError);
        }

        if (!IsValidSpotifyId(albumId))
        {
            return new SpotifyLibrespotAlbumResult(null, "invalid_album_id");
        }

        var result = await RequestLibrespotPayloadAsync(
            blobPath,
            "album",
            new { album_id = albumId.Trim(), include_tracks = includeTracks },
            cancellationToken);
        return new SpotifyLibrespotAlbumResult(result.PayloadJson, result.Error, result.IsPartial, result.Failures);
    }

    public async Task<SpotifyLibrespotArtistResult> GetLibrespotArtistAsync(
        string blobPath,
        string artistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return new SpotifyLibrespotArtistResult(null, MissingBlobError);
        }

        if (!IsValidSpotifyId(artistId))
        {
            return new SpotifyLibrespotArtistResult(null, "invalid_artist_id");
        }

        var result = await RequestLibrespotPayloadAsync(
            blobPath,
            "artist",
            new { artist_id = artistId.Trim() },
            cancellationToken);
        return new SpotifyLibrespotArtistResult(result.PayloadJson, result.Error, result.IsPartial, result.Failures);
    }

    public async Task<SpotifyLibrespotPodcastResult> GetLibrespotPodcastMetadataAsync(
        string blobPath,
        string type,
        string spotifyId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return new SpotifyLibrespotPodcastResult(null, MissingBlobError);
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return new SpotifyLibrespotPodcastResult(null, "missing_type");
        }

        var normalizedType = type.Trim().ToLowerInvariant();
        if (normalizedType is not ("show" or "episode"))
        {
            return new SpotifyLibrespotPodcastResult(null, "invalid_type");
        }

        if (!IsValidSpotifyId(spotifyId))
        {
            return new SpotifyLibrespotPodcastResult(null, "invalid_spotify_id");
        }

        var result = await RequestLibrespotPayloadAsync(
            blobPath,
            normalizedType,
            new { spotify_id = spotifyId.Trim() },
            cancellationToken);
        return new SpotifyLibrespotPodcastResult(result.PayloadJson, result.Error, result.IsPartial, result.Failures);
    }

    private async Task<LibrespotPayloadResult> RequestLibrespotPayloadAsync(
        string blobPath,
        string operation,
        object arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var worker = await GetOrCreateLibrespotWorkerAsync(blobPath, cancellationToken);
            if (worker is null)
            {
                return new LibrespotPayloadResult(null, RequestFailedError, false, []);
            }

            return await worker.RequestAsync(operation, arguments, LibrespotMetadataRequestTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify librespot {Operation} request failed.", operation);
            await RemoveLibrespotWorkerAsync(blobPath);
            return new LibrespotPayloadResult(null, ExceptionError, false, []);
        }
    }

    private static LibrespotPayloadResult ParseLibrespotPayloadResult(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
        var partial = root.TryGetProperty("partial", out var partialProp) && partialProp.ValueKind == JsonValueKind.True;
        if (!ok && !partial)
        {
            var error = root.TryGetProperty(ErrorField, out var errorProp) ? errorProp.GetString() : UnknownError;
            return new LibrespotPayloadResult(null, error, false, ReadLibrespotFailures(root));
        }

        if (!root.TryGetProperty(PayloadField, out var payloadProp))
        {
            return new LibrespotPayloadResult(null, MissingPayloadError, false, ReadLibrespotFailures(root));
        }

        var errorValue = root.TryGetProperty(ErrorField, out var resultError) ? resultError.GetString() : null;
        return new LibrespotPayloadResult(payloadProp.GetRawText(), errorValue, partial, ReadLibrespotFailures(root));
    }

    private static List<SpotifyLibrespotItemFailure> ReadLibrespotFailures(JsonElement root)
    {
        if (!root.TryGetProperty("failures", out var failures) || failures.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return failures.EnumerateArray()
            .Select(item => new SpotifyLibrespotItemFailure(
                item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty(ErrorField, out var error) ? error.GetString() ?? UnknownError : UnknownError))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToList();
    }

    private async Task<LibrespotWorkerProcess?> GetOrCreateLibrespotWorkerAsync(
        string blobPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        var normalizedPath = Path.GetFullPath(blobPath);
        var signature = GetCredentialFileSignature(normalizedPath);
        await _librespotWorkerLock.WaitAsync(cancellationToken);
        try
        {
            if (_librespotWorkers.TryGetValue(normalizedPath, out var current))
            {
                if (current.IsRunning && current.CredentialSignature == signature)
                {
                    return current;
                }

                _librespotWorkers.Remove(normalizedPath);
                await current.DisposeAsync();
            }

            var pythonExecutable = await EnsureSpotifyAuthEnvironmentAsync(cancellationToken);
            var workerScript = ResolveToolFilePath(ResolveRepoRoot(), LibrespotWorkerScript);
            if (workerScript is null)
            {
                _logger.LogWarning("Spotify librespot worker was not found.");
                return null;
            }

            var workerDirectory = CreateAuthWorkingDirectory(
                Path.Join(GetConfigRoot(), "spotify", "workers"),
                GetConfigRoot());
            var temporaryCredentials = await CreateTemporaryPlaintextCredentialFileAsync(
                normalizedPath,
                workerDirectory,
                cancellationToken);
            if (temporaryCredentials is null)
            {
                TryDeleteDirectory(workerDirectory);
                return null;
            }

            try
            {
                var worker = await LibrespotWorkerProcess.StartAsync(
                    pythonExecutable,
                    workerScript,
                    workerDirectory,
                    temporaryCredentials,
                    signature,
                    _logger,
                    LibrespotMetadataRequestTimeout,
                    cancellationToken);
                _librespotWorkers[normalizedPath] = worker;
                return worker;
            }
            catch
            {
                TryDeleteFile(temporaryCredentials);
                TryDeleteFile(Path.Join(workerDirectory, "credentials.json"));
                TryDeleteDirectory(workerDirectory);
                throw;
            }
        }
        finally
        {
            _librespotWorkerLock.Release();
        }
    }

    private async Task RemoveLibrespotWorkerAsync(string blobPath)
    {
        var normalizedPath = Path.GetFullPath(blobPath);
        await _librespotWorkerLock.WaitAsync();
        try
        {
            if (_librespotWorkers.Remove(normalizedPath, out var worker))
            {
                await worker.DisposeAsync();
            }
        }
        finally
        {
            _librespotWorkerLock.Release();
        }
    }

    private static string GetCredentialFileSignature(string path)
    {
        var info = new FileInfo(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private async Task<string?> CreateTemporaryPlaintextCredentialFileAsync(
        string credentialPath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var json = await ReadBlobTextAndMigrateAsync(credentialPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        Directory.CreateDirectory(targetDirectory);
        var tempPath = Path.Join(targetDirectory, "credentials.json");
        await WriteTextAtomicallyAsync(tempPath, json, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return tempPath;
    }

    public async Task<SpotifyBlobPayload?> TryLoadBlobPayloadAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        try
        {
            var json = await ReadBlobTextAndMigrateAsync(blobPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            using var jsonDoc = JsonDocument.Parse(json);
            if (ClassifyBlobKind(jsonDoc.RootElement) != SpotifyBlobKind.WebPlayer)
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<SpotifyBlobPayload>(json, _jsonOptions);
            if (payload == null || !HasWebPlayerCookie(payload.Cookies, SpotifyDcCookie))
            {
                return null;
            }

            return payload;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Spotify blob payload is invalid JSON at {BlobPath}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath));
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read Spotify blob payload at {BlobPath}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath));
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied reading Spotify blob payload at {BlobPath}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath));
            return null;
        }
    }

    public async Task<bool> IsWebPlayerBlobAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        return await TryLoadBlobPayloadAsync(blobPath, cancellationToken) is not null;
    }

    public async Task<bool> IsLibrespotBlobAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return false;
        }

        try
        {
            var json = await ReadBlobTextAndMigrateAsync(blobPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }
            using var doc = JsonDocument.Parse(json);
            return ClassifyBlobKind(doc.RootElement) == SpotifyBlobKind.Librespot;
        }
        catch (JsonException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify librespot blob is invalid JSON at {BlobPath}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath));
            }
            return false;
        }
        catch (IOException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read Spotify librespot blob at {BlobPath}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath));
            }
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Access denied reading Spotify librespot blob at {BlobPath}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath));
            }
            return false;
        }
    }

    public async Task<SpotifyBlobResult> SaveWebPlayerBlobAsync(
        string blobPath,
        string spDc,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            throw new ArgumentException("Blob path is required.", nameof(blobPath));
        }
        if (string.IsNullOrWhiteSpace(spDc))
        {
            throw new ArgumentException("sp_dc is required.", nameof(spDc));
        }

        var blobDir = Path.GetDirectoryName(blobPath);
        if (string.IsNullOrWhiteSpace(blobDir))
        {
            throw new InvalidOperationException("Unable to resolve blob directory.");
        }
        Directory.CreateDirectory(blobDir);
        var payload = new SpotifyBlobPayload
        {
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultWebPlayerUserAgent : userAgent.Trim(),
            Cookies = new List<SpotifyBlobCookie>
            {
                new()
                {
                    Name = SpotifyDcCookie,
                    Value = spDc.Trim(),
                    Domain = SpotifyCookieDomain,
                    Path = "/",
                    Secure = true,
                    HttpOnly = true,
                    SameSite = "None"
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        await _webPlayerCredentialStore.WriteTextAsync(blobPath, json, cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Saved Spotify web player blob at {BlobPath} with {CookieCount} cookies.",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(blobPath), payload.Cookies.Count);
        }

        return new SpotifyBlobResult
        {
            BlobPath = blobPath,
            CreatedAt = payload.CreatedAt
        };
    }

    public async Task<SpotifyBlobResult> SaveWebPlayerBlobWithCookiesAsync(
        string blobPath,
        IReadOnlyCollection<SpotifyBlobCookie> cookies,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            throw new ArgumentException("Blob path is required.", nameof(blobPath));
        }
        if (cookies == null || cookies.Count == 0)
        {
            throw new ArgumentException("Cookies are required.", nameof(cookies));
        }

        var spDc = cookies.FirstOrDefault(cookie =>
            cookie.Name.Equals(SpotifyDcCookie, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(spDc))
        {
            throw new ArgumentException("sp_dc is required in cookies.", nameof(cookies));
        }

        var blobDir = Path.GetDirectoryName(blobPath);
        if (string.IsNullOrWhiteSpace(blobDir))
        {
            throw new InvalidOperationException("Unable to resolve blob directory.");
        }
        Directory.CreateDirectory(blobDir);

        var filtered = cookies
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Value))
            .Where(cookie =>
                string.IsNullOrWhiteSpace(cookie.Domain) ||
                cookie.Domain.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
            .Select(cookie => new SpotifyBlobCookie
            {
                Name = cookie.Name.Trim(),
                Value = cookie.Value.Trim(),
                Domain = string.IsNullOrWhiteSpace(cookie.Domain) ? SpotifyCookieDomain : cookie.Domain.Trim(),
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Expires = cookie.Expires,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                SameSite = cookie.SameSite
            })
            .ToList();

        if (filtered.Count == 0)
        {
            throw new ArgumentException("No valid Spotify cookies were provided.", nameof(cookies));
        }

        var payload = new SpotifyBlobPayload
        {
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultWebPlayerUserAgent : userAgent.Trim(),
            Cookies = filtered
        };

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        await _webPlayerCredentialStore.WriteTextAsync(blobPath, json, cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Saved Spotify web player blob at {BlobPath} with {CookieCount} cookies.",
                blobPath, payload.Cookies.Count);
        }

        return new SpotifyBlobResult
        {
            BlobPath = blobPath,
            CreatedAt = payload.CreatedAt
        };
    }

    private async Task WriteTextAtomicallyAsync(string targetPath, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Unable to resolve target directory for atomic write.");
        }

        Directory.CreateDirectory(directory);
        var tempPath = $"{targetPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed to clean up temporary Spotify web-player blob file {Path}", tempPath);
                }
            }
        }
    }

    public async Task<SpotifyWebPlayerTokenCheck> TestWebPlayerAccessTokenFromCookiesAsync(
        string spDc,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(spDc))
        {
            return new SpotifyWebPlayerTokenCheck
            {
                Ok = false,
                Message = "sp_dc is required."
            };
        }

        using var client = CreateCookieClientFromRawCookies(spDc, userAgent);

        var response = await RequestWebPlayerAccessTokenAsync(client, cancellationToken);
        if (!response.IsSuccess)
        {
            var message = response.StatusCode.HasValue
                ? $"Request failed with status {response.StatusCode.Value}."
                : "Request failed.";
            if (!string.IsNullOrWhiteSpace(response.ErrorSnippet))
            {
                message = $"{message} {response.ErrorSnippet}";
            }

            return new SpotifyWebPlayerTokenCheck
            {
                Ok = false,
                StatusCode = response.StatusCode,
                Message = message
            };
        }

        return new SpotifyWebPlayerTokenCheck
        {
            Ok = !string.IsNullOrWhiteSpace(response.AccessToken),
            StatusCode = response.StatusCode,
            Message = string.IsNullOrWhiteSpace(response.AccessToken)
                ? "Token response missing access token."
                : "Token fetched successfully.",
            ExpiresAtUnixMs = response.ExpiresAtUnixMs,
            IsAnonymous = response.IsAnonymous,
            Country = response.Country,
            ClientId = response.ClientId
        };
    }

    public async Task<string?> GetWebPlayerAccessTokenFromCookiesAsync(
        string spDc,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(spDc))
        {
            return null;
        }

        using var client = CreateCookieClientFromRawCookies(spDc, userAgent);

        var response = await RequestWebPlayerAccessTokenAsync(client, cancellationToken);
        if (!response.IsSuccess)
        {
            if (response.StatusCode.HasValue)
            {
                _logger.LogWarning(
                    "Spotify Web Player token request failed: {Status} {Body}",
                    response.StatusCode.Value,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(response.ErrorSnippet));
            }
            else
            {
                _logger.LogWarning("Spotify Web Player token request failed.");
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(response.AccessToken))
        {
            _logger.LogWarning("Spotify Web Player token response missing accessToken.");
            return null;
        }

        return response.AccessToken;
    }

    public async Task<string> EnsureSpotifyAuthEnvironmentAsync(CancellationToken cancellationToken)
    {
        var repoRoot = ResolveRepoRoot();
        var vendorRoot = ResolveSpotifyAuthVendorRoot(repoRoot);
        if (vendorRoot == null)
        {
            throw new FileNotFoundException(
                "Spotify auth vendor folder not found.",
                Path.Join(repoRoot, ProjectWebFolder, ToolsFolder, SpotifyLibrespotFolder, SpotizerrPhoenixFolder));
        }

        var configRoot = GetConfigRoot();
        var venvPath = Path.Join(configRoot, "spotify", ".venv");
        var pythonPath = Path.Join(venvPath, "bin", "python");
        if (File.Exists(pythonPath) && await DependenciesReadyAsync(pythonPath, vendorRoot, cancellationToken))
        {
            return pythonPath;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Preparing Spotify auth environment at {Path}", venvPath);
        }
        var createResult = await RunProcessAsync("python3", configRoot, cancellationToken, "-m", "venv", venvPath);
        if (!createResult.Success)
        {
            throw new InvalidOperationException($"Failed to create Spotify auth venv: {createResult.Error}");
        }

        var requirementsPath = Path.Join(vendorRoot, "requirements.txt");
        if (!File.Exists(requirementsPath))
        {
            throw new FileNotFoundException("Spotify auth requirements not found.", requirementsPath);
        }

        var installResult = await RunProcessAsync(
            pythonPath,
            configRoot,
            cancellationToken,
            "-m",
            "pip",
            "install",
            "-r",
            requirementsPath);
        if (!installResult.Success)
        {
            throw new InvalidOperationException($"Failed to install Spotify auth requirements: {installResult.Error}");
        }

        if (!await DependenciesReadyAsync(pythonPath, vendorRoot, cancellationToken))
        {
            throw new InvalidOperationException("Spotify auth dependencies are not available after installation.");
        }

        return pythonPath;
    }

    private async Task<bool> DependenciesReadyAsync(string pythonExecutable, string vendorRoot, CancellationToken cancellationToken)
    {
        var vendorRootLiteral = JsonSerializer.Serialize(vendorRoot);
        var checkResult = await RunProcessAsync(
            pythonExecutable,
            GetConfigRoot(),
            cancellationToken,
            "-c",
            $"import sys; sys.path.insert(0, {vendorRootLiteral}); import librespot, zeroconf, Cryptodome");
        return checkResult.Success;
    }

    private async Task<string?> ReadBlobTextAndMigrateAsync(string blobPath, CancellationToken cancellationToken)
    {
        var stored = await File.ReadAllTextAsync(blobPath, cancellationToken);
        if (_webPlayerCredentialStore.IsProtectedForPurpose(stored))
        {
            return await _webPlayerCredentialStore.ReadTextAsync(blobPath, cancellationToken);
        }

        if (_librespotCredentialStore.IsProtectedForPurpose(stored))
        {
            return await _librespotCredentialStore.ReadTextAsync(blobPath, cancellationToken);
        }

        if (ProtectedCredentialFileStore.IsProtectedText(stored))
        {
            return null;
        }

        await ProtectBlobFileByKindAsync(blobPath, stored, cancellationToken);
        return stored;
    }

    private async Task ProtectBlobFileByKindAsync(string blobPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return;
        }

        var stored = await File.ReadAllTextAsync(blobPath, cancellationToken);
        if (ProtectedCredentialFileStore.IsProtectedText(stored))
        {
            return;
        }

        await ProtectBlobFileByKindAsync(blobPath, stored, cancellationToken);
    }

    private async Task ProtectBlobFileByKindAsync(string blobPath, string json, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            switch (ClassifyBlobKind(document.RootElement))
            {
                case SpotifyBlobKind.WebPlayer:
                    await _webPlayerCredentialStore.WriteTextAsync(blobPath, json, cancellationToken);
                    break;
                case SpotifyBlobKind.Librespot:
                    await _librespotCredentialStore.WriteTextAsync(blobPath, json, cancellationToken);
                    break;
            }
        }
        catch (JsonException)
        {
            // Invalid blob JSON remains untouched so callers can report the same invalid-blob behavior.
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort only.
        }
    }

    private async Task<SpotifyAccessTokenResult> RequestLibrespotWebApiTokenAsync(string blobPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RequestLibrespotPayloadAsync(
                blobPath,
                "token",
                new
                {
                    scopes = new[]
                    {
                        "playlist-read",
                        "playlist-read-private",
                        "user-library-read",
                        "user-read-private",
                        "user-read-email"
                    }
                },
                cancellationToken);
            if (string.IsNullOrWhiteSpace(result.PayloadJson))
            {
                return new SpotifyAccessTokenResult(null, null, result.Error ?? UnknownError);
            }

            using var document = JsonDocument.Parse(result.PayloadJson);
            var root = document.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var token) ? token.GetString() : null;
            long? expiresAt = root.TryGetProperty("expires_at_unix_ms", out var expiry) && expiry.TryGetInt64(out var parsedExpiry)
                ? parsedExpiry
                : null;
            return string.IsNullOrWhiteSpace(accessToken)
                ? new SpotifyAccessTokenResult(null, expiresAt, MissingPayloadError)
                : new SpotifyAccessTokenResult(accessToken, expiresAt, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify librespot token request failed.");
            return new SpotifyAccessTokenResult(null, null, ExceptionError);
        }
    }

    private string GetConfigRoot()
    {
        var configDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return configDir.Trim();
        }

        var deezspotagDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(deezspotagDataDir))
        {
            return deezspotagDataDir.Trim();
        }

        return _environment.ContentRootPath;
    }

    private string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(_environment.ContentRootPath);
        while (current != null)
        {
            if (Directory.Exists(Path.Join(current.FullName, ".git")) || File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return _environment.ContentRootPath;
    }

    private string? ResolveSpotifyAuthHelperPath(string repoRoot)
        => ResolveToolFilePath(repoRoot, ZeroconfAuthScript);

    private string? ResolveSpotifyAuthVendorRoot(string repoRoot)
        => ResolveToolDirectoryPath(repoRoot, SpotifyLibrespotFolder, SpotizerrPhoenixFolder);

    private string? ResolveToolFilePath(string repoRoot, params string[] relativeSegments)
        => EnumerateToolPathCandidates(repoRoot, relativeSegments).FirstOrDefault(File.Exists);

    private string? ResolveToolDirectoryPath(string repoRoot, params string[] relativeSegments)
        => EnumerateToolPathCandidates(repoRoot, relativeSegments).FirstOrDefault(Directory.Exists);

    private string[] EnumerateToolPathCandidates(string repoRoot, params string[] relativeSegments)
    {
        var relativePath = JoinPath(relativeSegments);
        var candidates = new[]
        {
            Path.Join(_environment.ContentRootPath, ToolsFolder, relativePath),
            Path.Join(repoRoot, ProjectWebFolder, ToolsFolder, relativePath),
            Path.Join(repoRoot, "src", ProjectWebFolder, ToolsFolder, relativePath),
            Path.Join(repoRoot, ToolsFolder, relativePath),
        };

        return candidates;
    }

    private static string JoinPath(params string[] segments)
    {
        var path = string.Empty;
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                path = segment;
                continue;
            }

            path = Path.Join(path, segment);
        }

        return path;
    }

    private static bool IsValidSpotifyId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return SpotifyIdRegex.IsMatch(value.Trim());
    }

    private static SpotifyBlobKind ClassifyBlobKind(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return SpotifyBlobKind.Unknown;
        }

        if (TryGetPropertyIgnoreCase(root, "auth_type", out _) && TryGetPropertyIgnoreCase(root, "auth_data", out _))
        {
            return SpotifyBlobKind.Librespot;
        }

        if (TryGetPropertyIgnoreCase(root, "credentials", out var credentialsElement)
            && credentialsElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(credentialsElement.GetString()))
        {
            return SpotifyBlobKind.Librespot;
        }

        if (TryGetPropertyIgnoreCase(root, "cookies", out var cookiesElement)
            && cookiesElement.ValueKind == JsonValueKind.Array
            && HasWebPlayerCookie(cookiesElement, SpotifyDcCookie))
        {
            return SpotifyBlobKind.WebPlayer;
        }

        return SpotifyBlobKind.Unknown;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var matchingValue = element.EnumerateObject()
                .Where(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Value)
                .FirstOrDefault();

            if (matchingValue.ValueKind != JsonValueKind.Undefined)
            {
                value = matchingValue;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool HasWebPlayerCookie(IEnumerable<SpotifyBlobCookie> cookies, string cookieName)
    {
        return cookies
            .Where(cookie => cookie.Name.Equals(cookieName, StringComparison.OrdinalIgnoreCase))
            .Any(cookie => !string.IsNullOrWhiteSpace(cookie.Value));
    }

    private static bool HasWebPlayerCookie(JsonElement cookiesElement, string cookieName)
    {
        if (cookiesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in cookiesElement.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(item, "name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString();
            if (!string.Equals(name, cookieName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetPropertyIgnoreCase(item, "value", out var valueElement)
                && valueElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(valueElement.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static ProcessStartInfo CreatePythonScriptStartInfo(
        string pythonExecutable,
        string scriptPath,
        string? workingDirectory,
        params string[] arguments)
    {
        var validatedPythonExecutable = EnsureSafeExecutablePath(pythonExecutable);
        var validatedScriptPath = EnsureSafeExecutablePath(scriptPath);
        var resolvedWorkingDirectory = ResolveWorkingDirectory(workingDirectory, validatedScriptPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = validatedPythonExecutable,
            WorkingDirectory = resolvedWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(validatedScriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyPythonCompatibilityEnvironment(startInfo);
        return startInfo;
    }

    private static string ResolveWorkingDirectory(string? preferredWorkingDirectory, string validatedScriptPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredWorkingDirectory))
        {
            var fullPath = Path.GetFullPath(preferredWorkingDirectory);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        return Path.GetDirectoryName(validatedScriptPath) ?? Environment.CurrentDirectory;
    }

    private static string CreateAuthWorkingDirectory(string blobDir, string configRoot)
    {
        var authRoot = Path.Join(Path.GetDirectoryName(blobDir) ?? configRoot, "auth");
        Directory.CreateDirectory(authRoot);

        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var workingDirectory = Path.Join(authRoot, sessionId);
        Directory.CreateDirectory(workingDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                workingDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return workingDirectory;
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort only.
        }
    }

    private static bool TryParseJsonFromStdout(string stdout, out JsonDocument document, out string parseError)
    {
        try
        {
            document = JsonDocument.Parse(stdout);
            parseError = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            parseError = $"Full output parse failed: {ClipForError(ex.Message)}.";
        }

        var lines = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var candidate = lines[i];
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var first = candidate[0];
            if (first is not '{' and not '[')
            {
                continue;
            }

            try
            {
                document = JsonDocument.Parse(candidate);
                parseError = string.Empty;
                return true;
            }
            catch (JsonException)
            {
                // Continue scanning earlier lines. Some helpers log text before the final JSON line.
            }
        }

        parseError += $" Output tail: {ClipForError(stdout)}.";
        document = null!;
        return false;
    }

    private static string ClipForError(string value, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + "...";
    }

    private void RemoveExistingBlobs(string blobDir)
    {
        try
        {
            if (!Directory.Exists(blobDir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(blobDir, "*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to delete existing Spotify blob at {BlobPath}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(file));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to purge existing Spotify blobs in {BlobDir}", blobDir);
        }
    }

    private static async Task<(bool Success, string Error)> RunProcessAsync(
        string fileName,
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var validatedFileName = ResolveAndValidateExecutable(fileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = validatedFileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        ApplyPythonCompatibilityEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var processOutput = await WaitForProcessExitAsync(process, timeout: null, cancellationToken);
        if (processOutput.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(processOutput.StandardError)
                ? processOutput.StandardOutput
                : processOutput.StandardError;
            if (error.Length > 600)
            {
                error = error[..600] + "…";
            }
            return (false, error);
        }

        return (true, processOutput.StandardOutput);
    }

    private static async Task<ProcessOutputResult> WaitForProcessExitAsync(
        Process process,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = timeout.HasValue && timeout.Value > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(timeout!.Value);
        }

        try
        {
            await process.WaitForExitAsync(timeoutCts?.Token ?? cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts?.IsCancellationRequested == true)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            var timeoutMessage = $"{ProcessTimeoutError}: helper exceeded {timeout!.Value.TotalSeconds:0}s.";
            return new ProcessOutputResult(-1, string.Empty, timeoutMessage);
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        return new ProcessOutputResult(process.ExitCode, stdout, stderr);
    }

    private async Task<ProcessOutputResult> WaitForSpotifyAuthProcessExitAsync(
        Process process,
        string lockKey,
        CancellationToken cancellationToken)
    {
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = new StringBuilder();
        while (true)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("PROGRESS:", StringComparison.Ordinal))
            {
                TryApplySpotifyAuthProgress(lockKey, line["PROGRESS:".Length..]);
                continue;
            }

            if (stderr.Length > 0)
            {
                stderr.AppendLine();
            }
            stderr.Append(line);
        }

        await process.WaitForExitAsync(cancellationToken);
        return new ProcessOutputResult(process.ExitCode, (await stdoutTask).Trim(), stderr.ToString().Trim());
    }

    private static void TryApplySpotifyAuthProgress(string lockKey, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var phase = root.TryGetProperty("phase", out var phaseElement) ? phaseElement.GetString() : null;
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            var deviceName = root.TryGetProperty("deviceName", out var deviceNameElement) ? deviceNameElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(phase) && !string.IsNullOrWhiteSpace(message))
            {
                SetGenerationStatus(lockKey, phase, message, deviceName);
            }
        }
        catch (JsonException)
        {
            // Helper progress does not affect credential capture.
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
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // The process has already exited or this platform does not support tree kill.
        }
    }

    private static void ApplyPythonCompatibilityEnvironment(ProcessStartInfo startInfo)
    {
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            return;
        }

        var executableName = Path.GetFileName(startInfo.FileName);
        if (!executableName.StartsWith("python", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        startInfo.Environment[ProtobufRuntimeEnv] = ProtobufRuntimeValue;
    }

    private static string ResolveAndValidateExecutable(string executableNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(executableNameOrPath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executableNameOrPath));
        }

        if (Path.IsPathRooted(executableNameOrPath))
        {
            return EnsureSafeExecutablePath(executableNameOrPath);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException("Unable to resolve executable from PATH.", executableNameOrPath);
        }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", string.Empty } : new[] { string.Empty };
        foreach (var directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Join(directory, executableNameOrPath + extension);
                if (File.Exists(candidate))
                {
                    return EnsureSafeExecutablePath(candidate);
                }
            }
        }

        throw new FileNotFoundException("Unable to resolve executable from PATH.", executableNameOrPath);
    }

    private static string EnsureSafeExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (!Path.IsPathRooted(fullPath) || !File.Exists(fullPath))
        {
            throw new FileNotFoundException("Executable path is invalid or missing.", fullPath);
        }

        return fullPath;
    }

    private sealed record LibrespotPayloadResult(
        string? PayloadJson,
        string? Error,
        bool IsPartial,
        IReadOnlyList<SpotifyLibrespotItemFailure> Failures);
    private sealed record ProcessOutputResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class LibrespotWorkerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private readonly ILogger<SpotifyBlobService> _logger;
        private readonly Task _stderrTask;
        private readonly string _runtimeDirectory;

        private LibrespotWorkerProcess(
            Process process,
            string credentialSignature,
            string runtimeDirectory,
            ILogger<SpotifyBlobService> logger)
        {
            _process = process;
            CredentialSignature = credentialSignature;
            _runtimeDirectory = runtimeDirectory;
            _logger = logger;
            _stderrTask = DrainStandardErrorAsync();
        }

        public string CredentialSignature { get; }
        public bool IsRunning => !_process.HasExited;

        public static async Task<LibrespotWorkerProcess> StartAsync(
            string pythonExecutable,
            string scriptPath,
            string workingDirectory,
            string credentialPath,
            string credentialSignature,
            ILogger<SpotifyBlobService> logger,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var startInfo = CreatePythonScriptStartInfo(
                pythonExecutable,
                scriptPath,
                workingDirectory,
                CredentialsArg,
                credentialPath);
            startInfo.RedirectStandardInput = true;
            var process = new Process { StartInfo = startInfo };
            process.Start();
            var worker = new LibrespotWorkerProcess(process, credentialSignature, workingDirectory, logger);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                var readyLine = await process.StandardOutput.ReadLineAsync(timeoutSource.Token);
                if (string.IsNullOrWhiteSpace(readyLine))
                {
                    throw new InvalidOperationException("Spotify librespot worker exited before session initialization.");
                }

                using var ready = JsonDocument.Parse(readyLine);
                if (!ready.RootElement.TryGetProperty("ready", out var readyValue)
                    || readyValue.ValueKind != JsonValueKind.True)
                {
                    var error = ready.RootElement.TryGetProperty(ErrorField, out var errorValue)
                        ? errorValue.GetString()
                        : RequestFailedError;
                    throw new InvalidOperationException($"Spotify librespot worker failed to start: {error}");
                }

                return worker;
            }
            catch
            {
                await worker.DisposeAsync();
                throw;
            }
        }

        public async Task<LibrespotPayloadResult> RequestAsync(
            string operation,
            object arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                if (!IsRunning)
                {
                    throw new InvalidOperationException("Spotify librespot worker is not running.");
                }

                var requestId = Guid.NewGuid().ToString("N");
                var request = JsonSerializer.Serialize(new { id = requestId, operation, arguments });
                await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                string? response;
                try
                {
                    response = await _process.StandardOutput.ReadLineAsync(timeoutSource.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryKillProcessTree(_process);
                    throw new TimeoutException($"Spotify librespot {operation} request exceeded {timeout.TotalSeconds:0}s.");
                }

                if (string.IsNullOrWhiteSpace(response))
                {
                    throw new InvalidOperationException($"Spotify librespot {operation} worker returned no response.");
                }

                using (var document = JsonDocument.Parse(response))
                {
                    var root = document.RootElement;
                    var responseId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
                    if (!string.Equals(responseId, requestId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Spotify librespot worker response did not match its request.");
                    }
                }

                return ParseLibrespotPayloadResult(response);
            }
            finally
            {
                _requestLock.Release();
            }
        }

        private async Task DrainStandardErrorAsync()
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line) && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Spotify librespot worker: {Message}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(line));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await _process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        TryKillProcessTree(_process);
                        await _process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                await _stderrTask;
            }
            finally
            {
                _requestLock.Dispose();
                _process.Dispose();
                TryDeleteDirectory(_runtimeDirectory);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _librespotWorkerLock.WaitAsync();
        try
        {
            foreach (var worker in _librespotWorkers.Values)
            {
                await worker.DisposeAsync();
            }
            _librespotWorkers.Clear();
        }
        finally
        {
            _librespotWorkerLock.Release();
            _librespotWorkerLock.Dispose();
        }
    }

    private static string NormalizeAccountLockKey(string blobDir, string accountName)
    {
        var normalizedDir = Path.GetFullPath(blobDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"{normalizedDir}::{accountName.Trim()}";
    }
    private enum SpotifyBlobKind
    {
        Unknown = 0,
        WebPlayer = 1,
        Librespot = 2
    }

    public sealed record SpotifyAccessTokenResult(string? AccessToken, long? ExpiresAtUnixMs, string? Error);
    public sealed record SpotifyWebPlayerTokenInfo(
        string? AccessToken,
        long? ExpiresAtUnixMs,
        bool? IsAnonymous,
        string? Country,
        string? ClientId,
        string? Error);
    public sealed record SpotifyLibrespotItemFailure(string Id, string Error);
    public sealed record SpotifyLibrespotPlaylistResult(string? PayloadJson, string? Error, bool IsPartial = false, IReadOnlyList<SpotifyLibrespotItemFailure>? Failures = null);
    public sealed record SpotifyLibrespotTracksResult(string? PayloadJson, string? Error, bool IsPartial = false, IReadOnlyList<SpotifyLibrespotItemFailure>? Failures = null);
    public sealed record SpotifyLibrespotSearchResult(string? PayloadJson, string? Error, bool IsPartial = false, IReadOnlyList<SpotifyLibrespotItemFailure>? Failures = null);
    public sealed record SpotifyLibrespotAlbumResult(string? PayloadJson, string? Error, bool IsPartial = false, IReadOnlyList<SpotifyLibrespotItemFailure>? Failures = null);
    public sealed record SpotifyLibrespotArtistResult(string? PayloadJson, string? Error, bool IsPartial = false, IReadOnlyList<SpotifyLibrespotItemFailure>? Failures = null);
    public sealed record SpotifyLibrespotPodcastResult(string? PayloadJson, string? Error, bool IsPartial = false, IReadOnlyList<SpotifyLibrespotItemFailure>? Failures = null);

    public HttpClient? CreateCookieClient(SpotifyBlobPayload payload)
    {
        var cookieContainer = new CookieContainer();
        foreach (var cookie in payload.Cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Domain))
            {
                continue;
            }

            var cookieItem = new Cookie(cookie.Name, cookie.Value, cookie.Path ?? "/", cookie.Domain)
            {
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly
            };

            if (cookie.Expires.HasValue && cookie.Expires.Value > 0)
            {
                cookieItem.Expires = DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires.Value).UtcDateTime;
            }

            cookieContainer.Add(cookieItem);
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler);
        if (!string.IsNullOrWhiteSpace(payload.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(payload.UserAgent);
        }

        return client;
    }

    private static HttpClient CreateCookieClientFromRawCookies(string spDc, string? userAgent)
    {
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Cookie(SpotifyDcCookie, spDc.Trim(), "/", SpotifyCookieDomain) { Secure = true, HttpOnly = true });

        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler);
        var resolvedUserAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultWebPlayerUserAgent : userAgent;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(resolvedUserAgent);
        return client;
    }


    private async Task<string?> GetWebPlayerAccessTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var result = await RequestWebPlayerAccessTokenAsync(client, cancellationToken);
        if (!result.IsSuccess)
        {
            if (result.StatusCode.HasValue)
            {
                _logger.LogWarning(
                    "Spotify Web Player token request failed: {Status} {Body}",
                    result.StatusCode.Value,
                    result.ErrorSnippet ?? string.Empty);
            }
            else
            {
                _logger.LogWarning("Spotify Web Player token request failed.");
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            _logger.LogWarning("Spotify Web Player token response missing accessToken.");
            return null;
        }

        if (result.IsAnonymous == true)
        {
            _logger.LogWarning("Spotify Web Player token is anonymous; personalized sections may be unavailable.");
        }

        return result.AccessToken;
    }

    private static async Task<WebPlayerTokenResponse> RequestWebPlayerAccessTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await WarmWebPlayerSessionAsync(client, cancellationToken);
            var (totp, version) = SpotifyWebPlayerTotp.Generate();
            if (string.IsNullOrWhiteSpace(totp))
            {
                return CreateFailedTokenResponse("TOTP generation failed.");
            }

            using var request = CreateWebPlayerTokenRequest(totp, version);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Referrer = SpotifyOpenReferrerUri;
            using var tokenResponse = await client.SendAsync(request, cancellationToken);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                return await CreateErrorTokenResponseAsync(tokenResponse, cancellationToken);
            }

            return await ParseSuccessTokenResponseAsync(tokenResponse, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFailedTokenResponse("web_player_timeout");
        }
        catch (HttpRequestException)
        {
            return CreateFailedTokenResponse("web_player_request_failed");
        }
        catch (JsonException)
        {
            return CreateFailedTokenResponse("web_player_invalid_response");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFailedTokenResponse("web_player_token_failed");
        }
    }

    private static HttpRequestMessage CreateWebPlayerTokenRequest(string totp, int version)
    {
        var query = $"reason=init&productType=web-player&totp={totp}&totpVer={version}&totpServer={totp}";
        var tokenUri = BuildSpotifyUri(SpotifyOpenTokenPath, query);
        return new HttpRequestMessage(HttpMethod.Get, tokenUri);
    }

    private static async Task<WebPlayerTokenResponse> CreateErrorTokenResponseAsync(
        HttpResponseMessage tokenResponse,
        CancellationToken cancellationToken)
    {
        var errorBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        var trimmed = errorBody.Length > 200 ? errorBody[..200] : errorBody;
        return new WebPlayerTokenResponse
        {
            IsSuccess = false,
            StatusCode = (int)tokenResponse.StatusCode,
            ErrorSnippet = trimmed
        };
    }

    private static async Task<WebPlayerTokenResponse> ParseSuccessTokenResponseAsync(
        HttpResponseMessage tokenResponse,
        CancellationToken cancellationToken)
    {
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        if (!tokenDoc.RootElement.TryGetProperty("accessToken", out var accessTokenElement))
        {
            return new WebPlayerTokenResponse
            {
                IsSuccess = false,
                StatusCode = (int)tokenResponse.StatusCode
            };
        }

        var accessToken = accessTokenElement.GetString();
        var clientId = tokenDoc.RootElement.TryGetProperty("clientId", out var clientIdElement)
            ? clientIdElement.GetString()
            : null;
        var country = tokenDoc.RootElement.TryGetProperty("country", out var countryElement)
            ? countryElement.GetString()
            : null;

        return new WebPlayerTokenResponse
        {
            IsSuccess = true,
            StatusCode = (int)tokenResponse.StatusCode,
            AccessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken,
            ExpiresAtUnixMs = TryReadExpiresAt(tokenDoc.RootElement),
            IsAnonymous = TryReadIsAnonymous(tokenDoc.RootElement),
            Country = country,
            ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId
        };
    }

    private static bool? TryReadIsAnonymous(JsonElement root)
    {
        if (!root.TryGetProperty("isAnonymous", out var isAnonymousElement))
        {
            return null;
        }

        return isAnonymousElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static long? TryReadExpiresAt(JsonElement root)
    {
        if (root.TryGetProperty("accessTokenExpirationTimestampMs", out var expiresElement)
            && expiresElement.TryGetInt64(out var expiresValue))
        {
            return expiresValue;
        }

        return null;
    }

    private static WebPlayerTokenResponse CreateFailedTokenResponse(string errorSnippet)
    {
        return new WebPlayerTokenResponse
        {
            IsSuccess = false,
            ErrorSnippet = errorSnippet
        };
    }

    private static async Task WarmWebPlayerSessionAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildSpotifyUri("/"));
            request.Headers.Accept.ParseAdd("text/html");
            request.Headers.Referrer = SpotifyOpenReferrerUri;
            using var response = await client.SendAsync(request, cancellationToken);
            _ = response.Content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort warmup; token request will still run.
        }
    }

    private static Uri BuildSpotifyUri(string path, string? query = null)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttps, SpotifyOpenHost)
        {
            Path = path
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            builder.Query = query;
        }

        return builder.Uri;
    }

    private sealed class WebPlayerTokenResponse
    {
        public bool IsSuccess { get; init; }
        public int? StatusCode { get; init; }
        public string? ErrorSnippet { get; init; }
        public string? AccessToken { get; init; }
        public long? ExpiresAtUnixMs { get; init; }
        public bool? IsAnonymous { get; init; }
        public string? Country { get; init; }
        public string? ClientId { get; init; }
    }
}
