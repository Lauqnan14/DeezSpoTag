using System.Text.Json;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistMetadataUpdaterService : BackgroundService
{
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromMinutes(30);
    private const string SpotifyPlatform = "spotify";
    private const string DeezerPlatform = "deezer";
    private const string ApplePlatform = "apple";
    private const string MetadataSourceAuto = "auto";
    private const string MetadataSourceSpotify = SpotifyPlatform;
    private const string MetadataSourceDeezer = DeezerPlatform;
    private const string MetadataSourceApple = ApplePlatform;
    private const string PlexTarget = "plex";
    private const string JellyfinTarget = "jellyfin";
    private const string BothTargets = "both";

    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexClient;
    private readonly JellyfinApiClient _jellyfinClient;
    private readonly DeezerClient _deezerClient;
    private readonly AppleMusicCatalogService _appleMusicCatalogService;
    private readonly LibraryConfigStore _configStore;
    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArtistMetadataUpdaterService> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statePath;
    private MetadataUpdaterStatusSnapshot _status = MetadataUpdaterStatusSnapshot.Idle();
    private Task? _activeRun;

    public ArtistMetadataUpdaterService(
        IServiceProvider serviceProvider,
        IWebHostEnvironment environment,
        ILogger<ArtistMetadataUpdaterService> logger)
    {
        _libraryRepository = serviceProvider.GetRequiredService<LibraryRepository>();
        _spotifyArtistService = serviceProvider.GetRequiredService<SpotifyArtistService>();
        _platformAuthService = serviceProvider.GetRequiredService<PlatformAuthService>();
        _plexClient = serviceProvider.GetRequiredService<PlexApiClient>();
        _jellyfinClient = serviceProvider.GetRequiredService<JellyfinApiClient>();
        _deezerClient = serviceProvider.GetRequiredService<DeezerClient>();
        _appleMusicCatalogService = serviceProvider.GetRequiredService<AppleMusicCatalogService>();
        _configStore = serviceProvider.GetRequiredService<LibraryConfigStore>();
        _environment = environment;
        _httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        _logger = logger;
        _statePath = Path.Join(
            AppDataPaths.GetDataRoot(environment),
            "library-artist-images",
            SpotifyPlatform,
            "metadata-updater-state.json");
    }

    public MetadataUpdaterStatusSnapshot GetStatus()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    public async Task RegisterFromManualPushAsync(
        ManualPushRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return;
        }

        var artistId = request.ArtistId;
        var artistName = request.ArtistName;
        if (artistId <= 0 || string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        var state = await LoadStateAsync(cancellationToken);
        var normalizedTarget = NormalizeTarget(request.Target);
        var normalizedInterval = NormalizeIntervalDays(request.IntervalDays ?? 30);
        var tracked = state.Artists.FirstOrDefault(item => item.ArtistId == artistId);
        if (tracked is null)
        {
            tracked = new MetadataUpdaterTrackedArtist
            {
                ArtistId = artistId
            };
            state.Artists.Add(tracked);
        }

        tracked.ArtistName = artistName.Trim();
        tracked.Target = normalizedTarget;
        tracked.IncludeAvatar = request.IncludeAvatar;
        tracked.IncludeBackground = request.IncludeBackground;
        tracked.IncludeBio = request.IncludeBio;
        tracked.IntervalDays = normalizedInterval;
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            tracked.Source = NormalizeMetadataSource(request.Source);
        }
        tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SaveStateAsync(state, cancellationToken);
    }

    public async Task<bool> EnqueueRunAsync(MetadataUpdaterRunRequest request, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeRun is { IsCompleted: false })
            {
                return false;
            }

            var normalized = request ?? new MetadataUpdaterRunRequest();
            _activeRun = Task.Run(() => RunInternalAsync(normalized, isAutomatic: false, CancellationToken.None), CancellationToken.None);
            return true;
        }
        finally
        {
            _runGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryRunAutomaticCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Metadata updater automatic cycle failed.");
            }

            try
            {
                await Task.Delay(SchedulerInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task TryRunAutomaticCycleAsync(CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeRun is { IsCompleted: false })
            {
                return;
            }

            var state = await LoadStateAsync(cancellationToken);
            if (state.Artists.Count == 0)
            {
                return;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var dueArtists = state.Artists.Where(artist =>
            {
                var intervalDays = NormalizeIntervalDays(artist.IntervalDays);
                if (intervalDays <= 0 || !artist.LastPushedAtUtc.HasValue)
                {
                    return true;
                }

                return nowUtc - artist.LastPushedAtUtc.Value >= TimeSpan.FromDays(intervalDays);
            }).ToList();
            if (dueArtists.Count == 0)
            {
                return;
            }

            _activeRun = Task.Run(
                () => RunInternalAsync(
                    new MetadataUpdaterRunRequest
                    {
                        IntervalDays = null,
                        IncludeAllArtists = false,
                        Force = false
                    },
                    isAutomatic: true,
                    CancellationToken.None),
                CancellationToken.None);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task RunInternalAsync(
        MetadataUpdaterRunRequest request,
        bool isAutomatic,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        BeginRunStatus(isAutomatic, startedAtUtc);

        try
        {
            if (!TryEnsureLibraryConfigured())
            {
                return;
            }

            var runPreparation = await PrepareRunAsync(request, cancellationToken);
            if (runPreparation is null)
            {
                return;
            }

            var state = runPreparation.State;
            var allCandidates = runPreparation.Candidates;
            var auth = await _platformAuthService.LoadAsync();
            var counters = new MetadataRunCounters(allCandidates.Count);

            foreach (var tracked in allCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                counters.ProcessedArtists++;
                UpdateProgressStatus(tracked.ArtistName, counters);

                var outcome = await ProcessTrackedArtistAsync(
                    tracked,
                    request,
                    auth,
                    runPreparation.NowUtc,
                    cancellationToken);
                counters.Apply(outcome);
                UpdateCounterStatus(counters);
                await SaveStateAsync(state, cancellationToken);
            }

            UpdateStatus(_status with
            {
                Running = false,
                CurrentArtist = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update completed",
                Message = $"Processed {counters.ProcessedArtists} artists ({counters.SuccessfulArtists} success, {counters.FailedArtists} failed, {counters.SkippedArtists} skipped)."
            });
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(_status with
            {
                Running = false,
                CurrentArtist = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update cancelled",
                Message = "Metadata updater was cancelled."
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater run failed.");
            UpdateStatus(_status with
            {
                Running = false,
                CurrentArtist = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update failed",
                Message = ex.Message
            });
        }
    }

    private void BeginRunStatus(bool isAutomatic, DateTimeOffset startedAtUtc)
    {
        UpdateStatus(_status with
        {
            Running = true,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = null,
            Phase = isAutomatic ? "Automatic metadata renewal started" : "Metadata update started",
            Message = null,
            ProcessedArtists = 0,
            SuccessfulArtists = 0,
            FailedArtists = 0,
            SkippedArtists = 0,
            TotalArtists = 0,
            CurrentArtist = null
        });
    }

    private bool TryEnsureLibraryConfigured()
    {
        if (_libraryRepository.IsConfigured)
        {
            return true;
        }

        UpdateStatus(_status with
        {
            Running = false,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Phase = "Metadata update failed",
            Message = "Library database is not configured."
        });
        return false;
    }

    private async Task<PreparedRunState?> PrepareRunAsync(
        MetadataUpdaterRunRequest request,
        CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken);
        if (request.IncludeAllArtists == true || state.Artists.Count == 0)
        {
            await SeedArtistsFromLibraryAsync(state, request, cancellationToken);
            await SaveStateAsync(state, cancellationToken);
        }

        var allCandidates = BuildRunCandidates(state.Artists, request);
        UpdateStatus(_status with { TotalArtists = allCandidates.Count });
        if (allCandidates.Count == 0)
        {
            UpdateStatus(_status with
            {
                Running = false,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update completed",
                Message = "No tracked artists available for metadata updater."
            });
            return null;
        }

        return new PreparedRunState(state, allCandidates, DateTimeOffset.UtcNow);
    }

    private static List<MetadataUpdaterTrackedArtist> BuildRunCandidates(
        IReadOnlyCollection<MetadataUpdaterTrackedArtist> artists,
        MetadataUpdaterRunRequest request)
    {
        var candidates = artists
            .Where(artist => artist.ArtistId > 0 && !string.IsNullOrWhiteSpace(artist.ArtistName))
            .ToList();
        if (!request.ArtistId.HasValue)
        {
            return candidates;
        }

        return candidates
            .Where(artist => artist.ArtistId == request.ArtistId.Value)
            .ToList();
    }

    private async Task<ArtistProcessingOutcome> ProcessTrackedArtistAsync(
        MetadataUpdaterTrackedArtist tracked,
        MetadataUpdaterRunRequest request,
        PlatformAuthState auth,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var effectiveIntervalDays = NormalizeIntervalDays(request.IntervalDays ?? tracked.IntervalDays);
        if (ShouldSkipTrackedArtist(tracked, request, effectiveIntervalDays, nowUtc))
        {
            tracked.IntervalDays = effectiveIntervalDays;
            return ArtistProcessingOutcome.Skipped;
        }

        ApplyRequestOverrides(tracked, request, effectiveIntervalDays);
        try
        {
            var updated = await PushTrackedArtistMetadataAsync(tracked, auth, cancellationToken);
            return updated
                ? ArtistProcessingOutcome.Succeeded
                : ArtistProcessingOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater failed for artist {ArtistId}", tracked.ArtistId);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Metadata updater failed for {tracked.ArtistName}: {ex.Message}"));
            return ArtistProcessingOutcome.Failed;
        }
    }

    private static bool ShouldSkipTrackedArtist(
        MetadataUpdaterTrackedArtist tracked,
        MetadataUpdaterRunRequest request,
        int effectiveIntervalDays,
        DateTimeOffset nowUtc)
    {
        if (request.Force == true || effectiveIntervalDays <= 0 || !tracked.LastPushedAtUtc.HasValue)
        {
            return false;
        }

        var elapsed = nowUtc - tracked.LastPushedAtUtc.Value;
        return elapsed < TimeSpan.FromDays(effectiveIntervalDays);
    }

    private static void ApplyRequestOverrides(
        MetadataUpdaterTrackedArtist tracked,
        MetadataUpdaterRunRequest request,
        int effectiveIntervalDays)
    {
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            tracked.Source = NormalizeMetadataSource(request.Source);
        }

        if (!string.IsNullOrWhiteSpace(request.Target))
        {
            tracked.Target = NormalizeTarget(request.Target);
        }

        tracked.IntervalDays = effectiveIntervalDays;
        if (request.IncludeAvatar.HasValue)
        {
            tracked.IncludeAvatar = request.IncludeAvatar.Value;
        }

        if (request.IncludeBackground.HasValue)
        {
            tracked.IncludeBackground = request.IncludeBackground.Value;
        }

        if (request.IncludeBio.HasValue)
        {
            tracked.IncludeBio = request.IncludeBio.Value;
        }
    }

    private async Task<bool> PushTrackedArtistMetadataAsync(
        MetadataUpdaterTrackedArtist tracked,
        PlatformAuthState auth,
        CancellationToken cancellationToken)
    {
        var artist = await _libraryRepository.GetArtistAsync(tracked.ArtistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Metadata updater skipped artist {tracked.ArtistId}: artist missing."));
            return false;
        }

        tracked.ArtistName = artist.Name;
        var source = NormalizeMetadataSource(tracked.Source);
        var resolved = await ResolveArtistMetadataAsync(
            artist.Id,
            artist.Name,
            source,
            cancellationToken);
        if (resolved is null)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Metadata updater failed for {artist.Name}: {source} metadata unavailable."));
            return false;
        }

        var prepared = await PrepareVisualsAsync(tracked, resolved.Candidates, cancellationToken);
        await UpdateManagedArtistVisualsAsync(artist.Id, prepared, cancellationToken);

        var biography = tracked.IncludeBio
            ? SanitizeBiography(resolved.Biography)
            : null;
        var pushed = await PushArtistMetadataAsync(
            new PushMetadataRequest(
                artist.Id,
                auth,
                artist.Name,
                tracked.Target,
                tracked.IncludeAvatar ? prepared.AvatarPath : null,
                tracked.IncludeBackground ? prepared.BackgroundPath : null,
                biography),
            cancellationToken);

        if (!pushed.Updated)
        {
            var warningText = pushed.Warnings.Count == 0
                ? "No server metadata was updated."
                : string.Join(" ", pushed.Warnings);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Metadata updater could not push {artist.Name}: {warningText}"));
            return false;
        }

        tracked.LastPushedAtUtc = DateTimeOffset.UtcNow;
        tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
        tracked.AvatarRotationIndex = prepared.NextAvatarIndex;
        tracked.BackgroundRotationIndex = prepared.NextBackgroundIndex;
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Metadata updater pushed {artist.Name} to {tracked.Target}."));
        return true;
    }

    private async Task UpdateManagedArtistVisualsAsync(
        long artistId,
        PreparedVisuals prepared,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(prepared.AvatarPath))
        {
            await _libraryRepository.UpdateArtistImagePathAsync(artistId, prepared.AvatarPath!, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(prepared.BackgroundPath))
        {
            await _libraryRepository.UpdateArtistBackgroundPathAsync(artistId, prepared.BackgroundPath!, cancellationToken);
        }
    }

    private void UpdateProgressStatus(string artistName, MetadataRunCounters counters)
    {
        UpdateStatus(_status with
        {
            ProcessedArtists = counters.ProcessedArtists,
            CurrentArtist = artistName,
            Phase = $"Updating {artistName}"
        });
    }

    private void UpdateCounterStatus(MetadataRunCounters counters)
    {
        UpdateStatus(_status with
        {
            SuccessfulArtists = counters.SuccessfulArtists,
            FailedArtists = counters.FailedArtists,
            SkippedArtists = counters.SkippedArtists
        });
    }

    private async Task SeedArtistsFromLibraryAsync(
        MetadataUpdaterState state,
        MetadataUpdaterRunRequest request,
        CancellationToken cancellationToken)
    {
        var artists = await _libraryRepository.GetArtistsAsync("all", cancellationToken);
        var target = NormalizeTarget(request.Target);
        var hasSourceOverride = !string.IsNullOrWhiteSpace(request.Source);
        var source = NormalizeMetadataSource(request.Source);
        var intervalDays = NormalizeIntervalDays(request.IntervalDays ?? 30);
        var byId = state.Artists.ToDictionary(item => item.ArtistId);
        foreach (var artist in artists)
        {
            if (artist.Id <= 0 || string.IsNullOrWhiteSpace(artist.Name))
            {
                continue;
            }

            if (!byId.TryGetValue(artist.Id, out var tracked))
            {
                tracked = new MetadataUpdaterTrackedArtist
                {
                    ArtistId = artist.Id
                };
                state.Artists.Add(tracked);
                byId[artist.Id] = tracked;
            }

            tracked.ArtistName = artist.Name.Trim();
            if (hasSourceOverride || string.IsNullOrWhiteSpace(tracked.Source))
            {
                tracked.Source = source;
            }
            tracked.Target = target;
            tracked.IntervalDays = intervalDays;
            tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task<ResolvedArtistMetadata?> ResolveArtistMetadataAsync(
        long artistId,
        string artistName,
        string source,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeMetadataSource(source);
        var candidates = new List<ArtworkCandidate>();
        var spotify = await TryCollectSpotifyMetadataAsync(
            normalizedSource,
            artistId,
            artistName,
            candidates,
            cancellationToken);
        if (!spotify.Succeeded)
        {
            return null;
        }

        if (!await TryCollectDeezerMetadataAsync(normalizedSource, artistName, candidates, cancellationToken))
        {
            return null;
        }

        if (!await TryCollectAppleMetadataAsync(normalizedSource, artistName, candidates, cancellationToken))
        {
            return null;
        }

        return new ResolvedArtistMetadata(spotify.Biography, candidates);
    }

    private async Task<(bool Succeeded, string? Biography)> TryCollectSpotifyMetadataAsync(
        string normalizedSource,
        long artistId,
        string artistName,
        List<ArtworkCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (normalizedSource is not (MetadataSourceAuto or MetadataSourceSpotify))
        {
            return (true, null);
        }

        var page = await _spotifyArtistService.GetArtistPageAsync(
            artistId,
            artistName,
            forceRefresh: true,
            forceRematch: false,
            cancellationToken);
        if (page?.Artist is null)
        {
            return (normalizedSource != MetadataSourceSpotify, null);
        }

        await AddSpotifyArtworkCandidatesAsync(artistId, page, candidates, cancellationToken);
        return (true, page.Artist.Biography);
    }

    private async Task<bool> TryCollectDeezerMetadataAsync(
        string normalizedSource,
        string artistName,
        List<ArtworkCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (normalizedSource is not (MetadataSourceAuto or MetadataSourceDeezer))
        {
            return true;
        }

        var deezerCandidate = await TryResolveDeezerArtworkCandidateAsync(artistName, cancellationToken);
        if (deezerCandidate is null)
        {
            return normalizedSource != MetadataSourceDeezer || candidates.Count > 0;
        }

        candidates.Add(deezerCandidate);
        return true;
    }

    private async Task<bool> TryCollectAppleMetadataAsync(
        string normalizedSource,
        string artistName,
        List<ArtworkCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (normalizedSource is not (MetadataSourceAuto or MetadataSourceApple))
        {
            return true;
        }

        var appleCandidate = await TryResolveAppleArtworkCandidateAsync(artistName, cancellationToken);
        if (appleCandidate is null)
        {
            return normalizedSource != MetadataSourceApple || candidates.Count > 0;
        }

        candidates.Add(appleCandidate);
        return true;
    }

    private async Task AddSpotifyArtworkCandidatesAsync(
        long artistId,
        SpotifyArtistPageResult page,
        List<ArtworkCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var cacheRoot = Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            "library-artist-images",
            SpotifyPlatform);
        var spotifyId = await _libraryRepository.GetArtistSourceIdAsync(artistId, SpotifyPlatform, cancellationToken);
        if (!string.IsNullOrWhiteSpace(spotifyId) && Directory.Exists(cacheRoot))
        {
            try
            {
                candidates.AddRange(
                    Directory.GetFiles(cacheRoot, $"*{spotifyId}.*", SearchOption.TopDirectoryOnly)
                        .Where(File.Exists)
                        .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                        .Select(path => ArtworkCandidate.FromLocal(
                            path,
                            $"spotify-cache:{Path.GetFullPath(path)}",
                            SpotifyPlatform)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed to enumerate Spotify cache files for artist {ArtistId}", artistId);
                }
            }
        }

        candidates.AddRange(page.Artist.Images
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .Select((image, index) => ArtworkCandidate.FromRemote(
                image.Url!,
                $"spotify:{index}:{image.Url}",
                SpotifyPlatform)));
    }

    private async Task<ArtworkCandidate?> TryResolveDeezerArtworkCandidateAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        var deezerImage = await ArtworkFallbackHelper.TryResolveDeezerArtistImageAsync(
            _deezerClient,
            deezerTrackId: null,
            size: 1200,
            _logger,
            cancellationToken,
            artistName);
        if (string.IsNullOrWhiteSpace(deezerImage))
        {
            return null;
        }

        return ArtworkCandidate.FromRemote(deezerImage!, $"deezer:{deezerImage}", MetadataSourceDeezer);
    }

    private async Task<ArtworkCandidate?> TryResolveAppleArtworkCandidateAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        string? appleImage = null;
        try
        {
            appleImage = await AppleQueueHelpers.ResolveAppleArtistImageAsync(
                _appleMusicCatalogService,
                artistName,
                "us",
                1200,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple Music artist artwork lookup failed for {ArtistName}", artistName);
            }
        }

        if (string.IsNullOrWhiteSpace(appleImage))
        {
            appleImage = await AppleQueueHelpers.ResolveItunesArtistImageAsync(
                _httpClientFactory,
                artistName,
                1200,
                _logger,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(appleImage))
        {
            return null;
        }

        return ArtworkCandidate.FromRemote(appleImage!, $"apple:{appleImage}", MetadataSourceApple);
    }

    private async Task<PreparedVisuals> PrepareVisualsAsync(
        MetadataUpdaterTrackedArtist tracked,
        IReadOnlyList<ArtworkCandidate> sourceCandidates,
        CancellationToken cancellationToken)
    {
        var managedRoot = Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            "library-artist-images",
            SpotifyPlatform,
            "artists",
            tracked.ArtistId.ToString());

        Directory.CreateDirectory(managedRoot);

        var candidates = sourceCandidates.ToList();

        var avatarSlot = ResolveSlotCandidate(managedRoot, "avatar");
        if (!string.IsNullOrWhiteSpace(avatarSlot))
        {
            candidates.Insert(0, ArtworkCandidate.FromLocal(avatarSlot, $"slot:avatar:{Path.GetFullPath(avatarSlot)}", "managed"));
        }
        var backgroundSlot = ResolveSlotCandidate(managedRoot, "background");
        if (!string.IsNullOrWhiteSpace(backgroundSlot))
        {
            candidates.Insert(0, ArtworkCandidate.FromLocal(backgroundSlot, $"slot:background:{Path.GetFullPath(backgroundSlot)}", "managed"));
        }

        candidates = candidates
            .Select(NormalizeCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .GroupBy(candidate => candidate.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var nextAvatarIndex = tracked.AvatarRotationIndex;
        var nextBackgroundIndex = tracked.BackgroundRotationIndex;
        string? avatarPath = avatarSlot;
        string? backgroundPath = backgroundSlot;
        ArtworkCandidate? selectedAvatarCandidate = null;

        if (tracked.IncludeAvatar)
        {
            var avatarSelection = await RotateAndMaterializeSlotAsync(
                candidates,
                tracked.AvatarRotationIndex,
                managedRoot,
                "avatar",
                excludedIdentity: null,
                cancellationToken);
            avatarPath = avatarSelection.Path;
            selectedAvatarCandidate = avatarSelection.Candidate;
            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                nextAvatarIndex = (tracked.AvatarRotationIndex + 1) % Math.Max(1, candidates.Count);
            }
        }

        if (tracked.IncludeBackground)
        {
            var backgroundSelection = await RotateAndMaterializeSlotAsync(
                candidates,
                tracked.BackgroundRotationIndex,
                managedRoot,
                "background",
                selectedAvatarCandidate?.Identity,
                cancellationToken);
            backgroundPath = backgroundSelection.Path;
            if (!string.IsNullOrWhiteSpace(backgroundPath))
            {
                nextBackgroundIndex = (tracked.BackgroundRotationIndex + 1) % Math.Max(1, candidates.Count);
            }
        }

        return new PreparedVisuals(avatarPath, backgroundPath, nextAvatarIndex, nextBackgroundIndex);
    }

    private async Task<(string? Path, ArtworkCandidate? Candidate)> RotateAndMaterializeSlotAsync(
        IReadOnlyList<ArtworkCandidate> candidates,
        int rotationIndex,
        string managedRoot,
        string slot,
        string? excludedIdentity,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return (ResolveSlotCandidate(managedRoot, slot), null);
        }

        var boundedIndex = Math.Abs(rotationIndex) % candidates.Count;
        for (var offset = 0; offset < candidates.Count; offset++)
        {
            var selected = candidates[(boundedIndex + offset) % candidates.Count];
            if (!string.IsNullOrWhiteSpace(excludedIdentity)
                && candidates.Count > 1
                && string.Equals(selected.Identity, excludedIdentity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var materialized = await TryMaterializeSlotCandidateAsync(selected, managedRoot, slot, cancellationToken);
            if (!string.IsNullOrWhiteSpace(materialized.Path))
            {
                return materialized;
            }
        }

        return (ResolveSlotCandidate(managedRoot, slot), null);
    }

    private async Task<(string? Path, ArtworkCandidate? Candidate)> TryMaterializeSlotCandidateAsync(
        ArtworkCandidate selected,
        string managedRoot,
        string slot,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(selected.LocalPath) && File.Exists(selected.LocalPath))
        {
            if (!await IsArtworkCandidateUsableAsync(selected.LocalPath, cancellationToken))
            {
                LogRejectedArtworkCandidate(slot, managedRoot, selected.Source);
                return (null, null);
            }

            return (await CopyIntoSlotAsync(managedRoot, slot, selected.LocalPath, cancellationToken), selected);
        }

        if (string.IsNullOrWhiteSpace(selected.RemoteUrl))
        {
            return (null, null);
        }

        var downloaded = await DownloadIntoSlotAsync(managedRoot, slot, selected.RemoteUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(downloaded))
        {
            return (null, null);
        }

        if (await IsArtworkCandidateUsableAsync(downloaded, cancellationToken))
        {
            return (downloaded, selected);
        }

        LogRejectedArtworkCandidate(slot, managedRoot, selected.Source);
        TryDeleteBestEffort(downloaded);
        return (null, null);
    }

    private void LogRejectedArtworkCandidate(string slot, string managedRoot, string source)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Rejected text-heavy artwork candidate for {Slot}. artist={ArtistId} source={Source}",
                slot,
                managedRoot,
                source);
        }
    }

    private static ArtworkCandidate? NormalizeCandidate(ArtworkCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.LocalPath))
        {
            var fullPath = Path.GetFullPath(candidate.LocalPath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            return candidate with
            {
                LocalPath = fullPath,
                Identity = string.IsNullOrWhiteSpace(candidate.Identity) ? fullPath : candidate.Identity
            };
        }

        if (!string.IsNullOrWhiteSpace(candidate.RemoteUrl))
        {
            return candidate with
            {
                Identity = string.IsNullOrWhiteSpace(candidate.Identity) ? candidate.RemoteUrl : candidate.Identity
            };
        }

        return null;
    }

    private async Task<PushOutcome> PushArtistMetadataAsync(
        PushMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var updates = new PushUpdateAccumulator();
        if (request.Target is PlexTarget or BothTargets)
        {
            await PushToPlexAsync(request, updates, warnings, cancellationToken);
        }

        if (request.Target is JellyfinTarget or BothTargets)
        {
            await PushToJellyfinAsync(request, updates, warnings, cancellationToken);
        }

        return new PushOutcome(updates.HasAnyUpdate, warnings);
    }

    private async Task PushToPlexAsync(
        PushMetadataRequest request,
        PushUpdateAccumulator updates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var plex = request.Auth.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            warnings.Add("Plex is not configured.");
            return;
        }

        try
        {
            var location = await _plexClient.FindArtistLocationAsync(plex.Url, plex.Token, request.ArtistName, cancellationToken);
            if (location is null)
            {
                warnings.Add("Plex artist not found.");
                return;
            }

            if (request.LocalArtistId > 0 && !string.IsNullOrWhiteSpace(location.RatingKey))
            {
                await _libraryRepository.UpsertArtistSourceIdAsync(request.LocalArtistId, PlexTarget, location.RatingKey, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(request.AvatarPath) && File.Exists(request.AvatarPath))
            {
                updates.AvatarUpdated = await _plexClient.UpdateArtistPosterFromFileAsync(
                    plex.Url,
                    plex.Token,
                    location.RatingKey,
                    request.AvatarPath,
                    cancellationToken) || updates.AvatarUpdated;
            }

            if (!string.IsNullOrWhiteSpace(request.BackgroundPath) && File.Exists(request.BackgroundPath))
            {
                updates.BackgroundUpdated = await _plexClient.UpdateArtistArtFromFileAsync(
                    plex.Url,
                    plex.Token,
                    location.RatingKey,
                    request.BackgroundPath,
                    cancellationToken) || updates.BackgroundUpdated;
            }

            if (!string.IsNullOrWhiteSpace(request.Biography))
            {
                updates.BioUpdated = await _plexClient.UpdateArtistBiographyAsync(
                    plex.Url,
                    plex.Token,
                    location.SectionKey,
                    location.RatingKey,
                    request.Biography,
                    cancellationToken) || updates.BioUpdated;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater Plex push failed for {Artist}", request.ArtistName);
            warnings.Add("Plex update failed.");
        }
    }

    private async Task PushToJellyfinAsync(
        PushMetadataRequest request,
        PushUpdateAccumulator updates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var jellyfin = request.Auth.Jellyfin;
        if (jellyfin is null || string.IsNullOrWhiteSpace(jellyfin.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            warnings.Add("Jellyfin is not configured.");
            return;
        }

        try
        {
            var artistId = await _jellyfinClient.FindArtistIdAsync(jellyfin.Url, jellyfin.ApiKey, request.ArtistName, cancellationToken);
            if (string.IsNullOrWhiteSpace(artistId))
            {
                warnings.Add("Jellyfin artist not found.");
                return;
            }

            if (request.LocalArtistId > 0)
            {
                await _libraryRepository.UpsertArtistSourceIdAsync(request.LocalArtistId, JellyfinTarget, artistId, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(request.AvatarPath) && File.Exists(request.AvatarPath))
            {
                updates.AvatarUpdated = await _jellyfinClient.UpdateArtistImageAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    artistId,
                    request.AvatarPath,
                    cancellationToken) || updates.AvatarUpdated;
            }

            if (!string.IsNullOrWhiteSpace(request.BackgroundPath) && File.Exists(request.BackgroundPath))
            {
                updates.BackgroundUpdated = await _jellyfinClient.UpdateArtistBackdropAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    artistId,
                    request.BackgroundPath,
                    cancellationToken) || updates.BackgroundUpdated;
            }

            if (!string.IsNullOrWhiteSpace(request.Biography))
            {
                updates.BioUpdated = await _jellyfinClient.UpdateArtistOverviewAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    artistId,
                    request.Biography,
                    cancellationToken) || updates.BioUpdated;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater Jellyfin push failed for {Artist}", request.ArtistName);
            warnings.Add("Jellyfin update failed.");
        }
    }

    private static async Task<string?> CopyIntoSlotAsync(string managedRoot, string slot, string sourcePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        var extension = ImageFileExtensionResolver.NormalizeStandardImageExtension(Path.GetExtension(sourcePath));
        var destination = Path.Join(managedRoot, $"{slot}{extension}");
        await using (var sourceStream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        await using (var destinationStream = File.Create(destination))
        {
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        }

        DeleteSlotVariants(managedRoot, slot, destination);
        return destination;
    }

    private async Task<bool> IsArtworkCandidateUsableAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(imagePath);
            using var image = await Image.LoadAsync<Rgba32>(stream, cancellationToken);
            return !LikelyContainsOverlayText(image);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Artwork text inspection failed for {Path}", imagePath);
            }
            return true;
        }
    }

    private static bool LikelyContainsOverlayText(Image<Rgba32> image)
    {
        const int maxWidth = 384;
        using var sampled = image.CloneAs<Rgba32>();
        if (sampled.Width > maxWidth)
        {
            var resizedHeight = Math.Max(1, (int)Math.Round(sampled.Height * (maxWidth / (double)sampled.Width)));
            sampled.Mutate(ctx => ctx.Resize(maxWidth, resizedHeight));
        }

        if (sampled.Width < 48 || sampled.Height < 48)
        {
            return false;
        }

        var topBandHeight = Math.Max(8, (int)Math.Round(sampled.Height * 0.22));
        var bottomBandStart = Math.Max(0, sampled.Height - topBandHeight);
        var middleStart = topBandHeight;
        var middleHeight = Math.Max(8, bottomBandStart - middleStart);

        var top = AnalyzeBand(sampled, 0, topBandHeight);
        var middle = AnalyzeBand(sampled, middleStart, middleHeight);
        var bottom = AnalyzeBand(sampled, bottomBandStart, sampled.Height - bottomBandStart);

        return IsTextHeavyBand(top, middle) || IsTextHeavyBand(bottom, middle);
    }

    private static ArtworkBandAnalysis AnalyzeBand(Image<Rgba32> image, int startRow, int height)
    {
        var yStart = Math.Max(1, startRow);
        var yEnd = Math.Min(image.Height - 1, startRow + Math.Max(1, height));
        if (yEnd <= yStart)
        {
            return new ArtworkBandAnalysis(0, 0);
        }

        var totalPixels = 0;
        var edgePixels = 0;
        var transitions = 0;
        var rows = 0;

        for (var y = yStart; y < yEnd; y++)
        {
            rows++;
            var previousEdge = false;
            var rowTransitions = 0;

            for (var x = 1; x < image.Width - 1; x++)
            {
                var current = image[x, y];
                var right = image[x + 1, y];
                var down = image[x, y + 1];
                var edge = Math.Abs(GetLuminance(current) - GetLuminance(right))
                           + Math.Abs(GetLuminance(current) - GetLuminance(down)) >= 95;

                totalPixels++;
                if (edge)
                {
                    edgePixels++;
                }

                if (x > 1 && edge != previousEdge)
                {
                    rowTransitions++;
                }

                previousEdge = edge;
            }

            transitions += rowTransitions;
        }

        if (totalPixels <= 0 || rows <= 0)
        {
            return new ArtworkBandAnalysis(0, 0);
        }

        return new ArtworkBandAnalysis(
            edgePixels / (double)totalPixels,
            transitions / (double)(rows * Math.Max(1, image.Width - 2)));
    }

    private static bool IsTextHeavyBand(ArtworkBandAnalysis band, ArtworkBandAnalysis middle)
    {
        return band.EdgeDensity >= 0.135
            && band.TransitionDensity >= 0.18
            && band.EdgeDensity >= Math.Max(0.04, middle.EdgeDensity) * 1.45
            && band.TransitionDensity >= Math.Max(0.06, middle.TransitionDensity) * 1.35;
    }

    private static double GetLuminance(Rgba32 pixel)
        => (pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114);

    private async Task<string?> DownloadIntoSlotAsync(string managedRoot, string slot, string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var extension = ImageFileExtensionResolver.ResolveStandardImageExtension(response.Content.Headers.ContentType?.MediaType, url);
        var destination = Path.Join(managedRoot, $"{slot}{extension}");
        await using (var destinationStream = File.Create(destination))
        {
            await response.Content.CopyToAsync(destinationStream, cancellationToken);
        }

        DeleteSlotVariants(managedRoot, slot, destination);
        return destination;
    }

    private static string? ResolveSlotCandidate(string managedRoot, string slot)
    {
        if (!Directory.Exists(managedRoot))
        {
            return null;
        }

        return Directory.GetFiles(managedRoot, $"{slot}.*", SearchOption.TopDirectoryOnly)
            .Where(File.Exists)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void DeleteSlotVariants(string managedRoot, string slot, string keepPath)
    {
        if (!Directory.Exists(managedRoot))
        {
            return;
        }

        foreach (var path in Directory.GetFiles(managedRoot, $"{slot}.*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static void TryDeleteBestEffort(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private async Task<MetadataUpdaterState> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return new MetadataUpdaterState();
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<MetadataUpdaterState>(stream, _jsonOptions, cancellationToken);
            return state ?? new MetadataUpdaterState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load metadata updater state.");
            return new MetadataUpdaterState();
        }
    }

    private async Task SaveStateAsync(MetadataUpdaterState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_statePath);
        await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken);
    }

    private void UpdateStatus(MetadataUpdaterStatusSnapshot status)
    {
        lock (_statusLock)
        {
            _status = status;
        }
    }

    private static int NormalizeIntervalDays(int value) => Math.Clamp(value, 0, 365);

    private static string NormalizeTarget(string? value)
    {
        var normalized = (value ?? PlexTarget).Trim().ToLowerInvariant();
        return normalized switch
        {
            JellyfinTarget => JellyfinTarget,
            BothTargets => BothTargets,
            _ => PlexTarget
        };
    }

    private static string NormalizeMetadataSource(string? value)
    {
        var normalized = (value ?? MetadataSourceAuto).Trim().ToLowerInvariant();
        return normalized switch
        {
            MetadataSourceSpotify => MetadataSourceSpotify,
            MetadataSourceDeezer => MetadataSourceDeezer,
            MetadataSourceApple => MetadataSourceApple,
            _ => MetadataSourceAuto
        };
    }

    private static string? SanitizeBiography(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return text;
    }

    private sealed record PreparedRunState(
        MetadataUpdaterState State,
        List<MetadataUpdaterTrackedArtist> Candidates,
        DateTimeOffset NowUtc);
    private sealed record ResolvedArtistMetadata(string? Biography, IReadOnlyList<ArtworkCandidate> Candidates);
    private enum ArtistProcessingOutcome
    {
        Succeeded,
        Failed,
        Skipped
    }
    private sealed class MetadataRunCounters
    {
        public MetadataRunCounters(int totalArtists)
        {
            TotalArtists = totalArtists;
        }

        public int TotalArtists { get; }
        public int ProcessedArtists { get; set; }
        public int SuccessfulArtists { get; private set; }
        public int FailedArtists { get; private set; }
        public int SkippedArtists { get; private set; }

        public void Apply(ArtistProcessingOutcome outcome)
        {
            switch (outcome)
            {
                case ArtistProcessingOutcome.Succeeded:
                    SuccessfulArtists++;
                    return;
                case ArtistProcessingOutcome.Failed:
                    FailedArtists++;
                    return;
                case ArtistProcessingOutcome.Skipped:
                    SkippedArtists++;
                    return;
                default:
                    return;
            }
        }
    }

    private sealed record PreparedVisuals(string? AvatarPath, string? BackgroundPath, int NextAvatarIndex, int NextBackgroundIndex);
    private sealed record ArtworkBandAnalysis(double EdgeDensity, double TransitionDensity);
    private sealed record ArtworkCandidate(string Identity, string Source, string? LocalPath, string? RemoteUrl)
    {
        public static ArtworkCandidate FromLocal(string path, string identity, string source)
            => new(identity, source, path, null);

        public static ArtworkCandidate FromRemote(string url, string identity, string source)
            => new(identity, source, null, url);
    }
    private sealed record PushOutcome(bool Updated, IReadOnlyList<string> Warnings);
    private sealed record PushMetadataRequest(
        long LocalArtistId,
        PlatformAuthState Auth,
        string ArtistName,
        string Target,
        string? AvatarPath,
        string? BackgroundPath,
        string? Biography);
    private sealed class PushUpdateAccumulator
    {
        public bool AvatarUpdated { get; set; }
        public bool BackgroundUpdated { get; set; }
        public bool BioUpdated { get; set; }
        public bool HasAnyUpdate => AvatarUpdated || BackgroundUpdated || BioUpdated;
    }
}

public sealed class MetadataUpdaterRunRequest
{
    public long? ArtistId { get; set; }
    public string? Source { get; set; }
    public string? Target { get; set; }
    public int? IntervalDays { get; set; }
    public bool? IncludeAvatar { get; set; }
    public bool? IncludeBackground { get; set; }
    public bool? IncludeBio { get; set; }
    public bool? IncludeAllArtists { get; set; }
    public bool? Force { get; set; }
}

public sealed class ManualPushRegistrationRequest
{
    public long ArtistId { get; set; }
    public string ArtistName { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Target { get; set; }
    public bool IncludeAvatar { get; set; }
    public bool IncludeBackground { get; set; }
    public bool IncludeBio { get; set; }
    public int? IntervalDays { get; set; }
}

public sealed class MetadataUpdaterState
{
    public int Version { get; set; } = 1;
    public List<MetadataUpdaterTrackedArtist> Artists { get; set; } = new();
}

public sealed class MetadataUpdaterTrackedArtist
{
    public long ArtistId { get; set; }
    public string ArtistName { get; set; } = string.Empty;
    public string Source { get; set; } = "auto";
    public string Target { get; set; } = "plex";
    public bool IncludeAvatar { get; set; } = true;
    public bool IncludeBackground { get; set; } = true;
    public bool IncludeBio { get; set; }
    public int IntervalDays { get; set; } = 30;
    public DateTimeOffset? LastPushedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int AvatarRotationIndex { get; set; }
    public int BackgroundRotationIndex { get; set; }
}

public sealed record MetadataUpdaterStatusSnapshot(
    bool Running,
    string Phase,
    string? Message,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int TotalArtists,
    int ProcessedArtists,
    int SuccessfulArtists,
    int FailedArtists,
    int SkippedArtists,
    string? CurrentArtist)
{
    public static MetadataUpdaterStatusSnapshot Idle()
        => new(
            Running: false,
            Phase: "Idle",
            Message: null,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            TotalArtists: 0,
            ProcessedArtists: 0,
            SuccessfulArtists: 0,
            FailedArtists: 0,
            SkippedArtists: 0,
            CurrentArtist: null);
}
