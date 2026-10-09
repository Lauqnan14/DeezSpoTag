using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Dj;
using DeezSpoTag.Services.Library.Sonic;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services;

public sealed class MelodayOptions
{
    [JsonRequired]
    public bool Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public int ExcludePlayedDays { get; set; } = 4;
    public int HistoryLookbackDays { get; set; } = 30;
    public int MaxTracks { get; set; } = 50;
    public double HistoricalRatio { get; set; } = 0.3;
    public int SonicSimilarLimit { get; set; } = 8;
    public double SonicSimilarityDistance { get; set; } = 0.35;
    public int UpdateIntervalMinutes { get; set; } = 30;
    public string Mode { get; set; } = MelodayModes.Sonic;
    public List<MelodayScheduleSlot> Slots { get; set; } = MelodayScheduleSlots.Normalize(null);
    public List<MelodayLibrarySchedule> Libraries { get; set; } = new();
    public int MissedRunGraceMinutes { get; set; } = 60;
    public string MoodMapPath { get; set; } = "Resources/meloday/assets/moodmap.json";
    public List<string> TargetServers { get; set; } = new() { MelodayTargetServers.Plex, MelodayTargetServers.Jellyfin, MelodayTargetServers.Navidrome };

    /// <summary>
    /// How a run updates each target playlist. "match" mirrors the generated mix onto the
    /// target; "append" only adds tracks that are missing and leaves anything the user added
    /// on the server. Same append/match choice the other sync surfaces offer.
    /// </summary>
    public string TargetSyncMode { get; set; } = MelodayTargetServers.Match;
    public List<long> TargetLibraryIds { get; set; } = new();
}

public sealed record MelodayRunResult(
    bool Success,
    string Message,
    string? PlaylistId,
    string Status = "complete",
    IReadOnlyList<MelodayHistoryImportResult>? HistorySources = null);
public sealed record MelodayStatusDto(
    bool Enabled,
    string? NextSlot,
    DateTimeOffset? LastRunUtc,
    string? LastMessage,
    int MaxTracks,
    int HistoryLookbackDays,
    int ExcludePlayedDays,
    int MissedRunGraceMinutes,
    IReadOnlyList<MelodayHistoryImportResult> HistorySources);

public static class MelodayModes
{
    public const string Direct = "direct";
    public const string Sonic = "sonic";
    public const string Both = "both";

    public static string Normalize(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            Direct => Direct,
            Both => Both,
            Sonic => Sonic,
            _ => Sonic
        };
    }
}

public static class MelodayTargetServers
{
    /// <summary>
    ///     The three self-hosted servers, aliased to the one canonical definition.
    /// </summary>
    /// <remarks>
    ///     These were declared here as a second, byte-identical copy of
    ///     <see cref="MediaServerTargetServices" />, which is where the Folder tab and the
    ///     per-surface checkboxes read the same ids from. Meloday still exposes the shorter names
    ///     because its own call sites read better with them, but the value now has one definition
    ///     and a change to one cannot drift from the other.
    /// </remarks>
    public const string Plex = MediaServerTargetServices.Plex;

    public const string Jellyfin = MediaServerTargetServices.Jellyfin;

    public const string Navidrome = MediaServerTargetServices.Navidrome;

    public const string YouTubeMusic = "ytmusic";

    /// <summary>Replace the target playlist with the current mix.</summary>
    public const string Match = "match";

    /// <summary>Only add tracks that are missing, leaving anything added on the server.</summary>
    public const string Append = "append";

    public static IReadOnlyList<string> All { get; } = new[] { Plex, Jellyfin, Navidrome, YouTubeMusic };

    public static string NormalizeSyncMode(string? mode)
        => (mode ?? string.Empty).Trim().ToLowerInvariant() == Append ? Append : Match;

    public static List<string> Normalize(IEnumerable<string>? values, bool defaultToAll)
    {
        var normalized = new List<string>();
        foreach (var target in (values ?? Array.Empty<string>())
                     .Select(value => (value ?? string.Empty).Trim().ToLowerInvariant())
                     .Where(candidate => candidate is Plex or Jellyfin or Navidrome or YouTubeMusic
                         && !normalized.Contains(candidate, StringComparer.OrdinalIgnoreCase)))
        {
            normalized.Add(target);
        }

        return normalized.Count == 0 && defaultToAll
            ? All.ToList()
            : normalized;
    }
}

public sealed class MelodayCollaborators
{
    public MelodayCollaborators(
        PlexApiClient plexApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        PlaylistSyncService playlistSyncService,
        PlexHistoryImportService historyImportService,
        JellyfinHistoryImportService jellyfinHistoryImportService,
        NavidromeHistoryImportService navidromeHistoryImportService)
    {
        PlexApiClient = plexApiClient;
        AuthService = authService;
        LibraryRepository = libraryRepository;
        PlaylistSyncService = playlistSyncService;
        HistoryImportService = historyImportService;
        JellyfinHistoryImportService = jellyfinHistoryImportService;
        NavidromeHistoryImportService = navidromeHistoryImportService;
    }

    public PlexApiClient PlexApiClient { get; }
    public PlatformAuthService AuthService { get; }
    public LibraryRepository LibraryRepository { get; }
    public PlaylistSyncService PlaylistSyncService { get; }
    public PlexHistoryImportService HistoryImportService { get; }
    public JellyfinHistoryImportService JellyfinHistoryImportService { get; }
    public NavidromeHistoryImportService NavidromeHistoryImportService { get; }
}

public sealed class MelodayService
{
    private const string MelodayAppUserName = "Meloday";
    private const string MelodayAppUserId = "deezspotag:meloday";
    private readonly MelodayOptions _options;
    private readonly PlexApiClient _plexApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly PlexHistoryImportService _historyImportService;
    private readonly JellyfinHistoryImportService _jellyfinHistoryImportService;
    private readonly NavidromeHistoryImportService _navidromeHistoryImportService;
    private readonly ILogger<MelodayService> _logger;
    private readonly MelodaySettingsStore _settingsStore;
    private readonly MelodayRunStateStore _runStateStore;
    private readonly MelodayArtworkPool _artworkPool;
    private readonly MelodayArtworkAssignments _artworkAssignments;
    private readonly MelodayCoverComposer _coverComposer;
    private readonly Random _random = new();
    private readonly string _webRoot;
    private readonly IDjStrategyCatalog _djStrategyCatalog;
    private readonly MelodayDjContextBuilder _djContextBuilder;
    private readonly DjSonicAffinityResolver _djAffinityResolver;
    private readonly ISonicSimilarityService _sonicSimilarity;
    private DateTimeOffset? _lastRunUtc;
    private string? _lastMessage;
    private IReadOnlyList<MelodayHistoryImportResult> _lastImportResults = Array.Empty<MelodayHistoryImportResult>();

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex FeaturingParentheticalRegex = CreateRegex(@"(\(|\[)\s*(feat\.?|ft\.?|featuring).*?(\)|\])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeaturingInlineRegex = CreateRegex(@"\b(feat\.?|ft\.?|featuring)\s+[^\-\(\[]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record RunModeContext(
        IReadOnlyList<MediaServerTarget> TargetServers,
        LibraryDto Library,
        IReadOnlyList<long> HistoryTrackIds,
        IReadOnlyList<long> BalancedHistorical,
        SimilarTrackContext SimilarContext,
        string SlotId,
        string SlotName,
        string SlotGenerateAt,
        string WeekdayId,
        MelodayDaypart Daypart,
        string? Username,
        long MixUserId,
        PlexAuth? SonicPlex,
        MelodayDjContext? DjContext = null,
        MelodayDjResolution? DjResolution = null,
        bool UsedAllDayFallback = false);
    private static readonly Regex DashVersionRegex = CreateRegex(@"\s-\s.*(mix|dub|remix|edit|version)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingSpaceOrHyphenRegex = CreateRegex(@"[\s-]+$", RegexOptions.Compiled);
    private static readonly Regex MultiWhitespaceRegex = CreateRegex(@"\s+", RegexOptions.Compiled);
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);

    private static readonly IReadOnlyList<string> VersionKeywords = new[]
    {
        "extended", "deluxe", "remaster", "remastered", "live", "acoustic", "edit",
        "version", "anniversary", "special edition", "radio edit", "album version",
        "original mix", "remix", "mix", "dub", "instrumental", "karaoke", "cover",
        "rework", "re-edit", "bootleg", "vip", "session", "alternate", "take",
        "mix cut", "cut", "dj mix"
    };

    private static readonly int[] AllDayHours = Enumerable.Range(0, 24).ToArray();

    private sealed record MelodayPlaylistInstance(
        MelodayLibrarySchedule Library,
        MelodayScheduleSlot Slot,
        string Mode);

    public MelodayService(
        IOptions<MelodayOptions> options,
        MelodayCollaborators collaborators,
        IWebHostEnvironment env,
        ILogger<MelodayService> logger,
        MelodaySettingsStore settingsStore,
        MelodayRunStateStore runStateStore,
        MelodayArtworkPool artworkPool,
        MelodayArtworkAssignments artworkAssignments,
        MelodayCoverComposer coverComposer,
        IDjStrategyCatalog djStrategyCatalog,
        MelodayDjContextBuilder djContextBuilder,
        DjSonicAffinityResolver djAffinityResolver,
        ISonicSimilarityService sonicSimilarity)
    {
        _options = options.Value;
        _plexApiClient = collaborators.PlexApiClient;
        _authService = collaborators.AuthService;
        _libraryRepository = collaborators.LibraryRepository;
        _playlistSyncService = collaborators.PlaylistSyncService;
        _historyImportService = collaborators.HistoryImportService;
        _jellyfinHistoryImportService = collaborators.JellyfinHistoryImportService;
        _navidromeHistoryImportService = collaborators.NavidromeHistoryImportService;
        _webRoot = env.WebRootPath;
        _logger = logger;
        _settingsStore = settingsStore;
        _runStateStore = runStateStore;
        _artworkPool = artworkPool;
        _artworkAssignments = artworkAssignments;
        _coverComposer = coverComposer;
        _djStrategyCatalog = djStrategyCatalog;
        _djContextBuilder = djContextBuilder;
        _djAffinityResolver = djAffinityResolver;
        _sonicSimilarity = sonicSimilarity;
    }

    private Task<MelodayOptions> GetEffectiveOptionsAsync()
    {
        return _settingsStore.LoadAsync(_options);
    }

    /// <summary>Manual run: generate every playlist instance the current schedule asks for, today.</summary>
    public async Task<MelodayRunResult> RunAsync(bool refreshHistory, CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectiveOptionsAsync();
        effective.Mode = MelodayModes.Normalize(effective.Mode);
        if (!effective.Enabled)
        {
            _lastMessage = "Meloday disabled.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var instances = ResolvePlaylistInstances(effective);
        if (instances.Count == 0)
        {
            _lastMessage = "No Meloday schedule slots are enabled for any library.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        return await RunInstancesAsync(effective, instances, refreshHistory, DateTimeOffset.Now, cancellationToken);
    }

    /// <summary>Scheduled run: generate exactly one (library, slot, mode) instance.</summary>
    public async Task<MelodayRunResult> RunSlotAsync(
        long libraryId,
        string slotId,
        string mode,
        CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectiveOptionsAsync();
        effective.Mode = MelodayModes.Normalize(effective.Mode);
        if (!effective.Enabled)
        {
            _lastMessage = "Meloday disabled.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var instance = ResolvePlaylistInstances(effective).FirstOrDefault(candidate =>
            candidate.Library.LibraryId == libraryId
            && string.Equals(candidate.Slot.Id, MelodayScheduleSlots.NormalizeSlotId(slotId), StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Mode, MelodayModes.Normalize(mode), StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            _lastMessage = $"Meloday playlist {MelodayScheduleSlots.SlotIdForMix(libraryId, slotId, mode, MelodayScheduleSlots.WeekdayIdFromLocal(DateTimeOffset.Now))} is no longer scheduled.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        return await RunInstancesAsync(effective, new[] { instance }, refreshHistory: true, DateTimeOffset.Now, cancellationToken);
    }

    private static List<MelodayPlaylistInstance> ResolvePlaylistInstances(MelodayOptions effective)
    {
        var instances = new List<MelodayPlaylistInstance>();
        foreach (var library in effective.Libraries)
        {
            if (!library.IsTargeted)
            {
                continue;
            }

            foreach (var slotId in library.SlotIds)
            {
                var slot = effective.Slots.FirstOrDefault(candidate => string.Equals(
                    candidate.Id,
                    slotId,
                    StringComparison.OrdinalIgnoreCase));
                if (slot is null)
                {
                    continue;
                }

                foreach (var mode in ResolveRunModes(library.Mode))
                {
                    instances.Add(new MelodayPlaylistInstance(library, slot, mode));
                }
            }
        }

        return instances;
    }

    private async Task<MelodayRunResult> RunInstancesAsync(
        MelodayOptions effective,
        IReadOnlyList<MelodayPlaylistInstance> instances,
        bool refreshHistory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var selectedServers = MelodayTargetServers.Normalize(effective.TargetServers, defaultToAll: true);
        var auth = await _authService.LoadAsync();
        var targets = ResolveTargetServers(auth, selectedServers);
        if (targets.Count == 0)
        {
            _lastMessage = "Selected Meloday target server auth is missing.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        if (refreshHistory)
        {
            var importResults = new List<MelodayHistoryImportResult>();
            if (selectedServers.Contains(MelodayTargetServers.Plex, StringComparer.OrdinalIgnoreCase))
            {
                importResults.Add(await _historyImportService.ImportDetailedAsync(cancellationToken));
            }
            if (selectedServers.Contains(MelodayTargetServers.Jellyfin, StringComparer.OrdinalIgnoreCase))
            {
                importResults.Add(await _jellyfinHistoryImportService.ImportDetailedAsync(cancellationToken));
            }
            if (selectedServers.Contains(MelodayTargetServers.Navidrome, StringComparer.OrdinalIgnoreCase))
            {
                importResults.Add(await _navidromeHistoryImportService.ImportDetailedAsync(cancellationToken));
            }

            _lastImportResults = importResults;
            var configuredImports = importResults.Where(static result => result.Configured).ToList();
            if (configuredImports.Count > 0 && configuredImports.All(static result => !result.Available))
            {
                _lastMessage = "Meloday history refresh is blocked because every configured media server is unavailable.";
                return new MelodayRunResult(false, _lastMessage, null, "blocked", importResults);
            }
        }

        await _libraryRepository.BackfillPlayHistoryLibraryIdsAsync(cancellationToken);
        await _libraryRepository.DeleteLegacyMelodayMixesAsync(cancellationToken);

        var scheduledLibraryIds = instances.Select(static instance => instance.Library.LibraryId).ToHashSet();
        var configuredFolders = new List<FolderDto>();
        foreach (var folder in await _libraryRepository.GetConfiguredEnabledMusicFoldersAsync(cancellationToken))
        {
            if (!folder.LibraryId.HasValue || !scheduledLibraryIds.Contains(folder.LibraryId.Value))
            {
                continue;
            }

            var tracks = await _libraryRepository.GetTrackIdsForLibraryScopeAsync(
                folder.LibraryId.Value, folder.Id, cancellationToken);
            if (tracks.Count > 0)
            {
                configuredFolders.Add(folder);
            }
        }

        await _libraryRepository.DeleteInactiveMelodayMixesAsync(
            ResolvePlaylistInstances(effective)
                .SelectMany(static instance => MelodayScheduleSlots.MixIdsForScheduledPlaylist(
                    instance.Library.LibraryId,
                    instance.Slot.Id,
                    instance.Mode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            cancellationToken);
        if (configuredFolders.Count == 0)
        {
            _lastMessage = "No selected nonempty configured music libraries were found.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var historyUserIds = new List<long>();
        foreach (var target in targets)
        {
            var userId = await EnsureHistoryUserAsync(target, cancellationToken);
            if (!historyUserIds.Contains(userId))
            {
                historyUserIds.Add(userId);
            }
        }

        var mixUserId = await EnsureMelodayAppUserAsync(cancellationToken);
        var username = ResolveMelodayDisplayUsername(targets);

        var lookbackStart = now.AddDays(-effective.HistoryLookbackDays);
        var excludeStart = now.AddDays(-effective.ExcludePlayedDays);

        var sonicPlex = targets.FirstOrDefault(static target => target.IsPlex)?.Plex;
        var results = new List<MelodayRunResult>();
        foreach (var libraryGroup in instances.GroupBy(static instance => instance.Library.LibraryId))
        {
            var library = libraryGroup.First().Library;
            var libraryFolders = configuredFolders
                .Where(folder => folder.LibraryId == library.LibraryId)
                .ToList();
            var excludedTrackIds = new HashSet<long>();
            foreach (var historyUserId in historyUserIds)
            {
                foreach (var folder in libraryFolders)
                {
                    excludedTrackIds.UnionWith(await _libraryRepository.GetPlayedTrackIdsSinceAsync(
                        historyUserId, library.LibraryId, excludeStart, cancellationToken, folder.Id));
                }
            }

            var allowedTrackIds = new HashSet<long>();
            foreach (var folder in libraryFolders)
            {
                allowedTrackIds.UnionWith(await _libraryRepository.GetTrackIdsForLibraryScopeAsync(
                    library.LibraryId, folder.Id, cancellationToken));
            }

            var similarContext = new SimilarTrackContext(
                new Dictionary<long, string>(),
                excludedTrackIds,
                excludeStart,
                sonicPlex,
                effective,
                new Dictionary<long, PlexTrackMetadata>(),
                allowedTrackIds,
                cancellationToken);

            foreach (var instance in libraryGroup)
            {
                results.Add(await RunInstanceAsync(
                    instance,
                    library,
                    libraryFolders,
                    historyUserIds,
                    lookbackStart,
                    excludeStart,
                    now,
                    effective,
                    targets,
                    username,
                    mixUserId,
                    sonicPlex,
                    similarContext,
                    cancellationToken));
            }
        }

        var successful = results.Where(static result => result.Success).ToList();
        if (successful.Count == 0)
        {
            _lastMessage = string.Join(" ", results.Select(static result => result.Message).Where(static message => !string.IsNullOrWhiteSpace(message)));
            return new MelodayRunResult(false, string.IsNullOrWhiteSpace(_lastMessage) ? "Meloday failed." : _lastMessage, null);
        }

        _lastRunUtc = DateTimeOffset.UtcNow;
        _lastMessage = $"Generated {successful.Count} of {results.Count} Meloday playlists across {libraryGroupCount(instances)} {(libraryGroupCount(instances) == 1 ? "library" : "libraries")}.";
        if (successful.Count < results.Count)
        {
            var failureMessages = results
                .Where(static result => !result.Success)
                .Select(static result => result.Message)
                .Where(static message => !string.IsNullOrWhiteSpace(message));
            _lastMessage += $" {string.Join(" ", failureMessages)}";
        }
        var targetSyncWarnings = successful
            .Where(static result => string.IsNullOrWhiteSpace(result.PlaylistId))
            .Select(static result => result.Message)
            .Where(static message => !string.IsNullOrWhiteSpace(message));
        if (targetSyncWarnings.Any())
        {
            _lastMessage += $" {string.Join(" ", targetSyncWarnings)}";
        }

        var firstPlaylistId = successful
            .Select(static result => result.PlaylistId)
            .FirstOrDefault(static playlistId => !string.IsNullOrWhiteSpace(playlistId));
        var endpointUnavailable = _lastImportResults.Any(static result => result.Configured
            && !string.Equals(result.EndpointStatus, "available", StringComparison.OrdinalIgnoreCase));
        var mappingDegraded = _lastImportResults.Any(static result => result.Configured
            && string.Equals(result.MappingStatus, "degraded", StringComparison.OrdinalIgnoreCase));
        var degraded = endpointUnavailable || mappingDegraded;
        if (degraded)
        {
            _lastMessage += endpointUnavailable
                ? " History refresh has unavailable source endpoints; see source diagnostics."
                : " History refresh has unresolved local mappings; see source diagnostics.";
        }
        return new MelodayRunResult(
            true,
            _lastMessage,
            firstPlaylistId,
            degraded ? "degraded" : "complete",
            _lastImportResults);

        static int libraryGroupCount(IReadOnlyList<MelodayPlaylistInstance> list)
            => list.Select(static instance => instance.Library.LibraryId).Distinct().Count();
    }

    private async Task<MelodayRunResult> RunInstanceAsync(
        MelodayPlaylistInstance instance,
        MelodayLibrarySchedule librarySchedule,
        List<FolderDto> libraryFolders,
        IReadOnlyList<long> historyUserIds,
        DateTimeOffset lookbackStart,
        DateTimeOffset excludeStart,
        DateTimeOffset now,
        MelodayOptions effective,
        IReadOnlyList<MediaServerTarget> targets,
        string? username,
        long mixUserId,
        PlexAuth? sonicPlex,
        SimilarTrackContext similarContext,
        CancellationToken cancellationToken)
    {
        var libraryId = librarySchedule.LibraryId;
        var daypartHours = MelodayScheduleMath.DaypartHours(effective.Slots, instance.Slot.Id);
        var isFullDayWindow = daypartHours.Count == 24;
        var history = new List<PlayHistoryEntryDto>();
        foreach (var historyUserId in historyUserIds)
        {
            foreach (var folder in libraryFolders)
            {
                var userHistory = await _libraryRepository.GetPlayHistoryEntriesAsync(
                    historyUserId, libraryId, lookbackStart, daypartHours, now,
                    cancellationToken, folder.Id, now.Offset);
                history.AddRange(userHistory);
            }
        }

        var historyTrackIds = history
            .Select(entry => entry.TrackId)
            .Where(trackId => !similarContext.ExcludedTrackIds.Contains(trackId))
            .Distinct()
            .ToList();
        var usedAllDayFallback = false;
        if (historyTrackIds.Count == 0 && !isFullDayWindow)
        {
            usedAllDayFallback = true;
            history.Clear();
            foreach (var historyUserId in historyUserIds)
            {
                foreach (var folder in libraryFolders)
                {
                    var allDayHistory = await _libraryRepository.GetPlayHistoryEntriesAsync(
                        historyUserId, libraryId, lookbackStart, AllDayHours, now,
                        cancellationToken, folder.Id, now.Offset);
                    history.AddRange(allDayHistory);
                }
            }

            historyTrackIds = history
                .Select(entry => entry.TrackId)
                .Where(trackId => !similarContext.ExcludedTrackIds.Contains(trackId))
                .Distinct()
                .ToList();
            _logger.LogInformation(
                "Meloday found no eligible {SlotName} history for library {LibraryId}; exact-folder all-day fallback resolved {HistoryTrackCount} tracks.",
                instance.Slot.Name,
                libraryId,
                historyTrackIds.Count);
        }

        var historyAnalyses = await _libraryRepository.GetTrackAnalysisByTrackIdsAsync(
            historyTrackIds.Where(similarContext.AllowedTrackIds.Contains).ToList(),
            cancellationToken);
        var historyGenresByTrackId = historyAnalyses.Values
            .Where(IsCompletedAnalysis)
            .Select(analysis => (analysis.TrackId, Genres: ResolveAnalysisGenres(analysis)))
            .Where(static entry => entry.Genres.Count > 0)
            .ToDictionary(static entry => entry.TrackId, static entry => entry.Genres);
        var balancedHistorical = BuildBalancedHistoricalSelection(
            history,
            similarContext.ExcludedTrackIds,
            historyGenresByTrackId,
            effective.MaxTracks);
        var runModeContext = new RunModeContext(
            targets,
            new LibraryDto(libraryId, libraryFolders.Select(static folder => folder.LibraryName).FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name)) ?? $"Library {libraryId}"),
            historyTrackIds,
            balancedHistorical,
            similarContext,
            instance.Slot.Id,
            instance.Slot.Name,
            instance.Slot.GenerateAt,
            MelodayScheduleSlots.WeekdayIdFromLocal(now),
            new MelodayDaypart(daypartHours, MelodayScheduleSlots.SlotPhrase(instance.Slot.Id)),
            username,
            mixUserId,
            sonicPlex);

        // One resolution for the whole time occasion. The key deliberately omits the
        // mode, so Direct and Sonic of one slot get the same DJ and the mode
        // comparison is not confounded by a second random variable.
        var djContext = await BuildDjContextAsync(
            libraryId,
            runModeContext.Library.Name,
            instance.Slot,
            runModeContext.WeekdayId,
            daypartHours,
            usedAllDayFallback,
            history,
            similarContext,
            cancellationToken);
        var djResolution = MelodayDjResolver.Resolve(
            librarySchedule.DjSelection,
            djContext,
            _djStrategyCatalog);

        if (djResolution.FallbackDiagnostic is not null)
        {
            _logger.LogInformation(
                "Meloday DJ fallback for library {LibraryId}/{SlotId}: {Diagnostic}",
                libraryId,
                instance.Slot.Id,
                djResolution.FallbackDiagnostic);
        }

        var result = await RunModeAsync(
            instance.Mode,
            runModeContext with
            {
                DjContext = djContext,
                DjResolution = djResolution,
                UsedAllDayFallback = usedAllDayFallback,
            },
            cancellationToken);
        if (result.Success)
        {
            await _runStateStore.SetAsync(
                MelodayRunStateStore.Key(libraryId, instance.Slot.Id, instance.Mode),
                new MelodayRunStateEntry(
                    DateOnly.FromDateTime(DateTimeOffset.Now.DateTime).ToString("o"),
                    DateTimeOffset.UtcNow,
                    "complete"),
                cancellationToken);
        }

        return result;
    }


    /// <summary>
    /// How much wider than the playlist a DJ's candidate pool is.
    /// </summary>
    /// <remarks>
    /// A DJ choosing from exactly <c>MaxTracks</c> candidates has no choice left, and
    /// would be reduced to reordering what Mode already decided. Three times the
    /// playlist is enough to give each strategy room without making the qualifying
    /// query meaningfully more expensive.
    /// </remarks>
    private const int DjCandidatePoolHeadroom = 3;

    /// <summary>
    /// Writes what a DJ did, so the playlist can be explained later.
    /// </summary>
    /// <remarks>
    /// Never fails the run. The playlist has already been generated and published by
    /// this point, and refusing to record provenance would throw away a good playlist
    /// because an explanation of it could not be written.
    /// </remarks>
    private async Task RecordDjGenerationAsync(
        RunModeContext context,
        string mode,
        long mixCacheId,
        DjPlaylistOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = SonicModelIdentity.Current;
            await _libraryRepository.UpsertMelodayGenerationAsync(
                new LibraryRepository.MelodayGenerationUpsertInput(
                    mixCacheId,
                    BuildMelodayMixId(context.Library.Id, context.SlotId, mode, context.WeekdayId),
                    context.Library.Id,
                    context.SlotId,
                    context.WeekdayId,
                    MelodayModes.Normalize(mode),
                    outcome.ConfiguredDj,
                    outcome.ResolvedDj,
                    outcome.WasRandom,
                    outcome.OccurrenceKey,
                    outcome.ContextSource,
                    identity.EmbeddingVersion,
                    context.DjContext?.SonicCoveragePercent,
                    outcome.SeedSummary,
                    outcome.Candidates.Count,
                    outcome.Diagnostics,
                    null,
                    DateTimeOffset.UtcNow),
                outcome.Candidates
                    .Select((candidate, index) => new LibraryRepository.MelodayGenerationItemUpsertInput(
                        index,
                        candidate.TrackId > 0 ? candidate.TrackId : null,
                        candidate.Similarity,
                        candidate.Reason,
                        ResolveRelatedSeedId(outcome.SeedAffinities, candidate.TrackId)))
                    .ToList(),
                cancellationToken);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Failed to record Meloday DJ provenance for {MixId}.", context.SlotId);
        }
    }

    /// <summary>
    /// The seed a track was measured against, when it was measured against exactly one.
    /// </summary>
    /// <remarks>
    /// Null when the strategy did not name one, or when several seeds were equally
    /// close. Picking one arbitrarily would invent a provenance the strategy did not
    /// assert, and an honest null is more useful than a confident wrong answer.
    /// </remarks>
    private static long? ResolveRelatedSeedId(
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> seedAffinities,
        long trackId)
    {
        if (!seedAffinities.TryGetValue(trackId, out var affinities) || affinities.Count != 1)
        {
            return null;
        }

        // Exactly one seed affinity exists here, so single it out instead of looping.
        return affinities.Keys.First();
    }


    /// <summary>
    /// Explains who built this playlist and on what evidence.
    /// </summary>
    /// <remarks>
    /// The context-source line is not decoration. Meloday falls back to all-day history
    /// when a slot has no eligible history of its own, and without this the playlist
    /// would claim to be an evening selection while having been built from every hour of
    /// the day.
    /// </remarks>
    private static string AppendDjProvenance(string description, string mode, DjPlaylistOutcome outcome)
    {
        var lines = new List<string>
        {
            $"Time context: {outcome.ContextSource}",
            $"Configured DJ: {(outcome.WasRandom ? MelodayDjSelections.Random : outcome.ConfiguredDj)}",
            $"Resolved DJ: {outcome.ResolvedDj}",
            $"Mode: {MelodayModes.Normalize(mode)}",
        };

        if (!string.IsNullOrWhiteSpace(outcome.SeedSummary))
        {
            lines.Add($"Seeds: {outcome.SeedSummary}");
        }

        foreach (var diagnostic in outcome.Diagnostics.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            lines.Add(diagnostic);
        }

        return description + "\n\n" + string.Join("\n", lines);
    }


    private sealed record DjPlaylistOutcome(
        List<long> OrderedTrackIds,
        IReadOnlyList<DjCandidate> Candidates,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>> SeedAffinities,
        string ResolvedDj,
        string ConfiguredDj,
        bool WasRandom,
        string OccurrenceKey,
        string ContextSource,
        string? SeedSummary,
        IReadOnlyList<string> Diagnostics);

    /// <summary>
    /// Runs one DJ over this mode's candidate pool.
    /// </summary>
    /// <returns>
    /// Null when no DJ applies, which is the signal to use the mode's own ordering. A DJ
    /// that runs but produces nothing is <em>not</em> null: it is reported, because a
    /// silently empty playlist would be indistinguishable from a library with no music.
    /// </returns>
    private async Task<DjPlaylistOutcome?> TryBuildDjPlaylistAsync(
        string mode,
        bool isDirect,
        RunModeContext context,
        CancellationToken cancellationToken)
    {
        var resolution = context.DjResolution;
        var djContext = context.DjContext;
        if (resolution is null || djContext is null || resolution.ResolvedDj.Length == 0)
        {
            return null;
        }

        var descriptor = _djStrategyCatalog.GetById(resolution.ResolvedDj);
        if (descriptor is null)
        {
            return null;
        }

        var maxTracks = context.SimilarContext.Options.MaxTracks;

        // Mode's contribution: the qualified pool, and only that. Direct and Sonic differ
        // here exactly as they always did — Sonic keeps its Plex priority rerank — so the
        // two modes remain comparable under one DJ.
        var candidatePool = await BuildVibeDrivenTrackSelectionAsync(
            context.HistoryTrackIds,
            context.BalancedHistorical,
            context.Library.Id,
            prioritizePlexSonicMatches: !isDirect,
            context.SimilarContext,
            maxTracks * DjCandidatePoolHeadroom);

        if (candidatePool.Count == 0)
        {
            return null;
        }

        // The pool is this mode's, so it is stamped onto a copy. A strategy must not be
        // able to see or influence what the other mode considered.
        var scopedContext = djContext with { EligibleTrackIds = candidatePool };

        // The descriptor's own bounds, folded the same way DjDefinitionDto folds them so
        // a DJ with an inverted range contributes tracks instead of nothing.
        var djMin = Math.Max(0, Math.Min(descriptor.MinTracks, descriptor.MaxTracks));
        var trackCount = Math.Clamp(maxTracks, djMin, Math.Max(djMin, descriptor.MaxTracks));
        var affinities = await _djAffinityResolver.ResolveAsync(
            context.Library.Id,
            scopedContext.Seeds,
            candidatePool,
            trackCount,
            cancellationToken);

        var result = descriptor.Strategy.BuildPlaylist(new DjStrategyRequest
        {
            Definition = descriptor.ToDefinition(),
            Seeds = scopedContext.Seeds,
            CandidateTrackIds = candidatePool,
            TrackCount = trackCount,
            SonicCoveragePercent = scopedContext.SonicCoveragePercent,
            OccurrenceKey = scopedContext.OccurrenceKey,
            SeedAffinities = affinities,
        });

        return new DjPlaylistOutcome(
            result.Candidates
                .Select(static candidate => candidate.TrackId)
                .Where(static trackId => trackId > 0)
                .Distinct()
                .Take(maxTracks)
                .ToList(),
            result.Candidates,
            affinities,
            resolution.ResolvedDj,
            resolution.ConfiguredDj,
            resolution.WasRandom,
            resolution.OccurrenceKey,
            scopedContext.ContextSource,
            result.SeedSummary,
            result.Diagnostics);
    }

    /// <summary>
    /// Builds what a DJ knows about this time slot, before any mode has narrowed
    /// anything.
    /// </summary>
    /// <remarks>
    /// The candidate pool is left empty on purpose. It is the mode's output, it differs
    /// per mode, and a strategy must not be able to influence what the mode considered —
    /// so each mode stamps its own pool onto a copy of this context once it has one.
    /// Eligibility only needs seeds, which is why Random DJ can be resolved before any
    /// mode runs.
    /// </remarks>
    private async Task<MelodayDjContext> BuildDjContextAsync(
        long libraryId,
        string libraryName,
        MelodayScheduleSlot slot,
        string weekdayId,
        IReadOnlyList<int> daypartHours,
        bool usedAllDayFallback,
        IReadOnlyList<PlayHistoryEntryDto> history,
        SimilarTrackContext similarContext,
        CancellationToken cancellationToken)
    {
        var analyses = await _libraryRepository.GetTrackAnalysisByTrackIdsAsync(
            similarContext.AllowedTrackIds.ToList(),
            cancellationToken);

        var context = _djContextBuilder.Build(
            libraryId,
            libraryName,
            slot.Id,
            slot.Name,
            weekdayId,
            daypartHours,
            usedAllDayFallback,
            history,
            similarContext.ExcludedTrackIds,
            Array.Empty<long>(),
            analyses.ToDictionary(static entry => entry.Key, static entry => entry.Value),
            new HashSet<long>(),
            await ResolveSonicCoveragePercentAsync(libraryId, cancellationToken),
            DjOccurrenceKey.ForOccurrence(libraryId, slot.Id, DateOnly.FromDateTime(DateTimeOffset.Now.DateTime)));

        // Second phase: only the seeds need to know whether they have a vector, and
        // there are at most MaxSeeds of them. Probing the whole library would issue a
        // query per track to answer a question about sixty.
        var embedded = await ResolveEmbeddedSeedIdsAsync(context.Seeds, cancellationToken);

        return context with
        {
            EmbeddedTrackIds = embedded,
            Seeds = context.Seeds
                .Select(seed => seed with { HasEmbedding = embedded.Contains(seed.TrackId) })
                .ToList(),
        };
    }

    /// <summary>
    /// How much of the library has sonic vectors, as a percentage.
    /// </summary>
    /// <remarks>
    /// Advisory only. A DJ that cannot be told the coverage should still build a
    /// playlist and report low coverage in its diagnostics, rather than Meloday refusing
    /// to generate because one aggregate count was unavailable.
    /// </remarks>
    private async Task<double> ResolveSonicCoveragePercentAsync(long libraryId, CancellationToken cancellationToken)
    {
        try
        {
            var coverage = await _sonicSimilarity.GetCoverageAsync(libraryId, cancellationToken);
            return coverage.TotalTracks <= 0
                ? 0d
                : Math.Round(coverage.TracksWithEmbedding * 100d / coverage.TotalTracks, 1);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Sonic coverage unavailable for library {LibraryId}.", libraryId);
            return 0d;
        }
    }

    private async Task<IReadOnlySet<long>> ResolveEmbeddedSeedIdsAsync(
        IReadOnlyList<DjSeed> seeds,
        CancellationToken cancellationToken)
    {
        if (seeds.Count == 0)
        {
            return new HashSet<long>();
        }

        try
        {
            var identity = SonicModelIdentity.Current;
            return await _libraryRepository.GetSonicEmbeddedTrackIdsAsync(
                seeds.Select(static seed => seed.TrackId).ToList(),
                identity.ModelId,
                identity.ModelVersion,
                identity.EmbeddingVersion,
                cancellationToken);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // HasEmbedding = false is the honest answer: the strategy then plays its
            // seeds alone and says so, which is what it is built to do.
            _logger.LogWarning(ex, "Sonic embedding lookup failed for Meloday DJ seeds; treating them as unembedded.");
            return new HashSet<long>();
        }
    }

    private async Task<MelodayRunResult> RunModeAsync(
        string mode,
        RunModeContext context,
        CancellationToken cancellationToken)
    {
        // Mode answers one question — which tracks are reasonable candidates — and a DJ
        // answers the other: which of those form this playlist, and in what order.
        //
        // The division is the point. Running the mode's own ordering after a DJ would
        // let it undo the DJ's work: a Journey that deliberately arcs across the
        // playlist would be handed straight back to a greedy nearest-neighbour walk,
        // and the arc would never reach the server. So when a DJ is active its order is
        // final, and OrderTracksDirect / OrderTracksSonicAsync are not called at all.
        //
        // With no DJ the previous behaviour runs unchanged, in the same order, with the
        // same inputs. That path is not a fallback bolted on afterwards; it is the
        // original code path.
        var isDirect = string.Equals(mode, MelodayModes.Direct, StringComparison.OrdinalIgnoreCase);
        var djOutcome = await TryBuildDjPlaylistAsync(mode, isDirect, context, cancellationToken);

        IReadOnlyList<long> orderedTrackIds;
        if (djOutcome is not null)
        {
            orderedTrackIds = djOutcome.OrderedTrackIds;
        }
        else
        {
            var finalTracks = isDirect
                ? await BuildDirectTrackSelectionAsync(context.HistoryTrackIds, context.BalancedHistorical, context.Library.Id, context.SimilarContext)
                : await BuildSonicTrackSelectionAsync(context.HistoryTrackIds, context.BalancedHistorical, context.Library.Id, context.SimilarContext);

            if (finalTracks.Count == 0)
            {
                return new MelodayRunResult(false, $"No tracks available for {context.Library.Name} Meloday {GetModeLabel(mode)}.", null);
            }

            var selectedTrackIds = finalTracks.Take(context.SimilarContext.Options.MaxTracks).ToList();
            orderedTrackIds = isDirect
                ? OrderTracksDirect(selectedTrackIds, context.Daypart, context.SimilarContext.LiveMetadataByTrackId)
                : await OrderTracksSonicAsync(
                    selectedTrackIds,
                    context.Daypart,
                    context.SonicPlex,
                    context.SimilarContext.Options,
                    context.SimilarContext.RatingKeyByTrackId,
                    context.SimilarContext.LiveMetadataByTrackId,
                    cancellationToken);
        }

        if (orderedTrackIds.Count == 0)
        {
            return new MelodayRunResult(false, $"No tracks available for {context.Library.Name} Meloday {GetModeLabel(mode)}.", null);
        }

        var persistedMetadata = (await _libraryRepository.GetPlexTrackMetadataAsync(orderedTrackIds, cancellationToken))
            .ToDictionary(entry => entry.TrackId);
        var trackAnalyses = await _libraryRepository.GetTrackAnalysisByTrackIdsAsync(
            orderedTrackIds,
            cancellationToken);

        var title = MelodayScheduleSlots.PlaylistName(context.Library.Name, context.SlotName, mode, context.WeekdayId);
        var playlistText = BuildTitleAndDescription(new PlaylistDescriptionContext(
            context.SimilarContext.Options,
            context.SlotId,
            context.SlotName,
            context.SlotGenerateAt,
            context.Daypart,
            orderedTrackIds,
            context.SimilarContext.LiveMetadataByTrackId,
            persistedMetadata,
            trackAnalyses,
            context.Username,
            DateTimeOffset.Now));
        // The DJ goes in the description and the persisted metadata, never the title.
        //
        // The title is the playlist's identity as a person sees it on the server, and a
        // Random DJ changes weekly. Naming it would rename "Tuesday Evening" every week,
        // and although app-side renaming does not break provider tracking, a playlist
        // whose name flickers between performers is harder to recognise than one whose
        // description explains who filled it.
        var description = djOutcome is null
            ? playlistText.Description
            : AppendDjProvenance(playlistText.Description, mode, djOutcome);

        // The artwork is deliberately left alone. Its filename is a content hash, so
        // adding the DJ to the overlay would mint a new JPEG every time Random moved and
        // nothing prunes images/meloday/generated, turning a weekly detail into permanent
        // disk growth.
        var cover = await TryGenerateCoverAsync(
            context.SimilarContext.Options,
            context.SlotName,
            context.SlotId,
            context.Library.Id,
            mode,
            context.WeekdayId,
            playlistText.CoverTagline,
            cancellationToken);
        var coverUrls = string.IsNullOrWhiteSpace(cover?.FilePath)
            ? Array.Empty<string>()
            : new[] { $"/images/meloday/generated/{Path.GetFileName(cover.FilePath)}" };
        var mixCacheId = await _libraryRepository.UpsertMixCacheAsync(
            new LibraryRepository.MixCacheUpsertInput(
                BuildMelodayMixId(context.Library.Id, context.SlotId, mode, context.WeekdayId),
                context.MixUserId,
                context.Library.Id,
                title,
                description,
                coverUrls,
                orderedTrackIds.Count,
                DateTimeOffset.UtcNow,
                ResolveNextOccurrenceUtc(context.SlotGenerateAt, DateTimeOffset.UtcNow)),
            cancellationToken);
        await _libraryRepository.ReplaceMixItemsAsync(mixCacheId, orderedTrackIds, cancellationToken);
        if (djOutcome is not null)
        {
            await RecordDjGenerationAsync(context, mode, mixCacheId, djOutcome, cancellationToken);
        }
        var mixTracks = await _libraryRepository.GetMixTracksAsync(mixCacheId, cancellationToken);
        var existingPlaylistIds = await _libraryRepository.GetMixSyncPlaylistIdsAsync(mixCacheId, cancellationToken);
        var syncResult = await _playlistSyncService.SyncGeneratedLocalPlaylistAsync(
            new PlaylistSyncService.GeneratedLocalPlaylistSyncRequest(
                title,
                description,
                title,
                mixTracks,
                context.TargetServers.Select(static target => target.Service).ToList(),
                cover?.FilePath,
                cover?.ContentType,
                cover?.Url,
                ExistingPlaylistIds: existingPlaylistIds,
                AppendMissingOnly: MelodayTargetServers.NormalizeSyncMode(context.SimilarContext.Options.TargetSyncMode) == MelodayTargetServers.Append),
            cancellationToken);
        foreach (var target in syncResult.Targets)
        {
            if (!target.Success || string.IsNullOrWhiteSpace(target.PlaylistId))
            {
                continue;
            }

            await _libraryRepository.UpsertMixSyncAsync(mixCacheId, target.Service, target.PlaylistId, cancellationToken);
        }
        if (!syncResult.Success)
        {
            return new MelodayRunResult(true, $"{context.Library.Name} {MelodayScheduleSlots.WeekdayDisplayName(context.WeekdayId)} {context.SlotName} Meloday {GetModeLabel(mode)} was created in the app but was not synced to any target server. {syncResult.Message}", null);
        }

        return new MelodayRunResult(true, $"{context.Library.Name} {MelodayScheduleSlots.WeekdayDisplayName(context.WeekdayId)} {context.SlotName} Meloday {GetModeLabel(mode)} playlist updated. {syncResult.Message}", syncResult.FirstPlaylistId);
    }

    /// <summary>
    /// The concrete modes a configured mode expands to.
    ///
    /// <para>"both" is a configuration value, not a runnable mode: it names two
    /// independent instances that differ in how they pick and order tracks. Anything
    /// that needs one concrete mode per generation — the scheduler above all — must
    /// expand through here rather than passing the configured value straight down,
    /// or "both" reaches RunSlotAsync, matches no instance, and silently generates
    /// nothing.</para>
    /// </summary>
    internal static string[] ResolveRunModes(string mode)
    {
        return MelodayModes.Normalize(mode) switch
        {
            MelodayModes.Direct => new[] { MelodayModes.Direct },
            MelodayModes.Both => new[] { MelodayModes.Direct, MelodayModes.Sonic },
            _ => new[] { MelodayModes.Sonic }
        };
    }

    private static string GetModeLabel(string mode)
    {
        return string.Equals(mode, MelodayModes.Direct, StringComparison.OrdinalIgnoreCase) ? "Direct" : "Sonic";
    }

    private static string BuildMelodayMixId(long libraryId, string slotId, string mode, string weekdayId)
        => MelodayScheduleSlots.SlotIdForMix(libraryId, slotId, mode, weekdayId);

    private static DateTimeOffset ResolveNextOccurrenceUtc(string generateAt, DateTimeOffset nowUtc)
    {
        var minutes = MelodayScheduleSlots.TryParseMinutes(generateAt);
        if (minutes is null)
        {
            return nowUtc.AddDays(7);
        }

        var local = DateTimeOffset.Now;
        var next = new DateTimeOffset(local.Year, local.Month, local.Day, minutes.Value / 60, minutes.Value % 60, 0, local.Offset)
            .AddDays(7);
        return next.ToUniversalTime();
    }

    private static IReadOnlyList<MediaServerTarget> ResolveTargetServers(
        PlatformAuthState auth,
        IReadOnlyCollection<string>? selectedServers = null)
    {
        var selected = MelodayTargetServers.Normalize(selectedServers, defaultToAll: true);
        var targets = new List<MediaServerTarget>();
        if (selected.Contains(MelodayTargetServers.Plex, StringComparer.OrdinalIgnoreCase)
            && TryGetPlexConnection(auth.Plex, out var plex, out _, out _))
        {
            var username = !string.IsNullOrWhiteSpace(plex.Username) ? plex.Username : plex.ServerName;
            targets.Add(new MediaServerTarget(MelodayTargetServers.Plex, plex, null, null, username));
        }

        if (selected.Contains(MelodayTargetServers.Jellyfin, StringComparer.OrdinalIgnoreCase)
            && auth.Jellyfin is { } jellyfin
            && !string.IsNullOrWhiteSpace(jellyfin.Url)
            && !string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            && !string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            var username = !string.IsNullOrWhiteSpace(jellyfin.Username) ? jellyfin.Username : jellyfin.ServerName;
            targets.Add(new MediaServerTarget(MelodayTargetServers.Jellyfin, null, jellyfin, null, username));
        }

        if (selected.Contains(MelodayTargetServers.Navidrome, StringComparer.OrdinalIgnoreCase)
            && auth.Navidrome is { } navidrome
            && !string.IsNullOrWhiteSpace(navidrome.Url)
            && !string.IsNullOrWhiteSpace(navidrome.Username)
            && !string.IsNullOrWhiteSpace(navidrome.Password))
        {
            var username = !string.IsNullOrWhiteSpace(navidrome.Username) ? navidrome.Username : navidrome.ServerName;
            targets.Add(new MediaServerTarget(MelodayTargetServers.Navidrome, null, null, navidrome, username));
        }

        return targets;
    }

    private static string ResolveMelodayDisplayUsername(IReadOnlyList<MediaServerTarget> targets)
        => string.Join(", ", targets
            .Select(static target => target.Username)
            .Where(static username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private async Task<long> EnsureMelodayAppUserAsync(CancellationToken cancellationToken)
        => await _libraryRepository.EnsurePlexUserAsync(
            MelodayAppUserName,
            MelodayAppUserId,
            "deezspotag",
            "meloday",
            cancellationToken);

    private async Task<long> EnsureHistoryUserAsync(MediaServerTarget target, CancellationToken cancellationToken)
    {
        if (target.Plex is not null)
        {
            return await _libraryRepository.EnsurePlexUserAsync(
                target.Username,
                target.Plex.Username,
                target.Plex.Url,
                target.Plex.MachineIdentifier,
                cancellationToken);
        }

        if (target.Navidrome is not null)
        {
            return await _libraryRepository.EnsurePlexUserAsync(
                target.Username,
                $"navidrome:{target.Navidrome.Username}",
                target.Navidrome.Url,
                target.Navidrome.ServerName,
                cancellationToken);
        }

        var jellyfin = target.Jellyfin!;
        return await _libraryRepository.EnsurePlexUserAsync(
            target.Username,
            $"jellyfin:{jellyfin.UserId}",
            jellyfin.Url,
            jellyfin.ServerName,
            cancellationToken);
    }

    private static bool TryGetPlexConnection(
        PlexAuth? plex,
        [NotNullWhen(true)] out PlexAuth? configuredPlex,
        [NotNullWhen(true)] out string? plexUrl,
        [NotNullWhen(true)] out string? plexToken)
    {
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            configuredPlex = null;
            plexUrl = null;
            plexToken = null;
            return false;
        }

        configuredPlex = plex;
        plexUrl = plex.Url;
        plexToken = plex.Token;
        return true;
    }

    public static List<long> NormalizeTargetLibraryIds(IEnumerable<long>? values)
        => values?
            .Where(static id => id > 0)
            .Distinct()
            .Order()
            .ToList() ?? new List<long>();

    private async Task<List<long>> BuildDirectTrackSelectionAsync(
        IReadOnlyList<long> historyTrackIds,
        IReadOnlyList<long> balancedHistorical,
        long libraryId,
        SimilarTrackContext context,
        int trackLimit = 0)
    {
        return await BuildVibeDrivenTrackSelectionAsync(
            historyTrackIds,
            balancedHistorical,
            libraryId,
            prioritizePlexSonicMatches: false,
            context,
            trackLimit);
    }

    private async Task<List<long>> BuildSonicTrackSelectionAsync(
        IReadOnlyList<long> historyTrackIds,
        IReadOnlyList<long> balancedHistorical,
        long libraryId,
        SimilarTrackContext context,
        int trackLimit = 0)
    {
        return await BuildVibeDrivenTrackSelectionAsync(
            historyTrackIds,
            balancedHistorical,
            libraryId,
            prioritizePlexSonicMatches: true,
            context,
            trackLimit);
    }

    private async Task<List<long>> BuildVibeDrivenTrackSelectionAsync(
        IReadOnlyList<long> historyTrackIds,
        IReadOnlyList<long> balancedHistorical,
        long libraryId,
        bool prioritizePlexSonicMatches,
        SimilarTrackContext context,
        int trackLimit = 0)
    {
        var allowedIds = context.AllowedTrackIds.ToList();
        var analysisByTrackId = await _libraryRepository.GetTrackAnalysisByTrackIdsAsync(
            allowedIds,
            context.CancellationToken);
        var analyzedHistoryTrackIds = ResolveAnalyzedHistoryTrackIds(
            historyTrackIds,
            balancedHistorical,
            analysisByTrackId);
        var historicalTrackIds = Sample(
                analyzedHistoryTrackIds,
                ResolveHistoricalTrackCount(context.Options))
            .ToList();
        var vibeSeedTrackIds = historicalTrackIds.Count > 0
            ? historicalTrackIds.ToList()
            : Sample(
                    analyzedHistoryTrackIds,
                    Math.Max(1, context.Options.SonicSimilarLimit))
                .ToList();

        var outputExclusions = new HashSet<long>(context.ExcludedTrackIds);
        outputExclusions.UnionWith(historyTrackIds);
        outputExclusions.UnionWith(vibeSeedTrackIds);
        var candidateLimit = Math.Max(context.Options.MaxTracks * 8, context.Options.MaxTracks);
        var vibeMatches = MelodayVibeSelector.Select(
            vibeSeedTrackIds,
            analysisByTrackId.Values.ToList(),
            context.AllowedTrackIds,
            outputExclusions,
            candidateLimit,
            context.Options.SonicSimilarityDistance);

        var orderedVibeCandidates = await PrioritizePlexSonicVibeMatchesAsync(
            vibeMatches,
            vibeSeedTrackIds,
            prioritizePlexSonicMatches,
            context);
        var candidatePool = historicalTrackIds
            .Concat(orderedVibeCandidates)
            .Distinct()
            .ToList();
        var finalTracks = await ApplyPlexRatingFiltersAsync(candidatePool, context, trackLimit);
        _logger.LogInformation(
            "Meloday vibe selection for library {LibraryId}: usableAnalyses={AnalyzedCount}, profileSeeds={ProfileSeedCount}, historicalIncluded={HistoricalTrackCount}, vibeQualified={VibeQualifiedCount}, selected={SelectedCount}.",
            libraryId,
            analysisByTrackId.Values.Count(MelodayVibeSelector.IsUsableAnalysis),
            vibeSeedTrackIds.Count,
            historicalTrackIds.Count,
            vibeMatches.Count,
            finalTracks.Count);
        return finalTracks;
    }

    private async Task<List<long>> ApplyPlexRatingFiltersAsync(
        IReadOnlyList<long> candidateTracks,
        SimilarTrackContext context,
        int trackLimit = 0)
    {
        if (candidateTracks.Count == 0)
        {
            return new List<long>();
        }

        var limit = trackLimit > 0 ? trackLimit : context.Options.MaxTracks;
        if (context.Plex is null)
        {
            return (await ProcessTracksAsync(
                    candidateTracks.ToList(),
                    context.Options,
                    context.LiveMetadataByTrackId,
                    context.CancellationToken,
                    limit))
                .Take(limit)
                .ToList();
        }

        var finalTracks = new List<long>();
        var loadedCandidates = new List<long>();
        foreach (var candidateBatch in candidateTracks.Chunk(Math.Max(1, context.Options.MaxTracks)))
        {
            await EnsureRatingKeysAsync(
                candidateBatch,
                context.Plex,
                context.RatingKeyByTrackId,
                context.CancellationToken);
            await EnsurePlexMetadataAsync(
                context.Plex,
                candidateBatch,
                context.RatingKeyByTrackId,
                context.LiveMetadataByTrackId,
                context.CancellationToken);

            loadedCandidates.AddRange(candidateBatch);
            finalTracks = (await ProcessTracksAsync(
                    loadedCandidates,
                    context.Options,
                    context.LiveMetadataByTrackId,
                    context.CancellationToken,
                    limit))
                .Take(limit)
                .ToList();
            if (finalTracks.Count >= limit)
            {
                break;
            }
        }

        return finalTracks;
    }

    private static IReadOnlyList<long> ResolveAnalyzedHistoryTrackIds(
        IReadOnlyList<long> historyTrackIds,
        IReadOnlyList<long> balancedHistorical,
        IReadOnlyDictionary<long, TrackAnalysisResultDto> analysisByTrackId)
    {
        return balancedHistorical
            .Concat(historyTrackIds)
            .Where(analysisByTrackId.ContainsKey)
            .Where(trackId => MelodayVibeSelector.IsUsableAnalysis(analysisByTrackId[trackId]))
            .Distinct()
            .ToList();
    }

    private static int ResolveHistoricalTrackCount(MelodayOptions options)
        => options.HistoricalRatio <= 0
            ? 0
            : Math.Clamp(
                (int)Math.Round(options.MaxTracks * options.HistoricalRatio),
                1,
                options.MaxTracks);

    private async Task<List<long>> PrioritizePlexSonicVibeMatchesAsync(
        IReadOnlyList<MelodayVibeMatch> vibeMatches,
        IReadOnlyList<long> seedTrackIds,
        bool prioritizePlexSonicMatches,
        SimilarTrackContext context)
    {
        if (!prioritizePlexSonicMatches || context.Plex is null || seedTrackIds.Count == 0)
        {
            return vibeMatches.Select(static match => match.TrackId).ToList();
        }

        await EnsureRatingKeysAsync(
            seedTrackIds,
            context.Plex,
            context.RatingKeyByTrackId,
            context.CancellationToken);
        var plexSimilarIds = (await FetchSonicSimilarTrackIdsAsync(seedTrackIds.ToList(), context)).ToHashSet();
        return vibeMatches
            .OrderByDescending(match => plexSimilarIds.Contains(match.TrackId))
            .ThenByDescending(static match => match.Similarity)
            .Select(static match => match.TrackId)
            .ToList();
    }

    public async Task<MelodayStatusDto> GetStatusAsync()
    {
        var effective = await _settingsStore.LoadAsync(_options);
        return new MelodayStatusDto(
            effective.Enabled,
            MelodayScheduleMath.DescribeNextSlot(effective.Slots, DateTimeOffset.Now),
            _lastRunUtc,
            _lastMessage,
            effective.MaxTracks,
            effective.HistoryLookbackDays,
            effective.ExcludePlayedDays,
            effective.MissedRunGraceMinutes,
            _lastImportResults);
    }

    private IReadOnlyList<long> Sample(IReadOnlyList<long> source, int count)
    {
        if (source.Count == 0 || count <= 0)
        {
            return Array.Empty<long>();
        }

        return source
            .OrderBy(_ => _random.Next())
            .Take(Math.Min(count, source.Count))
            .ToList();
    }

    private async Task EnsureRatingKeysAsync(
        IReadOnlyList<long> trackIds,
        PlexAuth plex,
        Dictionary<long, string> ratingKeyByTrackId,
        CancellationToken cancellationToken)
    {
        var missing = trackIds
            .Where(trackId => !ratingKeyByTrackId.ContainsKey(trackId))
            .Distinct()
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var resolved = await ResolveRatingKeysAsync(missing, plex, cancellationToken);
        foreach (var entry in resolved)
        {
            ratingKeyByTrackId[entry.Key] = entry.Value;
        }
    }

    private async Task EnsurePlexMetadataAsync(
        PlexAuth plex,
        IReadOnlyList<long> trackIds,
        Dictionary<long, string> ratingKeyByTrackId,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId,
        CancellationToken cancellationToken)
    {
        var targets = trackIds
            .Where(trackId => !liveMetadataByTrackId.ContainsKey(trackId))
            .Where(trackId => ratingKeyByTrackId.ContainsKey(trackId))
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        foreach (var trackId in targets)
        {
            if (!ratingKeyByTrackId.TryGetValue(trackId, out var ratingKey) || string.IsNullOrWhiteSpace(ratingKey))
            {
                continue;
            }

            var metadata = await _plexApiClient.GetTrackMetadataAsync(
                plex.Url!,
                plex.Token!,
                ratingKey,
                cancellationToken);
            if (metadata is null)
            {
                continue;
            }

            liveMetadataByTrackId[trackId] = metadata;

            await _libraryRepository.UpsertPlexTrackMetadataAsync(
                new PlexTrackMetadataDto(
                    trackId,
                    metadata.RatingKey,
                    metadata.UserRating,
                    metadata.Genres,
                    metadata.Moods,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
    }

    private List<long> BuildBalancedHistoricalSelection(
        IReadOnlyList<PlayHistoryEntryDto> history,
        IReadOnlySet<long> excludedTrackIds,
        IReadOnlyDictionary<long, IReadOnlyList<string>> genresByTrackId,
        int maxTracks)
    {
        var filteredHistory = history
            .Where(entry => !excludedTrackIds.Contains(entry.TrackId))
            .ToList();
        if (filteredHistory.Count == 0)
        {
            return new List<long>();
        }

        var playCounts = filteredHistory
            .GroupBy(entry => entry.TrackId)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.PlayCount));

        var sortedTracks = playCounts
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .ToList();

        var splitIndex = Math.Max(1, sortedTracks.Count / 4);
        var popular = sortedTracks.Take(splitIndex).ToList();
        var rare = sortedTracks.Skip(splitIndex).ToList();

        var rareCount = Math.Min(rare.Count, (int)(maxTracks * 0.75));
        var popularCount = Math.Min(popular.Count, (int)(maxTracks * 0.25));

        var balanced = Sample(rare, rareCount)
            .Concat(Sample(popular, popularCount))
            .Distinct()
            .ToList();

        if (balanced.Count == 0)
        {
            balanced = Sample(sortedTracks, Math.Min(maxTracks, sortedTracks.Count)).ToList();
        }

        var genreCount = BuildGenreCount(filteredHistory, genresByTrackId);
        return RebalanceDominantGenre(balanced, genreCount, genresByTrackId, maxTracks);
    }

    private static Dictionary<string, int> BuildGenreCount(
        IReadOnlyList<PlayHistoryEntryDto> filteredHistory,
        IReadOnlyDictionary<long, IReadOnlyList<string>> genresByTrackId)
    {
        var genreCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in filteredHistory)
        {
            if (!genresByTrackId.TryGetValue(entry.TrackId, out var genres))
            {
                continue;
            }

            foreach (var genre in genres.Where(genre => !string.IsNullOrWhiteSpace(genre)))
            {
                genreCount[genre] = genreCount.TryGetValue(genre, out var count)
                    ? count + entry.PlayCount
                    : entry.PlayCount;
            }
        }

        return genreCount;
    }

    private static List<long> RebalanceDominantGenre(
        List<long> balanced,
        Dictionary<string, int> genreCount,
        IReadOnlyDictionary<long, IReadOnlyList<string>> genresByTrackId,
        int maxTracks)
    {
        if (genreCount.Count == 0)
        {
            return balanced;
        }

        var mostCommon = genreCount.OrderByDescending(entry => entry.Value).First();
        var maxGenreLimit = Math.Max(1, (int)(maxTracks * 0.25));
        var totalGenrePlays = genreCount.Values.Sum();
        if (mostCommon.Value <= totalGenrePlays * 0.25)
        {
            return balanced;
        }

        var nonDominant = balanced
            .Where(trackId => !TrackHasGenre(genresByTrackId, trackId, mostCommon.Key))
            .ToList();
        var dominant = balanced
            .Where(trackId => TrackHasGenre(genresByTrackId, trackId, mostCommon.Key))
            .Take(maxGenreLimit)
            .ToList();

        var rebalanced = nonDominant
            .Concat(dominant)
            .Distinct()
            .ToList();
        return rebalanced.Count > 0 ? rebalanced : balanced;
    }

    private static bool TrackHasGenre(
        IReadOnlyDictionary<long, IReadOnlyList<string>> genresByTrackId,
        long trackId,
        string genre)
    {
        return genresByTrackId.TryGetValue(trackId, out var genres)
               && genres.Any(value => string.Equals(value, genre, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<long>> FetchSonicSimilarTrackIdsAsync(
        List<long> referenceTrackIds,
        SimilarTrackContext context)
    {
        if (referenceTrackIds.Count == 0)
        {
            return Array.Empty<long>();
        }

        if (context.Plex is null)
        {
            return Array.Empty<long>();
        }

        var similarRatingKeys = await CollectSimilarRatingKeysAsync(referenceTrackIds, context);
        if (similarRatingKeys.Count == 0)
        {
            return Array.Empty<long>();
        }

        var mappedTrackIds = await MapSimilarRatingKeysAsync(similarRatingKeys, context.CancellationToken);
        return await BuildSimilarTrackOutputAsync(similarRatingKeys, mappedTrackIds, context);
    }

    private async Task<List<string>> CollectSimilarRatingKeysAsync(
        IReadOnlyList<long> referenceTrackIds,
        SimilarTrackContext context)
    {
        if (context.Plex is null)
        {
            return new List<string>();
        }

        var similarRatingKeys = new List<string>();
        foreach (var trackId in referenceTrackIds.Distinct())
        {
            if (!context.RatingKeyByTrackId.TryGetValue(trackId, out var ratingKey) || string.IsNullOrWhiteSpace(ratingKey))
            {
                continue;
            }

            var similars = await _plexApiClient.GetSonicallySimilarRatingKeysAsync(
                context.Plex.Url!,
                context.Plex.Token!,
                ratingKey,
                Math.Max(1, context.Options.SonicSimilarLimit),
                cancellationToken: context.CancellationToken);
            similarRatingKeys.AddRange(similars);
        }

        return similarRatingKeys;
    }

    private async Task<IReadOnlyDictionary<string, long>> MapSimilarRatingKeysAsync(
        IReadOnlyList<string> similarRatingKeys,
        CancellationToken cancellationToken)
    {
        var distinctSimilarRatingKeys = similarRatingKeys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return await _libraryRepository.GetTrackIdsByPlexRatingKeysAsync(distinctSimilarRatingKeys, cancellationToken);
    }

    private async Task<IReadOnlyList<long>> BuildSimilarTrackOutputAsync(
        IReadOnlyList<string> similarRatingKeys,
        IReadOnlyDictionary<string, long> mappedTrackIds,
        SimilarTrackContext context)
    {
        var similarMetadataByRatingKey = new Dictionary<string, PlexTrackMetadata?>(StringComparer.OrdinalIgnoreCase);
        var output = new List<long>();
        foreach (var ratingKey in similarRatingKeys)
        {
            var metadata = await GetOrLoadSimilarMetadataAsync(ratingKey, similarMetadataByRatingKey, context);
            if (!ShouldIncludeSimilarTrack(metadata, ratingKey, mappedTrackIds, context, out var trackId))
            {
                continue;
            }

            if (metadata is not null)
            {
                context.LiveMetadataByTrackId[trackId] = metadata;
            }

            context.RatingKeyByTrackId[trackId] = ratingKey;
            if (!output.Contains(trackId))
            {
                output.Add(trackId);
            }
        }

        return output;
    }

    private async Task<PlexTrackMetadata?> GetOrLoadSimilarMetadataAsync(
        string ratingKey,
        Dictionary<string, PlexTrackMetadata?> metadataByRatingKey,
        SimilarTrackContext context)
    {
        if (context.Plex is null)
        {
            return null;
        }

        if (metadataByRatingKey.TryGetValue(ratingKey, out var metadata))
        {
            return metadata;
        }

        metadata = await _plexApiClient.GetTrackMetadataAsync(
            context.Plex.Url!,
            context.Plex.Token!,
            ratingKey,
            context.CancellationToken);
        metadataByRatingKey[ratingKey] = metadata;
        return metadata;
    }

    private static bool ShouldIncludeSimilarTrack(
        PlexTrackMetadata? metadata,
        string ratingKey,
        IReadOnlyDictionary<string, long> mappedTrackIds,
        SimilarTrackContext context,
        out long trackId)
    {
        trackId = 0;
        if (metadata?.LastViewedAtUtc is { } lastViewedAtUtc && lastViewedAtUtc >= context.ExcludeStart)
        {
            return false;
        }

        if (!mappedTrackIds.TryGetValue(ratingKey, out trackId))
        {
            return false;
        }

        return context.AllowedTrackIds.Contains(trackId)
            && !context.ExcludedTrackIds.Contains(trackId);
    }

    private async Task<IReadOnlyList<long>> ProcessTracksAsync(
        List<long> trackIds,
        MelodayOptions options,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId,
        CancellationToken cancellationToken,
        int trackLimit = 0)
    {
        if (trackIds.Count == 0)
        {
            return Array.Empty<long>();
        }

        var uniqueTrackIds = trackIds.Distinct().ToList();
        var trackOrder = uniqueTrackIds
            .Select((trackId, index) => new { trackId, index })
            .ToDictionary(entry => entry.trackId, entry => entry.index);

        var summaries = await _libraryRepository.GetTrackSummariesAsync(uniqueTrackIds, cancellationToken);
        if (summaries.Count == 0)
        {
            return uniqueTrackIds;
        }

        var metadata = await _libraryRepository.GetPlexTrackMetadataAsync(uniqueTrackIds, cancellationToken);
        var persistedMetadataByTrackId = metadata.ToDictionary(entry => entry.TrackId);

        // 0 means "the playlist size", which is what every pre-DJ call site means. A DJ
        // asks for a wider pool, and the per-artist allowance has to widen with it, or
        // the pool would arrive pre-diversified down to playlist size and the DJ would
        // be choosing from less than it was offered.
        var state = new TrackFilterState(trackLimit > 0 ? trackLimit : options.MaxTracks);
        var orderedSummaries = summaries
            .OrderBy(summary => trackOrder.TryGetValue(summary.TrackId, out var index) ? index : int.MaxValue)
            .ToList();
        return orderedSummaries
            .Where(track => TryIncludeTrack(track, liveMetadataByTrackId, persistedMetadataByTrackId, state))
            .Select(track => track.TrackId)
            .ToList();
    }

    private static bool TryIncludeTrack(
        MixTrackDto track,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId,
        Dictionary<long, PlexTrackMetadataDto> persistedMetadataByTrackId,
        TrackFilterState state)
    {
        liveMetadataByTrackId.TryGetValue(track.TrackId, out var liveMetadata);
        persistedMetadataByTrackId.TryGetValue(track.TrackId, out var persistedMetadata);

        if (IsLowRated(liveMetadata, persistedMetadata))
        {
            return false;
        }

        var artistName = NormalizeArtistName(track.ArtistName);
        var dedupeKey = BuildDedupeKey(track.Title, artistName);
        if (!state.Seen.Add(dedupeKey))
        {
            return false;
        }

        if (HasReachedLimit(state.ArtistCountByName, artistName, state.ArtistLimit))
        {
            return false;
        }

        IncrementCount(state.ArtistCountByName, artistName);
        return true;
    }

    private static string NormalizeArtistName(string? artistName)
    {
        return string.IsNullOrWhiteSpace(artistName)
            ? "unknown"
            : artistName.Trim().ToLowerInvariant();
    }

    private static string BuildDedupeKey(string? title, string artistName)
    {
        var cleanedTitle = CleanTitle(title);
        if (string.IsNullOrWhiteSpace(cleanedTitle))
        {
            cleanedTitle = (title ?? string.Empty).Trim().ToLowerInvariant();
        }

        return $"{cleanedTitle}::{artistName}";
    }

    private static bool HasReachedLimit(Dictionary<string, int> counts, string key, int limit)
    {
        return counts.TryGetValue(key, out var count) && count >= limit;
    }

    private static void IncrementCount(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static bool IsLowRated(PlexTrackMetadata? liveMetadata, PlexTrackMetadataDto? persistedMetadata)
    {
        if (liveMetadata is not null)
        {
            if (IsExplicitLowRating(liveMetadata.ArtistUserRating))
            {
                return true;
            }

            if (IsExplicitLowRating(liveMetadata.AlbumUserRating))
            {
                return true;
            }

            if (IsExplicitLowRating(liveMetadata.UserRating))
            {
                return true;
            }
        }

        return IsExplicitLowRating(persistedMetadata?.UserRating);
    }

    internal static bool IsExplicitLowRating(int? rating)
        => rating is > 0 and <= 2;

    private static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var output = title.Trim().ToLowerInvariant();
        output = FeaturingParentheticalRegex.Replace(output, " ");
        output = FeaturingInlineRegex.Replace(output, " ");
        output = DashVersionRegex.Replace(output, " ");

        foreach (var keyword in VersionKeywords)
        {
            output = ReplaceWithTimeout(output, $@"\b{Regex.Escape(keyword)}\b", " ", RegexOptions.IgnoreCase);
        }

        output = TrailingSpaceOrHyphenRegex.Replace(output, string.Empty);
        output = MultiWhitespaceRegex.Replace(output, " ").Trim();
        return output;
    }

    private static List<long> OrderTracksDirect(
        List<long> trackIds,
        MelodayDaypart period,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId)
    {
        if (trackIds.Count <= 2)
        {
            return trackIds.ToList();
        }

        return trackIds
            .OrderByDescending(trackId => liveMetadataByTrackId.TryGetValue(trackId, out var metadata)
                && metadata.LastViewedAtUtc.HasValue
                && period.Hours.Contains(metadata.LastViewedAtUtc.Value.Hour))
            .ThenBy(trackId => liveMetadataByTrackId.TryGetValue(trackId, out var metadata) && metadata.LastViewedAtUtc.HasValue
                ? metadata.LastViewedAtUtc.Value
                : DateTimeOffset.MaxValue)
            .Distinct()
            .ToList();
    }

    private Task<IReadOnlyList<long>> OrderTracksSonicAsync(
        List<long> trackIds,
        MelodayDaypart period,
        PlexAuth? plex,
        MelodayOptions options,
        Dictionary<long, string> ratingKeyByTrackId,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId,
        CancellationToken cancellationToken)
    {
        return OrderTracksAsync(trackIds, period, plex, options, ratingKeyByTrackId, liveMetadataByTrackId, cancellationToken);
    }

    private static bool IsCompletedAnalysis(TrackAnalysisResultDto analysis)
    {
        return string.Equals(analysis.Status, "complete", StringComparison.OrdinalIgnoreCase)
            || string.Equals(analysis.Status, "completed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<long>> OrderTracksAsync(
        List<long> trackIds,
        MelodayDaypart period,
        PlexAuth? plex,
        MelodayOptions options,
        Dictionary<long, string> ratingKeyByTrackId,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId,
        CancellationToken cancellationToken)
    {
        if (trackIds.Count <= 2)
        {
            return trackIds;
        }

        var sortedByLastViewed = SortTracksByLastViewed(trackIds, liveMetadataByTrackId);
        var (firstTrackId, lastTrackId) = ResolveOrderAnchors(sortedByLastViewed, period, liveMetadataByTrackId);

        var middle = trackIds
            .Where(trackId => trackId != firstTrackId && trackId != lastTrackId)
            .ToList();

        var sortedMiddle = plex is null
            ? middle.OrderBy(_ => _random.Next()).ToList()
            : await SortBySonicSimilarityGreedyAsync(
                middle,
                plex,
                options,
                ratingKeyByTrackId,
                cancellationToken);

        var ordered = BuildOrderedTrackList(firstTrackId, sortedMiddle, lastTrackId);

        if (ordered.Count == 0)
        {
            return trackIds;
        }

        return ordered
            .Distinct()
            .Take(options.MaxTracks)
            .ToList();
    }

    private static List<long> SortTracksByLastViewed(
        List<long> trackIds,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId)
    {
        return trackIds
            .OrderBy(trackId => liveMetadataByTrackId.TryGetValue(trackId, out var metadata) && metadata.LastViewedAtUtc.HasValue
                ? metadata.LastViewedAtUtc.Value
                : DateTimeOffset.MaxValue)
            .ToList();
    }

    private static List<long> BuildOrderedTrackList(long? firstTrackId, List<long> sortedMiddle, long? lastTrackId)
    {
        var ordered = new List<long>();
        if (firstTrackId.HasValue)
        {
            ordered.Add(firstTrackId.Value);
        }

        ordered.AddRange(sortedMiddle);

        if (lastTrackId.HasValue && lastTrackId != firstTrackId)
        {
            ordered.Add(lastTrackId.Value);
        }

        return ordered;
    }

    private static (long? FirstTrackId, long? LastTrackId) ResolveOrderAnchors(
        List<long> sortedByLastViewed,
        MelodayDaypart period,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId)
    {
        if (sortedByLastViewed.Count == 0)
        {
            return (null, null);
        }

        var firstTrackId = sortedByLastViewed
            .Cast<long?>()
            .FirstOrDefault(trackId => IsPeriodTrack(trackId, period, liveMetadataByTrackId))
            ?? sortedByLastViewed[0];
        var lastTrackId = sortedByLastViewed
            .AsEnumerable()
            .Reverse()
            .Cast<long?>()
            .FirstOrDefault(trackId => IsPeriodTrack(trackId, period, liveMetadataByTrackId))
            ?? sortedByLastViewed[^1];
        return (firstTrackId, lastTrackId);
    }

    private static bool IsPeriodTrack(
        long? trackId,
        MelodayDaypart period,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId)
        => trackId.HasValue
           && liveMetadataByTrackId.TryGetValue(trackId.Value, out var metadata)
           && metadata.LastViewedAtUtc.HasValue
           && period.Hours.Contains(metadata.LastViewedAtUtc.Value.Hour);

    private async Task<List<long>> SortBySonicSimilarityGreedyAsync(
        List<long> trackIds,
        PlexAuth plex,
        MelodayOptions options,
        Dictionary<long, string> ratingKeyByTrackId,
        CancellationToken cancellationToken)
    {
        if (trackIds.Count <= 1)
        {
            return trackIds.ToList();
        }

        var remaining = trackIds.ToList();
        var sorted = new List<long>();
        var similarCache = new Dictionary<long, List<string>>();

        var startIndex = _random.Next(remaining.Count);
        var current = remaining[startIndex];
        remaining.RemoveAt(startIndex);
        sorted.Add(current);

        var limit = Math.Max(20, options.SonicSimilarLimit);
        while (remaining.Count > 0)
        {
            List<string>? currentSimilars = null;
            if (ratingKeyByTrackId.TryGetValue(current, out var currentRatingKey)
                && !string.IsNullOrWhiteSpace(currentRatingKey)
                && !similarCache.TryGetValue(current, out currentSimilars))
            {
                currentSimilars = await _plexApiClient.GetSonicallySimilarRatingKeysAsync(
                    plex.Url!,
                    plex.Token!,
                    currentRatingKey,
                    limit,
                    1.0,
                    cancellationToken);
                similarCache[current] = currentSimilars;
            }

            var nextTrack = remaining
                .OrderBy(candidate => SimilarityScore(candidate, currentSimilars, ratingKeyByTrackId))
                .First();

            sorted.Add(nextTrack);
            remaining.Remove(nextTrack);
            current = nextTrack;
        }

        return sorted;
    }

    private static int SimilarityScore(
        long candidateTrackId,
        List<string>? currentSimilars,
        Dictionary<long, string> ratingKeyByTrackId)
    {
        if (currentSimilars is null || currentSimilars.Count == 0)
        {
            return 100;
        }

        if (!ratingKeyByTrackId.TryGetValue(candidateTrackId, out var candidateRatingKey) || string.IsNullOrWhiteSpace(candidateRatingKey))
        {
            return 100;
        }

        for (var index = 0; index < currentSimilars.Count; index++)
        {
            if (string.Equals(currentSimilars[index], candidateRatingKey, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 100;
    }

    private async Task<Dictionary<long, string>> ResolveRatingKeysAsync(
        IReadOnlyList<long> trackIds,
        PlexAuth plex,
        CancellationToken cancellationToken)
    {
        var mapping = (await _libraryRepository.GetPlexRatingKeysByTrackIdsAsync(trackIds, cancellationToken))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var missing = trackIds.Where(id => !mapping.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return mapping;
        }

        var summaries = await _libraryRepository.GetTrackSummariesAsync(missing, cancellationToken);
        foreach (var track in summaries)
        {
            var queryVariants = new[]
            {
                $"{track.Title} {track.ArtistName}",
                track.Title
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            PlexTrack? bestMatch = null;
            foreach (var query in queryVariants)
            {
                var matches = await _plexApiClient.SearchTracksAsync(plex.Url!, plex.Token!, query, cancellationToken);
                bestMatch = SelectBestPlexTrackMatch(track, matches);
                if (bestMatch is not null)
                {
                    break;
                }
            }

            if (bestMatch is null || string.IsNullOrWhiteSpace(bestMatch.RatingKey))
            {
                continue;
            }

            mapping[track.TrackId] = bestMatch.RatingKey;
        }

        return mapping;
    }

    private static PlexTrack? SelectBestPlexTrackMatch(MixTrackDto track, List<PlexTrack> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (!TryCreateSourceMatchContext(track, out var source))
        {
            return null;
        }

        PlexTrack? best = null;
        var bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            if (!TryScoreCandidate(source, candidate, out var score))
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static bool TryCreateSourceMatchContext(MixTrackDto track, out SourceTrackMatchContext source)
    {
        var sourceTitle = CleanSourceTitle(track.Title, track.ArtistName);
        if (string.IsNullOrWhiteSpace(sourceTitle))
        {
            source = new SourceTrackMatchContext(string.Empty, string.Empty, string.Empty, string.Empty);
            return false;
        }

        var sourceArtist = (track.ArtistName ?? string.Empty).Trim();
        source = new SourceTrackMatchContext(
            sourceTitle,
            sourceArtist,
            NormalizeComparableText(sourceTitle),
            NormalizeComparableText(sourceArtist));
        return true;
    }

    private static string CleanSourceTitle(string? title, string? artistName)
    {
        var cleaned = (title ?? string.Empty).Trim();
        var artist = (artistName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            var prefix = $"{artist} - ";
            while (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..].Trim();
            }
        }

        var dashParts = cleaned
            .Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (dashParts.Count > 1)
        {
            var last = dashParts[^1];
            if (!string.IsNullOrWhiteSpace(last)
                && dashParts.Take(dashParts.Count - 1).All(part => string.Equals(part, artist, StringComparison.OrdinalIgnoreCase)))
            {
                cleaned = last;
            }
        }

        var normalized = CleanTitle(cleaned);
        return string.IsNullOrWhiteSpace(normalized) ? cleaned : normalized;
    }

    private static bool TryScoreCandidate(SourceTrackMatchContext source, PlexTrack candidate, out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(candidate.RatingKey))
        {
            return false;
        }

        var candidateTitle = (candidate.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        var candidateTitleClean = CleanTitle(candidateTitle);
        var candidateTitleNorm = NormalizeComparableText(string.IsNullOrWhiteSpace(candidateTitleClean) ? candidateTitle : candidateTitleClean);

        var titleExact = string.Equals(candidateTitle, source.SourceTitle, StringComparison.OrdinalIgnoreCase);
        var titleNormalized = !string.IsNullOrWhiteSpace(source.SourceTitleNormalized)
                              && string.Equals(candidateTitleNorm, source.SourceTitleNormalized, StringComparison.Ordinal);
        if (!titleExact && !titleNormalized)
        {
            return false;
        }

        score = titleExact ? 100 : 80;
        score += ScoreArtistMatch(source, candidate.Artist);
        return true;
    }

    private static int ScoreArtistMatch(SourceTrackMatchContext source, string? candidateArtistRaw)
    {
        if (string.IsNullOrWhiteSpace(source.SourceArtistNormalized))
        {
            return 0;
        }

        var candidateArtist = (candidateArtistRaw ?? string.Empty).Trim();
        var candidateArtistNorm = NormalizeComparableText(candidateArtist);
        if (string.Equals(candidateArtist, source.SourceArtist, StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        if (!string.IsNullOrWhiteSpace(candidateArtistNorm)
            && string.Equals(candidateArtistNorm, source.SourceArtistNormalized, StringComparison.Ordinal))
        {
            return 30;
        }

        if (!string.IsNullOrWhiteSpace(candidateArtistNorm)
            && (candidateArtistNorm.Contains(source.SourceArtistNormalized, StringComparison.Ordinal)
                || source.SourceArtistNormalized.Contains(candidateArtistNorm, StringComparison.Ordinal)))
        {
            return 15;
        }

        return 0;
    }

    private static string NormalizeComparableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray());

        return MultiWhitespaceRegex.Replace(normalized, " ").Trim();
    }

    private MelodayPlaylistText BuildTitleAndDescription(PlaylistDescriptionContext context)
    {
        var genres = new List<string>();
        var moods = new List<string>();

        foreach (var trackId in context.TrackIds)
        {
            if (context.LiveMetadataByTrackId.TryGetValue(trackId, out var liveMetadata))
            {
                genres.AddRange(liveMetadata.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre)));
                moods.AddRange(liveMetadata.Moods.Where(mood => !string.IsNullOrWhiteSpace(mood)));
            }
            else if (context.PersistedMetadataByTrackId.TryGetValue(trackId, out var persistedMetadata))
            {
                genres.AddRange(persistedMetadata.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre)));
                moods.AddRange(persistedMetadata.Moods.Where(mood => !string.IsNullOrWhiteSpace(mood)));
            }

            if (!context.TrackAnalysesByTrackId.TryGetValue(trackId, out var analysis)
                || !IsCompletedAnalysis(analysis))
            {
                continue;
            }

            moods.AddRange((analysis.MoodTags ?? Array.Empty<string>())
                .Where(static mood => !string.IsNullOrWhiteSpace(mood)));
            genres.AddRange(ResolveAnalysisGenres(analysis));
        }

        var sortedGenres = SortByFrequency(genres);
        var sortedMoods = SortByFrequency(moods);

        var mostCommonGenre = sortedGenres.Count > 0 ? sortedGenres[0] : "Eclectic";
        var mostCommonMood = sortedMoods.Count > 0 ? sortedMoods[0] : "Vibes";
        var secondCommonMood = sortedMoods.Count > 1 ? sortedMoods[1] : null;

        var descriptorMap = LoadDescriptorMap(context.Options);
        var descriptorSource = secondCommonMood ?? mostCommonMood;
        ChooseDescriptor(descriptorMap, descriptorSource);

        var dayName = context.Now.ToString("dddd");

        var highlights = BuildHighlightStyles(sortedGenres, sortedMoods, mostCommonGenre, mostCommonMood);
        var highlightsText = FormatHighlightStyles(highlights);

        var description = secondCommonMood is not null
            ? $"You listened to {ToDisplayLabel(mostCommonMood)} and {ToDisplayLabel(mostCommonGenre)} tracks on {dayName} {context.Daypart.Phrase}. Here's some {highlightsText} tracks as well."
            : $"You listened to {ToDisplayLabel(mostCommonGenre)} and {ToDisplayLabel(mostCommonMood)} tracks on {dayName} {context.Daypart.Phrase}. Here's some {highlightsText} tracks as well.";

        var displayUser = ResolveDisplayUserName(context.Username);
        var nextUpdate = GetNextUpdateTime(context.Now, context.SlotGenerateAt);
        description += $"\n\nMade for {displayUser} • Next update at {nextUpdate}.";

        var coverTagline = $"{ToDisplayLabel(mostCommonMood)} · {ToDisplayLabel(mostCommonGenre)}";
        return new MelodayPlaylistText(description, coverTagline);
    }

    private static string NormalizeVibeGenre(string genre)
        => MultiWhitespaceRegex.Replace(genre.Replace("---", " ", StringComparison.Ordinal), " ").Trim();

    private static IReadOnlyList<string> ResolveAnalysisGenres(TrackAnalysisResultDto analysis)
    {
        var genres = analysis.ResolvedGenres is { Count: > 0 }
            ? analysis.ResolvedGenres
            : analysis.EssentiaGenres is { Count: > 0 }
                ? analysis.EssentiaGenres
                : analysis.LastfmTags ?? Array.Empty<string>();
        return genres
            .Where(static genre => !string.IsNullOrWhiteSpace(genre))
            .Select(NormalizeVibeGenre)
            .Where(static genre => genre.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ToDisplayLabel(string value)
    {
        var words = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static word => word.Length == 0
                ? word
                : $"{char.ToUpperInvariant(word[0])}{word[1..]}");
        return string.Join(' ', words);
    }

    private static List<string> SortByFrequency(IReadOnlyList<string> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .ToList();
    }

    private string ChooseDescriptor(Dictionary<string, List<string>> descriptorMap, string descriptorSource)
    {
        if (descriptorMap.TryGetValue(descriptorSource, out var choices) && choices.Count > 0)
        {
            return choices[_random.Next(choices.Count)];
        }

        return "Vibrant";
    }

    private static List<string> BuildHighlightStyles(
        IReadOnlyList<string> sortedGenres,
        IReadOnlyList<string> sortedMoods,
        string mostCommonGenre,
        string mostCommonMood)
    {
        var highlights = sortedGenres
            .Take(3)
            .Concat(sortedMoods.Take(3))
            .Where(style => !string.Equals(style, mostCommonGenre, StringComparison.OrdinalIgnoreCase))
            .Where(style => !string.Equals(style, mostCommonMood, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (highlights.Count >= 6)
        {
            return highlights;
        }

        foreach (var style in sortedGenres.Concat(sortedMoods))
        {
            if (highlights.Contains(style, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            highlights.Add(style);
            if (highlights.Count >= 6)
            {
                break;
            }
        }

        return highlights;
    }

    private static string FormatHighlightStyles(List<string> styles)
    {
        if (styles.Count == 0)
        {
            return "eclectic";
        }

        if (styles.Count == 1)
        {
            return styles[0];
        }

        if (styles.Count == 2)
        {
            return $"{styles[0]} and {styles[1]}";
        }

        return $"{string.Join(", ", styles.Take(styles.Count - 1))}, and {styles[^1]}";
    }

    private static string ResolveDisplayUserName(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "you";
        }

        var first = username
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(first) ? username : first;
    }

    private static string GetNextUpdateTime(DateTimeOffset now, string generateAt)
    {
        var minutes = MelodayScheduleSlots.TryParseMinutes(generateAt);
        if (minutes is null)
        {
            return now.AddDays(7).ToString("dddd h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        }

        var nextUpdate = new DateTimeOffset(now.Year, now.Month, now.Day, minutes.Value / 60, minutes.Value % 60, 0, now.Offset)
            .AddDays(7);
        return nextUpdate.ToString("dddd h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
    }

    private Dictionary<string, List<string>> LoadDescriptorMap(MelodayOptions options)
    {
        var path = Path.Join(AppContext.BaseDirectory, options.MoodMapPath);
        if (!File.Exists(path))
        {
            _logger.LogWarning("Meloday mood map missing at {Path}", path);
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(path);
            var map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                      ?? new Dictionary<string, List<string>>();
            return new Dictionary<string, List<string>>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to parse Meloday mood map.");
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<GeneratedMelodayCover?> TryGenerateCoverAsync(
        MelodayOptions options,
        string slotName,
        string slotId,
        long libraryId,
        string mode,
        string weekdayId,
        string coverTagline,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webRoot))
        {
            return null;
        }

        var imageId = await _artworkAssignments.AssignAsync(libraryId, slotId, mode, weekdayId, cancellationToken);
        if (imageId is null)
        {
            _logger.LogWarning("Meloday artwork pool is empty at {Path}.", _artworkPool.SourceDirectory);
            return null;
        }

        var composed = _coverComposer.Compose(
            _artworkPool.ResolveSourcePath(imageId),
            slotName,
            coverTagline,
            libraryId,
            slotId,
            mode,
            weekdayId,
            options.BaseUrl);
        if (composed is null)
        {
            return null;
        }

        return new GeneratedMelodayCover(composed.Url, composed.FilePath, "image/jpeg");
    }

    private sealed record SimilarTrackContext(
        Dictionary<long, string> RatingKeyByTrackId,
        IReadOnlySet<long> ExcludedTrackIds,
        DateTimeOffset ExcludeStart,
        PlexAuth? Plex,
        MelodayOptions Options,
        Dictionary<long, PlexTrackMetadata> LiveMetadataByTrackId,
        IReadOnlySet<long> AllowedTrackIds,
        CancellationToken CancellationToken);

    private sealed record MediaServerTarget(
        string Service,
        PlexAuth? Plex,
        JellyfinAuth? Jellyfin,
        NavidromeAuth? Navidrome,
        string? Username)
    {
        public bool IsPlex => string.Equals(Service, "plex", StringComparison.OrdinalIgnoreCase);
        public bool IsJellyfin => string.Equals(Service, "jellyfin", StringComparison.OrdinalIgnoreCase);
        public bool IsNavidrome => string.Equals(Service, "navidrome", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record GeneratedMelodayCover(string? Url, string? FilePath, string ContentType);

    private sealed record MelodayPlaylistText(string Description, string CoverTagline);

    private sealed record PlaylistDescriptionContext(
        MelodayOptions Options,
        string SlotId,
        string SlotName,
        string SlotGenerateAt,
        MelodayDaypart Daypart,
        IReadOnlyList<long> TrackIds,
        Dictionary<long, PlexTrackMetadata> LiveMetadataByTrackId,
        Dictionary<long, PlexTrackMetadataDto> PersistedMetadataByTrackId,
        IReadOnlyDictionary<long, TrackAnalysisResultDto> TrackAnalysesByTrackId,
        string? Username,
        DateTimeOffset Now);

    private sealed record SourceTrackMatchContext(
        string SourceTitle,
        string SourceArtist,
        string SourceTitleNormalized,
        string SourceArtistNormalized);

    private sealed class TrackFilterState
    {
        public TrackFilterState(int maxTracks)
        {
            Seen = new HashSet<string>(StringComparer.Ordinal);
            ArtistCountByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ArtistLimit = Math.Max(1, (int)Math.Round(maxTracks * 0.05));
        }

        public HashSet<string> Seen { get; }
        public Dictionary<string, int> ArtistCountByName { get; }
        public int ArtistLimit { get; }
    }

    private sealed record MelodayDaypart(IReadOnlyList<int> Hours, string Phrase);
}
