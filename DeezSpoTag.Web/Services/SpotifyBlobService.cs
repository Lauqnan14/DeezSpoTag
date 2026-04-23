using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyBlobService
{
    private const string DefaultWebPlayerUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string ProtobufRuntimeEnv = "PROTOCOL_BUFFERS_PYTHON_IMPLEMENTATION";
    private const string ProtobufRuntimeValue = "python";
    private const string ProjectWebFolder = "DeezSpoTag.Web";
    private const string ToolsFolder = "Tools";
    private const string PayloadField = "payload";
    private const string ErrorField = "error";
    private const string MissingBlobError = "missing_blob";
    private const string HelperNotFoundError = "helper_not_found";
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
    private const string LibrespotTokenScript = "spotify_librespot_token.py";
    private const string LibrespotPlaylistScript = "spotify_librespot_playlist.py";
    private const string LibrespotTracksScript = "spotify_librespot_tracks.py";
    private const string LibrespotAlbumScript = "spotify_librespot_album.py";
    private const string LibrespotArtistScript = "spotify_librespot_artist.py";
    private const string LibrespotPodcastScript = "spotify_librespot_podcast.py";
    private const string SpotifyOpenHost = "open.spotify.com";
    private const string SpotifyOpenTokenPath = "/api/token";
    private const string CredentialsNotFoundError = "credentials_not_found";
    private const string AllRetriesFailedError = "all_retries_failed";
    private static readonly TimeSpan[] WebApiRetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    };
    private static readonly HashSet<string> NonRetryableWebApiErrors = new(StringComparer.Ordinal)
    {
        MissingBlobError,
        HelperNotFoundError,
        CredentialsNotFoundError,
        InvalidLibrespotBlobError
    };
    private static readonly Uri SpotifyOpenReferrerUri = BuildSpotifyUri("/");
    private static readonly Regex SpotifyIdRegex = new(
        "^[A-Za-z0-9]{22}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SpotifyBlobService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> TokenCache = new();

    public SpotifyBlobService(IWebHostEnvironment environment, ILogger<SpotifyBlobService> logger)
    {
        _environment = environment;
        _logger = logger;
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
            _logger.LogWarning(ex, "Failed to check Spotify blob existence for {BlobPath}", blobPath);
            return false;
        }
    }

    public Task<SpotifyBlobResult> GenerateBlobAsync(string accountName, bool headless, CancellationToken cancellationToken)
    {
        var configRoot = GetConfigRoot();
        var blobDir = Path.Join(configRoot, "spotify", "blobs");
        return GenerateBlobAsync(accountName, headless, blobDir, removeExisting: true, cancellationToken);
    }

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
                "--device-name", "DeezSpoTag",
                "--timeout", timeoutSeconds.ToString());

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var processOutput = await WaitForProcessExitAsync(process, cancellationToken);
            var stdout = processOutput.StandardOutput;
            var stderr = processOutput.StandardError;
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogWarning("Spotify credentials stderr: {Message}", stderr);
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                throw new InvalidOperationException("Spotify credentials generator produced no output.");
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
                    throw new InvalidOperationException($"Spotify credentials generator failed: {errorMessage}");
                }

                if (!File.Exists(blobPath))
                {
                    throw new InvalidOperationException("Spotify credentials generator did not create credentials.json.");
                }

                // A newly generated blob can reuse the same file path as a prior login.
                // Ensure stale token cache for that path is dropped immediately.
                InvalidateWebApiAccessToken(blobPath);

                return new SpotifyBlobResult
                {
                    BlobPath = blobPath,
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }
        }
        finally
        {
            TryDeleteDirectory(authWorkingDir);
        }
    }

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

        if (TokenCache.TryGetValue(blobPath, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
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

        TokenCache[blobPath] = (result.AccessToken!, expiresAt);
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
            _logger.LogDebug("Invalidated Spotify Web API token cache for {BlobPath}", blobPath);
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

        var client = CreateCookieClient(payload);
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

        var client = CreateCookieClient(payload);
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
            ResolveLibrespotPlaylistScriptPath,
            cancellationToken,
            CredentialsArg, blobPath,
            "--playlist-id", playlistId);
        return new SpotifyLibrespotPlaylistResult(result.PayloadJson, result.Error);
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

        var ids = string.Join(",", trackIds);
        var result = await RequestLibrespotPayloadAsync(
            blobPath,
            "tracks",
            ResolveLibrespotTracksScriptPath,
            cancellationToken,
            CredentialsArg, blobPath,
            "--track-ids", ids);
        return new SpotifyLibrespotTracksResult(result.PayloadJson, result.Error);
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

        var args = new List<string>
        {
            CredentialsArg, blobPath,
            "--album-id", albumId.Trim()
        };
        if (includeTracks)
        {
            args.Add("--include-tracks");
        }

        var result = await RequestLibrespotPayloadAsync(
            blobPath,
            "album",
            ResolveLibrespotAlbumScriptPath,
            cancellationToken,
            args.ToArray());
        return new SpotifyLibrespotAlbumResult(result.PayloadJson, result.Error);
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
            ResolveLibrespotArtistScriptPath,
            cancellationToken,
            CredentialsArg, blobPath,
            "--artist-id", artistId.Trim());
        return new SpotifyLibrespotArtistResult(result.PayloadJson, result.Error);
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
            "podcast",
            ResolveLibrespotPodcastScriptPath,
            cancellationToken,
            CredentialsArg, blobPath,
            "--type", normalizedType,
            "--id", spotifyId.Trim());
        return new SpotifyLibrespotPodcastResult(result.PayloadJson, result.Error);
    }

    private async Task<LibrespotPayloadResult> RequestLibrespotPayloadAsync(
        string blobPath,
        string helperName,
        Func<string, string?> resolveScriptPath,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        try
        {
            var pythonExecutable = await EnsureSpotifyAuthEnvironmentAsync(cancellationToken);
            var repoRoot = ResolveRepoRoot();
            var scriptPath = resolveScriptPath(repoRoot);
            if (scriptPath == null)
            {
                _logger.LogWarning("Spotify librespot {Helper} helper not found.", helperName);
                return new LibrespotPayloadResult(null, HelperNotFoundError);
            }

            var executionResult = await RunPythonScriptAsync(
                pythonExecutable,
                scriptPath,
                Path.GetDirectoryName(blobPath) ?? _environment.ContentRootPath,
                cancellationToken,
                arguments);

            if (string.IsNullOrWhiteSpace(executionResult.StandardOutput))
            {
                if (!string.IsNullOrWhiteSpace(executionResult.StandardError))
                {
                    _logger.LogWarning(
                        "Spotify librespot {Helper} request failed: {Error}",
                        helperName,
                        executionResult.StandardError);
                }

                return new LibrespotPayloadResult(null, RequestFailedError);
            }

            return ParseLibrespotPayloadResult(executionResult.StandardOutput);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify librespot {Helper} request failed.", helperName);
            return new LibrespotPayloadResult(null, ExceptionError);
        }
    }

    private static LibrespotPayloadResult ParseLibrespotPayloadResult(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            var error = root.TryGetProperty(ErrorField, out var errorProp) ? errorProp.GetString() : UnknownError;
            return new LibrespotPayloadResult(null, error);
        }

        if (!root.TryGetProperty(PayloadField, out var payloadProp))
        {
            return new LibrespotPayloadResult(null, MissingPayloadError);
        }

        return new LibrespotPayloadResult(payloadProp.GetRawText(), null);
    }

    public async Task<SpotifyBlobPayload?> TryLoadBlobPayloadAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(blobPath, cancellationToken);
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
            _logger.LogWarning(ex, "Spotify blob payload is invalid JSON at {BlobPath}.", blobPath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read Spotify blob payload at {BlobPath}.", blobPath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied reading Spotify blob payload at {BlobPath}.", blobPath);
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
            var json = await File.ReadAllTextAsync(blobPath, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return ClassifyBlobKind(doc.RootElement) == SpotifyBlobKind.Librespot;
        }
        catch (JsonException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify librespot blob is invalid JSON at {BlobPath}.", blobPath);
            }
            return false;
        }
        catch (IOException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read Spotify librespot blob at {BlobPath}.", blobPath);
            }
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Access denied reading Spotify librespot blob at {BlobPath}.", blobPath);
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
        await WriteTextAtomicallyAsync(blobPath, json, cancellationToken);
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
        await WriteTextAtomicallyAsync(blobPath, json, cancellationToken);
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
                    response.ErrorSnippet ?? string.Empty);
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

    private async Task<SpotifyAccessTokenResult> RequestLibrespotWebApiTokenAsync(string blobPath, CancellationToken cancellationToken)
    {
        try
        {
            var pythonExecutable = await EnsureSpotifyAuthEnvironmentAsync(cancellationToken);
            var repoRoot = ResolveRepoRoot();
            var scriptPath = ResolveLibrespotTokenScriptPath(repoRoot);
            if (scriptPath == null)
            {
                _logger.LogWarning("Spotify librespot token helper not found.");
                return new SpotifyAccessTokenResult(null, null, HelperNotFoundError);
            }

            var startInfo = CreatePythonScriptStartInfo(
                pythonExecutable,
                scriptPath,
                Path.GetDirectoryName(blobPath) ?? _environment.ContentRootPath,
                CredentialsArg, blobPath,
                "--scopes",
                "playlist-read",
                "playlist-read-private",
                "user-library-read",
                "user-read-private",
                "user-read-email");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var processOutput = await WaitForProcessExitAsync(process, cancellationToken);
            var stdout = processOutput.StandardOutput;
            var stderr = processOutput.StandardError;

            if (string.IsNullOrWhiteSpace(stdout))
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogWarning("Spotify librespot token request failed: {Error}", stderr);
                }
                return new SpotifyAccessTokenResult(null, null, RequestFailedError);
            }

            LibrespotTokenResult? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<LibrespotTokenResult>(stdout, _jsonOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Spotify librespot token parse failed.");
                return new SpotifyAccessTokenResult(null, null, "parse_failed");
            }

            if (parsed == null || !parsed.Ok || string.IsNullOrWhiteSpace(parsed.AccessToken))
            {
                var error = parsed?.Error ?? UnknownError;
                _logger.LogWarning("Spotify librespot token unavailable: {Error}", error);
                return new SpotifyAccessTokenResult(null, parsed?.ExpiresAtUnixMs, error);
            }

            return new SpotifyAccessTokenResult(parsed.AccessToken, parsed.ExpiresAtUnixMs, null);
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

    private string? ResolveLibrespotTokenScriptPath(string repoRoot)
        => ResolveToolFilePath(repoRoot, LibrespotTokenScript);

    private string? ResolveLibrespotPlaylistScriptPath(string repoRoot)
        => ResolveToolFilePath(repoRoot, LibrespotPlaylistScript);

    private string? ResolveLibrespotTracksScriptPath(string repoRoot)
        => ResolveToolFilePath(repoRoot, LibrespotTracksScript);

    private string? ResolveLibrespotAlbumScriptPath(string repoRoot)
        => ResolveToolFilePath(repoRoot, LibrespotAlbumScript);

    private string? ResolveLibrespotArtistScriptPath(string repoRoot)
        => ResolveToolFilePath(repoRoot, LibrespotArtistScript);

    private string? ResolveLibrespotPodcastScriptPath(string repoRoot)
        => ResolveToolFilePath(repoRoot, LibrespotPodcastScript);

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
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyPythonCompatibilityEnvironment(startInfo);
        return startInfo;
    }

    private static string CreateAuthWorkingDirectory(string blobDir, string configRoot)
    {
        var authRoot = Path.Join(Path.GetDirectoryName(blobDir) ?? configRoot, "auth");
        Directory.CreateDirectory(authRoot);

        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var workingDirectory = Path.Join(authRoot, sessionId);
        Directory.CreateDirectory(workingDirectory);
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
                    _logger.LogWarning(ex, "Failed to delete existing Spotify blob at {BlobPath}", file);
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
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
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
        var processOutput = await WaitForProcessExitAsync(process, cancellationToken);
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

    private static async Task<ProcessOutputResult> RunPythonScriptAsync(
        string pythonExecutable,
        string scriptPath,
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = CreatePythonScriptStartInfo(
            pythonExecutable,
            scriptPath,
            workingDirectory,
            arguments);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        return await WaitForProcessExitAsync(process, cancellationToken);
    }

    private static async Task<ProcessOutputResult> WaitForProcessExitAsync(Process process, CancellationToken cancellationToken)
    {
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        return new ProcessOutputResult(process.ExitCode, stdout, stderr);
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

    private sealed record LibrespotTokenResult(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_at_unix_ms")] long? ExpiresAtUnixMs,
        [property: JsonPropertyName(ErrorField)] string? Error);
    private sealed record LibrespotPayloadResult(string? PayloadJson, string? Error);
    private sealed record ProcessOutputResult(int ExitCode, string StandardOutput, string StandardError);
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
    public sealed record SpotifyLibrespotPlaylistResult(string? PayloadJson, string? Error);
    public sealed record SpotifyLibrespotTracksResult(string? PayloadJson, string? Error);
    public sealed record SpotifyLibrespotAlbumResult(string? PayloadJson, string? Error);
    public sealed record SpotifyLibrespotArtistResult(string? PayloadJson, string? Error);
    public sealed record SpotifyLibrespotPodcastResult(string? PayloadJson, string? Error);

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
