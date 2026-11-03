using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The Soulseek download engine.
/// </summary>
/// <remarks>
///     <para>
///         This is a thin adapter onto the shared engine pipeline. Dedupe-before-search, quality verification,
///         tagging, the final destination, the activity model and fallback all come from
///         <see cref="EngineQueueProcessorHelper"/> and the services it calls, so Soulseek behaves like every
///         other engine rather than reimplementing any of it.
///     </para>
///     <para>
///         The one thing that is Soulseek-specific is that resolution is a live peer search rather than a
///         catalogue lookup, so the search happens inside the download step when no candidate was chosen
///         earlier.
///     </para>
/// </remarks>
public sealed class SoulseekEngineProcessor : QueueEngineProcessorBase
{
    private const string EngineName = SoulseekQueueItem.EngineId;

    private readonly ISoulseekDownloadService _downloader;
    private readonly ISoulseekTransferService _sidecars;
    private readonly ISoulseekSearchService? _search;
    private readonly ISoulseekRealtimePublisher _realtime;
    private readonly DownloadQueueRepository? _queueRepository;
    private readonly IHostApplicationLifetime? _hostLifetime;
    private readonly ILogger<SoulseekEngineProcessor> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekEngineProcessor"/> class.</summary>
    public SoulseekEngineProcessor(
        EngineProcessorCommonDependencies commonDependencies,
        ISoulseekDownloadService downloader,
        ILogger<SoulseekEngineProcessor> logger,
        ISoulseekRealtimePublisher? realtime = null,
        ISoulseekTransferService? sidecars = null,
        DownloadQueueRepository? queueRepository = null,
        IHostApplicationLifetime? hostLifetime = null,
        ISoulseekSearchService? search = null)
        : base(EngineName, commonDependencies)
    {
        _downloader = downloader;
        _search = search;

        // Optional so a host that registers the engine without the Soulseek transport still resolves it, and
        // simply takes no sidecars rather than failing the download.
        _sidecars = sidecars ?? NullSoulseekSidecarFetcher.Instance;
        _logger = logger;
        _realtime = realtime ?? NullSoulseekRealtimePublisher.Instance;

        // Also optional, and for the same reason. Without them the transfer id simply stays off the payload:
        // the association in the transfer table is the authority either way, so a host that does not supply
        // them loses a convenience rather than the ability to resume.
        _queueRepository = queueRepository;
        _hostLifetime = hostLifetime;
    }

    /// <inheritdoc />
    public override async Task ProcessQueueItemAsync(DownloadQueueItem item, CancellationToken cancellationToken)
    {
        await EngineQueueProcessorHelper.ProcessQueueItemAsync(
            item,
            EngineName,
            CommonDependencies.CreateProcessorDeps(_logger),
            BuildCallbacks(item),
            cancellationToken);
    }

    private EngineQueueProcessorHelper.ProcessorCallbacks<SoulseekQueueItem> BuildCallbacks(DownloadQueueItem item)
    {
        DeezSpoTagSettings? effectiveSettings = null;
        return new(
            ResolveSoulseekSourceId,
            (payload, settings) => { effectiveSettings = settings; return BuildRequest(payload, settings); },
            ApplyRequestContext,
            DownloadAsync,
            null,
            _ => $"Download start: {item.QueueUuid} engine=soulseek",
            payload => payload.Title,
            static payload => payload.ToQueuePayload(),
            (payload, path, token) => AcceptAcquiredAudioAsync(payload, path, effectiveSettings, token),
            RejectAcquiredAudioAsync,
            CompleteAudioOnlyAsync);
    }

    /// <summary>
    ///     Ends a Soulseek download once the audio is verified, handing the file to the enrichment stage.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Everything that could make the download a failure has already happened and passed: the
    ///         transfer, the delivered size, the plan's quality step, and the exact peer. What follows used to
    ///         be immediate tag writing, which for a peer-to-peer source means writing whatever the peer
    ///         happened to embed - and reporting a tag-writing warning for a transfer that in fact succeeded.
    ///     </para>
    ///     <para>
    ///         So the audio phase ends here and says so plainly. The file stays exactly where promotion put
    ///         it, under the shared download root and owned by this queue item, and the enrichment stage is
    ///         what identifies it, applies templates and moves it into the destination.
    ///     </para>
    ///     <para>
    ///         Nothing is reported as failed or enriched here. A download whose enrichment later fails must be
    ///         recoverable from this point, not redownloaded.
    ///     </para>
    /// </remarks>
    private static Task CompleteAudioOnlyAsync(
        SoulseekQueueItem payload,
        string verifiedAudioPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        payload.Status = SoulseekDownloadStatus.Completed;
        payload.AudioAcquired = true;
        payload.AcquiredAudioPath = verifiedAudioPath;
        payload.FilePath = DownloadPathResolver.NormalizeDisplayPath(verifiedAudioPath);

        // Enrichment and finalization are outstanding work, not work that failed. The distinction is what
        // stops the queue from re-running a transfer that already succeeded.
        payload.EnrichmentPending = true;

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Returns the peer and filename the download will fetch.
    /// </summary>
    /// <remarks>
    ///     This is the Soulseek equivalent of a track id. It is empty when nothing has been chosen yet, which
    ///     is the normal case and tells the download step to run a search.
    /// </remarks>
    private static string ResolveSoulseekSourceId(SoulseekQueueItem payload)
        => string.IsNullOrWhiteSpace(payload.SoulseekUsername) || string.IsNullOrWhiteSpace(payload.SoulseekRemotePath)
            ? string.Empty
            : $"{payload.SoulseekUsername}:{payload.SoulseekRemotePath}";

    private static SoulseekDownloadRequest BuildRequest(SoulseekQueueItem payload, DeezSpoTagSettings settings)
        => SoulseekRequestBuilder.BuildRequest(payload, settings);

    private static void ApplyRequestContext(object request, EngineAudioPostDownloadHelper.EngineTrackContext context)
    {
        var soulseekRequest = (SoulseekDownloadRequest)request;
        soulseekRequest.OutputDir = context.OutputDir;
        soulseekRequest.FilenameFormat = context.FilenameFormat;
    }

    private async Task<string> DownloadAsync(
        SoulseekQueueItem payload,
        object request,
        DeezSpoTagSettings settings,
        Func<double, double, Task>? progressReporter,
        CancellationToken cancellationToken)
    {
        var soulseekRequest = (SoulseekDownloadRequest)request;

        // The transfer id has to reach the queue row while the transfer is still running, not only once it
        // finishes. Written through the existing payload update, so nothing new is persisted and the row keeps
        // the same shape it had.
        soulseekRequest.TransferAttachedAsync = async (status, token) =>
        {
            if (_queueRepository is null || status.TransferId is not { } transferId)
            {
                return;
            }

            payload.SoulseekTransferId = transferId.ToString();
            await QueueHelperUtils.UpdatePayloadAsync(_queueRepository, payload.Id, payload, token).ConfigureAwait(false);
        };

        // The host's own shutdown, kept apart from the item's cancellation so a shutdown does not cancel a
        // transfer slskd is still fetching and a later run can pick back up.
        soulseekRequest.HostStoppingToken = _hostLifetime?.ApplicationStopping ?? CancellationToken.None;

        // The shared pipeline persists the expected staging path on the payload before calling us, so that is
        // where the finished file must appear. Without it the transfer cannot be verified, and an unverifiable
        // download must not be allowed to look successful.
        var expectedPath = ResolveExpectedPath(payload);
        payload.Status = SoulseekDownloadStatus.Downloading;

        _realtime.PublishDownloadUpdate(new SoulseekDownloadProgress(
            payload.Id,
            payload.SoulseekUsername,
            payload.SoulseekRemotePath,
            string.IsNullOrWhiteSpace(payload.SoulseekRemotePath) ? SoulseekDownloadStage.Searching : SoulseekDownloadStage.Enqueued));

        SoulseekCompletedFile completed;
        try
        {
            completed = await _downloader
                .DownloadAsync(soulseekRequest, payload.Id, expectedPath, progressReporter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The download failed. Report it so the activity view does not sit on a silent stalled row.
            // The panel message is fixed text: an exception message can carry a credential, a local path or
            // half a stack trace, and the page renders whatever arrives here. The logged exception has it all.
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(
                    ex,
                    "Soulseek download for queue item {ItemId} failed at stage {Stage}.",
                    payload.Id,
                    payload.Status);
            }

            _realtime.PublishDownloadUpdate(new SoulseekDownloadProgress(
                payload.Id,
                payload.SoulseekUsername,
                payload.SoulseekRemotePath,
                SoulseekDownloadStage.Failed,
                Error: "The download failed. Check the server log for the cause."));
            _realtime.PublishImportUpdate(new SoulseekImportUpdate(payload.Id, "failed",
                Error: "The download failed. Check the server log for the cause."));
            throw;
        }

        _realtime.PublishDownloadUpdate(new SoulseekDownloadProgress(
            payload.Id,
            completed.Username,
            completed.Filename,
            SoulseekDownloadStage.Verifying,
            Size: completed.Size));

        if (!string.IsNullOrWhiteSpace(completed.Username))
        {
            payload.SoulseekUsername = completed.Username;
            payload.SoulseekRemotePath = completed.Filename;
        }

        payload.TotalSize = completed.Size;
        payload.Status = SoulseekDownloadStatus.Completed;

        // The peer's own cover and lyrics are taken here, while the download is still in the engine's hands,
        // and recorded on the payload as local paths. Fetching them later would mean the artwork step had to
        // reach back into slskd, by which time the peer may have moved the files.
        await TakePeerSidecarsAsync(payload, completed, settings, cancellationToken).ConfigureAwait(false);

        // The file is verified, but it is not imported yet: tagging, the final destination and enrichment all
        // happen in the shared post-download pipeline. That hand-off is what this event reports.
        _realtime.PublishImportUpdate(new SoulseekImportUpdate(payload.Id, "verified", completed.LocalPath));

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Soulseek download for {QueueUuid} verified {Bytes} bytes at {Path}.",
                payload.Id,
                completed.Size,
                completed.LocalPath);
        }

        return completed.LocalPath;
    }

    private async Task<string> AcceptAcquiredAudioAsync(
        SoulseekQueueItem payload, string acquiredPath, DeezSpoTagSettings? settings, CancellationToken cancellationToken)
    {
        var promoted = await _downloader.PromoteAcceptedAudioAsync(payload, acquiredPath, cancellationToken);
        if (SoulseekSidecarPolicy.UsesPeerSidecars(payload) && settings?.Soulseek?.UsePeerArtwork == true
            && File.Exists(payload.SoulseekPeerArtworkPath))
        {
            // Apply only peer artwork, after ownership and audio-quality verification. Enrichment
            // continues to use its existing preservation/overwrite rules.
            try
            {
                var source = payload.SoulseekPeerArtworkPath;
                var prepared = await ArtworkAssetProcessor.PrepareAsync(new ArtworkAssetRequest(
                    await File.ReadAllBytesAsync(source, cancellationToken), settings.LocalArtworkFormat,
                    settings.LocalArtworkSize, settings.EmbeddedArtworkSize, settings.EmbedMaxQualityCover,
                    settings.JpegImageQuality, Path.GetExtension(promoted)), cancellationToken);
                if (settings.Tags?.Cover == true) EmbeddedArtworkWriter.WriteAndVerify(promoted, prepared.Embedded);
                var savedSidecars = new HashSet<string>(StringComparer.Ordinal);
                if (settings.SaveArtwork)
                {
                    var name = PathTemplateGenerator.GenerateAlbumName(settings.CoverImageTemplate,
                        new DeezSpoTag.Core.Models.Album(payload.Album) { MainArtist = new DeezSpoTag.Core.Models.Artist(payload.Artist) }, settings, null);
                    foreach (var variant in prepared.Sidecars.Values)
                    {
                        var destination = Path.Join(Path.GetDirectoryName(promoted)!, name + "." + variant.Extension);
                        await File.WriteAllBytesAsync(destination, variant.Bytes, cancellationToken);
                        payload.SoulseekPeerArtworkPath = destination;
                        savedSidecars.Add(destination);
                    }
                }
                else payload.SoulseekPeerArtworkPath = string.Empty;
                if (!savedSidecars.Contains(source) && File.Exists(source)) File.Delete(source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex)
                    || ex is SixLabors.ImageSharp.ImageFormatException))
            {
                _logger.LogWarning(ex, "Could not apply Soulseek peer artwork for {QueueUuid}; retaining verified audio.", payload.Id);
            }
        }
        return promoted;
    }

    /// <summary>
    ///     Fetches the cover and lyrics the reader chose from the peer's own folder, and records where they
    ///     landed so the enrichment step can prefer them.
    /// </summary>
    /// <remarks>
    ///     The audio has already been verified by the time this runs, so a sidecar failure cannot cost the
    ///     reader the track. It is caught for that reason: the fetch is best-effort decoration attached to a
    ///     download that has already succeeded.
    /// </remarks>
    private async Task TakePeerSidecarsAsync(
        SoulseekQueueItem payload,
        SoulseekCompletedFile completed,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (!SoulseekSidecarPolicy.UsesPeerSidecars(payload)
            || string.IsNullOrWhiteSpace(payload.SoulseekUsername) || _search is null)
            return;
        var peer = settings.Soulseek ?? new SoulseekDownloadSettings();
        var artwork = peer.UsePeerArtwork && (settings.SaveArtwork || settings.Tags?.Cover == true);
        var lyrics = peer.UsePeerLyrics && (settings.SaveLyrics || settings.Tags?.Lyrics == true || settings.Tags?.SyncedLyrics == true);
        if (!artwork && !lyrics) return;

        try
        {
            var directory = SoulseekRemotePath.GetDirectory(completed.Filename);
            var listing = await _search.BrowseAsync(completed.Username, directory, cancellationToken).ConfigureAwait(false);
            var paths = listing.SelectMany(entry => entry.Files
                .Where(file => !file.IsLocked)
                .Select(file => SoulseekBrowseFilePolicy.Evaluate(completed.Username, file, peer, Array.Empty<string>(), entry.Directory).Filename));
            payload.SoulseekSidecarRemotePaths = SoulseekSidecarPolicy.SelectForTrack(
                completed.Filename, payload.Title, payload.Artist, paths, artwork, lyrics).ToList();
            if (payload.SoulseekSidecarRemotePaths.Count == 0) return;

            var audioPath = ResolveExpectedPath(payload);
            var extension = Path.GetExtension(completed.LocalPath);
            if (!string.Equals(Path.GetExtension(audioPath), extension, StringComparison.OrdinalIgnoreCase)) audioPath += extension;
            var output = Path.GetDirectoryName(audioPath)!;
            var fetched = await _sidecars.FetchSidecarsAsync(completed.Username,
                payload.SoulseekSidecarRemotePaths, output, cancellationToken).ConfigureAwait(false);
            foreach (var remote in payload.SoulseekSidecarRemotePaths)
            {
                if (!fetched.TryGetValue(remote, out var local) || !File.Exists(local)) continue;
                var role = SoulseekSidecarPolicy.Classify(remote);
                if (role == SoulseekSidecarPolicy.Cover)
                {
                    payload.SoulseekPeerArtworkPath = local;
                }
                else if (role == SoulseekSidecarPolicy.Lyrics)
                {
                    var destination = Path.ChangeExtension(audioPath, Path.GetExtension(local));
                    // Enrichment owns overwrite/upgrade decisions. Acquisition never replaces existing lyrics.
                    if (!File.Exists(destination)) File.Move(local, destination, overwrite: false);
                    payload.SoulseekPeerLyricsPath = destination;
                }
            }

            if (_logger.IsEnabled(LogLevel.Debug) && fetched.Count > 0)
            {
                _logger.LogDebug(
                    "Took {Count} peer sidecar file(s) with the Soulseek download for {QueueUuid}.",
                    fetched.Count,
                    payload.Id);
            }
        }
        catch (Exception ex) when (ex is SlskdApiException
                                   or HttpRequestException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or SoulseekUnavailableException
                                   or ArgumentException
                                   or NotSupportedException
                                   or InvalidOperationException)
        {
            // The audio is already verified and is about to be imported. Nothing a cover image did or did not
            // do may turn that into a failure. The filter is enumerated rather than "everything" so that a bug
            // in this code still surfaces instead of being filed as a peer problem.
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    ex,
                    "Could not take the peer sidecar files for Soulseek queue {QueueUuid}; continuing without them.",
                    payload.Id);
            }
        }
    }
    private Task RejectAcquiredAudioAsync(SoulseekQueueItem payload, string rejectedPath, CancellationToken cancellationToken)
        => _downloader.DeleteRejectedStagingAudio(payload, rejectedPath, cancellationToken);

    private static string ResolveExpectedPath(SoulseekQueueItem payload)
    {
        // QueueHelperUtils.PersistExpectedStagingPathAsync writes the path template's output here before the
        // download step runs.
        if (!string.IsNullOrWhiteSpace(payload.FilePath))
        {
            return DownloadPathResolver.ResolveIoPath(payload.FilePath);
        }

        // The earlier fallback joined the raw OutputDir and FilenameFormat, but the format still carries its
        // tokens at this point, so the "path" it produced is not the path the pipeline will actually use.
        // Verification against it would fail in ways that look like a transfer problem rather than the
        // missing staging write it is. Refusal says so directly.
        throw new InvalidOperationException(
            "Soulseek could not determine where the finished file should be written: no expected staging path " +
            "was recorded for this download, so it cannot be verified.");
    }
}
