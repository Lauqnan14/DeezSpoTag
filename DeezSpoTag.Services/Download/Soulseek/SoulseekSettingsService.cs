using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Reads and writes the Soulseek download behaviour settings and the per-folder share state.
/// </summary>
/// <remarks>
/// <para>
///     The folder tab remains the only place a folder's share enable/disable is decided. This service only
///     reads that state and exposes it; it never offers a competing toggle.
/// </para>
/// <para>
///     The slskd URL and API key are deliberately out of scope here: they live in the encrypted platform
///     auth state and never pass through <c>config.json</c>.
/// </para>
/// </remarks>
public sealed class SoulseekSettingsService
{
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<SoulseekSettingsService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekSettingsService"/> class.</summary>
    public SoulseekSettingsService(
        DeezSpoTagSettingsService settingsService,
        LibraryRepository libraryRepository,
        ILogger<SoulseekSettingsService> logger)
    {
        _settingsService = settingsService;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Reads the effective Soulseek settings.
    /// </summary>
    /// <returns>
    ///     A copy, so a caller cannot mutate persisted settings by holding the returned instance. Never
    ///     returns <see langword="null"/>; a missing section yields the documented defaults.
    /// </returns>
    public SoulseekDownloadSettings GetSettings()
        => (_settingsService.LoadSettings().Soulseek ?? new SoulseekDownloadSettings()).Clone();

    /// <summary>The Soulseek qualities enabled by Download Settings → Source.</summary>
    public IReadOnlyList<string> GetEnabledQualityCodes()
        => DownloadSourceOrder.ResolveEnabledSoulseekQualities(_settingsService.LoadSettings());

    /// <summary>
    ///     Overlays a partial update onto the persisted Soulseek settings.
    /// </summary>
    /// <remarks>
    ///     Only the members present on <paramref name="update"/> are applied, which matches how the settings
    ///     merge endpoint behaves for every other section. The result is normalized before it is saved, so a
    ///     hand-rolled API call cannot persist an out-of-range timeout or an unknown quality code.
    /// </remarks>
    public SoulseekDownloadSettings Update(SoulseekDownloadSettings update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var settings = _settingsService.LoadSettings();
        var current = settings.Soulseek ?? new SoulseekDownloadSettings();

        current.AllowUnknownQuality = update.AllowUnknownQuality;
        current.SearchTimeoutSeconds = update.SearchTimeoutSeconds;
        current.MinimumPeerUploadSpeedBytesPerSecond = update.MinimumPeerUploadSpeedBytesPerSecond;
        current.MaximumPeerQueueLength = update.MaximumPeerQueueLength;
        current.RequireFreeUploadSlot = update.RequireFreeUploadSlot;
        current.BlockedUsers = Materialize(update.BlockedUsers, current.BlockedUsers);
        current.BlockedFilenamePatterns = Materialize(update.BlockedFilenamePatterns, current.BlockedFilenamePatterns);
        current.PeerCooldownMinutes = update.PeerCooldownMinutes;
        current.SearchRetentionMinutes = update.SearchRetentionMinutes;
        current.AutoRetryIncompleteTransfers = update.AutoRetryIncompleteTransfers;
        current.AutomationEnabled = update.AutomationEnabled;
        current.UsePeerArtwork = update.UsePeerArtwork;
        current.UsePeerLyrics = update.UsePeerLyrics;

        settings.Soulseek = current;
        _settingsService.SaveSettings(settings);

        // SaveSettings runs the shared normalizer, so re-read rather than trusting the in-memory value.
        return GetSettings();
    }

    /// <summary>
    ///     Gets a value indicating whether the library database is available.
    /// </summary>
    /// <remarks>
    ///     Share state lives on folder rows, so without the library database there is nothing to read or write
    ///     and the API reports that plainly instead of pretending a folder is unshared.
    /// </remarks>
    public bool IsLibraryConfigured => _libraryRepository.IsConfigured;

    /// <summary>Reads every configured folder, shared or not.</summary>
    public async Task<IReadOnlyList<FolderDto>> GetAllFoldersAsync(CancellationToken cancellationToken = default)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return [];
        }

        return await _libraryRepository.GetFoldersAsync(cancellationToken);
    }

    /// <summary>
    ///     Reads the folders the user has enabled for Soulseek sharing.
    /// </summary>
    public async Task<IReadOnlyList<FolderDto>> GetSharedFoldersAsync(CancellationToken cancellationToken = default)
    {
        if (!_libraryRepository.IsConfigured)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Library database is not configured; Soulseek shares cannot be read.");
            }

            return [];
        }

        return await _libraryRepository.GetSoulseekSharedFoldersAsync(cancellationToken);
    }

    /// <summary>
    ///     Builds the desired share set from folder settings.
    /// </summary>
    public async Task<IReadOnlyList<SoulseekDesiredShare>> GetDesiredSharesAsync(CancellationToken cancellationToken = default)
    {
        var folders = await GetSharedFoldersAsync(cancellationToken);
        return folders
            .Select(folder => new SoulseekDesiredShare(
                folder.Id,
                folder.RootPath,
                folder.SoulseekShareAlias,
                folder.SoulseekShareInclude ?? [],
                folder.SoulseekShareExclude ?? []))
            .ToList();
    }

    /// <summary>Reads one folder's share state.</summary>
    public async Task<FolderDto?> GetFolderShareAsync(long folderId, CancellationToken cancellationToken = default)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return null;
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        return folders.FirstOrDefault(folder => folder.Id == folderId);
    }

    /// <summary>Enables or disables Soulseek sharing for one folder.</summary>
    public async Task<FolderDto?> SetFolderShareEnabledAsync(
        long folderId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return null;
        }

        return await _libraryRepository.UpdateFolderSoulseekShareEnabledAsync(folderId, enabled, cancellationToken);
    }

    /// <summary>Updates a folder's share alias and filters.</summary>
    public async Task<FolderDto?> UpdateFolderShareAsync(
        long folderId,
        string? alias,
        IReadOnlyList<string>? includeFilters,
        IReadOnlyList<string>? excludeFilters,
        CancellationToken cancellationToken = default)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return null;
        }

        var normalizedAlias = LibraryRepository.NormalizeShareAlias(alias);
        if (!string.Equals(normalizedAlias, alias?.Trim() ?? null, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(alias)
            && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Ignoring a Soulseek share alias for folder {FolderId} because it contains a path separator; slskd uses the alias to hide the local folder name.",
                folderId);
        }

        return await _libraryRepository.UpdateFolderSoulseekShareAsync(
            folderId,
            normalizedAlias,
            includeFilters,
            excludeFilters,
            cancellationToken);
    }

    /// <summary>Records a share scan outcome against one folder.</summary>
    public async Task<FolderDto?> RecordFolderShareScanAsync(
        long folderId,
        string? status,
        DateTimeOffset? scannedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return null;
        }

        return await _libraryRepository.UpdateFolderSoulseekShareScanAsync(folderId, status, scannedAtUtc, cancellationToken);
    }

    private static List<string> Materialize(List<string>? incoming, List<string>? existing)
        => incoming is null ? new List<string>(existing ?? []) : new List<string>(incoming);
}
