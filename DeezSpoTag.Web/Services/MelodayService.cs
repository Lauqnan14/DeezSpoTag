using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Options;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services;

public sealed class MelodayOptions
{
    public bool Enabled { get; set; } = true;
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
    public string MoodMapPath { get; set; } = "Resources/meloday/assets/moodmap.json";
    public string CoversPath { get; set; } = "Resources/meloday/assets/covers/flat";
    public string FontsPath { get; set; } = "Resources/meloday/assets/fonts/Circular";
    public string MainFontFile { get; set; } = "Circular-Bold.ttf";
    public string BrandFontFile { get; set; } = "Circular-Bold.ttf";
}

public sealed record MelodayRunResult(bool Success, string Message, string? PlaylistId);
public sealed record MelodayStatusDto(
    bool Enabled,
    string CurrentPeriod,
    DateTimeOffset? LastRunUtc,
    string? LastMessage,
    int MaxTracks,
    int HistoryLookbackDays,
    int ExcludePlayedDays);

public sealed class MelodayCollaborators
{
    public MelodayCollaborators(
        PlexApiClient plexApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        PlexHistoryImportService historyImportService)
    {
        PlexApiClient = plexApiClient;
        AuthService = authService;
        LibraryRepository = libraryRepository;
        HistoryImportService = historyImportService;
    }

    public PlexApiClient PlexApiClient { get; }
    public PlatformAuthService AuthService { get; }
    public LibraryRepository LibraryRepository { get; }
    public PlexHistoryImportService HistoryImportService { get; }
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
    private readonly MelodayOptions _options;
    private readonly PlexApiClient _plexApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly PlexHistoryImportService _historyImportService;
    private readonly ILogger<MelodayService> _logger;
    private readonly MelodaySettingsStore _settingsStore;
    private readonly Random _random = new();
    private readonly string _webRoot;
    private DateTimeOffset? _lastRunUtc;
    private string? _lastMessage;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex FeaturingParentheticalRegex = CreateRegex(@"(\(|\[)\s*(feat\.?|ft\.?|featuring).*?(\)|\])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeaturingInlineRegex = CreateRegex(@"\b(feat\.?|ft\.?|featuring)\s+[^\-\(\[]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

    private static readonly IReadOnlyDictionary<string, MelodayPeriod> DefaultPeriods = new Dictionary<string, MelodayPeriod>
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
        _authService = collaborators.AuthService;
        _libraryRepository = collaborators.LibraryRepository;
        _historyImportService = collaborators.HistoryImportService;
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

    public async Task<MelodayRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectiveOptionsAsync();
        if (!effective.Enabled)
        {
            _lastMessage = "Meloday disabled.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var auth = await _authService.LoadAsync();
        if (!TryGetPlexConnection(auth.Plex, out var plex, out var plexUrl, out var plexToken))
        {
            _lastMessage = "Plex auth missing.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        await _historyImportService.ImportAsync(cancellationToken);

        var libraries = await _libraryRepository.GetLibrariesAsync(cancellationToken);
        var library = SelectLibrary(libraries, effective.LibraryName);
        if (library is null)
        {
            _lastMessage = "No library configured.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var username = !string.IsNullOrWhiteSpace(plex.Username) ? plex.Username : plex.ServerName;
        var plexUserId = await _libraryRepository.EnsurePlexUserAsync(
            username,
            plex.Username,
            plex.Url,
            plex.MachineIdentifier,
            cancellationToken);

        var periodName = GetCurrentPeriodName();
        var period = DefaultPeriods[periodName];
        var now = DateTimeOffset.Now;
        var lookbackStart = now.AddDays(-effective.HistoryLookbackDays);
        var excludeStart = now.AddDays(-effective.ExcludePlayedDays);

        var history = await _libraryRepository.GetPlayHistoryEntriesAsync(
            plexUserId,
            library.Id,
            lookbackStart,
            period.Hours,
            now,
            cancellationToken);

        var excludedTrackIds = await _libraryRepository.GetPlayedTrackIdsSinceAsync(
            plexUserId,
            library.Id,
            excludeStart,
            cancellationToken);

        var historyTrackIds = history
            .Select(entry => entry.TrackId)
            .Where(id => !excludedTrackIds.Contains(id))
            .Distinct()
            .ToList();

        var ratingKeyByTrackId = await ResolveRatingKeysAsync(historyTrackIds, plex, cancellationToken);
        var liveMetadataByTrackId = new Dictionary<long, PlexTrackMetadata>();
        await EnsurePlexMetadataAsync(plex, historyTrackIds, ratingKeyByTrackId, liveMetadataByTrackId, cancellationToken);

        var balancedHistorical = BuildBalancedHistoricalSelection(history, excludedTrackIds, liveMetadataByTrackId, effective.MaxTracks);
        var similarContext = new SimilarTrackContext(
            ratingKeyByTrackId,
            excludedTrackIds,
            excludeStart,
            plex,
            effective,
            liveMetadataByTrackId,
            cancellationToken);
        var finalTracks = await BuildInitialTrackSelectionAsync(historyTrackIds, balancedHistorical, similarContext);
        await ExpandTrackSelectionAsync(finalTracks, balancedHistorical, similarContext);
        await FillWithRandomTracksAsync(library.Id, finalTracks, similarContext);

        if (finalTracks.Count == 0)
        {
            _lastMessage = "No tracks available for Meloday.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var selectedTrackIds = finalTracks.Take(effective.MaxTracks).ToList();
        await EnsureRatingKeysAsync(selectedTrackIds, plex, ratingKeyByTrackId, cancellationToken);
        await EnsurePlexMetadataAsync(plex, selectedTrackIds, ratingKeyByTrackId, liveMetadataByTrackId, cancellationToken);

        var orderedTrackIds = await OrderTracksAsync(
            selectedTrackIds,
            period,
            plex,
            effective,
            ratingKeyByTrackId,
            liveMetadataByTrackId,
            cancellationToken);

        var ratingKeyList = orderedTrackIds
            .Select(trackId => ratingKeyByTrackId.TryGetValue(trackId, out var ratingKey) ? ratingKey : null)
            .Where(ratingKey => !string.IsNullOrWhiteSpace(ratingKey))
            .Select(ratingKey => ratingKey!)
            .ToList();

        if (ratingKeyList.Count == 0)
        {
            _lastMessage = "No Plex rating keys found for selected tracks.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var persistedMetadata = (await _libraryRepository.GetPlexTrackMetadataAsync(orderedTrackIds, cancellationToken))
            .ToDictionary(entry => entry.TrackId);

        var (title, description) = BuildTitleAndDescription(new PlaylistDescriptionContext(
            effective,
            periodName,
            period,
            orderedTrackIds,
            liveMetadataByTrackId,
            persistedMetadata,
            username,
            DateTimeOffset.Now));

        var machineId = plex.MachineIdentifier;
        if (string.IsNullOrWhiteSpace(machineId))
        {
            var identity = await _plexApiClient.GetIdentityAsync(plexUrl, plexToken, cancellationToken);
            machineId = identity?.MachineIdentifier;
        }

        if (string.IsNullOrWhiteSpace(machineId))
        {
            _lastMessage = "Plex machine identifier missing.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        var playlistId = await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plexUrl,
            plexToken,
            machineId,
            title,
            ratingKeyList,
            options: new PlexApiClient.PlaylistUpsertOptions(
                ExistingTitlePrefix: effective.PlaylistPrefix),
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            _lastMessage = "Failed to create/update Plex playlist.";
            return new MelodayRunResult(false, _lastMessage, null);
        }

        await _plexApiClient.UpdatePlaylistMetadataAsync(
            plexUrl,
            plexToken,
            playlistId,
            title,
            description,
            cancellationToken);

        var posterUrl = await TryGenerateCoverAsync(effective, periodName, title, cancellationToken);
        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            await _plexApiClient.UpdatePlaylistPosterAsync(
                plexUrl,
                plexToken,
                playlistId,
                posterUrl,
                cancellationToken);
        }

        _lastRunUtc = DateTimeOffset.UtcNow;
        _lastMessage = "Meloday playlist updated.";
        return new MelodayRunResult(true, _lastMessage, playlistId);
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

    private static LibraryDto? SelectLibrary(IReadOnlyList<LibraryDto> libraries, string? libraryName)
    {
        if (!string.IsNullOrWhiteSpace(libraryName))
        {
            return libraries.FirstOrDefault(l => string.Equals(l.Name, libraryName, StringComparison.OrdinalIgnoreCase));
        }

        return libraries.Count > 0 ? libraries[0] : null;
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

        await EnsureRatingKeysAsync(candidatePool, context.Plex, context.RatingKeyByTrackId, context.CancellationToken);
        await EnsurePlexMetadataAsync(context.Plex, candidatePool, context.RatingKeyByTrackId, context.LiveMetadataByTrackId, context.CancellationToken);
        return (await ProcessTracksAsync(candidatePool, context.Options, context.LiveMetadataByTrackId, context.CancellationToken)).ToList();
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

            await EnsureRatingKeysAsync(extraCandidates, context.Plex, context.RatingKeyByTrackId, context.CancellationToken);
            await EnsurePlexMetadataAsync(context.Plex, extraCandidates, context.RatingKeyByTrackId, context.LiveMetadataByTrackId, context.CancellationToken);
            var processedExtra = await ProcessTracksAsync(extraCandidates, context.Options, context.LiveMetadataByTrackId, context.CancellationToken);
            if (!AppendUniqueTracks(finalTracks, processedExtra, context.Options.MaxTracks))
            {
                return;
            }
        }
    }

    private async Task<List<long>> BuildExtraCandidatesAsync(
        IReadOnlyList<long> finalTracks,
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

        await EnsureRatingKeysAsync(randomCandidates, context.Plex, context.RatingKeyByTrackId, context.CancellationToken);
        await EnsurePlexMetadataAsync(context.Plex, randomCandidates, context.RatingKeyByTrackId, context.LiveMetadataByTrackId, context.CancellationToken);
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

    public MelodayStatusDto GetStatus()
    {
        var effective = _settingsStore.LoadAsync(_options).GetAwaiter().GetResult();
        return new MelodayStatusDto(
            effective.Enabled,
            GetCurrentPeriodName(),
            _lastRunUtc,
            _lastMessage,
            effective.MaxTracks,
            effective.HistoryLookbackDays,
            effective.ExcludePlayedDays);
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
            .ToDictionary(group => group.Key, group => group.Count());

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
                genreCount[genre] = genreCount.TryGetValue(genre, out var count) ? count + 1 : 1;
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
        IReadOnlyList<long> referenceTrackIds,
        SimilarTrackContext context)
    {
        if (referenceTrackIds.Count == 0)
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

        return !context.ExcludedTrackIds.Contains(trackId);
    }

    private async Task<IReadOnlyList<long>> ProcessTracksAsync(
        IReadOnlyList<long> trackIds,
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

    private async Task<IReadOnlyList<long>> OrderTracksAsync(
        IReadOnlyList<long> trackIds,
        MelodayPeriod period,
        PlexAuth plex,
        MelodayOptions options,
        Dictionary<long, string> ratingKeyByTrackId,
        Dictionary<long, PlexTrackMetadata> liveMetadataByTrackId,
        CancellationToken cancellationToken)
    {
        if (trackIds.Count <= 2)
        {
            return trackIds;
        }

        var sortedByLastViewed = trackIds
            .OrderBy(trackId => liveMetadataByTrackId.TryGetValue(trackId, out var metadata) && metadata.LastViewedAtUtc.HasValue
                ? metadata.LastViewedAtUtc.Value
                : DateTimeOffset.MaxValue)
            .ToList();

        long? firstTrackId = sortedByLastViewed
            .Cast<long?>()
            .FirstOrDefault(trackId =>
                trackId.HasValue &&
                liveMetadataByTrackId.TryGetValue(trackId.Value, out var metadata) &&
                metadata.LastViewedAtUtc.HasValue &&
                period.Hours.Contains(metadata.LastViewedAtUtc.Value.Hour));
        long? lastTrackId = sortedByLastViewed
            .AsEnumerable()
            .Reverse()
            .Cast<long?>()
            .FirstOrDefault(trackId =>
                trackId.HasValue &&
                liveMetadataByTrackId.TryGetValue(trackId.Value, out var metadata) &&
                metadata.LastViewedAtUtc.HasValue &&
                period.Hours.Contains(metadata.LastViewedAtUtc.Value.Hour));

        if (!firstTrackId.HasValue && sortedByLastViewed.Count > 0)
        {
            firstTrackId = sortedByLastViewed[0];
        }

        if (!lastTrackId.HasValue && sortedByLastViewed.Count > 0)
        {
            lastTrackId = sortedByLastViewed[^1];
        }

        var middle = trackIds
            .Where(trackId => trackId != firstTrackId && trackId != lastTrackId)
            .ToList();

        var sortedMiddle = await SortBySonicSimilarityGreedyAsync(
            middle,
            plex,
            options,
            ratingKeyByTrackId,
            cancellationToken);

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

        if (ordered.Count == 0)
        {
            return trackIds;
        }

        return ordered
            .Distinct()
            .Take(options.MaxTracks)
            .ToList();
    }

    private async Task<List<long>> SortBySonicSimilarityGreedyAsync(
        IReadOnlyList<long> trackIds,
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
        IReadOnlyList<string>? currentSimilars,
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

    private static PlexTrack? SelectBestPlexTrackMatch(MixTrackDto track, IReadOnlyList<PlexTrack> candidates)
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
        var sourceTitle = (track.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sourceTitle))
        {
            source = new SourceTrackMatchContext(string.Empty, string.Empty, string.Empty, string.Empty);
            return false;
        }

        var sourceArtist = (track.ArtistName ?? string.Empty).Trim();
        var sourceTitleClean = CleanTitle(sourceTitle);
        source = new SourceTrackMatchContext(
            sourceTitle,
            sourceArtist,
            NormalizeComparableText(string.IsNullOrWhiteSpace(sourceTitleClean) ? sourceTitle : sourceTitleClean),
            NormalizeComparableText(sourceArtist));
        return true;
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
        var title = $"{context.Options.PlaylistPrefix} {mostCommonMood} {descriptor} {mostCommonGenre} {dayName} {context.PeriodName}";

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

    private static string FormatHighlightStyles(IReadOnlyList<string> styles)
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

    private async Task<string?> TryGenerateCoverAsync(
        MelodayOptions options,
        string periodName,
        string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_webRoot))
        {
            return null;
        }

        var staticPosterUrl = TryResolveStaticCoverUrl(options, periodName);
        if (!string.IsNullOrWhiteSpace(staticPosterUrl))
        {
            return staticPosterUrl;
        }

        var coverFile = ResolveCoverFile(periodName);
        if (string.IsNullOrWhiteSpace(coverFile))
        {
            return null;
        }

        var coverPath = Path.Join(AppContext.BaseDirectory, options.CoversPath, coverFile);
        if (!File.Exists(coverPath))
        {
            _logger.LogWarning("Meloday cover not found at {Path}", coverPath);
            return null;
        }

        var outputDir = Path.Join(_webRoot, "meloday", "covers");
        Directory.CreateDirectory(outputDir);
        var outputName = $"meloday_{periodName.Replace(" ", "-", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()}.jpg";
        var outputPath = Path.Join(outputDir, outputName);

        var displayTitle = title;
        if (displayTitle.StartsWith($"{options.PlaylistPrefix} ", StringComparison.OrdinalIgnoreCase))
        {
            displayTitle = displayTitle.Substring(options.PlaylistPrefix.Length).Trim();
        }

        await RenderCoverAsync(options, coverPath, outputPath, displayTitle, cancellationToken);
        var baseUrl = options.BaseUrl.TrimEnd('/');
        return $"{baseUrl}/meloday/covers/{outputName}";
    }

    private string? TryResolveStaticCoverUrl(MelodayOptions options, string periodName)
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

        var preferredFile = ResolveStaticCoverFile(periodName);
        if (!string.IsNullOrWhiteSpace(preferredFile) && File.Exists(Path.Join(staticDir, preferredFile)))
        {
            return $"{baseUrl}/images/meloday/{Uri.EscapeDataString(preferredFile)}";
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

        var index = GetPeriodIndex(periodName) % candidates.Count;
        var selected = candidates[index];
        return $"{baseUrl}/images/meloday/{Uri.EscapeDataString(selected)}";
    }

    private static string? ResolveStaticCoverFile(string periodName)
    {
        return periodName switch
        {
            DawnPeriodName => "1.jpg",
            EarlyMorningPeriodName => "2.jpg",
            MorningPeriodName => "3.jpg",
            AfternoonPeriodName => "4.jpg",
            EveningPeriodName => "5.jpg",
            NightPeriodName => "6.jpg",
            LateNightPeriodName => "7.jpg",
            _ => null
        };
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

    private static string? ResolveCoverFile(string periodName)
    {
        return periodName switch
        {
            DawnPeriodName => "dawn_blank.webp",
            EarlyMorningPeriodName => "early-morning_blank.webp",
            MorningPeriodName => "morning_blank.webp",
            AfternoonPeriodName => "afternoon_blank.webp",
            EveningPeriodName => "evening_blank.webp",
            NightPeriodName => "night_blank.webp",
            _ => "late-night_blank.webp"
        };
    }

    private static async Task RenderCoverAsync(
        MelodayOptions options,
        string baseImagePath,
        string outputPath,
        string text,
        CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync<Rgba32>(baseImagePath, cancellationToken);
        var fontCollection = new FontCollection();
        var mainFontPath = Path.Join(AppContext.BaseDirectory, options.FontsPath, options.MainFontFile);
        var brandFontPath = Path.Join(AppContext.BaseDirectory, options.FontsPath, options.BrandFontFile);
        var mainFont = fontCollection.Add(mainFontPath).CreateFont(64, FontStyle.Bold);
        var brandFont = fontCollection.Add(brandFontPath).CreateFont(80, FontStyle.Bold);

        var textBoxWidth = 630f;
        var textBoxRight = image.Width - 110f;
        var textBoxLeft = textBoxRight - textBoxWidth;
        var y = 100f;

        var wrappedLines = WrapText(text, mainFont, textBoxWidth);
        foreach (var line in wrappedLines)
        {
            var size = TextMeasurer.MeasureSize(line, new TextOptions(mainFont));
            var x = textBoxLeft + (textBoxWidth - size.Width);

            image.Mutate(ctx =>
            {
                ctx.DrawText(line, mainFont, Color.FromRgba(0, 0, 0, 120), new PointF(x, y));
                ctx.DrawText(line, mainFont, Color.White, new PointF(x, y));
            });
            y += size.Height + 10;
        }

        var melodayX = 110f;
        var melodayY = image.Height - 200f;
        image.Mutate(ctx =>
        {
            ctx.DrawText("Meloday", brandFont, Color.FromRgba(0, 0, 0, 120), new PointF(melodayX, melodayY));
            ctx.DrawText("Meloday", brandFont, Color.White, new PointF(melodayX, melodayY));
        });

        await image.SaveAsJpegAsync(outputPath, cancellationToken);
    }

    private static List<string> WrapText(string text, Font font, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
            var size = TextMeasurer.MeasureSize(candidate, new TextOptions(font));
            if (size.Width <= maxWidth)
            {
                current = candidate;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(current))
                {
                    lines.Add(current);
                }

                current = word;
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            lines.Add(current);
        }

        return lines;
    }

    private sealed record SimilarTrackContext(
        Dictionary<long, string> RatingKeyByTrackId,
        IReadOnlySet<long> ExcludedTrackIds,
        DateTimeOffset ExcludeStart,
        PlexAuth Plex,
        MelodayOptions Options,
        Dictionary<long, PlexTrackMetadata> LiveMetadataByTrackId,
        CancellationToken CancellationToken);

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
