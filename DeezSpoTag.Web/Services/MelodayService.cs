using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Services.Library;
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
    public string? LibraryName { get; set; }
    public string PlaylistPrefix { get; set; } = "Meloday for";
    public string? BaseUrl { get; set; }
    public int ExcludePlayedDays { get; set; } = 4;
    public int HistoryLookbackDays { get; set; } = 30;
    public int MaxTracks { get; set; } = 50;
    public double HistoricalRatio { get; set; } = 0.3;
    public int SonicSimilarLimit { get; set; } = 8;
    public double SonicSimilarityDistance { get; set; } = 0.35;
    public int UpdateIntervalMinutes { get; set; } = 30;
    public string Mode { get; set; } = MelodayModes.Sonic;
    public string MoodMapPath { get; set; } = "Resources/meloday/assets/moodmap.json";
    public List<string> SyncTargets { get; set; } = new();
}

public sealed record MelodayRunResult(bool Success, string Message, string? PlaylistId);
public sealed record MelodayStatusDto(
    bool Enabled,
    string CurrentPeriod,
    DateTimeOffset? LastRunUtc,
    string? LastMessage,
    int MaxTracks,
    int HistoryLookbackDays,
    int ExcludePlayedDays,
    string Mode);

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

public sealed class MelodayCollaborators
{
    public MelodayCollaborators(
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        NavidromeApiClient navidromeApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        PlexHistoryImportService historyImportService,
        JellyfinHistoryImportService jellyfinHistoryImportService,
        NavidromeHistoryImportService navidromeHistoryImportService)
    {
        PlexApiClient = plexApiClient;
        JellyfinApiClient = jellyfinApiClient;
        NavidromeApiClient = navidromeApiClient;
        AuthService = authService;
        LibraryRepository = libraryRepository;
        HistoryImportService = historyImportService;
        JellyfinHistoryImportService = jellyfinHistoryImportService;
        NavidromeHistoryImportService = navidromeHistoryImportService;
    }

    public PlexApiClient PlexApiClient { get; }
    public JellyfinApiClient JellyfinApiClient { get; }
    public NavidromeApiClient NavidromeApiClient { get; }
    public PlatformAuthService AuthService { get; }
    public LibraryRepository LibraryRepository { get; }
    public PlexHistoryImportService HistoryImportService { get; }
    public JellyfinHistoryImportService JellyfinHistoryImportService { get; }
    public NavidromeHistoryImportService NavidromeHistoryImportService { get; }
}

public sealed class MelodayService
{
    private const string DawnPeriodName = "Dawn";
    private const string EarlyMorningPeriodName = "Early Morning";
    private const string MorningPeriodName = "Morning";
    private const string AfternoonPeriodName = "Afternoon";
    private const string EveningPeriodName = "Evening";
    private const string NightPeriodName = "Night";
    private const string LateNightPeriodName = "Late Night";
    private const string MelodayAppUserName = "Meloday";
    private const string MelodayAppUserId = "deezspotag:meloday";
    private static readonly TimeSpan TargetSyncTimeout = TimeSpan.FromSeconds(20);
    private readonly MelodayOptions _options;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly PlexHistoryImportService _historyImportService;
    private readonly JellyfinHistoryImportService _jellyfinHistoryImportService;
    private readonly NavidromeHistoryImportService _navidromeHistoryImportService;
    private readonly ILogger<MelodayService> _logger;
    private readonly MelodaySettingsStore _settingsStore;
    private readonly Random _random = new();
    private readonly string _webRoot;
    private DateTimeOffset? _lastRunUtc;
    private string? _lastMessage;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex FeaturingParentheticalRegex = CreateRegex(@"(\(|\[)\s*(feat\.?|ft\.?|featuring).*?(\)|\])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeaturingInlineRegex = CreateRegex(@"\b(feat\.?|ft\.?|featuring)\s+[^\-\(\[]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record RunModeContext(
        IReadOnlyList<MediaServerTarget> SyncTargets,
        LibraryDto Library,
        IReadOnlyList<FolderDto> LibraryFolders,
        IReadOnlyList<long> HistoryTrackIds,
        IReadOnlyList<long> BalancedHistorical,
        SimilarTrackContext SimilarContext,
        string PeriodName,
        MelodayPeriod Period,
        string? Username,
        long MixUserId,
        PlexAuth? SonicPlex);

    private sealed record PlaylistSyncContext(
        MediaServerTarget Target,
        IReadOnlyList<FolderDto> LibraryFolders,
        string Title,
        string Description,
        IReadOnlyList<long> OrderedTrackIds,
        SimilarTrackContext SimilarContext,
        MelodayOptions TitleOptions,
        string PeriodName,
        GeneratedMelodayCover? Cover);
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

    private static readonly int[] DawnHours = [3, 4, 5];
    private static readonly int[] EarlyMorningHours = [6, 7, 8];
    private static readonly int[] MorningHours = [9, 10, 11];
    private static readonly int[] AfternoonHours = [12, 13, 14, 15];
    private static readonly int[] EveningHours = [16, 17, 18];
    private static readonly int[] NightHours = [19, 20, 21];
    private static readonly int[] LateNightHours = [22, 23, 0, 1, 2];

    private static readonly Dictionary<string, MelodayPeriod> DefaultPeriods = new Dictionary<string, MelodayPeriod>
    {
        [DawnPeriodName] = new MelodayPeriod(DawnHours, "at dawn"),
        [EarlyMorningPeriodName] = new MelodayPeriod(EarlyMorningHours, "in the early morning"),
        [MorningPeriodName] = new MelodayPeriod(MorningHours, "in the morning"),
        [AfternoonPeriodName] = new MelodayPeriod(AfternoonHours, "during the afternoon"),
        [EveningPeriodName] = new MelodayPeriod(EveningHours, "in the evening"),
        [NightPeriodName] = new MelodayPeriod(NightHours, "at night"),
        [LateNightPeriodName] = new MelodayPeriod(LateNightHours, "late at night")
    };

    public MelodayService(
        IOptions<MelodayOptions> options,
        MelodayCollaborators collaborators,
        IWebHostEnvironment env,
        ILogger<MelodayService> logger,
        MelodaySettingsStore settingsStore)
    {
        _options = options.Value;
        _plexApiClient = collaborators.PlexApiClient;
        _jellyfinApiClient = collaborators.JellyfinApiClient;
        _navidromeApiClient = collaborators.NavidromeApiClient;
        _authService = collaborators.AuthService;
        _libraryRepository = collaborators.LibraryRepository;
        _historyImportService = collaborators.HistoryImportService;
        _jellyfinHistoryImportService = collaborators.JellyfinHistoryImportService;
        _navidromeHistoryImportService = collaborators.NavidromeHistoryImportService;
        _webRoot = env.WebRootPath;
        _logger = logger;
        _settingsStore = settingsStore;
    }

    private Task<MelodayOptions> GetEffectiveOptionsAsync()
    {
        return _settingsStore.LoadAsync(_options);
    }

    public static string GetCurrentPeriodName(DateTimeOffset? now = null)
    {
        var hour = (now ?? DateTimeOffset.Now).Hour;
        var match = DefaultPeriods.FirstOrDefault(entry => entry.Value.Hours.Contains(hour));
        return string.IsNullOrWhiteSpace(match.Key) ? LateNightPeriodName : match.Key;
    }

    public async Task<MelodayRunResult> RunAsync(bool refreshHistory, CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectiveOptionsAsync();
        effective.Mode = MelodayModes.Normalize(effective.Mode);
        if (!effective.Enabled)
        {
            _lastMessage = "Meloday disabled.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var auth = await _authService.LoadAsync();
        var targets = ResolveTargetServers(auth, effective.SyncTargets);
        if (targets.Count == 0)
        {
            _lastMessage = "Plex, Jellyfin, or Navidrome auth missing.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        if (refreshHistory)
        {
            if (targets.Any(static target => target.IsPlex))
            {
                await _historyImportService.ImportAsync(cancellationToken);
            }

            if (targets.Any(static target => target.IsJellyfin))
            {
                await _jellyfinHistoryImportService.ImportAsync(cancellationToken);
            }

            if (targets.Any(static target => target.IsNavidrome))
            {
                await _navidromeHistoryImportService.ImportAsync(cancellationToken);
            }
        }

        await _libraryRepository.BackfillPlayHistoryLibraryIdsAsync(cancellationToken);
        await _libraryRepository.DeleteLegacyMelodayMixesAsync(cancellationToken);

        var configuredFolders = new List<FolderDto>();
        foreach (var folder in await _libraryRepository.GetConfiguredEnabledMusicFoldersAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(folder.PlexSectionId)
                && string.IsNullOrWhiteSpace(folder.JellyfinLibraryId)
                && string.IsNullOrWhiteSpace(folder.NavidromeLibraryId))
            {
                continue;
            }
            var tracks = await _libraryRepository.GetTrackIdsForLibraryScopeAsync(
                folder.LibraryId!.Value, folder.Id, cancellationToken);
            if (tracks.Count > 0)
            {
                configuredFolders.Add(folder);
            }
        }
        var libraries = ResolveMelodayLibraries(configuredFolders, effective.LibraryName);
        await _libraryRepository.DeleteInactiveMelodayMixesAsync(
            libraries.Select(static library => library.Id).ToList(), cancellationToken);
        if (libraries.Count == 0)
        {
            _lastMessage = string.IsNullOrWhiteSpace(effective.LibraryName)
                ? "No nonempty music folders with target-library bindings are configured."
                : $"Configured library '{effective.LibraryName.Trim()}' was not found, mapped, or contains no tracks.";
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

        var periodName = GetCurrentPeriodName();
        var period = DefaultPeriods[periodName];
        var now = DateTimeOffset.Now;
        var lookbackStart = now.AddDays(-effective.HistoryLookbackDays);
        var excludeStart = now.AddDays(-effective.ExcludePlayedDays);

        var sonicPlex = targets.FirstOrDefault(static target => target.IsPlex)?.Plex;
        var requestedModes = ResolveRunModes(effective.Mode);
        var results = new List<MelodayRunResult>();
        foreach (var library in libraries)
        {
            var libraryFolders = configuredFolders
                .Where(folder => folder.LibraryId == library.Id)
                .ToList();
            var history = new List<PlayHistoryEntryDto>();
            var excludedTrackIds = new HashSet<long>();
            foreach (var historyUserId in historyUserIds)
            {
                foreach (var folder in libraryFolders)
                {
                    var userHistory = await _libraryRepository.GetPlayHistoryEntriesAsync(
                        historyUserId, library.Id, lookbackStart, period.Hours, now,
                        cancellationToken, folder.Id);
                    history.AddRange(userHistory);

                    var userExcluded = await _libraryRepository.GetPlayedTrackIdsSinceAsync(
                        historyUserId, library.Id, excludeStart, cancellationToken, folder.Id);
                    excludedTrackIds.UnionWith(userExcluded);
                }
            }

            var historyTrackIds = history
                .Select(entry => entry.TrackId)
                .Where(id => !excludedTrackIds.Contains(id))
                .Distinct()
                .ToList();
            var ratingKeyByTrackId = new Dictionary<long, string>();
            var liveMetadataByTrackId = new Dictionary<long, PlexTrackMetadata>();
            var allowedTrackIds = new HashSet<long>();
            foreach (var folder in libraryFolders)
            {
                allowedTrackIds.UnionWith(await _libraryRepository.GetTrackIdsForLibraryScopeAsync(
                    library.Id, folder.Id, cancellationToken));
            }
            var balancedHistorical = BuildBalancedHistoricalSelection(
                history,
                excludedTrackIds,
                liveMetadataByTrackId,
                effective.MaxTracks);
            var similarContext = new SimilarTrackContext(
                ratingKeyByTrackId,
                excludedTrackIds,
                excludeStart,
                sonicPlex,
                effective,
                liveMetadataByTrackId,
                allowedTrackIds,
                cancellationToken);
            var runModeContext = new RunModeContext(
                targets,
                library,
                libraryFolders,
                historyTrackIds,
                balancedHistorical,
                similarContext,
                periodName,
                period,
                username,
                mixUserId,
                sonicPlex);

            foreach (var mode in requestedModes)
            {
                results.Add(await RunModeAsync(mode, runModeContext, cancellationToken));
            }
        }

        var successful = results.Where(static result => result.Success).ToList();
        if (successful.Count == 0)
        {
            _lastMessage = string.Join(" ", results.Select(static result => result.Message).Where(static message => !string.IsNullOrWhiteSpace(message)));
            return new MelodayRunResult(false, string.IsNullOrWhiteSpace(_lastMessage) ? "Meloday failed." : _lastMessage, null);
        }

        _lastRunUtc = DateTimeOffset.UtcNow;
        _lastMessage = $"Generated {successful.Count} of {results.Count} Meloday playlists across {libraries.Count} {(libraries.Count == 1 ? "library" : "libraries")}.";
        if (successful.Count < results.Count)
        {
            var failureMessages = results
                .Where(static result => !result.Success)
                .Select(static result => result.Message)
                .Where(static message => !string.IsNullOrWhiteSpace(message));
            _lastMessage += $" {string.Join(" ", failureMessages)}";
        }

        var firstPlaylistId = successful
            .Select(static result => result.PlaylistId)
            .FirstOrDefault(static playlistId => !string.IsNullOrWhiteSpace(playlistId));
        return new MelodayRunResult(true, _lastMessage, firstPlaylistId);
    }

    private async Task<MelodayRunResult> RunModeAsync(
        string mode,
        RunModeContext context,
        CancellationToken cancellationToken)
    {
        var finalTracks = string.Equals(mode, MelodayModes.Direct, StringComparison.OrdinalIgnoreCase)
            ? await BuildDirectTrackSelectionAsync(context.HistoryTrackIds, context.BalancedHistorical, context.Library.Id, context.SimilarContext)
            : await BuildSonicTrackSelectionAsync(context.HistoryTrackIds, context.BalancedHistorical, context.Library.Id, context.SimilarContext);

        if (finalTracks.Count == 0)
        {
            return new MelodayRunResult(false, $"No tracks available for {context.Library.Name} Meloday {GetModeLabel(mode)}.", null);
        }

        var selectedTrackIds = finalTracks.Take(context.SimilarContext.Options.MaxTracks).ToList();
        var orderedTrackIds = string.Equals(mode, MelodayModes.Direct, StringComparison.OrdinalIgnoreCase)
            ? OrderTracksDirect(selectedTrackIds, context.Period, context.SimilarContext.LiveMetadataByTrackId)
            : await OrderTracksSonicAsync(
                selectedTrackIds,
                context.Period,
                context.SonicPlex,
                context.SimilarContext.Options,
                context.SimilarContext.RatingKeyByTrackId,
                context.SimilarContext.LiveMetadataByTrackId,
                cancellationToken);

        var persistedMetadata = (await _libraryRepository.GetPlexTrackMetadataAsync(orderedTrackIds, cancellationToken))
            .ToDictionary(entry => entry.TrackId);

        var playlistPrefix = string.IsNullOrWhiteSpace(context.SimilarContext.Options.PlaylistPrefix)
            ? "Meloday"
            : context.SimilarContext.Options.PlaylistPrefix.Trim();
        var optionsForTitle = CloneOptionsWithPlaylistPrefix(
            context.SimilarContext.Options,
            $"{playlistPrefix} {context.Library.Name} {GetModeLabel(mode)}");
        var (title, description) = BuildTitleAndDescription(new PlaylistDescriptionContext(
            optionsForTitle,
            context.PeriodName,
            context.Period,
            orderedTrackIds,
            context.SimilarContext.LiveMetadataByTrackId,
            persistedMetadata,
            context.Username,
            DateTimeOffset.Now));

        var mixCacheId = await _libraryRepository.UpsertMixCacheAsync(
            new LibraryRepository.MixCacheUpsertInput(
                BuildMelodayMixId(mode, context.Library.Id),
                context.MixUserId,
                context.Library.Id,
                title,
                description,
                Array.Empty<string>(),
                orderedTrackIds.Count,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(Math.Max(5, context.SimilarContext.Options.UpdateIntervalMinutes))),
            cancellationToken);
        await _libraryRepository.ReplaceMixItemsAsync(mixCacheId, orderedTrackIds, cancellationToken);

        var cover = await TryGenerateCoverAsync(
            optionsForTitle,
            context.PeriodName,
            context.Library.Id,
            mode,
            cancellationToken);
        var syncResults = new List<(string Service, string? PlaylistId)>();
        foreach (var target in context.SyncTargets)
        {
            var targetPlaylistId = await TrySyncTargetPlaylistAsync(
                new PlaylistSyncContext(
                    target,
                    context.LibraryFolders,
                    title,
                    description,
                    orderedTrackIds,
                    context.SimilarContext,
                    optionsForTitle,
                    context.PeriodName,
                    cover),
                cancellationToken);
            syncResults.Add((target.Service, targetPlaylistId));
        }

        var synced = syncResults
            .Where(static result => !string.IsNullOrWhiteSpace(result.PlaylistId))
            .ToList();
        if (synced.Count == 0)
        {
            return new MelodayRunResult(true, $"{context.Library.Name} Meloday {GetModeLabel(mode)} was created in the app but was not synced to any target server.", null);
        }

        var services = string.Join(", ", synced.Select(static result => result.Service));
        return new MelodayRunResult(true, $"{context.Library.Name} Meloday {GetModeLabel(mode)} playlist updated in {services}.", synced[0].PlaylistId);
    }

    private async Task<string?> TrySyncTargetPlaylistAsync(
        PlaylistSyncContext context,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TargetSyncTimeout);

        try
        {
            return context.Target.Service switch
            {
                "plex" => await SyncMelodayToPlexAsync(context, timeout.Token),
                "jellyfin" => await SyncMelodayToJellyfinAsync(context, timeout.Token),
                "navidrome" => await SyncMelodayToNavidromeAsync(context, timeout.Token),
                _ => null
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Meloday target sync failed for {TargetService}.", context.Target.Service);
            return null;
        }
    }

    private static string[] ResolveRunModes(string mode)
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

    private static string BuildMelodayMixId(string mode, long libraryId)
        => $"meloday-{MelodayModes.Normalize(mode)}-{libraryId}";

    private static MelodayOptions CloneOptionsWithPlaylistPrefix(MelodayOptions source, string playlistPrefix) => new()
    {
        Enabled = source.Enabled,
        LibraryName = source.LibraryName,
        PlaylistPrefix = playlistPrefix,
        BaseUrl = source.BaseUrl,
        ExcludePlayedDays = source.ExcludePlayedDays,
        HistoryLookbackDays = source.HistoryLookbackDays,
        MaxTracks = source.MaxTracks,
        HistoricalRatio = source.HistoricalRatio,
        SonicSimilarLimit = source.SonicSimilarLimit,
        SonicSimilarityDistance = source.SonicSimilarityDistance,
        UpdateIntervalMinutes = source.UpdateIntervalMinutes,
        Mode = source.Mode,
        MoodMapPath = source.MoodMapPath,
        SyncTargets = NormalizeSyncTargets(source.SyncTargets).ToList()
    };

    private static IReadOnlyList<MediaServerTarget> ResolveTargetServers(PlatformAuthState auth, IReadOnlyList<string>? syncTargets)
    {
        var allowedTargets = NormalizeSyncTargets(syncTargets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new List<MediaServerTarget>();
        if (allowedTargets.Contains("plex") && TryGetPlexConnection(auth.Plex, out var plex, out _, out _))
        {
            var username = !string.IsNullOrWhiteSpace(plex.Username) ? plex.Username : plex.ServerName;
            targets.Add(new MediaServerTarget("plex", plex, null, null, username));
        }

        if (allowedTargets.Contains("jellyfin")
            && auth.Jellyfin is { } jellyfin
            && !string.IsNullOrWhiteSpace(jellyfin.Url)
            && !string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            && !string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            var username = !string.IsNullOrWhiteSpace(jellyfin.Username) ? jellyfin.Username : jellyfin.ServerName;
            targets.Add(new MediaServerTarget("jellyfin", null, jellyfin, null, username));
        }

        if (allowedTargets.Contains("navidrome")
            && auth.Navidrome is { } navidrome
            && !string.IsNullOrWhiteSpace(navidrome.Url)
            && !string.IsNullOrWhiteSpace(navidrome.Username)
            && !string.IsNullOrWhiteSpace(navidrome.Password))
        {
            var username = !string.IsNullOrWhiteSpace(navidrome.Username) ? navidrome.Username : navidrome.ServerName;
            targets.Add(new MediaServerTarget("navidrome", null, null, navidrome, username));
        }

        return targets;
    }

    private static IReadOnlyList<string> NormalizeSyncTargets(IReadOnlyList<string>? targets)
        => targets?
            .Select(NormalizeSyncTarget)
            .Where(static target => !string.IsNullOrWhiteSpace(target))
            .Select(static target => target!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() is { Count: > 0 } normalized
            ? normalized
            : new[] { "plex", "jellyfin", "navidrome" };

    private static string? NormalizeSyncTarget(string? target)
        => target?.Trim().ToLowerInvariant() switch
        {
            "plex" => "plex",
            "jellyfin" => "jellyfin",
            "navidrome" => "navidrome",
            _ => null
        };

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

    private async Task<string?> SyncMelodayToPlexAsync(
        PlaylistSyncContext context,
        CancellationToken cancellationToken)
    {
        var plex = context.Target.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return null;
        }

        var ratingKeyList = await ResolvePlexRatingKeysForSyncAsync(
            context.OrderedTrackIds,
            context.LibraryFolders,
            context.SimilarContext.RatingKeyByTrackId,
            cancellationToken);
        if (ratingKeyList.Count == 0)
        {
            return null;
        }

        var machineId = plex.MachineIdentifier;
        if (string.IsNullOrWhiteSpace(machineId))
        {
            var identity = await _plexApiClient.GetIdentityAsync(plex.Url, plex.Token, cancellationToken);
            machineId = identity?.MachineIdentifier;
        }

        if (string.IsNullOrWhiteSpace(machineId))
        {
            return null;
        }

        var playlistId = await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            machineId,
            context.Title,
            ratingKeyList,
            options: new PlexApiClient.PlaylistUpsertOptions(ExistingTitlePrefix: context.TitleOptions.PlaylistPrefix),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        await _plexApiClient.UpdatePlaylistMetadataAsync(
            plex.Url,
            plex.Token,
            playlistId,
            context.Title,
            context.Description,
            cancellationToken);

        if (context.Cover is { FilePath: { Length: > 0 } filePath } && File.Exists(filePath))
        {
            await _plexApiClient.UpdatePlaylistPosterFromFileAsync(
                plex.Url,
                plex.Token,
                playlistId,
                filePath,
                context.Cover.ContentType,
                cancellationToken);
            return playlistId;
        }

        if (!string.IsNullOrWhiteSpace(context.Cover?.Url))
        {
            await _plexApiClient.UpdatePlaylistPosterFromUrlAsync(
                plex.Url,
                plex.Token,
                playlistId,
                context.Cover.Url,
                cancellationToken);
        }

        return playlistId;
    }

    private async Task<List<string>> ResolvePlexRatingKeysForSyncAsync(
        IReadOnlyList<long> orderedTrackIds,
        IReadOnlyList<FolderDto> folders,
        Dictionary<long, string> ratingKeyByTrackId,
        CancellationToken cancellationToken)
    {
        var plex = (await _authService.LoadAsync()).Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return new List<string>();
        }
        var remoteTracks = new List<PlexTrack>();
        foreach (var sectionId in folders.Select(static folder => folder.PlexSectionId)
                     .Where(static id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var offset = 0; ; offset += 500)
            {
                var page = await _plexApiClient.GetLibraryTracksAsync(
                    plex.Url, plex.Token, sectionId!, offset, 500, cancellationToken);
                remoteTracks.AddRange(page);
                if (page.Count < 500) break;
            }
        }
        var summaries = (await _libraryRepository.GetTrackSummariesAsync(orderedTrackIds, cancellationToken))
            .ToDictionary(static summary => summary.TrackId);
        var ratingKeys = new List<string>();

        foreach (var trackId in orderedTrackIds)
        {
            if (!summaries.TryGetValue(trackId, out var summary))
            {
                continue;
            }
            var knownRatingKey = SelectBestPlexTrackMatch(summary, remoteTracks)?.RatingKey;
            if (string.IsNullOrWhiteSpace(knownRatingKey))
            {
                continue;
            }

            ratingKeyByTrackId[trackId] = knownRatingKey;
            if (!ratingKeys.Contains(knownRatingKey, StringComparer.OrdinalIgnoreCase))
            {
                ratingKeys.Add(knownRatingKey);
            }
        }

        return ratingKeys;
    }

    private async Task<string?> SyncMelodayToJellyfinAsync(
        PlaylistSyncContext context,
        CancellationToken cancellationToken)
    {
        var jellyfin = context.Target.Jellyfin;
        if (jellyfin is null
            || string.IsNullOrWhiteSpace(jellyfin.Url)
            || string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            || string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            return null;
        }

        var itemIds = await ResolveJellyfinItemIdsAsync(
            jellyfin, context.LibraryFolders, context.OrderedTrackIds, cancellationToken);
        if (itemIds.Count == 0)
        {
            return null;
        }

        var playlistId = await _jellyfinApiClient.FindPlaylistIdByNameAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            context.Title,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = await _jellyfinApiClient.CreatePlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                context.Title,
                itemIds,
                cancellationToken);
        }
        else
        {
            var entries = await _jellyfinApiClient.GetPlaylistEntriesAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                cancellationToken);
            if (entries.Count > 0)
            {
                await _jellyfinApiClient.RemovePlaylistEntriesAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    jellyfin.UserId,
                    playlistId,
                    entries.Select(static entry => entry.PlaylistEntryId).ToList(),
                    cancellationToken);
            }

            await _jellyfinApiClient.AddPlaylistItemsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                itemIds,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            await _jellyfinApiClient.UpdateItemMetadataAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                context.Title,
                context.Description,
                cancellationToken);
            if (context.Cover is { FilePath: { Length: > 0 } filePath } && File.Exists(filePath))
            {
                await _jellyfinApiClient.UpdateItemPrimaryImageFromFileAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    playlistId,
                    filePath,
                    context.Cover.ContentType,
                    cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(context.Cover?.Url))
            {
                await _jellyfinApiClient.UpdateItemPrimaryImageFromUrlAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    playlistId,
                    context.Cover.Url,
                    cancellationToken);
            }
        }

        return playlistId;
    }

    private async Task<string?> SyncMelodayToNavidromeAsync(
        PlaylistSyncContext context,
        CancellationToken cancellationToken)
    {
        var navidrome = context.Target.Navidrome;
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            return null;
        }

        var itemIds = await ResolveNavidromeItemIdsAsync(
            navidrome, context.LibraryFolders, context.OrderedTrackIds, cancellationToken);
        if (itemIds.Count == 0)
        {
            return null;
        }

        return await _navidromeApiClient.CreateOrUpdatePlaylistAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            context.Title,
            itemIds,
            existingPlaylistId: null,
            appendMissingOnly: false,
            cancellationToken);
    }

    private async Task<List<string>> ResolveJellyfinItemIdsAsync(
        JellyfinAuth jellyfin,
        IReadOnlyList<FolderDto> folders,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var summaries = await _libraryRepository.GetTrackSummariesAsync(orderedTrackIds, cancellationToken);
        var summaryByTrackId = summaries.ToDictionary(static summary => summary.TrackId);
        var itemIds = new List<string>();
        var libraryTracks = new List<JellyfinAudioTrack>();
        foreach (var libraryId in folders.Select(static folder => folder.JellyfinLibraryId)
                     .Where(static id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var offset = 0; ; offset += 1000)
            {
                var page = await _jellyfinApiClient.GetAudioTracksAsync(
                    jellyfin.Url!, jellyfin.ApiKey!, jellyfin.UserId!, libraryId,
                    offset, 1000, cancellationToken);
                libraryTracks.AddRange(page);
                if (page.Count < 1000) break;
            }
        }
        foreach (var trackId in orderedTrackIds)
        {
            if (!summaryByTrackId.TryGetValue(trackId, out var summary))
            {
                continue;
            }

            var match = libraryTracks.FirstOrDefault(candidate => IsJellyfinTrackMatch(summary, candidate));
            if (match is not null && !itemIds.Contains(match.Id, StringComparer.OrdinalIgnoreCase))
            {
                itemIds.Add(match.Id);
            }
        }

        return itemIds;
    }

    private async Task<List<string>> ResolveNavidromeItemIdsAsync(
        NavidromeAuth navidrome,
        IReadOnlyList<FolderDto> folders,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var summaries = await _libraryRepository.GetTrackSummariesAsync(orderedTrackIds, cancellationToken);
        var summaryByTrackId = summaries.ToDictionary(static summary => summary.TrackId);
        var itemIds = new List<string>();
        var cache = new Dictionary<string, List<NavidromeAudioTrack>>(StringComparer.OrdinalIgnoreCase);
        var allowedLibraryIds = folders
            .Select(static folder => folder.NavidromeLibraryId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var trackId in orderedTrackIds)
        {
            if (!summaryByTrackId.TryGetValue(trackId, out var summary))
            {
                continue;
            }

            var query = $"{summary.Title} {summary.ArtistName}".Trim();
            if (!cache.TryGetValue(query, out var matches))
            {
                matches = await _navidromeApiClient.SearchTracksAsync(
                    navidrome.Url!,
                    navidrome.Username!,
                    navidrome.Password!,
                    query,
                    cancellationToken);
                cache[query] = matches;
            }

            var match = matches.FirstOrDefault(candidate =>
                allowedLibraryIds.Contains(candidate.LibraryId ?? string.Empty)
                && IsNavidromeTrackMatch(summary, candidate));
            if (match is not null && !itemIds.Contains(match.Id, StringComparer.OrdinalIgnoreCase))
            {
                itemIds.Add(match.Id);
            }
        }

        return itemIds;
    }

    private static bool IsJellyfinTrackMatch(MixTrackDto summary, JellyfinAudioTrack candidate)
    {
        return string.Equals(NormalizeComparableText(summary.Title), NormalizeComparableText(candidate.Name), StringComparison.Ordinal)
            && NormalizeComparableText(candidate.Artist).Contains(NormalizeComparableText(summary.ArtistName), StringComparison.Ordinal);
    }

    private static bool IsNavidromeTrackMatch(MixTrackDto summary, NavidromeAudioTrack candidate)
    {
        return string.Equals(NormalizeComparableText(summary.Title), NormalizeComparableText(candidate.Title), StringComparison.Ordinal)
            && NormalizeComparableText(candidate.Artist).Contains(NormalizeComparableText(summary.ArtistName), StringComparison.Ordinal);
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

    private static IReadOnlyList<LibraryDto> ResolveMelodayLibraries(
        IReadOnlyList<FolderDto> folders,
        string? libraryName)
    {
        return folders
            .Where(folder => folder.LibraryId.HasValue && !string.IsNullOrWhiteSpace(folder.LibraryName))
            .Where(folder => string.IsNullOrWhiteSpace(libraryName)
                || string.Equals(folder.LibraryName, libraryName.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(folder => folder.LibraryId!.Value)
            .Select(group => new LibraryDto(group.Key, group.First().LibraryName!))
            .OrderBy(library => library.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<long>> BuildInitialTrackSelectionAsync(
        IReadOnlyList<long> historyTrackIds,
        IReadOnlyList<long> balancedHistorical,
        SimilarTrackContext context)
    {
        var guaranteedCount = Math.Clamp(
            (int)Math.Round(context.Options.MaxTracks * context.Options.HistoricalRatio),
            1,
            context.Options.MaxTracks);
        var guaranteedHistorical = Sample(balancedHistorical, guaranteedCount).ToList();
        if (guaranteedHistorical.Count == 0)
        {
            guaranteedHistorical = Sample(historyTrackIds, guaranteedCount).ToList();
        }

        var similarTracks = await FetchSonicSimilarTrackIdsAsync(guaranteedHistorical, context);
        var candidatePool = guaranteedHistorical
            .Concat(similarTracks)
            .Distinct()
            .ToList();

        if (context.Plex is not null)
        {
            await EnsureRatingKeysAsync(candidatePool, context.Plex, context.RatingKeyByTrackId, context.CancellationToken);
            await EnsurePlexMetadataAsync(context.Plex, candidatePool, context.RatingKeyByTrackId, context.LiveMetadataByTrackId, context.CancellationToken);
        }
        return (await ProcessTracksAsync(candidatePool, context.Options, context.LiveMetadataByTrackId, context.CancellationToken)).ToList();
    }

    private async Task<List<long>> BuildDirectTrackSelectionAsync(
        IReadOnlyList<long> historyTrackIds,
        IReadOnlyList<long> balancedHistorical,
        long libraryId,
        SimilarTrackContext context)
    {
        var finalTracks = Sample(balancedHistorical, context.Options.MaxTracks).ToList();
        if (finalTracks.Count == 0)
        {
            finalTracks = Sample(historyTrackIds, context.Options.MaxTracks).ToList();
        }

        var processed = await ProcessTracksAsync(finalTracks, context.Options, context.LiveMetadataByTrackId, context.CancellationToken);
        finalTracks = processed.ToList();
        await FillWithRandomTracksAsync(libraryId, finalTracks, context);
        return finalTracks;
    }

    private async Task<List<long>> BuildSonicTrackSelectionAsync(
        IReadOnlyList<long> historyTrackIds,
        IReadOnlyList<long> balancedHistorical,
        long libraryId,
        SimilarTrackContext context)
    {
        var finalTracks = await BuildInitialTrackSelectionAsync(historyTrackIds, balancedHistorical, context);
        await ExpandTrackSelectionAsync(finalTracks, balancedHistorical, context);
        await ExpandWithLocalVibeAnalysisAsync(finalTracks, balancedHistorical, libraryId, context);
        await FillWithRandomTracksAsync(libraryId, finalTracks, context);
        return finalTracks;
    }

    private async Task ExpandWithLocalVibeAnalysisAsync(
        List<long> finalTracks,
        IReadOnlyList<long> balancedHistorical,
        long libraryId,
        SimilarTrackContext context)
    {
        if (finalTracks.Count >= context.Options.MaxTracks)
        {
            return;
        }

        var seedTrackIds = finalTracks.Count > 0 ? finalTracks : balancedHistorical.ToList();
        if (seedTrackIds.Count == 0)
        {
            return;
        }

        var candidates = await CollectLocalVibeSimilarTrackIdsAsync(
            seedTrackIds,
            libraryId,
            context.Options.MaxTracks - finalTracks.Count,
            context);
        if (candidates.Count == 0)
        {
            return;
        }

        var processed = await ProcessTracksAsync(candidates, context.Options, context.LiveMetadataByTrackId, context.CancellationToken);
        AppendUniqueTracks(finalTracks, processed, context.Options.MaxTracks);
    }

    private async Task<List<long>> CollectLocalVibeSimilarTrackIdsAsync(
        IReadOnlyList<long> seedTrackIds,
        long libraryId,
        int limit,
        SimilarTrackContext context)
    {
        var selected = new List<(long TrackId, double Score)>();
        var seen = new HashSet<long>(seedTrackIds);
        foreach (var seedTrackId in seedTrackIds.Distinct().Take(Math.Max(1, context.Options.SonicSimilarLimit)))
        {
            var source = await _libraryRepository.GetTrackAnalysisAsync(seedTrackId, context.CancellationToken);
            if (source is null || !IsCompletedAnalysis(source))
            {
                continue;
            }

            var candidates = await _libraryRepository.GetTrackAnalysisCandidatesAsync(
                libraryId,
                seedTrackId,
                Math.Clamp(context.Options.SonicSimilarLimit * 8, 25, 250),
                context.CancellationToken);
            var sourceVector = BuildLocalVibeVector(source);
            foreach (var candidate in candidates)
            {
                if (!IsCompletedAnalysis(candidate)
                    || context.ExcludedTrackIds.Contains(candidate.TrackId)
                    || seen.Contains(candidate.TrackId))
                {
                    continue;
                }

                selected.Add((candidate.TrackId, CosineSimilarity(sourceVector, BuildLocalVibeVector(candidate))));
                seen.Add(candidate.TrackId);
            }
        }

        return selected
            .OrderByDescending(static item => item.Score)
            .Take(Math.Max(0, limit))
            .Select(static item => item.TrackId)
            .ToList();
    }

    private async Task ExpandTrackSelectionAsync(
        List<long> finalTracks,
        IReadOnlyList<long> balancedHistorical,
        SimilarTrackContext context)
    {
        var attempts = 0;
        while (finalTracks.Count < context.Options.MaxTracks)
        {
            attempts++;
            if (attempts > 8)
            {
                return;
            }

            var extraCandidates = await BuildExtraCandidatesAsync(finalTracks, balancedHistorical, context);
            if (extraCandidates.Count == 0)
            {
                return;
            }

            if (context.Plex is not null)
            {
                await EnsureRatingKeysAsync(extraCandidates, context.Plex, context.RatingKeyByTrackId, context.CancellationToken);
                await EnsurePlexMetadataAsync(context.Plex, extraCandidates, context.RatingKeyByTrackId, context.LiveMetadataByTrackId, context.CancellationToken);
            }
            var processedExtra = await ProcessTracksAsync(extraCandidates, context.Options, context.LiveMetadataByTrackId, context.CancellationToken);
            if (!AppendUniqueTracks(finalTracks, processedExtra, context.Options.MaxTracks))
            {
                return;
            }
        }
    }

    private async Task<List<long>> BuildExtraCandidatesAsync(
        List<long> finalTracks,
        IReadOnlyList<long> balancedHistorical,
        SimilarTrackContext context)
    {
        var missing = context.Options.MaxTracks - finalTracks.Count;
        var extraHistorical = Sample(
                balancedHistorical
                    .Where(id => !finalTracks.Contains(id))
                    .ToList(),
                missing)
            .ToList();
        var extraSimilar = await FetchSonicSimilarTrackIdsAsync(finalTracks, context);
        return extraHistorical
            .Concat(extraSimilar)
            .Where(id => !finalTracks.Contains(id))
            .Distinct()
            .ToList();
    }

    private async Task FillWithRandomTracksAsync(
        long libraryId,
        List<long> finalTracks,
        SimilarTrackContext context)
    {
        if (finalTracks.Count >= context.Options.MaxTracks)
        {
            return;
        }

        var missing = context.Options.MaxTracks - finalTracks.Count;
        var randomPool = await _libraryRepository.GetRandomTrackIdsAsync(
            libraryId,
            Math.Max(missing * 6, context.Options.MaxTracks * 2),
            context.CancellationToken);
        var randomCandidates = randomPool
            .Where(id => !context.ExcludedTrackIds.Contains(id))
            .Where(id => !finalTracks.Contains(id))
            .Distinct()
            .ToList();

        var processedRandom = await ProcessTracksAsync(randomCandidates, context.Options, context.LiveMetadataByTrackId, context.CancellationToken);
        AppendUniqueTracks(finalTracks, processedRandom, context.Options.MaxTracks);
    }

    private static bool AppendUniqueTracks(
        List<long> destination,
        IReadOnlyList<long> source,
        int maxTracks)
    {
        var before = destination.Count;
        foreach (var trackId in source)
        {
            if (destination.Contains(trackId))
            {
                continue;
            }

            destination.Add(trackId);
            if (destination.Count >= maxTracks)
            {
                break;
            }
        }

        return destination.Count > before;
    }

    public async Task<MelodayStatusDto> GetStatusAsync()
    {
        var effective = await _settingsStore.LoadAsync(_options);
        return new MelodayStatusDto(
            effective.Enabled,
            GetCurrentPeriodName(),
            _lastRunUtc,
            _lastMessage,
            effective.MaxTracks,
            effective.HistoryLookbackDays,
            effective.ExcludePlayedDays,
            MelodayModes.Normalize(effective.Mode));
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
        Dictionary<long, PlexTrackMetadata> metadataByTrackId,
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

        var genreCount = BuildGenreCount(filteredHistory, metadataByTrackId);
        return RebalanceDominantGenre(balanced, genreCount, metadataByTrackId, maxTracks);
    }

    private static Dictionary<string, int> BuildGenreCount(
        IReadOnlyList<PlayHistoryEntryDto> filteredHistory,
        Dictionary<long, PlexTrackMetadata> metadataByTrackId)
    {
        var genreCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in filteredHistory)
        {
            if (!metadataByTrackId.TryGetValue(entry.TrackId, out var metadata))
            {
                continue;
            }

            foreach (var genre in metadata.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre)))
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
        Dictionary<long, PlexTrackMetadata> metadataByTrackId,
        int maxTracks)
    {
        if (genreCount.Count == 0)
        {
            return balanced;
        }

        var mostCommon = genreCount.OrderByDescending(entry => entry.Value).First();
        var maxGenreLimit = Math.Max(1, (int)(maxTracks * 0.25));
        if (mostCommon.Value <= maxGenreLimit)
        {
            return balanced;
        }

        var nonDominant = balanced
            .Where(trackId => !TrackHasGenre(metadataByTrackId, trackId, mostCommon.Key))
            .Take(maxGenreLimit)
            .ToList();
        var dominant = balanced
            .Where(trackId => TrackHasGenre(metadataByTrackId, trackId, mostCommon.Key))
            .Take(maxGenreLimit)
            .ToList();

        var rebalanced = nonDominant
            .Concat(dominant)
            .Distinct()
            .ToList();
        return rebalanced.Count > 0 ? rebalanced : balanced;
    }

    private static bool TrackHasGenre(
        Dictionary<long, PlexTrackMetadata> metadataByTrackId,
        long trackId,
        string genre)
    {
        return metadataByTrackId.TryGetValue(trackId, out var metadata)
               && metadata.Genres.Any(value => string.Equals(value, genre, StringComparison.OrdinalIgnoreCase));
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
        CancellationToken cancellationToken)
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

        var state = new TrackFilterState(options.MaxTracks);
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

        var primaryGenre = GetPrimaryGenre(liveMetadata, persistedMetadata) ?? "Unknown";
        if (HasReachedLimit(state.GenreCountByName, primaryGenre, state.GenreLimit))
        {
            return false;
        }

        IncrementCount(state.ArtistCountByName, artistName);
        IncrementCount(state.GenreCountByName, primaryGenre);
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
            if (liveMetadata.ArtistUserRating.HasValue && liveMetadata.ArtistUserRating.Value <= 2)
            {
                return true;
            }

            if (liveMetadata.AlbumUserRating.HasValue && liveMetadata.AlbumUserRating.Value <= 2)
            {
                return true;
            }

            if (liveMetadata.UserRating.HasValue && liveMetadata.UserRating.Value <= 2)
            {
                return true;
            }
        }

        return persistedMetadata?.UserRating is <= 2;
    }

    private static string? GetPrimaryGenre(PlexTrackMetadata? liveMetadata, PlexTrackMetadataDto? persistedMetadata)
    {
        if (liveMetadata?.Genres.Count > 0)
        {
            return liveMetadata.Genres[0];
        }

        if (persistedMetadata?.Genres.Count > 0)
        {
            return persistedMetadata.Genres[0];
        }

        return null;
    }

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
        MelodayPeriod period,
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
        MelodayPeriod period,
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

    private static double[] BuildLocalVibeVector(TrackAnalysisResultDto track)
    {
        return new[]
        {
            track.MoodHappy ?? 0.5,
            track.MoodSad ?? 0.5,
            track.MoodRelaxed ?? 0.5,
            track.MoodAggressive ?? 0.5,
            track.MoodParty ?? 0.5,
            track.MoodAcoustic ?? 0.5,
            track.MoodElectronic ?? 0.5,
            track.Energy ?? 0.5,
            track.ValenceMl ?? track.Valence ?? 0.5,
            track.ArousalMl ?? track.Arousal ?? 0.5,
            track.DanceabilityMl ?? track.Danceability ?? 0.5,
            track.Instrumentalness ?? 0.5,
            NormalizeBpm(track.Bpm ?? 120)
        };
    }

    private static double NormalizeBpm(double bpm)
    {
        if (bpm <= 0)
        {
            return 0.5;
        }

        while (bpm < 77)
        {
            bpm *= 2;
        }

        while (bpm > 154)
        {
            bpm /= 2;
        }

        return Math.Clamp((bpm - 77d) / 77d, 0d, 1d);
    }

    private static double CosineSimilarity(double[] left, double[] right)
    {
        var dot = 0d;
        var leftMagnitude = 0d;
        var rightMagnitude = 0d;
        var count = Math.Min(left.Length, right.Length);
        for (var index = 0; index < count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private async Task<IReadOnlyList<long>> OrderTracksAsync(
        List<long> trackIds,
        MelodayPeriod period,
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
        MelodayPeriod period,
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
        MelodayPeriod period,
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

    private (string Title, string Description) BuildTitleAndDescription(PlaylistDescriptionContext context)
    {
        var genres = new List<string>();
        var moods = new List<string>();

        foreach (var trackId in context.TrackIds)
        {
            if (context.LiveMetadataByTrackId.TryGetValue(trackId, out var liveMetadata))
            {
                genres.AddRange(liveMetadata.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre)));
                moods.AddRange(liveMetadata.Moods.Where(mood => !string.IsNullOrWhiteSpace(mood)));
                continue;
            }

            if (!context.PersistedMetadataByTrackId.TryGetValue(trackId, out var persistedMetadata))
            {
                continue;
            }

            genres.AddRange(persistedMetadata.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre)));
            moods.AddRange(persistedMetadata.Moods.Where(mood => !string.IsNullOrWhiteSpace(mood)));
        }

        var sortedGenres = SortByFrequency(genres);
        var sortedMoods = SortByFrequency(moods);

        var mostCommonGenre = sortedGenres.Count > 0 ? sortedGenres[0] : "Eclectic";
        var mostCommonMood = sortedMoods.Count > 0 ? sortedMoods[0] : "Vibes";
        var secondCommonMood = sortedMoods.Count > 1 ? sortedMoods[1] : null;

        var descriptorMap = LoadDescriptorMap(context.Options);
        var descriptorSource = secondCommonMood ?? mostCommonMood;
        var descriptor = ChooseDescriptor(descriptorMap, descriptorSource);

        var dayName = context.Now.ToString("dddd");
        var title = context.Options.PlaylistPrefix.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Meloday";
        }

        var highlights = BuildHighlightStyles(sortedGenres, sortedMoods, mostCommonGenre, mostCommonMood);
        var highlightsText = FormatHighlightStyles(highlights);

        var description = secondCommonMood is not null
            ? $"You listened to {mostCommonMood} and {mostCommonGenre} tracks on {dayName} {context.Period.Phrase}. Here's some {highlightsText} tracks as well."
            : $"You listened to {mostCommonGenre} and {mostCommonMood} tracks on {dayName} {context.Period.Phrase}. Here's some {highlightsText} tracks as well.";

        var displayUser = ResolveDisplayUserName(context.Username);
        var nextUpdate = GetNextUpdateTime(context.Now, context.Period.Hours);
        description += $"\n\nMade for {displayUser} • Next update at {nextUpdate}.";

        return (title, description);
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

    private static string GetNextUpdateTime(DateTimeOffset now, IReadOnlyList<int> periodHours)
    {
        if (periodHours.Count == 0)
        {
            return now.AddHours(1).ToString("h:mm tt");
        }

        var nextHour = (periodHours[^1] + 1) % 24;
        var nextUpdate = new DateTimeOffset(now.Year, now.Month, now.Day, nextHour, 0, 0, now.Offset);
        if (nextUpdate <= now)
        {
            nextUpdate = nextUpdate.AddDays(1);
        }

        return nextUpdate.ToString("h:mm tt");
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
        string periodName,
        long libraryId,
        string mode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webRoot))
        {
            return null;
        }

        var staticPosterPath = TryResolveStaticCoverPath(periodName, libraryId, mode);
        if (!string.IsNullOrWhiteSpace(staticPosterPath))
        {
            return new GeneratedMelodayCover(
                TryResolveStaticCoverUrl(options, periodName, libraryId, mode),
                staticPosterPath,
                ResolveContentType(staticPosterPath));
        }

        _logger.LogWarning("Meloday artwork source missing at {Path}", Path.Join(_webRoot, "images", "meloday"));
        return null;
    }

    private string? TryResolveStaticCoverPath(string periodName, long libraryId, string mode)
    {
        var staticDir = Path.Join(_webRoot, "images", "meloday");
        if (!Directory.Exists(staticDir))
        {
            return null;
        }

        var candidates = Directory.EnumerateFiles(staticDir)
            .Where(path =>
            {
                var ext = Path.GetExtension(path);
                return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var index = GetArtworkIndex(periodName, libraryId, mode, candidates.Count);
        return candidates[index];
    }

    private string? TryResolveStaticCoverUrl(MelodayOptions options, string periodName, long libraryId, string mode)
    {
        var staticDir = Path.Join(_webRoot, "images", "meloday");
        if (!Directory.Exists(staticDir))
        {
            return null;
        }

        var baseUrl = options.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var candidates = Directory.EnumerateFiles(staticDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Where(name =>
            {
                var ext = Path.GetExtension(name);
                return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var index = GetArtworkIndex(periodName, libraryId, mode, candidates.Count);
        var selected = candidates[index];
        return $"{baseUrl}/images/meloday/{Uri.EscapeDataString(selected)}";
    }

    private static int GetArtworkIndex(string periodName, long libraryId, string mode, int candidateCount)
    {
        if (candidateCount <= 1) return 0;
        var modeOffset = string.Equals(mode, MelodayModes.Sonic, StringComparison.OrdinalIgnoreCase) ? 11 : 0;
        var value = ((long)GetPeriodIndex(periodName) * 31L) + (libraryId * 7L) + modeOffset;
        return (int)(Math.Abs(value) % candidateCount);
    }

    private static int GetPeriodIndex(string periodName)
    {
        return periodName switch
        {
            DawnPeriodName => 0,
            EarlyMorningPeriodName => 1,
            MorningPeriodName => 2,
            AfternoonPeriodName => 3,
            EveningPeriodName => 4,
            NightPeriodName => 5,
            LateNightPeriodName => 6,
            _ => 0
        };
    }

    private static string ResolveContentType(string value)
    {
        return Path.GetExtension(value).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
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

    private sealed record PlaylistDescriptionContext(
        MelodayOptions Options,
        string PeriodName,
        MelodayPeriod Period,
        IReadOnlyList<long> TrackIds,
        Dictionary<long, PlexTrackMetadata> LiveMetadataByTrackId,
        Dictionary<long, PlexTrackMetadataDto> PersistedMetadataByTrackId,
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
            GenreCountByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ArtistLimit = Math.Max(1, (int)Math.Round(maxTracks * 0.05));
            GenreLimit = Math.Max(1, (int)Math.Round(maxTracks * 0.15));
        }

        public HashSet<string> Seen { get; }
        public Dictionary<string, int> ArtistCountByName { get; }
        public Dictionary<string, int> GenreCountByName { get; }
        public int ArtistLimit { get; }
        public int GenreLimit { get; }
    }

    private sealed record MelodayPeriod(IReadOnlyList<int> Hours, string Phrase);
}
