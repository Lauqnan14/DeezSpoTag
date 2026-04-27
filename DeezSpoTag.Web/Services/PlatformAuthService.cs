using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public class PlatformAuthState
{
    public SpotifyConfig? Spotify { get; set; }
    public DiscogsAuth? Discogs { get; set; }
    public LastFmAuth? LastFm { get; set; }
    public BpmSupremeAuth? BpmSupreme { get; set; }
    public PlexAuth? Plex { get; set; }
    public JellyfinAuth? Jellyfin { get; set; }
    public AppleMusicAuth? AppleMusic { get; set; }
}

public class SpotifyConfig
{
    public string? ActiveAccount { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public List<SpotifyAccount> Accounts { get; set; } = new();
    public string? WebPlayerSpDc { get; set; }
    public string? WebPlayerUserAgent { get; set; }
}

public class SpotifyAccount
{
    public string Name { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? BlobPath { get; set; }
    public string? LibrespotBlobPath { get; set; }
    public string? WebPlayerBlobPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class DiscogsAuth
{
    public string? Token { get; set; }
    public string? Username { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Location { get; set; }
}

public class LastFmAuth
{
    public string? ApiKey { get; set; }
    public string? Username { get; set; }
}

public class BpmSupremeAuth
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? Library { get; set; }
}

public class PlexAuth
{
    public string? Url { get; set; }
    public string? Token { get; set; }
    public string? ServerName { get; set; }
    public string? MachineIdentifier { get; set; }
    public string? Version { get; set; }
    public string? Username { get; set; }
    public string? AvatarUrl { get; set; }
}

public class JellyfinAuth
{
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    public string? Username { get; set; }
    public string? UserId { get; set; }
    public string? ServerName { get; set; }
    public string? Version { get; set; }
    public string? AvatarUrl { get; set; }
}

public class AppleMusicAuth
{
    public string? Email { get; set; }
    public string? MediaUserToken { get; set; }
    public string? AuthorizationToken { get; set; }
    public bool WrapperReady { get; set; }
    public DateTimeOffset? WrapperLoggedInAt { get; set; }
}

public class PlatformAuthService
{
    private const string SpotifyFileName = "spotify.json";
    private const string DiscogsFileName = "discogs.json";
    private const string LastFmFileName = "lastfm.json";
    private const string BpmSupremeFileName = "bpmsupreme.json";
    private const string PlexFileName = "plex.json";
    private const string JellyfinFileName = "jellyfin.json";
    private const string AppleMusicFileName = "applemusic.json";
    private const string LegacyAggregateFileName = "platform-auth.json";
    private const string AutotagDirectory = "autotag";
    private const string MissingStatus = "missing";
    private const string PresentStatus = "present";
    private const string LegacyWebDataMarker = "/DeezSpoTag.Web/Data";

    private readonly ILogger<PlatformAuthService> _logger;
    private readonly string _contentRoot;
    private readonly string _dataRoot;
    private readonly string _authDirectory;
    private readonly string _legacyAggregateFilePath;
    private readonly string[] _legacyAggregateFileCandidates;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly object _statusLock = new();
    private string? _lastStatusSignature;
    private readonly record struct AuthStatusSnapshot(
        bool SpotifyConfigured,
        string? SpotifyAccount,
        string? SpotifyWebPlayerBlob,
        bool DiscogsConfigured,
        bool LastFmConfigured,
        bool BpmSupremeConfigured,
        bool PlexConfigured,
        bool JellyfinConfigured,
        bool AppleConfigured);
    private readonly record struct AuthStatusLogFields(
        string SpotifyAccount,
        string SpotifyBlob,
        string Discogs,
        string LastFm,
        string BpmSupreme,
        string Plex,
        string Jellyfin,
        string AppleMusic);
    private static readonly string[] PlatformFileNames =
    {
        SpotifyFileName,
        DiscogsFileName,
        LastFmFileName,
        BpmSupremeFileName,
        PlexFileName,
        JellyfinFileName,
        AppleMusicFileName
    };
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public PlatformAuthService(IWebHostEnvironment env, ILogger<PlatformAuthService> logger)
    {
        _logger = logger;
        _contentRoot = env.ContentRootPath;
        _dataRoot = AppDataPaths.GetDataRoot(env);
        _authDirectory = Path.Join(_dataRoot, AutotagDirectory);
        Directory.CreateDirectory(_authDirectory);
        _legacyAggregateFilePath = Path.Join(_authDirectory, LegacyAggregateFileName);
        _legacyAggregateFileCandidates = BuildLegacyAggregateCandidates();
    }

    public async Task<PlatformAuthState> LoadAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            return await LoadNoLockAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load platform auth state");
            return new PlatformAuthState();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<T> UpdateAsync<T>(Func<PlatformAuthState, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _fileLock.WaitAsync();
        try
        {
            var state = await LoadNoLockAsync();
            var result = update(state);
            await SaveNoLockAsync(state);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Failed to update platform auth state", ex);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(PlatformAuthState state)
    {
        await _fileLock.WaitAsync();
        try
        {
            await SaveNoLockAsync(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Failed to save platform auth state", ex);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SaveNoLockAsync(PlatformAuthState state)
    {
        await SavePlatformSectionNoLockAsync(SpotifyFileName, state.Spotify);
        await SavePlatformSectionNoLockAsync(DiscogsFileName, state.Discogs);
        await SavePlatformSectionNoLockAsync(LastFmFileName, state.LastFm);
        await SavePlatformSectionNoLockAsync(BpmSupremeFileName, state.BpmSupreme);
        await SavePlatformSectionNoLockAsync(PlexFileName, state.Plex);
        await SavePlatformSectionNoLockAsync(JellyfinFileName, state.Jellyfin);
        await SavePlatformSectionNoLockAsync(AppleMusicFileName, state.AppleMusic);
        TryRetireLegacyAggregateStateNoLock();
        LogAuthStatus(state);
    }

    private async Task<PlatformAuthState> LoadNoLockAsync()
    {
        await TryMigrateLegacyAuthStateNoLockAsync();

        var state = new PlatformAuthState
        {
            Spotify = await LoadPlatformSectionNoLockAsync<SpotifyConfig>(SpotifyFileName),
            Discogs = await LoadPlatformSectionNoLockAsync<DiscogsAuth>(DiscogsFileName),
            LastFm = await LoadPlatformSectionNoLockAsync<LastFmAuth>(LastFmFileName),
            BpmSupreme = await LoadPlatformSectionNoLockAsync<BpmSupremeAuth>(BpmSupremeFileName),
            Plex = await LoadPlatformSectionNoLockAsync<PlexAuth>(PlexFileName),
            Jellyfin = await LoadPlatformSectionNoLockAsync<JellyfinAuth>(JellyfinFileName),
            AppleMusic = await LoadPlatformSectionNoLockAsync<AppleMusicAuth>(AppleMusicFileName)
        };

        if (NormalizeSpotifyBlobPaths(state))
        {
            await SaveNoLockAsync(state);
        }

        LogAuthStatus(state);
        return state;
    }

    private async Task<T?> LoadPlatformSectionNoLockAsync<T>(string fileName)
        where T : class
    {
        return await LoadJsonFileNoLockAsync<T>(GetPlatformFilePath(fileName));
    }

    private async Task<T?> LoadJsonFileNoLockAsync<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Platform auth section is empty at {Path}", path);
                return null;
            }

            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (JsonException ex)
        {
            MoveCorruptAuthFileNoLock(path, ex);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load platform auth section from {Path}", path);
            return null;
        }
    }

    private async Task SavePlatformSectionNoLockAsync<T>(string fileName, T? section)
        where T : class
    {
        var path = GetPlatformFilePath(fileName);
        if (section is null)
        {
            TryDeletePlatformSectionNoLock(path);
            return;
        }

        var json = JsonSerializer.Serialize(section, _jsonOptions);
        var tempPath = $"{path}.tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private void TryDeletePlatformSectionNoLock(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete platform auth section at {Path}", path);
        }
    }

    private void MoveCorruptAuthFileNoLock(string path, JsonException ex)
    {
        var backupPath = $"{path}.bad-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(path, backupPath, overwrite: true);
            _logger.LogWarning(ex, "Platform auth section was invalid JSON. Moved to {BackupPath}", backupPath);
        }
        catch (Exception moveEx) when (moveEx is not OperationCanceledException)
        {
            _logger.LogWarning(moveEx, "Failed to move invalid platform auth section at {Path}", path);
        }
    }

    private async Task TryMigrateLegacyAuthStateNoLockAsync()
    {
        if (HasAnyPlatformFilesNoLock())
        {
            TryRetireLegacyAggregateStateNoLock();
            return;
        }

        foreach (var candidate in _legacyAggregateFileCandidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            PlatformAuthState? legacyState;
            try
            {
                var json = await File.ReadAllTextAsync(candidate);
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                legacyState = JsonSerializer.Deserialize<PlatformAuthState>(json, _jsonOptions);
                if (legacyState is null)
                {
                    continue;
                }
            }
            catch (JsonException ex)
            {
                MoveCorruptAuthFileNoLock(candidate, ex);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed migrating legacy platform auth state from {LegacyPath}.", candidate);
                continue;
            }

            await SaveNoLockAsync(legacyState);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Migrated platform auth state from legacy aggregate {LegacyPath} into dedicated platform auth files under {CurrentPath}.",
                    candidate,
                    _authDirectory);
            }
            return;
        }
    }

    private string[] BuildLegacyAggregateCandidates()
    {
        var candidates = new[]
        {
            _legacyAggregateFilePath,
            Path.Join(_dataRoot, "deezspotag", AutotagDirectory, LegacyAggregateFileName),
            Path.Join(_contentRoot, "Data", "deezspotag", AutotagDirectory, LegacyAggregateFileName),
            Path.Join(_contentRoot, "Data", AutotagDirectory, LegacyAggregateFileName)
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool HasAnyPlatformFilesNoLock()
    {
        return GetPlatformFileNames()
            .Select(GetPlatformFilePath)
            .Any(File.Exists);
    }

    private void TryRetireLegacyAggregateStateNoLock()
    {
        if (!HasAnyPlatformFilesNoLock())
        {
            return;
        }

        foreach (var candidate in _legacyAggregateFileCandidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var retiredPath = $"{candidate}.retired-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                File.Move(candidate, retiredPath, overwrite: true);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Retired legacy aggregate platform auth file at {LegacyPath}. Backup: {RetiredPath}",
                        candidate,
                        retiredPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed retiring legacy aggregate platform auth file at {LegacyPath}.", candidate);
            }
        }
    }

    private static string[] GetPlatformFileNames() => PlatformFileNames;

    private string GetPlatformFilePath(string fileName)
    {
        return Path.Join(_authDirectory, fileName);
    }

    private void LogAuthStatus(PlatformAuthState state)
    {
        var snapshot = BuildAuthStatusSnapshot(state);
        var signature = BuildAuthStatusSignature(snapshot);
        var logFields = BuildAuthStatusLogFields(snapshot);

        lock (_statusLock)
        {
            if (string.Equals(signature, _lastStatusSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastStatusSignature = signature;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Auth status: SpotifyAccount={SpotifyAccount} SpotifyBlob={SpotifyBlob} Discogs={Discogs} LastFm={LastFm} BpmSupreme={BpmSupreme} Plex={Plex} Jellyfin={Jellyfin} AppleMusic={AppleMusic}",
                logFields.SpotifyAccount,
                logFields.SpotifyBlob,
                logFields.Discogs,
                logFields.LastFm,
                logFields.BpmSupreme,
                logFields.Plex,
                logFields.Jellyfin,
                logFields.AppleMusic);
        }
    }

    private static AuthStatusSnapshot BuildAuthStatusSnapshot(PlatformAuthState state)
    {
        var spotifyAccount = state.Spotify?.ActiveAccount;
        var spotifyWebPlayerBlob = state.Spotify?.Accounts
            .FirstOrDefault(a => a.Name.Equals(spotifyAccount ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            ?.WebPlayerBlobPath;

        return new AuthStatusSnapshot(
            IsSpotifyConfigured(state.Spotify),
            spotifyAccount,
            spotifyWebPlayerBlob,
            !string.IsNullOrWhiteSpace(state.Discogs?.Token),
            !string.IsNullOrWhiteSpace(state.LastFm?.ApiKey),
            IsBpmSupremeConfigured(state.BpmSupreme),
            IsPlexConfigured(state.Plex),
            IsJellyfinConfigured(state.Jellyfin),
            state.AppleMusic?.WrapperReady == true);
    }

    private static string BuildAuthStatusSignature(AuthStatusSnapshot snapshot)
    {
        return
            $"SpotifyApi:{(snapshot.SpotifyConfigured ? "configured" : MissingStatus)}|" +
            $"SpotifyAccount:{(string.IsNullOrWhiteSpace(snapshot.SpotifyAccount) ? MissingStatus : snapshot.SpotifyAccount)}|" +
            $"SpotifyBlob:{(string.IsNullOrWhiteSpace(snapshot.SpotifyWebPlayerBlob) ? MissingStatus : PresentStatus)}|" +
            $"Discogs:{(snapshot.DiscogsConfigured ? PresentStatus : MissingStatus)}|" +
            $"LastFm:{(snapshot.LastFmConfigured ? PresentStatus : MissingStatus)}|" +
            $"BpmSupreme:{(snapshot.BpmSupremeConfigured ? PresentStatus : MissingStatus)}|" +
            $"Plex:{(snapshot.PlexConfigured ? PresentStatus : MissingStatus)}|" +
            $"Jellyfin:{(snapshot.JellyfinConfigured ? PresentStatus : MissingStatus)}|" +
            $"AppleMusic:{(snapshot.AppleConfigured ? "ready" : MissingStatus)}";
    }

    private static AuthStatusLogFields BuildAuthStatusLogFields(AuthStatusSnapshot snapshot)
    {
        return new AuthStatusLogFields(
            SpotifyAccount: ResolveStatus(snapshot.SpotifyAccount),
            SpotifyBlob: ResolveStatus(snapshot.SpotifyWebPlayerBlob),
            Discogs: ResolveStatus(snapshot.DiscogsConfigured),
            LastFm: ResolveStatus(snapshot.LastFmConfigured),
            BpmSupreme: ResolveStatus(snapshot.BpmSupremeConfigured),
            Plex: ResolveStatus(snapshot.PlexConfigured),
            Jellyfin: ResolveStatus(snapshot.JellyfinConfigured),
            AppleMusic: snapshot.AppleConfigured ? "ready" : MissingStatus);
    }

    private static string ResolveStatus(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? MissingStatus : PresentStatus;
    }

    private static string ResolveStatus(bool configured)
    {
        return configured ? PresentStatus : MissingStatus;
    }

    private static bool IsSpotifyConfigured(SpotifyConfig? spotify)
    {
        return spotify != null
               && !string.IsNullOrWhiteSpace(spotify.ClientId)
               && !string.IsNullOrWhiteSpace(spotify.ClientSecret);
    }

    private static bool IsBpmSupremeConfigured(BpmSupremeAuth? bpmSupreme)
    {
        return bpmSupreme != null
               && !string.IsNullOrWhiteSpace(bpmSupreme.Email)
               && !string.IsNullOrWhiteSpace(bpmSupreme.Password);
    }

    private static bool IsPlexConfigured(PlexAuth? plex)
    {
        return plex != null
               && !string.IsNullOrWhiteSpace(plex.Token)
               && !string.IsNullOrWhiteSpace(plex.Url);
    }

    private static bool IsJellyfinConfigured(JellyfinAuth? jellyfin)
    {
        return jellyfin != null
               && !string.IsNullOrWhiteSpace(jellyfin.ApiKey)
               && !string.IsNullOrWhiteSpace(jellyfin.Url);
    }

    private bool NormalizeSpotifyBlobPaths(PlatformAuthState state)
    {
        var accounts = state.Spotify?.Accounts;
        if (accounts is not { Count: > 0 })
        {
            return false;
        }

        var blobRoot = Path.Join(_dataRoot, "spotify", "blobs");
        var updated = false;
        foreach (var account in accounts)
        {
            updated |= TryNormalizeSpotifyAccountBlobPath(account, blobRoot);
            updated |= TryClassifySpotifyAccountBlobPaths(account);
        }

        return updated;
    }

    private bool TryNormalizeSpotifyAccountBlobPath(SpotifyAccount account, string blobRoot)
    {
        var currentPath = account.BlobPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(currentPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var candidatePath = Path.Join(blobRoot, fileName);
        if (IsLegacyWebBlobPath(currentPath))
        {
            return TryMigrateLegacySpotifyBlobPath(account, currentPath, candidatePath, blobRoot);
        }

        if (File.Exists(currentPath) || !File.Exists(candidatePath))
        {
            return false;
        }

        account.BlobPath = candidatePath;
        return true;
    }

    private static bool TryClassifySpotifyAccountBlobPaths(SpotifyAccount account)
    {
        var updated = false;
        if (string.IsNullOrWhiteSpace(account.BlobPath))
        {
            return false;
        }

        if (LooksLikeWebPlayerBlobPath(account.BlobPath))
        {
            if (!string.Equals(account.WebPlayerBlobPath, account.BlobPath, StringComparison.Ordinal))
            {
                account.WebPlayerBlobPath = account.BlobPath;
                updated = true;
            }
        }
        else
        {
            if (!string.Equals(account.LibrespotBlobPath, account.BlobPath, StringComparison.Ordinal))
            {
                account.LibrespotBlobPath = account.BlobPath;
                updated = true;
            }
        }

        return updated;
    }

    private static bool LooksLikeWebPlayerBlobPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".web.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyWebBlobPath(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        return normalizedPath.Contains(LegacyWebDataMarker, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryMigrateLegacySpotifyBlobPath(
        SpotifyAccount account,
        string currentPath,
        string candidatePath,
        string blobRoot)
    {
        Directory.CreateDirectory(blobRoot);
        if (!File.Exists(candidatePath) && File.Exists(currentPath))
        {
            try
            {
                File.Copy(currentPath, candidatePath, overwrite: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to migrate Spotify blob from legacy path {Path}", currentPath);
            }
        }

        if (!File.Exists(candidatePath))
        {
            return false;
        }

        account.BlobPath = candidatePath;
        return true;
    }

}
