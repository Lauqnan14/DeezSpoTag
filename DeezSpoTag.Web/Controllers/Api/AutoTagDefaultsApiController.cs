using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using System.Globalization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/autotag/defaults")]
[ApiController]
[Authorize]
public sealed class AutoTagDefaultsApiController : ControllerBase
{
    private readonly AutoTagDefaultsStore _store;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly TaggingProfileService _profileService;
    private readonly LibraryRepository _libraryRepository;
    private readonly AutoTagService _autoTagService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<AutoTagDefaultsApiController> _logger;

    public AutoTagDefaultsApiController(
        AutoTagDefaultsStore store,
        AutoTagProfileResolutionService profileResolutionService,
        TaggingProfileService profileService,
        LibraryRepository libraryRepository,
        AutoTagService autoTagService,
        DeezSpoTagSettingsService settingsService,
        ILogger<AutoTagDefaultsApiController> logger)
    {
        _store = store;
        _profileResolutionService = profileResolutionService;
        _profileService = profileService;
        _libraryRepository = libraryRepository;
        _autoTagService = autoTagService;
        _settingsService = settingsService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        return Ok(state.Defaults);
    }

    public sealed record UpdateDefaultsRequest(
        string? DefaultFileProfile,
        Dictionary<string, string>? LibrarySchedules,
        int? RecentDownloadWindowHours,
        bool? RenameSpotifyArtistFolders);

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] UpdateDefaultsRequest request, CancellationToken cancellationToken)
    {
        var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        var previousSchedules = state.Defaults.LibrarySchedules;
        var profiles = state.Profiles;

        var requestedDefaultReference = request.DefaultFileProfile?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedDefaultReference))
        {
            var defaultProfile = await _profileService.SetDefaultProfileAsync(requestedDefaultReference);
            if (defaultProfile is null)
            {
                return BadRequest("Selected default AutoTag profile does not exist.");
            }

            state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
            profiles = state.Profiles;
        }

        var allowedScheduleFolderIds = _libraryRepository.IsConfigured
            ? state.FoldersById.Keys
                .Select(id => id.ToString(CultureInfo.InvariantCulture))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scheduleCleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.LibrarySchedules != null)
        {
            foreach (var (key, value) in request.LibrarySchedules)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var folderId = key.Trim();
                if (_libraryRepository.IsConfigured && !allowedScheduleFolderIds.Contains(folderId))
                {
                    continue;
                }

                var schedule = value?.Trim();
                if (string.IsNullOrWhiteSpace(schedule))
                {
                    continue;
                }

                scheduleCleaned[folderId] = schedule;
            }
        }

        var resolvedDefaultProfileId = profiles
            .FirstOrDefault(profile => profile.IsDefault)
            ?.Id;
        var recentDownloadWindowHours = request.RecentDownloadWindowHours
            ?? state.Defaults.RecentDownloadWindowHours
            ?? AutoTagDefaultsDto.DefaultRecentDownloadWindowHours;
        if (recentDownloadWindowHours < 0)
        {
            recentDownloadWindowHours = AutoTagDefaultsDto.DefaultRecentDownloadWindowHours;
        }
        var renameSpotifyArtistFolders = request.RenameSpotifyArtistFolders
            ?? state.Defaults.RenameSpotifyArtistFolders
            ?? true;
        var defaults = new AutoTagDefaultsDto(
            resolvedDefaultProfileId,
            scheduleCleaned,
            recentDownloadWindowHours,
            renameSpotifyArtistFolders);
        await _store.SaveAsync(defaults);

        var normalizedState = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        await SyncRuntimeSettingsFromDefaultProfileAsync(normalizedState.Profiles);
        if (!AreSchedulesEquivalent(previousSchedules, normalizedState.Defaults.LibrarySchedules))
        {
            await StopRunningEnhancementForScheduleChangeAsync();
        }

        return Ok(normalizedState.Defaults);
    }

    private async Task StopRunningEnhancementForScheduleChangeAsync()
    {
        if (!_autoTagService.TryGetRunningEnhancementJobId(out var runningEnhancementJobId)
            || string.IsNullOrWhiteSpace(runningEnhancementJobId))
        {
            return;
        }

        var stopped = await _autoTagService.StopJobAsync(runningEnhancementJobId);
        if (stopped && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Stopped running enhancement job {JobId} after schedule update.",
                runningEnhancementJobId);
        }
    }

    private static bool AreSchedulesEquivalent(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        var normalizedLeft = NormalizeScheduleMap(left);
        var normalizedRight = NormalizeScheduleMap(right);
        if (normalizedLeft.Count != normalizedRight.Count)
        {
            return false;
        }

        foreach (var (folderId, schedule) in normalizedLeft)
        {
            if (!normalizedRight.TryGetValue(folderId, out var rightSchedule)
                || !string.Equals(schedule, rightSchedule, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> NormalizeScheduleMap(IReadOnlyDictionary<string, string>? source)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return normalized;
        }

        foreach (var (rawFolderId, rawSchedule) in source)
        {
            var folderId = rawFolderId?.Trim();
            var schedule = rawSchedule?.Trim();
            if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(schedule))
            {
                continue;
            }

            normalized[folderId] = schedule;
        }

        return normalized;
    }

    private async Task SyncRuntimeSettingsFromDefaultProfileAsync(IReadOnlyList<TaggingProfile> profiles)
    {
        var profile = profiles?.FirstOrDefault(item => item.IsDefault);
        if (profile == null)
        {
            return;
        }

        var settings = _settingsService.LoadSettings();
        settings.Tags ??= new TagSettings();
        var technical = profile.Technical ?? new TechnicalTagSettings();
        var folder = profile.FolderStructure ?? new FolderStructureSettings();

        settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
        settings.Tags.UseNullSeparator = technical.UseNullSeparator;
        settings.Tags.SaveID3v1 = technical.SaveID3v1;
        settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator ?? "default";
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
        settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
        settings.AlbumVariousArtists = technical.AlbumVariousArtists;
        settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
        settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
        settings.DateFormat = technical.DateFormat ?? "Y-M-D";
        settings.FeaturedToTitle = technical.FeaturedToTitle ?? "0";
        settings.TitleCasing = technical.TitleCasing ?? "nothing";
        settings.ArtistCasing = technical.ArtistCasing ?? "nothing";
        settings.SyncedLyrics = technical.SyncedLyrics;
        settings.SaveLyrics = technical.SaveLyrics;
        settings.LrcType = technical.LrcType ?? "lyrics,syllable-lyrics,unsynced-lyrics";
        settings.LrcFormat = technical.LrcFormat ?? "both";
        settings.LyricsFallbackEnabled = technical.LyricsFallbackEnabled;
        settings.LyricsFallbackOrder = technical.LyricsFallbackOrder ?? "apple,deezer,spotify,lrclib,musixmatch";
        settings.ArtworkFallbackEnabled = technical.ArtworkFallbackEnabled;
        settings.ArtworkFallbackOrder = technical.ArtworkFallbackOrder ?? "apple,deezer,spotify";
        settings.ArtistArtworkFallbackEnabled = technical.ArtistArtworkFallbackEnabled;
        settings.ArtistArtworkFallbackOrder = technical.ArtistArtworkFallbackOrder ?? "apple,deezer,spotify";

        settings.CreateArtistFolder = folder.CreateArtistFolder;
        settings.ArtistNameTemplate = folder.ArtistNameTemplate ?? "%artist%";
        settings.CreateAlbumFolder = folder.CreateAlbumFolder;
        settings.AlbumNameTemplate = folder.AlbumNameTemplate ?? "%album%";
        settings.CreateCDFolder = folder.CreateCDFolder;
        settings.CreateStructurePlaylist = folder.CreateStructurePlaylist;
        settings.CreateSingleFolder = folder.CreateSingleFolder;
        settings.CreatePlaylistFolder = folder.CreatePlaylistFolder;
        settings.PlaylistNameTemplate = folder.PlaylistNameTemplate ?? "%playlist%";
        settings.IllegalCharacterReplacer = folder.IllegalCharacterReplacer ?? "_";

        _settingsService.SaveSettings(settings);
        await Task.CompletedTask;
    }
}
