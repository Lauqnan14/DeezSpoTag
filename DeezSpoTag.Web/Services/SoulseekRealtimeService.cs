using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DeezSpoTag.Web.Services;

/// <summary>
///     Publishes Soulseek progress to connected clients over SignalR.
/// </summary>
/// <remarks>
///     <para>
///         Follows the <c>ActivitiesRealtimeService</c> pattern: fire and forget, <c>Clients.All</c>, and a
///         <c>Debug</c>-level catch so a broadcast failure never surfaces into the download path.
///     </para>
///     <para>
///         The event names are deliberately snake_case. Every other hub in this app uses lowerCamelCase with a
///         Changed or Updated suffix, but these eight names are a fixed part of the Soulseek integration's
///         contract, so they are published exactly as specified rather than renamed to match local style.
///     </para>
/// </remarks>
public sealed class SoulseekRealtimeService : ISoulseekRealtimePublisher
{
    /// <summary>How many ranked results travel on a single live update.</summary>
    public const int LiveResultSliceSize = 25;

    /// <summary>The event names, fixed by the integration's contract.</summary>
    public static class Events
    {
        public const string ConnectionState = "connection_state";
        public const string EngineHealth = "engine_health";
        public const string SearchUpdate = "search_update";
        public const string SearchResult = "search_result";
        public const string DownloadUpdate = "download_update";
        public const string ShareSyncUpdate = "share_sync_update";
        public const string ShareScanUpdate = "share_scan_update";
        public const string ImportUpdate = "import_update";
    }

    private readonly IHubContext<SoulseekHub> _hubContext;
    private readonly ILogger<SoulseekRealtimeService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekRealtimeService"/> class.</summary>
    public SoulseekRealtimeService(IHubContext<SoulseekHub> hubContext, ILogger<SoulseekRealtimeService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public void PublishConnectionState(SoulseekConnectionStatus status)
        => Publish(Events.ConnectionState, new
        {
            state = status.State.ToString().ToLowerInvariant(),
            status.Message,
            status.Username,
            status.LastError,
            checkedAtUtc = status.CheckedAtUtc
        });

    /// <inheritdoc />
    public void PublishEngineHealth(string state, string? message)
        => Publish(Events.EngineHealth, new { state, message });

    /// <inheritdoc />
    public void PublishSearchUpdate(SoulseekSearchProgress progress)
        => Publish(Events.SearchUpdate, new
        {
            progress.SearchId,
            progress.QueueUuid,
            progress.SearchText,

            // As a string, not the raw enum number. Every other event in this contract lowercases its
            // status, and a number arriving where a name belongs renders as a bare integer in the UI.
            stage = progress.Stage.ToString().ToLowerInvariant(),
            progress.Completed,
            progress.TimedOut,
            progress.ResponseCount,
            progress.ElapsedSeconds,
            final = progress.Final,
            candidateCount = progress.Results?.Count ?? 0,
            failed = progress.ErrorCode is not null,
            progress.ErrorCode,
            progress.Error,

            // Only the leading slice travels on each tick. The client merges by id, and the final event
            // makes it fetch the complete set, so nothing is ever truncated.
            results = (progress.Results ?? [])
                .Take(LiveResultSliceSize)
                .Select(ToLiveResult)
                .ToList()
        });

    /// <inheritdoc />
    public void PublishSearchResult(SoulseekSearchOutcome outcome)
        => Publish(Events.SearchResult, new
        {
            outcome.SearchId,
            outcome.SearchText,
            candidateCount = outcome.Candidates.Count,
            acceptedCount = outcome.Candidates.Count(candidate => candidate.Accepted),
            outcome.ResponseCount,
            outcome.Completed,
            outcome.TimedOut,

            // On the failure events these are what tell the client to stop waiting and say so. A failed search
            // carries no candidates, so without them a live panel reads it as an empty result set and keeps polling.
            failed = outcome.Failed,
            outcome.ErrorCode,
            outcome.Error,
            best = outcome.Best is null
                ? null
                : new
                {
                    outcome.Best.Username,
                    outcome.Best.Filename,
                    outcome.Best.Quality,
                    outcome.Best.QualityLabel,
                    outcome.Best.TierValue,
                    outcome.Best.CanonicalRank,
                    outcome.Best.Score
                }
        });

    /// <inheritdoc />
    public void PublishDownloadUpdate(SoulseekDownloadProgress progress)
        => Publish(Events.DownloadUpdate, new
        {
            progress.QueueUuid,
            progress.Username,
            progress.Filename,
            stage = progress.Stage.ToString().ToLowerInvariant(),
            progress.Progress,
            progress.BytesTransferred,
            progress.Size,
            progress.AverageSpeed,
            progress.Verified,
            progress.Error
        });

    /// <inheritdoc />
    public void PublishShareSyncUpdate(SoulseekShareReconciliation reconciliation)
        => Publish(Events.ShareSyncUpdate, new
        {
            reconciliation.GeneratedAtUtc,
            enabledCount = reconciliation.EnabledFolders.Count,
            missingCount = reconciliation.MissingFromSlskd.Count,
            unexpectedCount = reconciliation.UnexpectedlyShared.Count,
            reconciliation.SlskdUnavailable,
            diagnostics = reconciliation.Diagnostics
                .Select(diagnostic => new
                {
                    diagnostic.Code,
                    severity = diagnostic.Severity.ToString().ToLowerInvariant(),
                    diagnostic.Message,
                    diagnostic.FolderId
                })
                .ToList()
        });

    /// <inheritdoc />
    public void PublishShareScanUpdate(SoulseekShareScanStatus status)
        => Publish(Events.ShareScanUpdate, new
        {
            status.IsRunning,
            status.LastScanUtc,
            status.ShareCount,
            status.FileCount,
            status.Error,
            status.SlskdUnavailable
        });

    /// <inheritdoc />
    public void PublishImportUpdate(SoulseekImportUpdate update)
        => Publish(Events.ImportUpdate, new
        {
            update.QueueUuid,
            update.Stage,
            update.FinalPath,
            update.EnrichmentStatus,
            update.Error
        });

    /// <summary>
    ///     Flattens a candidate into the live wire shape. <c>id</c> is stable for the life of a search, which
    ///     is what lets the client merge updates without duplicating or reshuffling rows it has already drawn.
    /// </summary>
    private static object ToLiveResult(DeezSpoTag.Services.Download.Soulseek.SoulseekCandidate candidate)
    {
        // The same name the results endpoints send, so a row that arrives live reads identically to one
        // fetched after the search finalizes and the panel never has to fill the gap itself.
        var name = SoulseekFilenameProjection.Describe(candidate.Filename, candidate.Raw.DurationSeconds);

        return new
        {
            id = $"{candidate.Username}\u0000{candidate.Filename}",
            candidate.Username,
            candidate.Filename,
            title = name.Title,
            artist = name.Artist,
            album = name.Album,
            trackNumber = name.TrackNumber,
            size = candidate.Raw.Size,
            candidate.Quality,
            candidate.QualityLabel,
            candidate.TierValue,
            candidate.Score,
            candidate.Accepted,
            candidate.RejectedBecause
        };
    }

    private void Publish(string eventName, object payload)
    {
        _ = PublishAsync(eventName, payload);
    }

    private async Task PublishAsync(string eventName, object payload)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(eventName, payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to broadcast the Soulseek {EventName} event.", eventName);
            }
        }
    }
}
