using System.Globalization;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Matching;
using Microsoft.Extensions.Logging;
using IOFile = System.IO.File;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     The SoundCloud download engine's service: resolve an identity, resolve an authorized stream, transfer
///     the HLS playlist, and hand back a verified staging file.
/// </summary>
/// <remarks>
///     <para>
///         Shaped like <c>TidalDownloadService</c>. Candidate resolution, quality enforcement, the acquired
///         staging file, promotion, and rejected-file cleanup all happen here, and everything downstream of
///         the acquired file - tagging, artwork, destination, retries, fallback - stays with the shared
///         pipeline.
///     </para>
///     <para>
///         SoundCloud-specific protocol lives in <see cref="ISoundCloudClient"/>. This service owns the
///         decisions: which candidate is acceptable, which stream satisfies the request, and when the result
///         may be handed on.
///     </para>
/// </remarks>
public sealed class SoundCloudDownloadService
{
    private const string AcquiredStagingMarker = ".soundcloud-acquired";

    /// <summary>SoundCloud publishes MP3, so the extension is fixed rather than derived from a container probe.</summary>
    private const string AudioExtension = ".mp3";

    private const int MaxCandidateResults = 25;

    private readonly ISoundCloudClient _client;
    private readonly SoundCloudHlsDownloader _hlsDownloader;
    private readonly ILogger<SoundCloudDownloadService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoundCloudDownloadService" /> class.</summary>
    public SoundCloudDownloadService(
        ISoundCloudClient client,
        IHttpClientFactory httpClientFactory,
        ILogger<SoundCloudDownloadService> logger)
    {
        _client = client;
        _hlsDownloader = new SoundCloudHlsDownloader(httpClientFactory);
        _logger = logger;
    }

    /// <summary>
    ///     Runs one SoundCloud download and returns the acquired staging path.
    /// </summary>
    /// <param name="request">The resolved work.</param>
    /// <param name="progressCallback">Optional progress reporter, called with percentage and bytes per second.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    ///     An exact permalink on the request bypasses search entirely, so the track the reader picked is the
    ///     track that downloads. Search only runs when the item has no SoundCloud identity of its own, which is
    ///     the fallback case where the track originated somewhere else.
    /// </remarks>
    public async Task<string> DownloadAsync(
        SoundCloudDownloadRequest request,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(request.OutputDir);

        var trackUrl = ResolveTrackUrl(request);
        SoundCloudTrack track;
        if (!string.IsNullOrWhiteSpace(trackUrl))
        {
            track = await _client.ResolveTrackAsync(trackUrl, cancellationToken).ConfigureAwait(false);
            request.SoundCloudTrackUrl = track.PermalinkUrl;
        }
        else
        {
            track = await SearchValidatedTrackAsync(request, cancellationToken).ConfigureAwait(false);
            request.SoundCloudId = track.Id.ToString(CultureInfo.InvariantCulture);
            request.SoundCloudTrackUrl = track.PermalinkUrl;
        }

        var requestedTier = SoundCloudStereoQuality.Normalize(request.Quality);
        var stream = await _client.ResolveStreamAsync(track, request.Quality, cancellationToken).ConfigureAwait(false);

        // The delivered tier is recorded from what SoundCloud advertised, never from what was asked for. A
        // requested hq that resolves to sq has to be visible to the delivered-quality guard.
        var deliveredTier = SoundCloudStereoQuality.Normalize(stream.AdvertisedQuality);
        if (deliveredTier != SoundCloudStereoQualityTier.Unknown
            && requestedTier != SoundCloudStereoQualityTier.Unknown
            && SoundCloudStereoQuality.RankAdvertisedQuality(stream.AdvertisedQuality)
            > SoundCloudStereoQuality.RankAdvertisedQuality(SoundCloudStereoQuality.ToAdvertisedQuality(requestedTier)))
        {
            _logger.LogWarning(
                "SoundCloud delivered {DeliveredQuality} for a {RequestedQuality} step; the delivered-quality guard will judge it.",
                stream.AdvertisedQuality,
                request.Quality);
        }

        var outputPath = await BuildOutputPathAsync(request, cancellationToken).ConfigureAwait(false);
        var acquiredStagingPath = BuildAcquiredStagingPath(outputPath);
        DownloadFileUtilities.TryDeleteFile(acquiredStagingPath);

        await DownloadPlaylistAsync(stream.PlaylistUrl, acquiredStagingPath, progressCallback, cancellationToken)
            .ConfigureAwait(false);

        return acquiredStagingPath;
    }

    /// <summary>
    ///     Resolves a validated SoundCloud track identity for metadata, without downloading.
    /// </summary>
    /// <remarks>
    ///     Used by the fallback path, which has to persist an identity on the payload before requeueing rather
    ///     than leaving the next attempt to search again.
    /// </remarks>
    public async Task<SoundCloudTrack?> ResolveTrackAsync(
        string trackTitle,
        string artistName,
        string albumName,
        string isrc,
        int expectedDuration,
        CancellationToken cancellationToken)
    {
        var request = new SoundCloudDownloadRequest
        {
            TrackName = trackTitle,
            ArtistName = artistName,
            AlbumName = albumName,
            Isrc = isrc,
            DurationSeconds = expectedDuration,
            Quality = SoundCloudStereoQuality.Standard
        };

        try
        {
            return await SearchValidatedTrackAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "SoundCloud candidate resolution found no acceptable track.");
            }

            return null;
        }
    }

    /// <summary>
    ///     Promotes a verified acquired file to the canonical destination the pipeline expects.
    /// </summary>
    public Task<string> PromoteAcceptedAudioAsync(
        SoundCloudQueueItem payload,
        string acquiredPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAcquiredStagingPath(acquiredPath))
        {
            return Task.FromResult(acquiredPath);
        }

        var canonicalPath = ResolveCanonicalPath(acquiredPath);
        if (IOFile.Exists(canonicalPath))
        {
            throw new SoundCloudExistingFinalDestinationException(
                canonicalPath,
                $"Skipped before download: final destination already contains '{canonicalPath}' and the requested quality is not higher.");
        }

        IOFile.Move(acquiredPath, canonicalPath, overwrite: false);
        payload.FilePath = DownloadPathResolver.NormalizeDisplayPath(canonicalPath);
        return Task.FromResult(canonicalPath);
    }

    /// <summary>
    ///     Deletes an acquired file that failed verification, so it cannot be mistaken for a good download.
    /// </summary>
    public void DeleteRejectedStagingAudio(SoundCloudQueueItem payload, string rejectedPath)
    {
        if (!IsAcquiredStagingPath(rejectedPath))
        {
            return;
        }

        DownloadFileUtilities.TryDeleteFile(rejectedPath);
        payload.SoundCloudAcquisitionStage = "quality_rejected";
        DownloadLifecycleCheckpoint.ClearAcquisition(payload);
    }

    /// <summary>
    ///     Finds the best candidate the shared validator accepts.
    /// </summary>
    /// <remarks>
    ///     Uses <see cref="TrackCandidateValidator"/> rather than a SoundCloud-specific threshold, so a
    ///     similarly titled track by the wrong artist, or one of the wrong length, is rejected exactly as it
    ///     would be for any other engine.
    /// </remarks>
    private async Task<SoundCloudTrack> SearchValidatedTrackAsync(
        SoundCloudDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var source = new TrackMatchSource(
            request.Isrc,
            request.TrackName,
            request.ArtistName,
            NormalizeUsableAlbum(request.AlbumName),
            request.DurationSeconds > 0 ? request.DurationSeconds * 1000 : null);
        var options = new TrackCandidateValidationOptions(
            StrictWithoutIsrc: true,
            AllowMissingCandidateArtist: true,
            RequireCandidateDurationWhenSourceHasDuration: true,
            MaxIsrcDurationDifferenceMs: 20_000,
            MaxMetadataDurationDifferenceMs: 3_000);

        var candidates = new List<SoundCloudTrack>();
        foreach (var query in BuildSearchQueries(request.ArtistName, request.TrackName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.AddRange(await _client
                .SearchTracksAsync(query, MaxCandidateResults, cancellationToken)
                .ConfigureAwait(false));
        }

        SoundCloudTrack? best = null;
        var bestScore = double.MinValue;
        foreach (var candidate in candidates.Where(static candidate => candidate.Id > 0))
        {
            // Same track reached through more than one query is scored twice and cannot bias the outcome,
            // because the identical candidate produces the identical score and the first one wins.
            var validation = TrackCandidateValidator.Validate(
                source,
                new TrackMatchCandidate(
                    candidate.Id.ToString(CultureInfo.InvariantCulture),
                    candidate.Isrc,
                    candidate.Title,
                    candidate.Artist,
                    candidate.Album,
                    candidate.DurationMs > 0 ? candidate.DurationMs : null,
                    candidate.ReleaseYear),
                options);
            if (!validation.Accepted)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Rejected SoundCloud candidate id={TrackId} reason={Reason}",
                        candidate.Id,
                        LogSanitizer.OneLine(validation.Reason));
                }

                continue;
            }

            if (validation.Score > bestScore)
            {
                bestScore = validation.Score;
                best = candidate;
            }
        }

        if (best is null)
        {
            throw new SoundCloudNoCandidateException(
                $"SoundCloud has no track matching {SoundCloudStereoQuality.FormatRequested(request.Quality)} for this request.");
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Selected SoundCloud candidate {TrackId} score={Score}.",
                best.Id,
                bestScore);
        }

        // Re-hydrated so the chosen track carries its own authorization and advertised transcodings. The search
        // response is deliberately reduced and would not be transferable as-is.
        return await _client.ResolveTrackAsync(best.PermalinkUrl, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> BuildSearchQueries(string artistName, string trackName)
    {
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(trackName))
        {
            queries.Add($"{artistName} {trackName}");
        }

        if (!string.IsNullOrWhiteSpace(trackName))
        {
            queries.Add(trackName);
        }

        if (!string.IsNullOrWhiteSpace(artistName))
        {
            queries.Add(artistName);
        }

        return queries;
    }

    private static string? NormalizeUsableAlbum(string? albumName)
    {
        if (string.IsNullOrWhiteSpace(albumName))
        {
            return null;
        }

        var normalized = albumName.Trim();
        return normalized.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase) ? null : normalized;
    }

    private static string ResolveTrackUrl(SoundCloudDownloadRequest request)
    {
        foreach (var candidate in new[] { request.SoundCloudTrackUrl, request.ServiceUrl }
                     .Where(value => !string.IsNullOrWhiteSpace(value)
                         && SoundCloudHydrationParser.IsSoundCloudTrackUrl(value)))
        {
            return candidate.Trim();
        }

        return string.Empty;
    }

    private async Task<string> BuildOutputPathAsync(SoundCloudDownloadRequest request, CancellationToken cancellationToken)
    {
        var context = new AudioFilePathHelper.AudioPathContext
        {
            OutputDir = request.OutputDir,
            Title = request.TrackName,
            Artist = request.ArtistName,
            Album = request.AlbumName,
            AlbumArtist = request.AlbumArtist,
            ReleaseDate = request.ReleaseDate,
            TrackNumber = request.SpotifyTrackNumber,
            DiscNumber = request.SpotifyDiscNumber,
            FilenameFormat = request.FilenameFormat,
            IncludeTrackNumber = request.IncludeTrackNumber,
            Position = request.Position,
            UseAlbumTrackNumber = request.UseAlbumTrackNumber,
            Sanitize = value => DownloadFileUtilities.SanitizeFilename(value)
        };

        var outputPath = AudioFilePathHelper.BuildOutputPath(context, AudioExtension);

        var decision = await DownloadDedupeService.CheckFinalDestinationAsync(
            DownloadDedupeService.FromEngineDownloadRequest(request, outputPath),
            cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new SoundCloudExistingFinalDestinationException(
                outputPath,
                decision.Message ?? "SoundCloud final destination rejected by dedupe.");
        }

        return outputPath;
    }

    /// <summary>
    ///     Fetches the media playlist, downloads its segments concurrently, decrypts, and assembles them in
    ///     playlist order into <paramref name="outputPath"/>.
    /// </summary>
    private Task DownloadPlaylistAsync(
        string playlistUrl,
        string outputPath,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
        => _hlsDownloader.DownloadAsync(playlistUrl, outputPath, progressCallback, cancellationToken);

    private static string BuildAcquiredStagingPath(string canonicalPath)
    {
        var extension = Path.GetExtension(canonicalPath);
        return Path.Join(
            Path.GetDirectoryName(canonicalPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(canonicalPath) + AcquiredStagingMarker + extension);
    }

    private static bool IsAcquiredStagingPath(string path)
        => Path.GetFileNameWithoutExtension(path).EndsWith(AcquiredStagingMarker, StringComparison.Ordinal);

    private static string ResolveCanonicalPath(string stagingPath)
    {
        var extension = Path.GetExtension(stagingPath);
        var stem = Path.GetFileNameWithoutExtension(stagingPath);
        return Path.Join(
            Path.GetDirectoryName(stagingPath) ?? string.Empty,
            stem[..^AcquiredStagingMarker.Length] + extension);
    }

}

/// <summary>
///     Raised when the canonical destination already holds a file the dedupe service will not replace.
/// </summary>
/// <remarks>
///     Separate from every other failure so the queue can adopt the existing file instead of reporting a
///     download error for something that is not one.
/// </remarks>
internal sealed class SoundCloudExistingFinalDestinationException : InvalidOperationException
{
    public SoundCloudExistingFinalDestinationException(string filePath, string message)
        : base(message)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}