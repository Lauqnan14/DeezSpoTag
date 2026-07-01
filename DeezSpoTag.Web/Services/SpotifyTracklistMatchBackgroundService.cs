using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyTracklistMatchBackgroundService : BackgroundService
{
    private const int MaxTransientAttempts = 3;
    private const int IdentityHydrationBatchSize = 64;
    private readonly ISpotifyTracklistMatchQueue _queue;
    private readonly ISpotifyTracklistMatchStore _store;
    private readonly DeezerClient _deezerClient;
    private readonly SpotifyTracklistService _tracklistService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<SpotifyTracklistMatchBackgroundService> _logger;

    public SpotifyTracklistMatchBackgroundService(
        ISpotifyTracklistMatchQueue queue,
        ISpotifyTracklistMatchStore store,
        DeezerClient deezerClient,
        SpotifyTracklistService tracklistService,
        ISettingsService settingsService,
        ILogger<SpotifyTracklistMatchBackgroundService> logger)
    {
        _queue = queue;
        _store = store;
        _deezerClient = deezerClient;
        _tracklistService = tracklistService;
        _settingsService = settingsService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = ReadConcurrencySettings();
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            var batch = ReadActiveBatch();
            if (batch.Count == 0)
            {
                continue;
            }

            var hydratedTracks = await _tracklistService.HydrateSpotifyIdentityBatchAsync(
                batch.Select(item => item.Track).ToList(),
                stoppingToken);
            for (var index = 0; index < batch.Count; index++)
            {
                var item = batch[index];
                var hydratedTrack = index < hydratedTracks.Count
                    ? hydratedTracks[index]
                    : item.Track;
                await gate.WaitAsync(stoppingToken);
                _ = ProcessItemAsync(item, hydratedTrack, gate, stoppingToken);
            }
        }
    }

    private List<SpotifyTracklistMatchWorkItem> ReadActiveBatch()
    {
        var batch = new List<SpotifyTracklistMatchWorkItem>(IdentityHydrationBatchSize);
        while (batch.Count < IdentityHydrationBatchSize && _queue.Reader.TryRead(out var item))
        {
            if (_store.IsActive(item.Token))
            {
                batch.Add(item);
            }
        }

        return batch;
    }

    private int ReadConcurrencySettings()
    {
        var settings = _settingsService.LoadSettings();
        var matchConcurrency = settings.SpotifyMatchConcurrency > 0
            ? settings.SpotifyMatchConcurrency
            : 1;
        return matchConcurrency;
    }

    private async Task ProcessItemAsync(
        SpotifyTracklistMatchWorkItem item,
        SpotifyTrackSummary resolvedTrack,
        SemaphoreSlim gate,
        CancellationToken stoppingToken)
    {
        try
        {
            if (!_store.IsActive(item.Token))
            {
                return;
            }

            var strictMode = _settingsService.LoadSettings().StrictSpotifyDeezerMode;
            await ResolveWithRetriesAsync(item, resolvedTrack, strictMode, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify tracklist background match failed for {TrackName}", item.Track.Name);
            _store.RecordMatch(
                item.Token,
                item.Index,
                string.Empty,
                item.Track.Id,
                "unmatched_final",
                "background_exception",
                MaxTransientAttempts);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ResolveWithRetriesAsync(
        SpotifyTracklistMatchWorkItem item,
        SpotifyTrackSummary resolvedTrack,
        bool strictMode,
        CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            if (!_store.IsActive(item.Token))
            {
                return;
            }

            RecordMatchAttemptStart(item, attempt);
            var result = await ResolveTrackAsync(item, resolvedTrack, strictMode, stoppingToken);
            if (await TryHandleTerminalResultAsync(
                    item,
                    resolvedTrack,
                    result,
                    strictMode,
                    attempt,
                    stoppingToken))
            {
                return;
            }

            _store.RecordProgress(
                item.Token,
                item.Index,
                item.Track.Id,
                "transient_failure",
                result.Reason,
                attempt);

            if (attempt < MaxTransientAttempts)
            {
                var backoffMs = (attempt * 800) + Random.Shared.Next(150, 450);
                await Task.Delay(backoffMs, stoppingToken);
                continue;
            }

            _store.RecordMatch(
                item.Token,
                item.Index,
                string.Empty,
                item.Track.Id,
                "unmatched_final",
                $"transient_exhausted:{result.Reason}",
                attempt);
            return;
        }
    }

    private void RecordMatchAttemptStart(SpotifyTracklistMatchWorkItem item, int attempt)
    {
        var preStatus = attempt > 1 ? "rechecking" : "matching";
        _store.RecordProgress(
            item.Token,
            item.Index,
            item.Track.Id,
            preStatus,
            attempt > 1 ? "retrying_transient_failure" : "match_started",
            attempt);
    }

    private async Task<SpotifyTracklistResolveResult> ResolveTrackAsync(
        SpotifyTracklistMatchWorkItem item,
        SpotifyTrackSummary resolvedTrack,
        bool strictMode,
        CancellationToken stoppingToken)
    {
        return await SpotifyTracklistResolver.ResolveDeezerTrackAsync(
            _deezerClient,
            resolvedTrack,
            new SpotifyTrackResolveOptions(
                AllowFallbackSearch: item.AllowFallbackSearch,
                PreferIsrcOnly: !item.AllowFallbackSearch,
                StrictMode: strictMode,
                BypassNegativeCanonicalCache: true,
                Logger: _logger,
                CancellationToken: stoppingToken));
    }

    private async Task<bool> TryHandleTerminalResultAsync(
        SpotifyTracklistMatchWorkItem item,
        SpotifyTrackSummary resolvedTrack,
        SpotifyTracklistResolveResult result,
        bool strictMode,
        int attempt,
        CancellationToken stoppingToken)
    {
        if (result.Outcome == SpotifyTracklistResolveOutcome.Matched)
        {
            _store.RecordMatch(
                item.Token,
                item.Index,
                result.DeezerId ?? string.Empty,
                item.Track.Id,
                "matched",
                result.Reason,
                attempt);
            return true;
        }

        if (!ShouldRunTerminalMetadataPass(result))
        {
            return false;
        }

        var terminalMetadataResult = await SpotifyTracklistResolver.ResolveFinalUnmatchedFromMetadataAsync(
            _deezerClient,
            resolvedTrack,
            strictMode,
            _logger,
            stoppingToken);
        if (terminalMetadataResult.Outcome == SpotifyTracklistResolveOutcome.Matched)
        {
            _store.RecordMatch(
                item.Token,
                item.Index,
                terminalMetadataResult.DeezerId ?? string.Empty,
                item.Track.Id,
                "matched",
                terminalMetadataResult.Reason,
                attempt);
            return true;
        }

        _store.RecordMatch(
            item.Token,
            item.Index,
            string.Empty,
            item.Track.Id,
            "unmatched_final",
            terminalMetadataResult.Reason,
            attempt);
        return true;
    }

    internal static bool ShouldRunTerminalMetadataPass(SpotifyTracklistResolveResult result) =>
        result.Outcome == SpotifyTracklistResolveOutcome.HardMismatch;
}
