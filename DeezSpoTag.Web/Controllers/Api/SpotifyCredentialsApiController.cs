using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.ComponentModel.DataAnnotations;

namespace DeezSpoTag.Web.Controllers.Api;

public abstract class SpotifyCredentialsApiControllerCore : ControllerBase
{
    private const string DefaultProfileUserId = "default";
    private const string SpotifyAccessTokenProbeHost = "open.spotify.com";
    private const string SpotifyAccessTokenProbePath = "/get_access_token";
    private const string SpotifyAccessTokenProbeQuery = "reason=transport&productType=web_player";
    private const string SpotifyPathfinderProbeHost = "api-partner.spotify.com";
    private const string SpotifyPathfinderProbePath = "/pathfinder/v2/query";
    private static readonly Regex AccountNameRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
    private static readonly System.Text.Json.JsonSerializerOptions SpotifyUserAuthSerializerOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    public sealed class SpotifyCredentialsCollaborators
    {
        public required PlatformAuthService PlatformAuthService { get; init; }
        public required SpotifyUserAuthStore UserAuthStore { get; init; }
        public required SpotifyBlobService BlobService { get; init; }
        public required SpotifyPathfinderMetadataClient PathfinderMetadataClient { get; init; }
        public required LibraryConfigStore ConfigStore { get; init; }
        public required IConfiguration Configuration { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
    }

    private readonly PlatformAuthService _platformAuthService;
    private readonly SpotifyUserAuthStore _userAuthStore;
    private readonly SpotifyBlobService _blobService;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly LibraryConfigStore _configStore;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly IWebHostEnvironment _environment;

    private static string BuildSpotifyProbeUrl(string host, string path, string? query = null)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttps, host)
        {
            Path = path
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            builder.Query = query;
        }

        return builder.Uri.ToString();
    }

    protected SpotifyCredentialsApiControllerCore(
        SpotifyCredentialsCollaborators collaborators,
        ILogger logger,
        IWebHostEnvironment environment)
    {
        _platformAuthService = collaborators.PlatformAuthService;
        _userAuthStore = collaborators.UserAuthStore;
        _blobService = collaborators.BlobService;
        _pathfinderMetadataClient = collaborators.PathfinderMetadataClient;
        _configStore = collaborators.ConfigStore;
        _configuration = collaborators.Configuration;
        _httpClientFactory = collaborators.HttpClientFactory;
        _logger = logger;
        _environment = environment;
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts()
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var state = await LoadUserStateWithFallbackAsync(userId);
        var accounts = state.Accounts ?? new List<SpotifyUserAccount>();
        string? activeAccountPlan = null;
        var activeName = state.ActiveAccount;
        if (!string.IsNullOrWhiteSpace(activeName))
        {
            var activeAccount = accounts.FirstOrDefault(account => account.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase));
            if (activeAccount != null)
            {
                activeAccountPlan = "Free";
            }
        }

        return Ok(new
        {
            accounts,
            activeAccount = state.ActiveAccount,
            activeAccountPlan
        });
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var state = await _platformAuthService.LoadAsync();
        var spotify = state.Spotify ?? new SpotifyConfig();
        var hasClientCreds = !string.IsNullOrWhiteSpace(spotify.ClientId)
            && !string.IsNullOrWhiteSpace(spotify.ClientSecret);
        var userState = await LoadUserStateWithFallbackAsync(userId);
        var activeBlobPath = SpotifyUserAuthStore.ResolveActiveBlobPath(userState);
        var hasBlobAccount = !string.IsNullOrWhiteSpace(activeBlobPath)
            && _blobService.BlobExists(activeBlobPath);
        var hasConfig = hasClientCreds || hasBlobAccount;
        return Ok(new
        {
            clientId = spotify.ClientId,
            clientSecretSaved = !string.IsNullOrWhiteSpace(spotify.ClientSecret),
            hasConfig
        });
    }

    [HttpPost("config")]
    public async Task<IActionResult> SaveConfig([FromBody] SpotifyApiConfigRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (request == null)
        {
            return BadRequest("Missing payload.");
        }

        var clientId = request.ClientId?.Trim();
        var clientSecret = request.ClientSecret?.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return BadRequest("Client ID and Client Secret are required.");
        }

        await _platformAuthService.UpdateAsync(state =>
        {
            state.Spotify ??= new SpotifyConfig();
            state.Spotify.ClientId = clientId;
            state.Spotify.ClientSecret = clientSecret;
            return 0;
        });

        return Ok(new { saved = true });
    }

    [HttpPost("accounts/{name}")]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> SaveAccount(
        string name,
        [FromForm, Required] IFormFile blobFile,
        [FromForm] string? region)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Account name is required.");
        }

        if (!IsValidAccountName(name))
        {
            return BadRequest("Account name must be 1-64 chars: letters, numbers, dot, underscore, hyphen.");
        }

        await _blobService.EnsureSpotifyAuthEnvironmentAsync(HttpContext.RequestAborted);
        var state = await _userAuthStore.LoadAsync(userId);

        var blobDir = _userAuthStore.GetUserBlobDir(userId);
        Directory.CreateDirectory(blobDir);
        var extension = ResolveBlobExtension(blobFile.FileName);
        if (extension == null)
        {
            return BadRequest("Unsupported credentials file type. Use .json or .blob.");
        }

        var blobPath = Path.Join(blobDir, $"{name}{extension}");
        var tempPath = Path.Join(blobDir, $"{name}.upload{extension}");

        await using (var stream = System.IO.File.Create(tempPath))
        {
            await blobFile.CopyToAsync(stream);
        }

        if (!await _pathfinderMetadataClient.ValidateBlobAsync(tempPath, HttpContext.RequestAborted))
        {
            System.IO.File.Delete(tempPath);
            return BadRequest("Spotify blob failed validation. Please upload a fresh blob.");
        }

        var lastKnownGood = BackupExistingBlob(blobPath, name);

        System.IO.File.Copy(tempPath, blobPath, overwrite: true);
        System.IO.File.Delete(tempPath);

        UpsertUploadedBlobAccount(state, name, region, blobPath, lastKnownGood, DateTimeOffset.UtcNow);

        if (string.IsNullOrWhiteSpace(state.ActiveAccount))
        {
            state.ActiveAccount = name;
        }

        await _userAuthStore.SaveAsync(userId, state);
        _logger.LogInformation("Saved Spotify blob for account {AccountName}", name);
        await UpdatePlatformSpotifyAccountAsync(name, blobPath, blobPath, state.ActiveAccount);

        return Ok(new { saved = true, blobPath });
    }

    [HttpPost("accounts/{name}/generate")]
    public async Task<IActionResult> GenerateAccount(string name, [FromBody] SpotifyBlobGenerateRequest? request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Account name is required.");
        }

        if (!IsValidAccountName(name))
        {
            return BadRequest("Account name must be 1-64 chars: letters, numbers, dot, underscore, hyphen.");
        }

        SpotifyUserAuthState state;
        var blobDir = _userAuthStore.GetUserBlobDir(userId);
        SpotifyBlobResult result;
        try
        {
            await _blobService.EnsureSpotifyAuthEnvironmentAsync(cancellationToken);
            state = await _userAuthStore.LoadAsync(userId);
            result = await _blobService.GenerateBlobAsync(name, request?.Headless ?? false, blobDir, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Spotify credentials generation timed out for account {AccountName}.", name);
            return StatusCode(StatusCodes.Status504GatewayTimeout, "Spotify credentials generation timed out.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Spotify credentials generation could not access data path for account {AccountName}.", name);
            return StatusCode(StatusCodes.Status500InternalServerError, "Spotify data directory is not writable.");
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Spotify credentials generation dependencies are missing for account {AccountName}.", name);
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Spotify credentials generation failed due to I/O error for account {AccountName}.", name);
            return StatusCode(StatusCodes.Status500InternalServerError, "Spotify credentials generation failed due to an I/O error.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Spotify credentials generation failed for account {AccountName}.", name);
            return BadRequest(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Spotify credentials generation request failed for account {AccountName}.", name);
            return StatusCode(StatusCodes.Status502BadGateway, "Spotify credentials generation request failed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Spotify credentials generation failed unexpectedly for account {AccountName}.", name);
            return StatusCode(StatusCodes.Status500InternalServerError, "Spotify credentials generation failed unexpectedly.");
        }

        var librespotBlobPath = result.BlobPath;
        string? webPlayerBlobPath = null;

        var cookieSpDc = state.WebPlayerSpDc;
        if (!string.IsNullOrWhiteSpace(cookieSpDc))
        {
            webPlayerBlobPath = Path.Join(blobDir, $"{name}.web.json");
            await _blobService.SaveWebPlayerBlobAsync(
                webPlayerBlobPath,
                cookieSpDc,
                state.WebPlayerSpKey,
                state.WebPlayerUserAgent,
                cancellationToken);

            if (!await _pathfinderMetadataClient.ValidateBlobAsync(webPlayerBlobPath, cancellationToken))
            {
                return BadRequest("Generated Spotify web-player blob failed validation.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var existing = state.Accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            state.Accounts.Add(new SpotifyUserAccount
            {
                Name = name,
                Region = request?.Region?.Trim(),
                BlobPath = librespotBlobPath,
                LibrespotBlobPath = librespotBlobPath,
                WebPlayerBlobPath = webPlayerBlobPath,
                CreatedAt = now,
                UpdatedAt = now,
                LastValidatedAt = now
            });
        }
        else
        {
            var trimmedRegion = request?.Region?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedRegion))
            {
                existing.Region = trimmedRegion;
            }
            existing.BlobPath = librespotBlobPath;
            existing.LibrespotBlobPath = librespotBlobPath;
            existing.WebPlayerBlobPath = webPlayerBlobPath ?? existing.WebPlayerBlobPath;
            existing.UpdatedAt = now;
            existing.LastValidatedAt = now;
        }

        if (string.IsNullOrWhiteSpace(state.ActiveAccount))
        {
            state.ActiveAccount = name;
        }

        await _userAuthStore.SaveAsync(userId, state);
        await UpdatePlatformSpotifyAccountAsync(name, librespotBlobPath, webPlayerBlobPath, state.ActiveAccount);

        return Ok(new { generated = true, librespotBlobPath, webPlayerBlobPath });
    }

    [HttpPost("accounts/{name}/regenerate")]
    public Task<IActionResult> RegenerateAccount(string name, [FromBody] SpotifyBlobGenerateRequest? request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Task.FromResult<IActionResult>(BadRequest(ModelState));
        }

        return GenerateAccount(name, request, cancellationToken);
    }

    public sealed class SpotifyApiConfigRequest
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
    }

    public sealed class SpotifyWebPlayerCookieRequest
    {
        public string? SpDc { get; set; }
        public string? SpKey { get; set; }
        public string? UserAgent { get; set; }
        public List<SpotifyBlobCookie>? Cookies { get; set; }
    }

    private static string? ResolveBlobExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".json";
        }

        return extension is ".json" or ".blob" ? extension : null;
    }

    private static void UpsertUploadedBlobAccount(
        SpotifyUserAuthState state,
        string accountName,
        string? region,
        string blobPath,
        string? lastKnownGoodBlobPath,
        DateTimeOffset now)
    {
        var existing = state.Accounts.FirstOrDefault(a => a.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            state.Accounts.Add(new SpotifyUserAccount
            {
                Name = accountName,
                Region = region?.Trim(),
                BlobPath = blobPath,
                WebPlayerBlobPath = blobPath,
                CreatedAt = now,
                UpdatedAt = now,
                LastValidatedAt = now,
                LastKnownGoodBlobPath = lastKnownGoodBlobPath
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(region))
        {
            existing.Region = region.Trim();
        }

        existing.BlobPath = blobPath;
        existing.WebPlayerBlobPath = blobPath;
        existing.UpdatedAt = now;
        existing.LastValidatedAt = now;
        if (!string.IsNullOrWhiteSpace(lastKnownGoodBlobPath))
        {
            existing.LastKnownGoodBlobPath = lastKnownGoodBlobPath;
        }
    }

    private static (string? SpDc, string? SpKey, string? UserAgent, List<SpotifyBlobCookie>? Cookies) NormalizeWebPlayerCookieRequest(
        SpotifyWebPlayerCookieRequest request)
    {
        var spDc = request.SpDc?.Trim();
        var spKey = string.IsNullOrWhiteSpace(request.SpKey) ? null : request.SpKey.Trim();
        var userAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim();
        return (spDc, spKey, userAgent, request.Cookies);
    }

    private static (string? SpDc, string? SpKey) ResolveCookieCredentials(
        string? spDc,
        string? spKey,
        List<SpotifyBlobCookie>? providedCookies)
    {
        if (providedCookies == null || providedCookies.Count == 0)
        {
            return (spDc, spKey);
        }

        var spDcCookie = providedCookies.FirstOrDefault(cookie =>
            cookie?.Name != null && cookie.Name.Equals("sp_dc", StringComparison.OrdinalIgnoreCase));
        if (spDcCookie != null && !string.IsNullOrWhiteSpace(spDcCookie.Value))
        {
            spDc = spDcCookie.Value.Trim();
        }

        var spKeyCookie = providedCookies.FirstOrDefault(cookie =>
            cookie?.Name != null && cookie.Name.Equals("sp_key", StringComparison.OrdinalIgnoreCase));
        if (spKeyCookie != null && !string.IsNullOrWhiteSpace(spKeyCookie.Value))
        {
            spKey = spKeyCookie.Value.Trim();
        }

        return (spDc, spKey);
    }

    private static string ResolveEffectiveAccountName(string? profileId, string? activeAccount)
    {
        var effectiveAccountName = string.IsNullOrWhiteSpace(profileId) ? activeAccount : profileId;
        if (string.IsNullOrWhiteSpace(effectiveAccountName))
        {
            return "web-player";
        }

        effectiveAccountName = effectiveAccountName.Trim();
        return IsValidAccountName(effectiveAccountName)
            ? effectiveAccountName
            : "web-player";
    }

    private static SpotifyUserAccount GetOrCreateAccount(SpotifyUserAuthState state, string accountName)
    {
        var account = state.Accounts.FirstOrDefault(existing =>
            existing.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase));
        if (account != null)
        {
            return account;
        }

        account = new SpotifyUserAccount
        {
            Name = accountName,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        state.Accounts.Add(account);
        return account;
    }

    private string ResolveWebPlayerBlobPath(string userId, SpotifyUserAccount account, string accountName)
    {
        if (!string.IsNullOrWhiteSpace(account.WebPlayerBlobPath))
        {
            return account.WebPlayerBlobPath;
        }

        var blobDir = _userAuthStore.GetUserBlobDir(userId);
        Directory.CreateDirectory(blobDir);
        return Path.Join(blobDir, $"{accountName}.web.json");
    }

    private static string? BackupExistingBlob(string targetBlobPath, string accountName)
    {
        if (!System.IO.File.Exists(targetBlobPath))
        {
            return null;
        }

        var extension = Path.GetExtension(targetBlobPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".json";
        }

        var backupPath = Path.Join(
            Path.GetDirectoryName(targetBlobPath) ?? string.Empty,
            $"{accountName}.lastgood{extension}");
        System.IO.File.Copy(targetBlobPath, backupPath, overwrite: true);
        return backupPath;
    }

    private async Task<SpotifyBlobResult> PersistWebPlayerBlobAsync(
        string targetBlobPath,
        List<SpotifyBlobCookie>? providedCookies,
        string spDc,
        string? spKey,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (providedCookies != null && providedCookies.Count > 0)
        {
            return await _blobService.SaveWebPlayerBlobWithCookiesAsync(
                targetBlobPath,
                providedCookies,
                userAgent,
                cancellationToken);
        }

        return await _blobService.SaveWebPlayerBlobAsync(
            targetBlobPath,
            spDc,
            spKey,
            userAgent,
            cancellationToken);
    }

    private static void UpdateWebPlayerAccount(
        SpotifyUserAccount account,
        string blobPath,
        string? lastKnownGood,
        DateTimeOffset now)
    {
        account.WebPlayerBlobPath = blobPath;
        account.UpdatedAt = now;
        account.LastValidatedAt = now;
        if (!string.IsNullOrWhiteSpace(lastKnownGood))
        {
            account.LastKnownGoodBlobPath = lastKnownGood;
        }
    }

    private async Task PersistWebPlayerPlatformStateAsync(
        string accountName,
        string blobPath,
        SpotifyUserAuthState state)
    {
        var now = DateTimeOffset.UtcNow;
        await _platformAuthService.UpdateAsync(platformState =>
        {
            platformState.Spotify ??= new SpotifyConfig();
            var platformAccount = platformState.Spotify.Accounts.FirstOrDefault(existing =>
                existing.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase));
            if (platformAccount == null)
            {
                platformState.Spotify.Accounts.Add(new SpotifyAccount
                {
                    Name = accountName,
                    BlobPath = blobPath,
                    WebPlayerBlobPath = blobPath,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                platformAccount.WebPlayerBlobPath = blobPath;
                platformAccount.UpdatedAt = now;
            }

            platformState.Spotify.ActiveAccount = accountName;
            platformState.Spotify.WebPlayerSpDc = state.WebPlayerSpDc;
            platformState.Spotify.WebPlayerSpKey = state.WebPlayerSpKey;
            platformState.Spotify.WebPlayerUserAgent = state.WebPlayerUserAgent;
            return 0;
        });
    }

    [HttpPost("web-player")]
    public async Task<IActionResult> SaveWebPlayerCookies([FromBody] SpotifyWebPlayerCookieRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        if (request == null)
        {
            return BadRequest("Missing payload.");
        }

        var normalizedRequest = NormalizeWebPlayerCookieRequest(request);
        var spDc = normalizedRequest.SpDc;
        var spKey = normalizedRequest.SpKey;
        var userAgent = normalizedRequest.UserAgent;
        var providedCookies = normalizedRequest.Cookies;

        if (string.IsNullOrWhiteSpace(spDc) && (providedCookies == null || providedCookies.Count == 0))
        {
            return BadRequest("sp_dc is required.");
        }

        (spDc, spKey) = ResolveCookieCredentials(spDc, spKey, providedCookies);

        if (string.IsNullOrWhiteSpace(spDc))
        {
            return BadRequest("sp_dc is required.");
        }

        var state = await _userAuthStore.LoadAsync(userId);
        state.WebPlayerSpDc = spDc;
        state.WebPlayerSpKey = spKey;
        state.WebPlayerUserAgent = userAgent;

        var profile = await FetchSpotifyProfileAsync(
            state.WebPlayerSpDc,
            state.WebPlayerSpKey,
            state.WebPlayerUserAgent,
            HttpContext.RequestAborted);
        var effectiveAccountName = ResolveEffectiveAccountName(profile?.Id, state.ActiveAccount);

        var account = GetOrCreateAccount(state, effectiveAccountName);

        state.ActiveAccount = effectiveAccountName;
        var targetBlobPath = ResolveWebPlayerBlobPath(userId, account, effectiveAccountName);

        _logger.LogInformation("Saving Spotify web player cookies for account {Account}. Target blob: {BlobPath}",
            effectiveAccountName, targetBlobPath);
        var lastKnownGood = BackupExistingBlob(targetBlobPath, effectiveAccountName);
        var blobResult = await PersistWebPlayerBlobAsync(
            targetBlobPath,
            providedCookies,
            spDc,
            state.WebPlayerSpKey,
            state.WebPlayerUserAgent,
            HttpContext.RequestAborted);
        UpdateWebPlayerAccount(account, blobResult.BlobPath, lastKnownGood, DateTimeOffset.UtcNow);

        if (!await _pathfinderMetadataClient.ValidateBlobAsync(blobResult.BlobPath, HttpContext.RequestAborted))
        {
            var activeAccount = state.ActiveAccount;
            if (!string.IsNullOrWhiteSpace(activeAccount))
            {
                var activeAccountEntry = state.Accounts.FirstOrDefault(a =>
                    a.Name.Equals(activeAccount, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(activeAccountEntry?.LastKnownGoodBlobPath)
                    && System.IO.File.Exists(activeAccountEntry.LastKnownGoodBlobPath))
                {
                    System.IO.File.Copy(activeAccountEntry.LastKnownGoodBlobPath, blobResult.BlobPath, overwrite: true);
                }
            }
            return BadRequest("Web player cookies failed validation.");
        }

        await _userAuthStore.SaveAsync(userId, state);

        var webPlayerBlobPath = account.WebPlayerBlobPath ?? blobResult.BlobPath;
        if (!string.IsNullOrWhiteSpace(webPlayerBlobPath))
        {
            await PersistWebPlayerPlatformStateAsync(effectiveAccountName, webPlayerBlobPath, state);
        }

        return Ok(new { saved = true, webPlayerBlobPath = blobResult.BlobPath });
    }

    [HttpPost("web-player/clear")]
    public async Task<IActionResult> ClearWebPlayerCookies()
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var state = await _userAuthStore.LoadAsync(userId);
        var activeAccountName = state.ActiveAccount;
        string? removedBlobPath = null;
        var accountNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAccountName(accountNames, activeAccountName);

        if (!string.IsNullOrWhiteSpace(activeAccountName))
        {
            var account = state.Accounts.FirstOrDefault(a =>
                a.Name.Equals(activeAccountName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(account?.WebPlayerBlobPath))
            {
                removedBlobPath = account!.WebPlayerBlobPath;
                account.WebPlayerBlobPath = null;
                account.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        state.WebPlayerSpDc = null;
        state.WebPlayerSpKey = null;
        state.WebPlayerUserAgent = null;
        await _userAuthStore.SaveAsync(userId, state);

        if (!string.IsNullOrWhiteSpace(removedBlobPath) && System.IO.File.Exists(removedBlobPath))
        {
            try
            {
                System.IO.File.Delete(removedBlobPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to delete Spotify web-player blob {BlobPath}", removedBlobPath);
            }
        }

        await _platformAuthService.UpdateAsync(platformState =>
        {
            if (platformState.Spotify is null)
            {
                return 0;
            }

            platformState.Spotify.WebPlayerSpDc = null;
            platformState.Spotify.WebPlayerSpKey = null;
            platformState.Spotify.WebPlayerUserAgent = null;
            if (!string.IsNullOrWhiteSpace(removedBlobPath))
            {
                foreach (var account in platformState.Spotify.Accounts
                             .Where(account => string.Equals(account.BlobPath, removedBlobPath, StringComparison.OrdinalIgnoreCase)))
                {
                    account.BlobPath = null;
                    account.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            return 0;
        });

        ClearSpotifySessionArtifacts(userId, accountNames);

        return Ok(new { cleared = true, removedBlobPath });
    }

    private sealed class SpotifyProfileResult
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Product { get; set; }
    }

    private async Task UpdatePlatformSpotifyAccountAsync(
        string accountName,
        string librespotBlobPath,
        string? webPlayerBlobPath,
        string? activeAccount)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(librespotBlobPath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        List<string> removedBlobPaths = [];
        await _platformAuthService.UpdateAsync(platformState =>
        {
            platformState.Spotify ??= new SpotifyConfig();
            removedBlobPaths = RemoveNonTargetPlatformAccounts(platformState.Spotify, accountName);
            UpsertPlatformAccount(platformState.Spotify, accountName, librespotBlobPath, webPlayerBlobPath, now);
            if (!string.IsNullOrWhiteSpace(activeAccount))
            {
                platformState.Spotify.ActiveAccount = activeAccount;
            }
            return 0;
        });

        DeleteObsoletePlatformBlobFiles(removedBlobPaths, librespotBlobPath);
    }

    private static List<string> RemoveNonTargetPlatformAccounts(SpotifyConfig spotify, string accountName)
    {
        var removedBlobPaths = new List<string>();
        for (var i = spotify.Accounts.Count - 1; i >= 0; i--)
        {
            var existingAccount = spotify.Accounts[i];
            if (existingAccount.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(existingAccount.BlobPath))
            {
                removedBlobPaths.Add(existingAccount.BlobPath);
            }

            spotify.Accounts.RemoveAt(i);
        }

        return removedBlobPaths;
    }

    private static void UpsertPlatformAccount(
        SpotifyConfig spotify,
        string accountName,
        string librespotBlobPath,
        string? webPlayerBlobPath,
        DateTimeOffset now)
    {
        var platformAccount = spotify.Accounts.FirstOrDefault(account =>
            account.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase));
        if (platformAccount == null)
        {
            spotify.Accounts.Add(new SpotifyAccount
            {
                Name = accountName,
                BlobPath = librespotBlobPath,
                LibrespotBlobPath = librespotBlobPath,
                WebPlayerBlobPath = webPlayerBlobPath,
                CreatedAt = now,
                UpdatedAt = now
            });
            return;
        }

        platformAccount.BlobPath = librespotBlobPath;
        platformAccount.LibrespotBlobPath = librespotBlobPath;
        platformAccount.WebPlayerBlobPath = webPlayerBlobPath ?? platformAccount.WebPlayerBlobPath;
        platformAccount.UpdatedAt = now;
    }

    private void DeleteObsoletePlatformBlobFiles(IEnumerable<string> removedBlobPaths, string currentBlobPath)
    {
        try
        {
            var dataRoot = AppDataPaths.GetDataRoot(_environment);
            foreach (var removedBlobPath in removedBlobPaths)
            {
                if (string.IsNullOrWhiteSpace(removedBlobPath) || removedBlobPath.Equals(currentBlobPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(removedBlobPath);
                if (!fullPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete previous Spotify blob files after login.");
        }
    }

    private async Task<SpotifyProfileResult?> FetchSpotifyProfileAsync(
        string? spDc,
        string? spKey,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spDc))
        {
            return null;
        }

        string? token;
        try
        {
            token = await _blobService.GetWebPlayerAccessTokenFromCookiesAsync(spDc, spKey, userAgent, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Spotify profile lookup timed out while validating web-player cookies.");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Spotify profile lookup failed while validating web-player cookies.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify profile lookup failed unexpectedly while validating web-player cookies.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var claims = SpotifyAccessTokenParser.TryParse(token);
        if (claims is null)
        {
            return null;
        }

        return new SpotifyProfileResult
        {
            Id = claims.Subject,
            DisplayName = claims.DisplayName ?? claims.Subject,
            Product = claims.Product
        };
    }

    private static string? ReadCookieValue(IEnumerable<SpotifyBlobCookie> cookies, string cookieName)
    {
        var cookie = cookies.FirstOrDefault(entry =>
            entry.Name.Equals(cookieName, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(cookie?.Value) ? null : cookie.Value;
    }

    private async Task<(string? SpDc, string? SpKey, string? UserAgent)> ResolveWebPlayerCredentialsAsync(
        SpotifyUserAuthState state,
        CancellationToken cancellationToken)
    {
        var spDc = state.WebPlayerSpDc;
        var spKey = state.WebPlayerSpKey;
        var userAgent = state.WebPlayerUserAgent;
        if (!string.IsNullOrWhiteSpace(spDc))
        {
            return (spDc, spKey, userAgent);
        }

        var blobPath = SpotifyUserAuthStore.ResolveActiveWebPlayerBlobPath(state);
        if (string.IsNullOrWhiteSpace(blobPath) || !System.IO.File.Exists(blobPath))
        {
            return (spDc, spKey, userAgent);
        }

        var payload = await _blobService.TryLoadBlobPayloadAsync(blobPath, cancellationToken);
        if (payload?.Cookies?.Count > 0)
        {
            spDc = ReadCookieValue(payload.Cookies, "sp_dc") ?? spDc;
            spKey = ReadCookieValue(payload.Cookies, "sp_key") ?? spKey;
        }

        if (string.IsNullOrWhiteSpace(userAgent) && !string.IsNullOrWhiteSpace(payload?.UserAgent))
        {
            userAgent = payload.UserAgent;
        }

        return (spDc, spKey, userAgent);
    }

    [HttpGet("web-player/test")]
    public async Task<IActionResult> TestWebPlayerCookies(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var state = await LoadUserStateWithFallbackAsync(userId);
        var credentials = await ResolveWebPlayerCredentialsAsync(state, cancellationToken);
        var spDc = credentials.SpDc;
        var spKey = credentials.SpKey;
        var userAgent = credentials.UserAgent;

        if (string.IsNullOrWhiteSpace(spDc))
        {
            return BadRequest(new { ok = false, message = "sp_dc is not configured." });
        }

        var result = await _blobService.TestWebPlayerAccessTokenFromCookiesAsync(
            spDc,
            spKey,
            userAgent,
            cancellationToken);

        return Ok(result);
    }

    public sealed class SpotifyWebPlayerStatusResponse
    {
        public bool HasSpDc { get; set; }
        public bool HasSpKey { get; set; }
        public bool HasUserAgent { get; set; }
        public string? ActiveAccount { get; set; }
        public string? BlobPath { get; set; }
        public bool BlobFileExists { get; set; }
        public bool BlobPayloadHasCookies { get; set; }
        public bool BlobPayloadHasUserAgent { get; set; }
        public IReadOnlyList<string> BlobCookieNames { get; set; } = Array.Empty<string>();
    }

    public sealed class SpotifyConnectionStatusResponse
    {
        public string? ActiveAccount { get; set; }
        public string? WebPlayerBlobPath { get; set; }
        public string? LibrespotBlobPath { get; set; }
        public bool WebPlayerOk { get; set; }
        public string? WebPlayerError { get; set; }
        public bool LibrespotOk { get; set; }
        public string? LibrespotError { get; set; }
        public bool Ok => WebPlayerOk && LibrespotOk;
    }

    [HttpGet("web-player/status")]
    public async Task<IActionResult> GetWebPlayerStatus(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var state = await LoadUserStateWithFallbackAsync(userId);
        var activeAccount = state.ActiveAccount;
        var blobPath = SpotifyUserAuthStore.ResolveActiveWebPlayerBlobPath(state);
        var blobExists = !string.IsNullOrWhiteSpace(blobPath) && System.IO.File.Exists(blobPath);

        var payload = blobExists
            ? await _blobService.TryLoadBlobPayloadAsync(blobPath!, cancellationToken)
            : null;

        var cookieNames = payload?.Cookies
            ?.Select(cookie => cookie.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
        var hasSpDcCookie = payload?.Cookies?.Any(cookie =>
            cookie.Name.Equals("sp_dc", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cookie.Value)) == true;
        var hasSpKeyCookie = payload?.Cookies?.Any(cookie =>
            cookie.Name.Equals("sp_key", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cookie.Value)) == true;

        return Ok(new SpotifyWebPlayerStatusResponse
        {
            HasSpDc = !string.IsNullOrWhiteSpace(state.WebPlayerSpDc) || hasSpDcCookie,
            HasSpKey = !string.IsNullOrWhiteSpace(state.WebPlayerSpKey) || hasSpKeyCookie,
            HasUserAgent = !string.IsNullOrWhiteSpace(state.WebPlayerUserAgent) || !string.IsNullOrWhiteSpace(payload?.UserAgent),
            ActiveAccount = activeAccount,
            BlobPath = blobPath,
            BlobFileExists = blobExists,
            BlobPayloadHasCookies = payload?.Cookies?.Count > 0,
            BlobPayloadHasUserAgent = !string.IsNullOrWhiteSpace(payload?.UserAgent),
            BlobCookieNames = cookieNames
        });
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetSpotifyConnectionStatus(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var state = await LoadUserStateWithFallbackAsync(userId);
        var activeAccount = state.ActiveAccount;
        var webPlayerBlobPath = SpotifyUserAuthStore.ResolveActiveWebPlayerBlobPath(state);
        var librespotBlobPath = SpotifyUserAuthStore.ResolveActiveLibrespotBlobPath(state);
        var webPlayerStatus = await ValidateWebPlayerConnectionAsync(webPlayerBlobPath, cancellationToken);
        var librespotStatus = await ValidateLibrespotConnectionAsync(librespotBlobPath, cancellationToken);

        return Ok(new SpotifyConnectionStatusResponse
        {
            ActiveAccount = activeAccount,
            WebPlayerBlobPath = webPlayerBlobPath,
            LibrespotBlobPath = librespotBlobPath,
            WebPlayerOk = webPlayerStatus.Ok,
            WebPlayerError = webPlayerStatus.Error,
            LibrespotOk = librespotStatus.Ok,
            LibrespotError = librespotStatus.Error
        });
    }

    private async Task<(bool Ok, string? Error)> ValidateWebPlayerConnectionAsync(string? webPlayerBlobPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webPlayerBlobPath))
        {
            return (false, "missing_web_player_blob");
        }

        try
        {
            var tokenInfo = await _blobService.GetWebPlayerTokenInfoAsync(webPlayerBlobPath, cancellationToken);
            if (tokenInfo == null || !string.IsNullOrWhiteSpace(tokenInfo.Error) || string.IsNullOrWhiteSpace(tokenInfo.AccessToken))
            {
                return (false, tokenInfo?.Error ?? "web_player_token_failed");
            }

            return tokenInfo.IsAnonymous == true
                ? (false, "web_player_anonymous")
                : (true, null);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Spotify web player status check timed out.");
            return (false, "web_player_timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Spotify web player status check failed.");
            return (false, "web_player_request_failed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify web player status check failed unexpectedly.");
            return (false, "web_player_status_failed");
        }
    }

    private async Task<(bool Ok, string? Error)> ValidateLibrespotConnectionAsync(string? librespotBlobPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(librespotBlobPath))
        {
            return (false, "missing_librespot_blob");
        }

        try
        {
            var librespotToken = await _blobService.GetWebApiAccessTokenAsync(
                librespotBlobPath,
                allowRetries: false,
                cancellationToken: cancellationToken);
            return !string.IsNullOrWhiteSpace(librespotToken.AccessToken)
                ? (true, null)
                : (false, librespotToken.Error ?? "librespot_token_failed");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Spotify librespot status check timed out.");
            return (false, "librespot_timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Spotify librespot status check failed.");
            return (false, "librespot_request_failed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify librespot status check failed unexpectedly.");
            return (false, "librespot_status_failed");
        }
    }

    private async Task<SpotifyUserAuthState> LoadUserStateWithFallbackAsync(string userId)
    {
        var state = await _userAuthStore.LoadAsync(userId);
        var changed = false;
        var userAuthFileExists = System.IO.File.Exists(_userAuthStore.GetUserAuthFilePath(userId));

        // Only import fallback/platform state when the user auth file does not exist yet.
        // If an auth file exists (even empty), treat it as the source of truth and avoid
        // resurrecting stale platform entries after logout.
        if (!userAuthFileExists && state.Accounts.Count == 0 && string.IsNullOrWhiteSpace(state.ActiveAccount))
        {
            var fallback = await _userAuthStore.TryLoadFallbackAsync(userId);
            if (fallback != null)
            {
                state = fallback;
                changed = true;
            }
        }

        if (!userAuthFileExists)
        {
            changed |= await TryImportPlatformSpotifyAsync(state);
        }
        changed |= SpotifyUserAuthStore.EnsureActiveAccount(state);

        if (changed)
        {
            await _userAuthStore.SaveAsync(userId, state);
        }

        return state;
    }

    private async Task<bool> TryImportPlatformSpotifyAsync(SpotifyUserAuthState state)
    {
        var platformState = await _platformAuthService.LoadAsync();
        var spotify = platformState.Spotify;
        if (spotify == null)
        {
            return false;
        }

        var changed = ImportWebPlayerValuesFromPlatform(state, spotify);
        changed |= ImportPlatformAccounts(state, spotify);
        changed |= ImportPlatformActiveAccount(state, spotify);
        return changed;
    }

    private static bool ImportWebPlayerValuesFromPlatform(SpotifyUserAuthState state, SpotifyConfig spotify)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(state.WebPlayerSpDc) && !string.IsNullOrWhiteSpace(spotify.WebPlayerSpDc))
        {
            state.WebPlayerSpDc = spotify.WebPlayerSpDc;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(state.WebPlayerSpKey) && !string.IsNullOrWhiteSpace(spotify.WebPlayerSpKey))
        {
            state.WebPlayerSpKey = spotify.WebPlayerSpKey;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(state.WebPlayerUserAgent) && !string.IsNullOrWhiteSpace(spotify.WebPlayerUserAgent))
        {
            state.WebPlayerUserAgent = spotify.WebPlayerUserAgent;
            changed = true;
        }

        return changed;
    }

    private static bool ImportPlatformAccounts(SpotifyUserAuthState state, SpotifyConfig spotify)
    {
        if (state.Accounts.Count == 0)
        {
            return ImportAllPlatformAccounts(state, spotify);
        }

        return ImportMissingPlatformActiveAccount(state, spotify);
    }

    private static bool ImportAllPlatformAccounts(SpotifyUserAuthState state, SpotifyConfig spotify)
    {
        var imported = false;
        foreach (var platformAccount in spotify.Accounts)
        {
            if (string.IsNullOrWhiteSpace(platformAccount.Name))
            {
                continue;
            }

            var createdAt = platformAccount.CreatedAt == default ? DateTimeOffset.UtcNow : platformAccount.CreatedAt;
            var updatedAt = platformAccount.UpdatedAt == default ? createdAt : platformAccount.UpdatedAt;
            state.Accounts.Add(new SpotifyUserAccount
            {
                Name = platformAccount.Name,
                Region = platformAccount.Region,
                BlobPath = platformAccount.BlobPath,
                LibrespotBlobPath = platformAccount.LibrespotBlobPath,
                WebPlayerBlobPath = platformAccount.WebPlayerBlobPath,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            });
            imported = true;
        }

        return imported;
    }

    private static bool ImportMissingPlatformActiveAccount(SpotifyUserAuthState state, SpotifyConfig spotify)
    {
        if (string.IsNullOrWhiteSpace(spotify.ActiveAccount))
        {
            return false;
        }

        var platformActive = spotify.Accounts.FirstOrDefault(a =>
            a.Name.Equals(spotify.ActiveAccount, StringComparison.OrdinalIgnoreCase));
        if (platformActive == null)
        {
            return false;
        }

        var existing = state.Accounts.FirstOrDefault(a =>
            a.Name.Equals(platformActive.Name, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            var createdAt = platformActive.CreatedAt == default ? DateTimeOffset.UtcNow : platformActive.CreatedAt;
            var updatedAt = platformActive.UpdatedAt == default ? createdAt : platformActive.UpdatedAt;
            state.Accounts.Add(new SpotifyUserAccount
            {
                Name = platformActive.Name,
                Region = platformActive.Region,
                BlobPath = platformActive.BlobPath,
                LibrespotBlobPath = platformActive.LibrespotBlobPath,
                WebPlayerBlobPath = platformActive.WebPlayerBlobPath,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            });
            return true;
        }

        var updated = false;
        if (string.IsNullOrWhiteSpace(existing.BlobPath) && !string.IsNullOrWhiteSpace(platformActive.BlobPath))
        {
            existing.BlobPath = platformActive.BlobPath;
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(existing.LibrespotBlobPath) && !string.IsNullOrWhiteSpace(platformActive.LibrespotBlobPath))
        {
            existing.LibrespotBlobPath = platformActive.LibrespotBlobPath;
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(existing.WebPlayerBlobPath) && !string.IsNullOrWhiteSpace(platformActive.WebPlayerBlobPath))
        {
            existing.WebPlayerBlobPath = platformActive.WebPlayerBlobPath;
            updated = true;
        }

        if (updated)
        {
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return updated;
    }

    private static bool ImportPlatformActiveAccount(SpotifyUserAuthState state, SpotifyConfig spotify)
    {
        if (!string.IsNullOrWhiteSpace(state.ActiveAccount) || string.IsNullOrWhiteSpace(spotify.ActiveAccount))
        {
            return false;
        }

        state.ActiveAccount = spotify.ActiveAccount;
        return true;
    }

    public sealed class WebPlayerDiagnosticResult
    {
        public string Url { get; set; } = string.Empty;
        public int StatusCode { get; set; }
        public string? ContentType { get; set; }
        public string? Snippet { get; set; }
    }

    private async Task<WebPlayerDiagnosticResult> ProbeWebPlayerEndpointAsync(
        HttpClient client,
        string url,
        string logPrefix,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("*/*");
        var response = await client.SendAsync(request, cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var snippet = body.Length > 200 ? body[..200] : body;
        var normalizedSnippet = string.IsNullOrWhiteSpace(snippet)
            ? "empty"
            : snippet.Replace("\r", " ").Replace("\n", " ").Trim();
        var level = response.IsSuccessStatusCode ? "info" : "warn";

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            level,
            $"{logPrefix} probe {url} -> {(int)response.StatusCode} {response.ReasonPhrase}; content-type={contentType ?? "none"}; snippet={normalizedSnippet}"));

        return new WebPlayerDiagnosticResult
        {
            Url = url,
            StatusCode = (int)response.StatusCode,
            ContentType = contentType,
            Snippet = string.IsNullOrWhiteSpace(snippet) ? null : snippet
        };
    }

    [HttpGet("web-player/diagnostic")]
    public async Task<IActionResult> DiagnoseWebPlayerAccess(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        const string logPrefix = "Spotify Web Player diagnostic";

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"{logPrefix} started."));

        var results = new List<WebPlayerDiagnosticResult>
        {
            await ProbeWebPlayerEndpointAsync(
                client,
                BuildSpotifyProbeUrl(SpotifyAccessTokenProbeHost, SpotifyAccessTokenProbePath, SpotifyAccessTokenProbeQuery),
                logPrefix,
                cancellationToken),
            await ProbeWebPlayerEndpointAsync(
                client,
                BuildSpotifyProbeUrl(SpotifyPathfinderProbeHost, SpotifyPathfinderProbePath),
                logPrefix,
                cancellationToken)
        };

        var hasFailures = results.Any(result => result.StatusCode < 200 || result.StatusCode >= 300);
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            hasFailures ? "warn" : "info",
            $"{logPrefix} finished. {(hasFailures ? "Some probes failed." : "All probes succeeded.")}"));

        return Ok(new { results });
    }

    [HttpDelete("accounts/{name}")]
    public async Task<IActionResult> DeleteAccount(string name)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        name = name?.Trim() ?? string.Empty;
        if (!IsValidAccountName(name))
        {
            return BadRequest("Invalid account name.");
        }

        var state = await _userAuthStore.LoadAsync(userId);

        var account = state.Accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (account == null)
        {
            return NotFound("Spotify account not found.");
        }

        var blobPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectAccountBlobPaths(account, blobPaths);

        var removedActiveAccount = state.ActiveAccount != null &&
                                   state.ActiveAccount.Equals(name, StringComparison.OrdinalIgnoreCase);
        state.Accounts.Remove(account);
        if (removedActiveAccount)
        {
            state.ActiveAccount = null;
            state.WebPlayerSpDc = null;
            state.WebPlayerSpKey = null;
            state.WebPlayerUserAgent = null;
        }

        await _userAuthStore.SaveAsync(userId, state);

        await _platformAuthService.UpdateAsync(platformState =>
        {
            if (platformState.Spotify == null)
            {
                return 0;
            }

            RemovePlatformAccount(platformState.Spotify, name, blobPaths);

            return 0;
        });

        DeleteBlobArtifacts(blobPaths);

        var accountNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAccountName(accountNames, name);
        ClearSpotifySessionArtifacts(userId, accountNames);

        return Ok(new { deleted = true });
    }

    private static void CollectAccountBlobPaths(SpotifyUserAccount account, HashSet<string> blobPaths)
    {
        if (!string.IsNullOrWhiteSpace(account.BlobPath))
        {
            blobPaths.Add(account.BlobPath);
        }

        if (!string.IsNullOrWhiteSpace(account.LibrespotBlobPath))
        {
            blobPaths.Add(account.LibrespotBlobPath);
        }

        if (!string.IsNullOrWhiteSpace(account.WebPlayerBlobPath))
        {
            blobPaths.Add(account.WebPlayerBlobPath);
        }

        if (!string.IsNullOrWhiteSpace(account.LastKnownGoodBlobPath))
        {
            blobPaths.Add(account.LastKnownGoodBlobPath);
        }
    }

    private static void RemovePlatformAccount(SpotifyConfig spotify, string name, HashSet<string> blobPaths)
    {
        for (var i = spotify.Accounts.Count - 1; i >= 0; i--)
        {
            var platformAccount = spotify.Accounts[i];
            if (!platformAccount.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(platformAccount.BlobPath))
            {
                blobPaths.Add(platformAccount.BlobPath);
            }

            spotify.Accounts.RemoveAt(i);
        }

        if (!string.IsNullOrWhiteSpace(spotify.ActiveAccount) &&
            spotify.ActiveAccount.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            spotify.ActiveAccount = null;
            spotify.WebPlayerSpDc = null;
            spotify.WebPlayerSpKey = null;
            spotify.WebPlayerUserAgent = null;
        }
    }

    private void DeleteBlobArtifacts(HashSet<string> blobPaths)
    {
        foreach (var blobPath in blobPaths)
        {
            _blobService.InvalidateWebApiAccessToken(blobPath);
            if (!System.IO.File.Exists(blobPath))
            {
                continue;
            }

            try
            {
                System.IO.File.Delete(blobPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to delete Spotify blob at {BlobPath}", blobPath);
            }
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var blobPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accountNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = await _userAuthStore.LoadAsync(userId);
        CollectUserBlobArtifacts(userId, blobPaths, accountNames);
        CollectStateBlobArtifacts(state, blobPaths, accountNames);

        state.Accounts.Clear();
        state.ActiveAccount = null;
        state.WebPlayerSpDc = null;
        state.WebPlayerSpKey = null;
        state.WebPlayerUserAgent = null;
        await _userAuthStore.SaveAsync(userId, state);

        await _platformAuthService.UpdateAsync(platformState =>
        {
            if (platformState.Spotify == null)
            {
                return 0;
            }

            CollectPlatformBlobArtifacts(platformState.Spotify.Accounts, blobPaths, accountNames);
            platformState.Spotify.ActiveAccount = null;
            platformState.Spotify.Accounts.Clear();
            platformState.Spotify.WebPlayerSpDc = null;
            platformState.Spotify.WebPlayerSpKey = null;
            platformState.Spotify.WebPlayerUserAgent = null;
            return 0;
        });

        var removedProfileCount = 0;
        if (IsSingleUserMode())
        {
            removedProfileCount = await ClearSingleUserResidualArtifactsAsync(userId, blobPaths, accountNames);
        }
        else
        {
            ClearUserSpotifyProfileArtifacts(userId);
        }

        foreach (var blobPath in blobPaths)
        {
            _blobService.InvalidateWebApiAccessToken(blobPath);
            if (!System.IO.File.Exists(blobPath))
            {
                continue;
            }

            try
            {
                System.IO.File.Delete(blobPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to delete Spotify blob at {BlobPath}", blobPath);
            }
        }

        ClearSpotifySessionArtifacts(userId, accountNames);

        return Ok(new { loggedOut = true, removedBlobCount = blobPaths.Count, removedProfileCount });
    }

    private static void AddAccountName(HashSet<string> accountNames, string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        accountNames.Add(accountName.Trim());
    }

    private void CollectUserBlobArtifacts(string userId, HashSet<string> blobPaths, HashSet<string> accountNames)
    {
        try
        {
            var userBlobDir = _userAuthStore.GetUserBlobDir(userId);
            if (!Directory.Exists(userBlobDir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(userBlobDir))
            {
                blobPaths.Add(file);

                var stem = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(stem))
                {
                    continue;
                }

                var accountStem = stem.Split('.', 2)[0].Trim();
                if (string.IsNullOrWhiteSpace(accountStem) ||
                    accountStem.Equals("credentials", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                accountNames.Add(accountStem);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to collect Spotify blob artifacts for user {UserId}", userId);
        }
    }

    private static void CollectStateBlobArtifacts(SpotifyUserAuthState state, HashSet<string> blobPaths, HashSet<string> accountNames)
    {
        foreach (var account in state.Accounts)
        {
            AddAccountName(accountNames, account.Name);
            if (!string.IsNullOrWhiteSpace(account.BlobPath))
            {
                blobPaths.Add(account.BlobPath);
            }
            if (!string.IsNullOrWhiteSpace(account.LibrespotBlobPath))
            {
                blobPaths.Add(account.LibrespotBlobPath);
            }
            if (!string.IsNullOrWhiteSpace(account.WebPlayerBlobPath))
            {
                blobPaths.Add(account.WebPlayerBlobPath);
            }
            if (!string.IsNullOrWhiteSpace(account.LastKnownGoodBlobPath))
            {
                blobPaths.Add(account.LastKnownGoodBlobPath);
            }
        }
    }

    private static void CollectPlatformBlobArtifacts(
        IReadOnlyCollection<SpotifyAccount> accounts,
        HashSet<string> blobPaths,
        HashSet<string> accountNames)
    {
        foreach (var account in accounts)
        {
            AddAccountName(accountNames, account.Name);
            if (!string.IsNullOrWhiteSpace(account.BlobPath))
            {
                blobPaths.Add(account.BlobPath);
            }
        }
    }

    private bool IsSingleUserMode()
    {
        return _configuration.GetValue<bool>("IsSingleUser", true);
    }

    private async Task<int> ClearSingleUserResidualArtifactsAsync(
        string currentUserId,
        HashSet<string> blobPaths,
        HashSet<string> accountNames)
    {
        var removedProfiles = 0;
        try
        {
            var dataRoot = AppDataPaths.GetDataRoot(_environment);
            var spotifyRoot = Path.Join(dataRoot, "spotify");
            var usersRoot = Path.Join(spotifyRoot, "users");
            if (Directory.Exists(usersRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(usersRoot))
                {
                    if (await ProcessSingleUserProfileArtifactsAsync(
                            dir,
                            currentUserId,
                            blobPaths,
                            accountNames))
                    {
                        removedProfiles++;
                    }
                }
            }

            ClearSharedSpotifyRootArtifacts(spotifyRoot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to clear residual Spotify artifacts in single-user logout mode.");
        }

        return removedProfiles;
    }

    private async Task<bool> ProcessSingleUserProfileArtifactsAsync(
        string profileDirectory,
        string currentUserId,
        HashSet<string> blobPaths,
        HashSet<string> accountNames)
    {
        var profileId = Path.GetFileName(profileDirectory);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        try
        {
            var profileState = await _userAuthStore.LoadAsync(profileId);
            CollectStateBlobArtifacts(profileState, blobPaths, accountNames);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load Spotify state during single-user logout for profile {UserId}", profileId);
        }

        CollectUserBlobArtifacts(profileId, blobPaths, accountNames);
        if (profileId.Equals(currentUserId, StringComparison.OrdinalIgnoreCase))
        {
            ClearUserSpotifyProfileArtifacts(profileId);
            return false;
        }

        return TryDeleteDirectory(profileDirectory, "stale Spotify user profile");
    }

    private void ClearSharedSpotifyRootArtifacts(string spotifyRoot)
    {
        TryDeleteDirectory(Path.Join(spotifyRoot, "auth"), "Spotify auth root");
        TryDeleteDirectory(Path.Join(spotifyRoot, "blobs"), "Spotify legacy blob root");
        TryDeleteFilesByPattern(spotifyRoot, "home-feed-cache*.json");
        TryDeleteFile(Path.Join(spotifyRoot, "browse-categories-cache.json"), "Spotify browse cache");
        TryDeleteFile(Path.Join(spotifyRoot, "spotify-deezer-track-map.json"), "Spotify track map cache");
    }

    private void ClearUserSpotifyProfileArtifacts(string userId)
    {
        try
        {
            var userRoot = _userAuthStore.GetUserRoot(userId);
            if (!Directory.Exists(userRoot))
            {
                return;
            }

            var authDir = Path.Join(userRoot, "auth");
            TryDeleteDirectory(authDir, "Spotify user auth helper folder");

            var blobsDir = _userAuthStore.GetUserBlobDir(userId);
            TryDeleteDirectory(blobsDir, "Spotify user blob folder");

            var authMarker = _userAuthStore.GetUserAuthFilePath(userId);
            foreach (var file in Directory.EnumerateFiles(userRoot))
            {
                if (string.Equals(file, authMarker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteFile(file, "Spotify user artifact");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to clear Spotify user profile artifacts for {UserId}", userId);
        }
    }

    private bool TryDeleteDirectory(string path, string label)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to remove {Label} at {Path}", label, path);
            return false;
        }
    }

    private void TryDeleteFilesByPattern(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            TryDeleteFile(file, $"Spotify cache ({pattern})");
        }
    }

    private void TryDeleteFile(string path, string label)
    {
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to remove {Label} at {Path}", label, path);
        }
    }

    private void ClearSpotifySessionArtifacts(string userId, IReadOnlyCollection<string> accountNames)
    {
        try
        {
            var dataRoot = AppDataPaths.GetDataRoot(_environment);
            var spotifyRoot = Path.Join(dataRoot, "spotify");
            if (!Directory.Exists(spotifyRoot))
            {
                SpotifyHomeFeedApiController.ClearRuntimeAndPersistedCaches();
                return;
            }

            var normalizedAccountNames = NormalizeAccountNames(accountNames);
            DeleteAuthHelperDirectories(Path.Join(spotifyRoot, "auth"), normalizedAccountNames);
            DeleteLegacyBlobArtifacts(Path.Join(spotifyRoot, "blobs"), normalizedAccountNames);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to clear Spotify session artifacts for user {UserId}", userId);
        }
        finally
        {
            SpotifyHomeFeedApiController.ClearRuntimeAndPersistedCaches();
        }
    }

    private static HashSet<string> NormalizeAccountNames(IReadOnlyCollection<string> accountNames)
    {
        return accountNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void DeleteAuthHelperDirectories(string authRoot, HashSet<string> normalizedAccountNames)
    {
        if (!Directory.Exists(authRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(authRoot))
        {
            var folderName = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(folderName) || !normalizedAccountNames.Contains(folderName))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to remove Spotify auth helper folder {Path}", directory);
            }
        }
    }

    private static bool ShouldDeleteLegacyBlobArtifact(string path, HashSet<string> normalizedAccountNames)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        var accountStem = stem.Split('.', 2)[0].Trim();
        return accountStem.Equals("credentials", StringComparison.OrdinalIgnoreCase)
            || normalizedAccountNames.Contains(accountStem);
    }

    private void DeleteLegacyBlobArtifacts(string legacyBlobDir, HashSet<string> normalizedAccountNames)
    {
        if (!Directory.Exists(legacyBlobDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(legacyBlobDir))
        {
            if (!ShouldDeleteLegacyBlobArtifact(file, normalizedAccountNames))
            {
                continue;
            }

            try
            {
                System.IO.File.Delete(file);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to remove legacy Spotify blob artifact {Path}", file);
            }
        }
    }

    private string? GetUserId()
    {
        if (IsSingleUserMode())
        {
            return DefaultProfileUserId;
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            return userId;
        }

        if (!LocalApiAccess.IsAllowed(HttpContext))
        {
            _logger.LogWarning("Spotify credentials request rejected: unauthenticated non-local request.");
            return null;
        }

        var fallbackUserId = ResolveSingleSpotifyUserId();
        if (string.IsNullOrWhiteSpace(fallbackUserId))
        {
            _logger.LogWarning("Spotify credentials request rejected: multiple spotify profiles found without authentication.");
            return null;
        }

        if (string.Equals(fallbackUserId, DefaultProfileUserId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Spotify credentials using default local profile (no authenticated user).");
        }

        return fallbackUserId;
    }

    private string? ResolveSingleSpotifyUserId()
    {
        try
        {
            var usersRoot = Path.Join(AppDataPaths.GetDataRoot(_environment), "spotify", "users");
            if (!Directory.Exists(usersRoot))
            {
                return DefaultProfileUserId;
            }

            var candidates = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(usersRoot))
            {
                var id = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var authPath = Path.Join(dir, "spotify-auth.json");
                if (!System.IO.File.Exists(authPath))
                {
                    continue;
                }

                SpotifyUserAuthState? state = null;
                try
                {
                    var json = System.IO.File.ReadAllText(authPath);
                    state = System.Text.Json.JsonSerializer.Deserialize<SpotifyUserAuthState>(
                        json,
                        SpotifyUserAuthSerializerOptions);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }

                if (state == null || (state.Accounts.Count == 0 && string.IsNullOrWhiteSpace(state.ActiveAccount)))
                {
                    continue;
                }

                candidates.Add(id);
            }

            if (candidates.Count == 0)
            {
                return DefaultProfileUserId;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve fallback Spotify user profile.");
            return null;
        }
    }

    private static bool IsValidAccountName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && AccountNameRegex.IsMatch(value);
    }
}

public class SpotifyBlobGenerateRequest
{
    public string? Region { get; set; }
    public bool? Headless { get; set; }
}
