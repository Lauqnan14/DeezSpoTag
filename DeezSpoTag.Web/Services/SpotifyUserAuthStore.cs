using System.Text.Json;
using System.Collections.Concurrent;
using DeezSpoTag.Services.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyUserAuthStore
{
    private sealed record FallbackAuthCandidate(string Path, DateTimeOffset ModifiedAt, SpotifyUserAuthState State);

    private readonly ILogger<SpotifyUserAuthStore> _logger;
    private readonly string _dataRoot;
    private readonly bool _isSingleUserMode;
    private readonly ProtectedCredentialFileStore _credentialStore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userFileGates = new(StringComparer.OrdinalIgnoreCase);
    private const string ProtectionPurpose = "DeezSpoTag.Spotify.UserAuth";
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public SpotifyUserAuthStore(
        IWebHostEnvironment env,
        IConfiguration configuration,
        ILogger<SpotifyUserAuthStore> logger,
        IDataProtectionProvider dataProtectionProvider)
    {
        _logger = logger;
        _dataRoot = AppDataPaths.GetDataRoot(env);
        _isSingleUserMode = configuration.GetValue<bool>("IsSingleUser", true);
        _credentialStore = new ProtectedCredentialFileStore(dataProtectionProvider, ProtectionPurpose);
    }

    public string GetUserRoot(string userId)
        => Path.Join(_dataRoot, "spotify", "users", userId);

    public string GetUserBlobDir(string userId)
        => Path.Join(GetUserRoot(userId), "blobs");

    public string GetUserAuthFilePath(string userId)
        => Path.Join(GetUserRoot(userId), "spotify-auth.json");

    public async Task<SpotifyUserAuthState> LoadAsync(string userId)
    {
        var gate = GetUserFileGate(userId);
        await gate.WaitAsync();
        try
        {
            var path = GetUserAuthFilePath(userId);
            if (!File.Exists(path))
            {
                return new SpotifyUserAuthState();
            }

            var json = await _credentialStore.ReadTextAndMigrateAsync(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new SpotifyUserAuthState();
            }

            var state = JsonSerializer.Deserialize<SpotifyUserAuthState>(json, _jsonOptions) ?? new SpotifyUserAuthState();
            var changed = NormalizeLegacyBlobPaths(userId, state);
            changed |= EnsureActiveAccount(state);
            if (changed)
            {
                await SaveNoLockAsync(userId, state);
            }

            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Spotify user auth state for {UserId}", userId);
            return new SpotifyUserAuthState();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SpotifyUserAuthState> LoadAuthoritativeAsync(
        string userId,
        PlatformAuthService platformAuthService)
    {
        var state = await LoadAsync(userId);
        var changed = false;
        var userAuthFileExists = File.Exists(GetUserAuthFilePath(userId));

        // An existing per-user file is authoritative, including an intentionally
        // empty file created by logout. Import legacy state only during migration.
        if (!userAuthFileExists && state.Accounts.Count == 0 && string.IsNullOrWhiteSpace(state.ActiveAccount))
        {
            var fallback = await TryLoadFallbackAsync(userId);
            if (fallback != null)
            {
                state = fallback;
                changed = true;
            }
        }

        if (!userAuthFileExists)
        {
            var platformState = await platformAuthService.LoadAsync();
            if (platformState.Spotify != null)
            {
                changed |= ImportPlatformState(state, platformState.Spotify);
            }
        }

        changed |= EnsureActiveAccount(state);
        if (changed)
        {
            await SaveAsync(userId, state);
        }

        return state;
    }

    private static bool ImportPlatformState(SpotifyUserAuthState state, SpotifyConfig spotify)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(state.WebPlayerSpDc) && !string.IsNullOrWhiteSpace(spotify.WebPlayerSpDc))
        {
            state.WebPlayerSpDc = spotify.WebPlayerSpDc;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(state.WebPlayerUserAgent) && !string.IsNullOrWhiteSpace(spotify.WebPlayerUserAgent))
        {
            state.WebPlayerUserAgent = spotify.WebPlayerUserAgent;
            changed = true;
        }

        changed |= ImportPlatformAccounts(state, spotify);
        if (string.IsNullOrWhiteSpace(state.ActiveAccount) && !string.IsNullOrWhiteSpace(spotify.ActiveAccount))
        {
            state.ActiveAccount = spotify.ActiveAccount;
            changed = true;
        }

        return changed;
    }

    private static bool ImportPlatformAccounts(SpotifyUserAuthState state, SpotifyConfig spotify)
    {
        var accounts = state.Accounts.Count == 0
            ? spotify.Accounts
            : spotify.Accounts.Where(account =>
                !string.IsNullOrWhiteSpace(spotify.ActiveAccount)
                && account.Name.Equals(spotify.ActiveAccount, StringComparison.OrdinalIgnoreCase));
        var changed = false;

        foreach (var source in accounts.Where(account => !string.IsNullOrWhiteSpace(account.Name)))
        {
            var target = state.Accounts.FirstOrDefault(account =>
                account.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                var createdAt = source.CreatedAt == default ? DateTimeOffset.UtcNow : source.CreatedAt;
                state.Accounts.Add(new SpotifyUserAccount
                {
                    Name = source.Name,
                    Region = source.Region,
                    BlobPath = source.BlobPath,
                    LibrespotBlobPath = source.LibrespotBlobPath,
                    WebPlayerBlobPath = source.WebPlayerBlobPath,
                    CreatedAt = createdAt,
                    UpdatedAt = source.UpdatedAt == default ? createdAt : source.UpdatedAt
                });
                changed = true;
                continue;
            }

            var updated = false;
            if (string.IsNullOrWhiteSpace(target.BlobPath) && !string.IsNullOrWhiteSpace(source.BlobPath))
            {
                target.BlobPath = source.BlobPath;
                updated = true;
            }
            if (string.IsNullOrWhiteSpace(target.LibrespotBlobPath) && !string.IsNullOrWhiteSpace(source.LibrespotBlobPath))
            {
                target.LibrespotBlobPath = source.LibrespotBlobPath;
                updated = true;
            }
            if (string.IsNullOrWhiteSpace(target.WebPlayerBlobPath) && !string.IsNullOrWhiteSpace(source.WebPlayerBlobPath))
            {
                target.WebPlayerBlobPath = source.WebPlayerBlobPath;
                updated = true;
            }
            if (updated)
            {
                target.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }

        return changed;
    }

    public async Task<SpotifyUserAuthState?> TryLoadFallbackAsync(string userId)
    {
        try
        {
            var usersRoot = Path.Join(_dataRoot, "spotify", "users");
            if (!Directory.Exists(usersRoot))
            {
                return null;
            }

            var candidates = await CollectFallbackCandidatesAsync(usersRoot, userId);

            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count > 1)
            {
                if (_isSingleUserMode)
                {
                    var selected = candidates
                        .OrderByDescending(candidate => candidate.ModifiedAt)
                        .First();

                    _logger.LogWarning(
                        "Single-user mode: multiple Spotify auth files found; importing the most recent profile {Path} into {UserId}.",
                        selected.Path,
                        userId);

                    return selected.State;
                }

                _logger.LogWarning(
                    "Multiple Spotify user auth files found; not auto-importing into {UserId}.",
                    userId);
                return null;
            }

            var candidate = candidates[0];
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Using Spotify auth fallback from {Path} for user {UserId}.", candidate.Path, userId);
            }
            return candidate.State;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to locate Spotify auth fallback for {UserId}", userId);
            return null;
        }
    }

    private async Task<List<FallbackAuthCandidate>> CollectFallbackCandidatesAsync(string usersRoot, string userId)
    {
        var candidates = new List<FallbackAuthCandidate>();
        foreach (var dir in Directory.EnumerateDirectories(usersRoot))
        {
            var candidate = await TryReadFallbackCandidateAsync(dir, userId);
            if (candidate != null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private async Task<FallbackAuthCandidate?> TryReadFallbackCandidateAsync(string directory, string userId)
    {
        var id = Path.GetFileName(directory);
        if (string.Equals(id, userId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var authPath = Path.Join(directory, "spotify-auth.json");
        if (!File.Exists(authPath))
        {
            return null;
        }

        SpotifyUserAuthState? state;
        try
        {
            var json = await _credentialStore.ReadTextAndMigrateAsync(authPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            state = JsonSerializer.Deserialize<SpotifyUserAuthState>(json, _jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read Spotify auth fallback from {Path}", authPath);
            }
            return null;
        }

        if (state == null || (state.Accounts.Count == 0 && string.IsNullOrWhiteSpace(state.ActiveAccount)))
        {
            return null;
        }

        var modified = File.GetLastWriteTimeUtc(authPath);
        return new FallbackAuthCandidate(authPath, new DateTimeOffset(modified, TimeSpan.Zero), state);
    }

    public async Task SaveAsync(string userId, SpotifyUserAuthState state)
    {
        var gate = GetUserFileGate(userId);
        await gate.WaitAsync();
        try
        {
            await SaveNoLockAsync(userId, state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to save Spotify user auth state for {userId}", ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetUserFileGate(string userId)
    {
        var normalizedUserId = string.IsNullOrWhiteSpace(userId) ? "default" : userId.Trim();
        return _userFileGates.GetOrAdd(normalizedUserId, static _ => new SemaphoreSlim(1, 1));
    }

    private async Task SaveNoLockAsync(string userId, SpotifyUserAuthState state)
    {
        var path = GetUserAuthFilePath(userId);
        var directory = Path.GetDirectoryName(path) ?? GetUserRoot(userId);
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(state, _jsonOptions);
        await _credentialStore.WriteTextAsync(path, json);
    }

    public static SpotifyUserAccount? ResolveActiveAccount(SpotifyUserAuthState state)
    {
        var best = ResolveBestAccount(state);
        if (!string.IsNullOrWhiteSpace(state.ActiveAccount))
        {
            var active = state.Accounts.FirstOrDefault(a =>
                a.Name.Equals(state.ActiveAccount, StringComparison.OrdinalIgnoreCase));
            if (active != null)
            {
                if (best == null)
                {
                    return active;
                }

                var activeScore = GetAccountHealthScore(active);
                var bestScore = GetAccountHealthScore(best);
                if (bestScore > activeScore)
                {
                    return best;
                }

                return active;
            }
        }

        return best;
    }

    public static SpotifyUserAccount? ResolveBestAccount(SpotifyUserAuthState state)
    {
        if (state.Accounts.Count == 0)
        {
            return null;
        }

        var candidates = state.Accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Name))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var withBlobs = candidates
            .Where(account =>
                !string.IsNullOrWhiteSpace(account.WebPlayerBlobPath)
                || !string.IsNullOrWhiteSpace(account.LibrespotBlobPath)
                || !string.IsNullOrWhiteSpace(account.BlobPath))
            .ToList();

        var withHealthyBlobs = candidates
            .Where(account => GetAccountHealthScore(account) >= 2)
            .ToList();
        var withAnyHealthyBlob = candidates
            .Where(account => GetAccountHealthScore(account) >= 1)
            .ToList();

        var pool = SelectPreferredAccountPool(candidates, withHealthyBlobs, withAnyHealthyBlob, withBlobs);

        return pool
            .OrderByDescending(account =>
            {
                var updatedAt = account.UpdatedAt == default ? account.CreatedAt : account.UpdatedAt;
                var stamp = account.LastValidatedAt ?? updatedAt;
                return stamp == default ? account.CreatedAt : stamp;
            })
            .FirstOrDefault();
    }

    private static IReadOnlyList<SpotifyUserAccount> SelectPreferredAccountPool(
        IReadOnlyList<SpotifyUserAccount> candidates,
        List<SpotifyUserAccount> withHealthyBlobs,
        List<SpotifyUserAccount> withAnyHealthyBlob,
        List<SpotifyUserAccount> withBlobs)
    {
        if (withHealthyBlobs.Count > 0)
        {
            return withHealthyBlobs;
        }

        if (withAnyHealthyBlob.Count > 0)
        {
            return withAnyHealthyBlob;
        }

        if (withBlobs.Count > 0)
        {
            return withBlobs;
        }

        return candidates;
    }

    private static int GetAccountHealthScore(SpotifyUserAccount account)
    {
        var hasWebBlob = PathExists(account.WebPlayerBlobPath);
        var hasLibrespotBlob = PathExists(account.LibrespotBlobPath) || PathExists(account.BlobPath);
        if (hasWebBlob && hasLibrespotBlob)
        {
            return 2;
        }

        if (hasWebBlob || hasLibrespotBlob)
        {
            return 1;
        }

        return 0;
    }

    private static bool PathExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private bool NormalizeLegacyBlobPaths(string userId, SpotifyUserAuthState state)
    {
        if (state.Accounts.Count == 0)
        {
            return false;
        }

        var changed = false;
        var userBlobDir = GetUserBlobDir(userId);
        Directory.CreateDirectory(userBlobDir);

        foreach (var account in state.Accounts)
        {
            var blobPathResult = NormalizeBlobPath(account.BlobPath, userBlobDir);
            account.BlobPath = blobPathResult.Path;
            changed |= blobPathResult.Changed;

            var librespotPathResult = NormalizeBlobPath(account.LibrespotBlobPath, userBlobDir);
            account.LibrespotBlobPath = librespotPathResult.Path;
            changed |= librespotPathResult.Changed;

            var webPathResult = NormalizeBlobPath(account.WebPlayerBlobPath, userBlobDir);
            account.WebPlayerBlobPath = webPathResult.Path;
            changed |= webPathResult.Changed;

            var lastKnownGoodResult = NormalizeBlobPath(account.LastKnownGoodBlobPath, userBlobDir);
            account.LastKnownGoodBlobPath = lastKnownGoodResult.Path;
            changed |= lastKnownGoodResult.Changed;
        }

        return changed;
    }

    private (bool Changed, string? Path) NormalizeBlobPath(string? path, string userBlobDir)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, path);
        }

        var current = path.Trim();
        var normalized = current.Replace('\\', '/');
        var fileName = Path.GetFileName(current);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return (false, path);
        }

        var preferredPath = Path.Join(userBlobDir, fileName);
        var isLegacyWebDataPath = normalized.Contains("/DeezSpoTag.Web/Data/", StringComparison.OrdinalIgnoreCase);
        if (isLegacyWebDataPath)
        {
            try
            {
                if (File.Exists(current) && !File.Exists(preferredPath))
                {
                    File.Copy(current, preferredPath, overwrite: false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to migrate Spotify blob from legacy path {Path}", current);
            }

            if (File.Exists(preferredPath))
            {
                return (!string.Equals(current, preferredPath, StringComparison.Ordinal), preferredPath);
            }
        }

        if (!File.Exists(current) && File.Exists(preferredPath))
        {
            return (true, preferredPath);
        }

        return (false, path);
    }

    public static bool EnsureActiveAccount(SpotifyUserAuthState state)
    {
        var account = ResolveActiveAccount(state);
        if (account == null)
        {
            return false;
        }

        if (!string.Equals(state.ActiveAccount, account.Name, StringComparison.OrdinalIgnoreCase))
        {
            state.ActiveAccount = account.Name;
            return true;
        }

        return false;
    }

    public static string? ResolveActiveBlobPath(SpotifyUserAuthState state)
    {
        return ResolveActiveLibrespotBlobPath(state);
    }

    public static string? ResolveActiveLibrespotBlobPath(SpotifyUserAuthState state)
    {
        var account = ResolveActiveAccount(state);
        if (!string.IsNullOrWhiteSpace(account?.LibrespotBlobPath))
        {
            return account!.LibrespotBlobPath!;
        }

        if (string.IsNullOrWhiteSpace(account?.BlobPath))
        {
            return null;
        }

        if (LooksLikeWebPlayerBlobPath(account.BlobPath))
        {
            return null;
        }

        return account.BlobPath;
    }

    public static string? ResolveActiveWebPlayerBlobPath(SpotifyUserAuthState state)
    {
        var account = ResolveActiveAccount(state);
        if (!string.IsNullOrWhiteSpace(account?.WebPlayerBlobPath))
        {
            return account!.WebPlayerBlobPath!;
        }
        return null;
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
}

public sealed class SpotifyUserAuthState
{
    public string? ActiveAccount { get; set; }
    public List<SpotifyUserAccount> Accounts { get; set; } = new();
    public string? WebPlayerSpDc { get; set; }
    public string? WebPlayerUserAgent { get; set; }
}

public sealed class SpotifyUserAccount
{
    public string Name { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? BlobPath { get; set; }
    public string? LibrespotBlobPath { get; set; }
    public string? WebPlayerBlobPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
    public string? LastKnownGoodBlobPath { get; set; }
}
