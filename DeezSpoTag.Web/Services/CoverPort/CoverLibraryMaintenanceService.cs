using System.Collections.Concurrent;
using System.Threading;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using SixLabors.ImageSharp;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class CoverLibraryMaintenanceService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".aiff", ".wma", ".alac"
    };

    private static readonly string[] ExternalCoverNames = { "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png" };
    private static readonly string[] CompilationMarkers = { "compilation", "greatest hits", "best of", "anthology", "collection", "various artists" };
    private static readonly string[] SingleMarkers = { "single", "ep", "e.p." };
    private readonly record struct AlbumMetadata(string Artist, string Album, string? Title);
    private readonly record struct ShazamCoverHints(
        string? AppleAlbumId,
        string? AppleUrl,
        string? SpotifyUrl,
        string? DeezerUrl,
        IReadOnlyList<string> DirectArtworkUrls);
    private readonly record struct AlbumArtworkState(
        string ExpectedReleaseType,
        string? ExternalCoverPath,
        (int width, int height)? ExternalSize,
        (int width, int height)? EmbeddedSize,
        bool HasExternal,
        bool HasEmbedded,
        bool HasAnimatedArtwork,
        bool HasLegacyAnimatedArtwork);
    private readonly record struct AlbumWorkPlan(
        bool NeedsEmbedded,
        bool NeedsExternal,
        bool NeedsUpgrade,
        bool NoArtworkAtAll,
        bool NeedsAnimatedArtwork)
    {
        public bool RequiresStillCoverUpdate => NeedsEmbedded || NeedsExternal || NeedsUpgrade || NoArtworkAtAll;

        public bool RequiresAnyWork => RequiresStillCoverUpdate || NeedsAnimatedArtwork;
    }
    private readonly record struct StillCoverUpdateContext(
        string AlbumDir,
        IReadOnlyList<string> AudioFiles,
        AlbumMetadata Metadata,
        AlbumArtworkState ArtworkState,
        AlbumWorkPlan WorkPlan,
        CoverLibraryMaintenanceRequest Request,
        ShazamCoverHints? ShazamHints = null);
    private readonly record struct AnimatedArtworkUpdateResult(
        bool AnimatedSaved,
        bool MatchingStillApplied);

    private readonly CoverSearchAndDownloadService _coverSearchService;
    private readonly AppleMusicCatalogService _appleMusicCatalogService;
    private readonly ITrackIdentityResolver _trackIdentityResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly ILogger<CoverLibraryMaintenanceService> _logger;

    public CoverLibraryMaintenanceService(
        CoverSearchAndDownloadService coverSearchService,
        AppleMusicCatalogService appleMusicCatalogService,
        ITrackIdentityResolver trackIdentityResolver,
        IHttpClientFactory httpClientFactory,
        ShazamRecognitionService shazamRecognitionService,
        ILogger<CoverLibraryMaintenanceService> logger)
    {
        _coverSearchService = coverSearchService;
        _appleMusicCatalogService = appleMusicCatalogService;
        _trackIdentityResolver = trackIdentityResolver;
        _httpClientFactory = httpClientFactory;
        _shazamRecognitionService = shazamRecognitionService;
        _logger = logger;
    }

    public async Task<CoverLibraryMaintenanceResult> RunAsync(
        CoverLibraryMaintenanceRequest request,
        CancellationToken cancellationToken = default,
        Func<CoverAlbumMaintenanceOutcome, int, int, CancellationToken, ValueTask>? onAlbumCompleted = null)
    {
        var rootPaths = request.RootPaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();

        if (rootPaths.Count == 0)
        {
            return new CoverLibraryMaintenanceResult(false, "At least one root path is required.", 0, 0, 0, 0, Array.Empty<string>());
        }

        var missingRoot = rootPaths.FirstOrDefault(path => !Directory.Exists(path));
        if (!string.IsNullOrWhiteSpace(missingRoot))
        {
            return new CoverLibraryMaintenanceResult(false, $"Root path does not exist: {missingRoot}", 0, 0, 0, 0, Array.Empty<string>());
        }

        var logs = new ConcurrentQueue<string>();
        var albumDirs = request.TargetFiles is { Count: > 0 }
            ? request.TargetFiles
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(Path.GetFullPath)
                .Where(path => rootPaths.Any(root => IsSameOrDescendantPath(path, root)))
                .Select(Path.GetDirectoryName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : CollectAlbumDirectories(rootPaths, request.IncludeSubfolders);
        var outcomes = new ConcurrentQueue<CoverAlbumMaintenanceOutcome>();
        var workerCount = Math.Clamp(request.WorkerCount, 1, 32);
        var completedAlbums = 0;
        await Parallel.ForEachAsync(
            albumDirs,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = workerCount
            },
            async (albumDir, ct) =>
            {
                CoverAlbumMaintenanceOutcome outcome;
                try
                {
                    outcome = await ProcessAlbumDirectoryAsync(albumDir, request, logs, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logs.Enqueue($"[error] {albumDir}: {ex.Message}");
                    outcome = CoverAlbumMaintenanceOutcome.Error(albumDir, null, null, null, ex.Message);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(ex, "Cover maintenance failed for {AlbumDir}", albumDir);
                    }
                }

                outcomes.Enqueue(outcome);
                var completed = Interlocked.Increment(ref completedAlbums);
                if (onAlbumCompleted != null)
                {
                    await onAlbumCompleted(outcome, completed, albumDirs.Count, ct);
                }
            });

        var albumResults = outcomes
            .OrderBy(result => result.AlbumDirectory, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var updated = albumResults.Count(static result => result.Updated);
        var skipped = albumResults.Count(static result => result.Status.Equals("skipped", StringComparison.OrdinalIgnoreCase));
        var errors = albumResults.Count(static result => result.Status.Equals("error", StringComparison.OrdinalIgnoreCase));
        return new CoverLibraryMaintenanceResult(
            Success: true,
            Message: $"Cover maintenance finished: {updated} updated, {skipped} skipped, {errors} errors.",
            AlbumsScanned: albumResults.Length,
            AlbumsUpdated: updated,
            AlbumsSkipped: skipped,
            Errors: errors,
            Logs: logs.Take(500).ToArray(),
            AlbumResults: albumResults);
    }

    private async Task<CoverAlbumMaintenanceOutcome> ProcessAlbumDirectoryAsync(
        string albumDir,
        CoverLibraryMaintenanceRequest request,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        var audioFiles = Directory
            .EnumerateFiles(albumDir)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
            .ToList();
        if (audioFiles.Count == 0)
        {
            const string message = "no supported audio files.";
            logs.Enqueue($"[skip] {albumDir}: {message}");
            return CoverAlbumMaintenanceOutcome.Skipped(albumDir, null, null, null, message, CoverPath: ResolveExternalCoverPath(albumDir));
        }

        ShazamCoverHints? shazamHints = null;
        if (!TryReadRequiredMetadata(audioFiles, out var metadata))
        {
            if (!request.UseShazamForUntaggedFiles)
            {
                const string message = "missing artist/album tags.";
                logs.Enqueue($"[skip] {albumDir}: {message}");
                return CoverAlbumMaintenanceOutcome.Skipped(
                    albumDir,
                    audioFiles[0],
                    null,
                    null,
                    message,
                    CoverPath: ResolveExternalCoverPath(albumDir),
                    AudioFilePaths: audioFiles);
            }

            var recognized = await TryRecognizeUntaggedAlbumAsync(audioFiles[0], logs, cancellationToken);
            if (recognized == null)
            {
                const string message = "missing artist/album tags; Shazam did not identify the file.";
                logs.Enqueue($"[skip] {albumDir}: {message}");
                return CoverAlbumMaintenanceOutcome.Skipped(
                    albumDir,
                    audioFiles[0],
                    null,
                    null,
                    message,
                    CoverPath: ResolveExternalCoverPath(albumDir),
                    AudioFilePaths: audioFiles);
            }

            metadata = recognized.Value.Metadata;
            shazamHints = recognized.Value.Hints;
            logs.Enqueue($"[shazam] {albumDir}: identified {metadata.Artist} - {metadata.Album}.");
        }

        var artworkState = InspectAlbumArtwork(albumDir, audioFiles[0], audioFiles.Count, metadata, request);
        var workPlan = BuildWorkPlan(request, artworkState);
        if (!workPlan.RequiresAnyWork)
        {
            return CoverAlbumMaintenanceOutcome.Skipped(
                albumDir,
                audioFiles[0],
                metadata.Artist,
                metadata.Album,
                "album artwork already satisfies the selected maintenance options.",
                HasAnimatedArtwork: artworkState.HasAnimatedArtwork,
                CoverPath: artworkState.ExternalCoverPath,
                AudioFilePaths: audioFiles);
        }

        var updatedAnything = false;
        var animatedResult = default(AnimatedArtworkUpdateResult);
        var stillUpdated = false;
        if (workPlan.NeedsAnimatedArtwork)
        {
            animatedResult = await TrySaveAnimatedArtworkAsync(albumDir, metadata, request, logs, cancellationToken);
            updatedAnything = animatedResult.AnimatedSaved || animatedResult.MatchingStillApplied || updatedAnything;
            stillUpdated = animatedResult.MatchingStillApplied;
        }

        if (workPlan.RequiresStillCoverUpdate && !animatedResult.AnimatedSaved)
        {
            var context = new StillCoverUpdateContext(albumDir, audioFiles, metadata, artworkState, workPlan, request, shazamHints);
            stillUpdated = await TryUpdateStillCoverAsync(
                context,
                logs,
                cancellationToken);
            updatedAnything = stillUpdated || updatedAnything;
        }

        return new CoverAlbumMaintenanceOutcome(
            AlbumDirectory: albumDir,
            RepresentativeFilePath: audioFiles[0],
            Artist: metadata.Artist,
            Album: metadata.Album,
            Status: updatedAnything ? "ok" : "skipped",
            Message: updatedAnything ? "cover maintenance completed." : "no cover maintenance update was applied.",
            StillCoverUpdated: stillUpdated,
            AnimatedArtworkSaved: animatedResult.AnimatedSaved,
            HasAnimatedArtwork: animatedResult.AnimatedSaved || artworkState.HasAnimatedArtwork,
            Updated: updatedAnything,
            CoverPath: ResolveExternalCoverPath(albumDir) ?? artworkState.ExternalCoverPath,
            AudioFilePaths: audioFiles);
    }

    private static bool IsSameOrDescendantPath(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryUpdateStillCoverAsync(
        StillCoverUpdateContext context,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        var query = BuildCoverSearchQuery(context.Metadata, context.ShazamHints);
        var tempCoverPath = Path.Join(context.AlbumDir, $".deezspotag-cover-{Guid.NewGuid():N}.jpg");
        var referenceBytes = await ReadReferenceImageBytesAsync(context.ArtworkState.ExternalCoverPath, context.AudioFiles[0], cancellationToken);
        var searchOptions = BuildSearchOptions(context.Request, context.ArtworkState.ExternalCoverPath, referenceBytes);
        var downloaded = await _coverSearchService.SearchAndDownloadAsync(query, tempCoverPath, searchOptions, cancellationToken);
        try
        {
            if (downloaded == null || !File.Exists(downloaded.OutputPath))
            {
                logs.Enqueue($"[miss] {context.AlbumDir}: no usable cover found for {context.Metadata.Artist} - {context.Metadata.Album}");
                return false;
            }

            var candidateReleaseType = ResolveCandidateReleaseType(downloaded.Candidate);
            if (!IsReleaseTypeCompatible(context.ArtworkState.ExpectedReleaseType, candidateReleaseType))
            {
                logs.Enqueue($"[skip] {context.AlbumDir}: release-type mismatch expected={context.ArtworkState.ExpectedReleaseType} candidate={candidateReleaseType}.");
                return false;
            }

            var hasReferenceImage = context.ArtworkState.HasExternal || context.ArtworkState.HasEmbedded;
            if (!hasReferenceImage && !HasStrongNoReferenceMatch(context.Metadata.Artist, context.Metadata.Album, downloaded.Candidate))
            {
                logs.Enqueue($"[skip] {context.AlbumDir}: rejected low-confidence no-reference candidate from {downloaded.Candidate.Source}.");
                return false;
            }

            var coverBytes = await File.ReadAllBytesAsync(downloaded.OutputPath, cancellationToken);
            var wroteAnything = false;
            if (context.Request.WriteExternalSidecar)
            {
                await File.WriteAllBytesAsync(ResolveStillCoverOutputPath(context), coverBytes, cancellationToken);
                wroteAnything = true;
            }

            if (context.Request.WriteEmbeddedCover)
            {
                foreach (var audioPath in context.AudioFiles)
                {
                    EmbedArtwork(audioPath, coverBytes);
                }

                wroteAnything = true;
            }

            if (!wroteAnything)
            {
                logs.Enqueue($"[skip] {context.AlbumDir}: profile does not allow embedding or saving artwork files.");
                return false;
            }

            logs.Enqueue($"[ok] {context.AlbumDir}: updated cover from {downloaded.Candidate.Source} ({downloaded.Width}x{downloaded.Height})");
            return true;
        }
        finally
        {
            TryDeleteTemporaryFile(tempCoverPath);
        }
    }

    private async Task<AnimatedArtworkUpdateResult> TrySaveAnimatedArtworkAsync(
        string albumDir,
        AlbumMetadata metadata,
        CoverLibraryMaintenanceRequest request,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        var saveRequest = new AppleQueueHelpers.AnimatedArtworkSaveRequest
        {
            Artist = metadata.Artist,
            Album = metadata.Album,
            SquareFileName = request.AnimatedArtworkSquareFileName,
            TallFileName = request.AnimatedArtworkTallFileName,
            Storefront = request.AppleStorefront,
            MaxResolution = request.AnimatedArtworkMaxResolution,
            OutputDir = albumDir,
            Logger = _logger,
            OutputFormats = request.AnimatedArtworkFormats,
            RenameExistingArtwork = request.RenameExistingAnimatedArtwork,
            OverwriteExisting = request.OverwriteExistingAnimatedArtwork,
            RemoveOldArtwork = request.RemoveOldAnimatedArtwork,
            MaxSizeMb = request.AnimatedArtworkMaxSizeMb
        };
        var existingPaths = await AppleQueueHelpers.SaveExistingAnimatedArtworkVariantsAsync(
            saveRequest,
            _logger,
            cancellationToken);
        if (!request.QueueAnimatedArtwork)
        {
            return new AnimatedArtworkUpdateResult(existingPaths.Count > 0, false);
        }

        var identity = await ResolveAppleIdentityAsync(metadata, request, cancellationToken);
        if (identity is null || string.IsNullOrWhiteSpace(identity.AppleId))
        {
            logs.Enqueue($"[skip] {albumDir}: animated artwork unavailable.");
            return default;
        }
        var resolvedIdentity = identity;

        var animatedResult = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
            _appleMusicCatalogService,
            _httpClientFactory,
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                AppleId = resolvedIdentity.AppleId,
                Artist = resolvedIdentity.AppleArtistName ?? metadata.Artist,
                Album = resolvedIdentity.AppleAlbumName ?? metadata.Album,
                SquareFileName = request.AnimatedArtworkSquareFileName,
                TallFileName = request.AnimatedArtworkTallFileName,
                Storefront = request.AppleStorefront,
                MaxResolution = request.AnimatedArtworkMaxResolution,
                OutputDir = albumDir,
                Logger = _logger,
                CollectionType = string.IsNullOrWhiteSpace(resolvedIdentity.AppleAlbumId) ? null : "album",
                CollectionId = resolvedIdentity.AppleAlbumId,
                OutputFormats = request.AnimatedArtworkFormats,
                RenameExistingArtwork = request.RenameExistingAnimatedArtwork,
                OverwriteExisting = request.OverwriteExistingAnimatedArtwork,
                RemoveOldArtwork = request.RemoveOldAnimatedArtwork,
                MaxSizeMb = request.AnimatedArtworkMaxSizeMb
            },
            cancellationToken);
        if (animatedResult.Paths.Count > 0)
        {
            var matchingStillApplied = await TryApplyMatchingAppleStillArtworkAsync(
                albumDir,
                metadata,
                resolvedIdentity,
                request,
                logs,
                cancellationToken);
            logs.Enqueue($"[ok] {albumDir}: saved animated artwork.");
            return new AnimatedArtworkUpdateResult(true, matchingStillApplied);
        }

        logs.Enqueue($"[skip] {albumDir}: {animatedResult.Message}");
        return default;
    }

    private async Task<bool> TryApplyMatchingAppleStillArtworkAsync(
        string albumDir,
        AlbumMetadata metadata,
        TrackIdentityResolution identity,
        CoverLibraryMaintenanceRequest request,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        var targetSize = Math.Max(request.TargetResolution, request.MinResolution);
        var artworkUrl = !string.IsNullOrWhiteSpace(identity.AppleAlbumId)
            ? await AppleQueueHelpers.ResolveAppleCollectionCoverFromCatalogAsync(
                _appleMusicCatalogService,
                "album",
                identity.AppleAlbumId,
                request.AppleStorefront,
                targetSize,
                _logger,
                cancellationToken)
            : await AppleQueueHelpers.ResolveAppleCoverFromCatalogAsync(
                _appleMusicCatalogService,
                new AppleQueueHelpers.AppleCatalogCoverLookup
                {
                    AppleId = identity.AppleId,
                    Title = metadata.Title,
                    Artist = identity.AppleArtistName ?? metadata.Artist,
                    Album = identity.AppleAlbumName ?? metadata.Album,
                    Storefront = request.AppleStorefront,
                    Size = targetSize,
                    Logger = _logger
                },
                cancellationToken);
        if (string.IsNullOrWhiteSpace(artworkUrl))
        {
            logs.Enqueue($"[warn] {albumDir}: matching Apple still artwork was unavailable.");
            return false;
        }

        var client = _httpClientFactory.CreateClient();
        var bytes = await client.GetByteArrayAsync(artworkUrl, cancellationToken);
        if (bytes.Length == 0)
        {
            logs.Enqueue($"[warn] {albumDir}: matching Apple still artwork was empty.");
            return false;
        }

        var baseName = BuildAlbumArtworkBaseFileName(metadata, request.CoverImageTemplate);
        var destinationPath = Path.Join(albumDir, $"{baseName}.{ResolveSidecarExtension(request)}");
        var temporaryPath = Path.Join(albumDir, $".{baseName}.{Guid.NewGuid():N}.tmp.{ResolveSidecarExtension(request)}");
        var wroteAnything = false;
        try
        {
            if (request.WriteExternalSidecar)
            {
                await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
                File.Move(temporaryPath, destinationPath, overwrite: true);
                wroteAnything = true;
            }

            if (request.WriteEmbeddedCover)
            {
                foreach (var audioPath in Directory.EnumerateFiles(albumDir)
                             .Where(path => AudioExtensions.Contains(Path.GetExtension(path))))
                {
                    EmbedArtwork(audioPath, bytes);
                }
                wroteAnything = true;
            }

            if (!wroteAnything)
            {
                logs.Enqueue($"[skip] {albumDir}: matching Apple still artwork was resolved, but profile does not allow embedding or saving artwork files.");
            }

            return wroteAnything;
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task<TrackIdentityResolution?> ResolveAppleIdentityAsync(
        AlbumMetadata metadata,
        CoverLibraryMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = await _trackIdentityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: null,
                    SourceUrl: null,
                    Title: metadata.Title,
                    Artist: metadata.Artist,
                    Album: metadata.Album,
                    Isrc: null,
                    DurationMs: null,
                    TargetPlatforms: new[] { "apple" },
                    Storefront: request.AppleStorefront,
                    Language: "en-US"),
                cancellationToken);
            return string.IsNullOrWhiteSpace(identity.AppleId) ? null : identity;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central Apple identity lookup failed for cover maintenance animated artwork.");
            return null;
        }
    }

    private static string BuildAlbumArtworkBaseFileName(AlbumMetadata metadata, string? coverImageTemplate)
    {
        var settings = DeezSpoTagSettingsService.GetStaticDefaultSettings();
        if (!string.IsNullOrWhiteSpace(coverImageTemplate))
        {
            settings.CoverImageTemplate = coverImageTemplate.Trim();
        }

        var artist = string.IsNullOrWhiteSpace(metadata.Artist) ? "Unknown Artist" : metadata.Artist.Trim();
        var album = string.IsNullOrWhiteSpace(metadata.Album) ? "Unknown Album" : metadata.Album.Trim();
        var albumModel = new Album(album)
        {
            MainArtist = new Artist(artist),
            Artists = new List<string> { artist }
        };

        return PathTemplateGenerator.GenerateAlbumName(
            settings.CoverImageTemplate,
            albumModel,
            settings,
            playlist: null);
    }

    private static string ResolveStillCoverOutputPath(StillCoverUpdateContext context)
    {
        var baseFileName = BuildAlbumArtworkBaseFileName(context.Metadata, context.Request.CoverImageTemplate);
        if (string.IsNullOrWhiteSpace(baseFileName))
        {
            baseFileName = "cover";
        }

        return Path.Join(context.AlbumDir, $"{baseFileName}.{ResolveSidecarExtension(context.Request)}");
    }

    private static string ResolveSidecarExtension(CoverLibraryMaintenanceRequest request)
    {
        var format = (request.LocalArtworkFormat ?? "jpg").Trim().TrimStart('.').ToLowerInvariant();
        return format == "png" ? "png" : "jpg";
    }

    private static CoverSearchQuery BuildCoverSearchQuery(AlbumMetadata metadata, ShazamCoverHints? hints)
    {
        return new CoverSearchQuery(
            Artist: metadata.Artist,
            Album: metadata.Album,
            AppleAlbumId: hints?.AppleAlbumId,
            AppleUrl: hints?.AppleUrl,
            SpotifyUrl: hints?.SpotifyUrl,
            DeezerUrl: hints?.DeezerUrl,
            DirectArtworkUrls: hints?.DirectArtworkUrls);
    }

    private async Task<(AlbumMetadata Metadata, ShazamCoverHints Hints)?> TryRecognizeUntaggedAlbumAsync(
        string audioFile,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        if (!_shazamRecognitionService.IsAvailable)
        {
            logs.Enqueue($"[skip] {audioFile}: Shazam recognizer is unavailable.");
            return null;
        }

        try
        {
            var recognition = await _shazamRecognitionService.RecognizeAsync(audioFile, cancellationToken);
            if (recognition?.HasCoreMetadata != true)
            {
                return null;
            }

            var artist = recognition.Artists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                         ?? recognition.Artist
                         ?? string.Empty;
            var album = string.IsNullOrWhiteSpace(recognition.Album)
                ? recognition.Title
                : recognition.Album;
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            {
                return null;
            }

            var artworkUrls = new[] { recognition.ArtworkHqUrl, recognition.ArtworkUrl }
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var deezerUrl = recognition.Tags.TryGetValue("SHAZAM_DEEZER_URL", out var deezerTags)
                ? deezerTags.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                : null;
            return (
                new AlbumMetadata(artist.Trim(), album.Trim(), recognition.Title),
                new ShazamCoverHints(
                    AppleAlbumId: recognition.AlbumAdamId,
                    AppleUrl: recognition.AppleMusicUrl,
                    SpotifyUrl: recognition.SpotifyUrl,
                    DeezerUrl: deezerUrl,
                    DirectArtworkUrls: artworkUrls));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logs.Enqueue($"[skip] {audioFile}: Shazam fingerprint failed ({ex.Message}).");
            _logger.LogDebug(ex, "Cover maintenance Shazam fallback failed for {Path}", audioFile);
            return null;
        }
    }

    private static AlbumArtworkState InspectAlbumArtwork(
        string albumDir,
        string firstAudioFile,
        int trackCount,
        AlbumMetadata metadata,
        CoverLibraryMaintenanceRequest request)
    {
        var externalCoverPath = ResolveExternalCoverPath(albumDir);
        var externalSize = externalCoverPath != null ? TryReadImageSize(externalCoverPath) : null;
        var embeddedSize = TryReadEmbeddedCoverSize(firstAudioFile);
        return new AlbumArtworkState(
            ExpectedReleaseType: ResolveReleaseType(metadata.Album, metadata.Artist, trackCount),
            ExternalCoverPath: externalCoverPath,
            ExternalSize: externalSize,
            EmbeddedSize: embeddedSize,
            HasExternal: externalSize.HasValue,
            HasEmbedded: embeddedSize.HasValue,
            HasAnimatedArtwork: HasAnimatedArtworkFiles(albumDir, request),
            HasLegacyAnimatedArtwork: HasNonCurrentAnimatedArtworkFiles(albumDir, request));
    }

    private static bool TryReadRequiredMetadata(IReadOnlyList<string> audioFiles, out AlbumMetadata metadata)
    {
        var readMetadata = TryReadAlbumMetadata(audioFiles);
        if (string.IsNullOrWhiteSpace(readMetadata.artist) || string.IsNullOrWhiteSpace(readMetadata.album))
        {
            metadata = default;
            return false;
        }

        metadata = new AlbumMetadata(readMetadata.artist, readMetadata.album, readMetadata.title);
        return true;
    }

    private static AlbumWorkPlan BuildWorkPlan(CoverLibraryMaintenanceRequest request, AlbumArtworkState artworkState)
    {
        var canEmbed = request.WriteEmbeddedCover;
        var canWriteSidecar = request.WriteExternalSidecar;
        var needsEmbedded = request.ReplaceMissingEmbeddedCovers && canEmbed && !artworkState.HasEmbedded;
        var needsExternal = request.SyncExternalCovers && canWriteSidecar && !artworkState.HasExternal;
        var minResolution = Math.Max(0, request.MinResolution);
        var externalLowRes = canWriteSidecar
            && artworkState.HasExternal
            && IsLowResolution(artworkState.ExternalSize!.Value, minResolution);
        var embeddedLowRes = canEmbed
            && artworkState.HasEmbedded
            && IsLowResolution(artworkState.EmbeddedSize!.Value, minResolution);
        var needsUpgrade = request.UpgradeLowResolutionCovers && (externalLowRes || embeddedLowRes);
        var stillCoverActionEnabled = (request.ReplaceMissingEmbeddedCovers && canEmbed)
            || (request.SyncExternalCovers && canWriteSidecar)
            || (request.UpgradeLowResolutionCovers && (canEmbed || canWriteSidecar));
        var noArtworkAtAll = stillCoverActionEnabled
            && !artworkState.HasExternal
            && !artworkState.HasEmbedded;
        var needsAnimatedArtwork = request.QueueAnimatedArtwork
            || request.OverwriteExistingAnimatedArtwork
            || request.RemoveOldAnimatedArtwork
            || (request.RenameExistingAnimatedArtwork && artworkState.HasLegacyAnimatedArtwork);
        return new AlbumWorkPlan(needsEmbedded, needsExternal, needsUpgrade, noArtworkAtAll, needsAnimatedArtwork);
    }

    private static CoverSearchOptions BuildSearchOptions(
        CoverLibraryMaintenanceRequest request,
        string? externalCoverPath,
        byte[]? referenceBytes)
    {
        return CoverSacadOptionMapper.Map(
            new SacadSearchOptionInput(
                Size: Math.Max(300, request.TargetResolution),
                SizeTolerancePercent: request.SizeTolerancePercent,
                PreserveFormat: request.PreserveSourceFormat,
                CoverSources: request.EnabledSources?.Select(source => source.ToString().ToLowerInvariant()).ToArray()),
            referenceImagePath: externalCoverPath,
            referenceImageBytes: referenceBytes,
            maxCandidatesToTry: 20);
    }

    private static async Task<byte[]?> ReadReferenceImageBytesAsync(
        string? externalCoverPath,
        string primaryAudioFile,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(externalCoverPath) && File.Exists(externalCoverPath))
        {
            return await File.ReadAllBytesAsync(externalCoverPath, cancellationToken);
        }

        return TryReadEmbeddedCoverBytes(primaryAudioFile);
    }

    private static void TryDeleteTemporaryFile(string tempCoverPath)
    {
        try
        {
            if (File.Exists(tempCoverPath))
            {
                File.Delete(tempCoverPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ignore best-effort cleanup failures
        }
    }

    private static List<string> CollectAlbumDirectories(IReadOnlyList<string> rootPaths, bool includeSubfolders)
    {
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var map = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootPath in rootPaths)
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", option))
            {
                if (!AudioExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    map.Add(dir);
                }
            }
        }

        return map.ToList();
    }

    private static (string? artist, string? album, string? title) TryReadAlbumMetadata(IReadOnlyList<string> audioFiles)
    {
        foreach (var audioFile in audioFiles)
        {
            try
            {
                using var tagFile = TagLib.File.Create(audioFile);
                var artist = tagFile.Tag.AlbumArtists?.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(artist))
                {
                    artist = tagFile.Tag.Performers?.FirstOrDefault();
                }

                var album = tagFile.Tag.Album;
                var title = tagFile.Tag.Title;
                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album))
                {
                    return (artist, album, title);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // continue to next file
            }
        }

        return (null, null, null);
    }

    private static bool HasAnimatedArtworkFiles(string albumDir, CoverLibraryMaintenanceRequest request)
    {
        return Directory.EnumerateFiles(albumDir)
            .Any(path =>
            {
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var filename = Path.GetFileNameWithoutExtension(path);
                return AnimatedArtworkNaming.IsRecognizedAnimatedStem(
                    filename,
                    request.AnimatedArtworkSquareFileName,
                    request.AnimatedArtworkTallFileName);
            });
    }

    private static bool HasNonCurrentAnimatedArtworkFiles(string albumDir, CoverLibraryMaintenanceRequest request)
    {
        return Directory.EnumerateFiles(albumDir)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(filename => !string.IsNullOrWhiteSpace(filename))
            .Any(filename =>
                AnimatedArtworkNaming.IsRecognizedAnimatedStem(
                    filename,
                    request.AnimatedArtworkSquareFileName,
                    request.AnimatedArtworkTallFileName)
                && !AnimatedArtworkNaming.IsCurrentStem(
                    filename,
                    request.AnimatedArtworkSquareFileName,
                    request.AnimatedArtworkTallFileName));
    }

    private static string? ResolveExternalCoverPath(string albumDir)
    {
        return ExternalCoverNames
            .Select(filename => Path.Join(albumDir, filename))
            .FirstOrDefault(File.Exists);
    }

    private static (int width, int height)? TryReadImageSize(string filePath)
    {
        try
        {
            var info = Image.Identify(filePath);
            return info == null ? null : (info.Width, info.Height);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static (int width, int height)? TryReadEmbeddedCoverSize(string audioFilePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(audioFilePath);
            var picture = tagFile.Tag.Pictures?.FirstOrDefault(pic => pic?.Data != null && pic.Data.Count > 0);
            if (picture?.Data == null || picture.Data.Count == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(picture.Data.Data);
            var info = Image.Identify(stream);
            return info == null ? null : (info.Width, info.Height);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static byte[]? TryReadEmbeddedCoverBytes(string audioFilePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(audioFilePath);
            var picture = tagFile.Tag.Pictures?.FirstOrDefault(pic => pic?.Data != null && pic.Data.Count > 0);
            return picture?.Data?.Data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsLowResolution((int width, int height) size, int minResolution)
    {
        if (minResolution <= 0)
        {
            return false;
        }

        return Math.Min(size.width, size.height) < minResolution;
    }

    private static void EmbedArtwork(string audioPath, byte[] artworkData)
    {
        using var file = TagLib.File.Create(audioPath);
        var picture = new TagLib.Picture
        {
            Data = artworkData,
            Type = TagLib.PictureType.FrontCover,
            MimeType = "image/jpeg",
            Description = "Cover"
        };
        file.Tag.Pictures = new TagLib.IPicture[] { picture };
        file.Save();
    }

    private static string ResolveReleaseType(string albumTitle, string artistName, int trackCount)
    {
        var normalizedAlbum = NormalizeToken(albumTitle);
        var normalizedArtist = NormalizeToken(artistName);
        if (ContainsAnyMarker(normalizedAlbum, CompilationMarkers) || normalizedArtist.Contains("various artists", StringComparison.Ordinal))
        {
            return "compilation";
        }

        if (trackCount <= 1 || ContainsAnyMarker(normalizedAlbum, SingleMarkers))
        {
            return "single";
        }

        return "album";
    }

    private static string ResolveCandidateReleaseType(CoverCandidate candidate)
    {
        var album = NormalizeToken(candidate.Album);
        var artist = NormalizeToken(candidate.Artist);
        if (ContainsAnyMarker(album, CompilationMarkers) || artist.Contains("various artists", StringComparison.Ordinal))
        {
            return "compilation";
        }
        if (ContainsAnyMarker(album, SingleMarkers))
        {
            return "single";
        }
        return "album";
    }

    private static bool IsReleaseTypeCompatible(string expected, string candidate)
    {
        return string.Equals(expected, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStrongNoReferenceMatch(string expectedArtist, string expectedAlbum, CoverCandidate candidate)
    {
        var albumOverlap = ComputeTokenOverlap(expectedAlbum, candidate.Album);
        var artistOverlap = ComputeTokenOverlap(expectedArtist, candidate.Artist);
        var confidence = (Math.Max(0d, candidate.SourceReliability) + Math.Max(0d, candidate.MatchConfidence)) / 2d;
        return albumOverlap >= 0.6d && artistOverlap >= 0.6d && confidence >= 0.45d;
    }

    private static double ComputeTokenOverlap(string? expected, string? candidate)
    {
        var expectedTokens = Tokenize(expected);
        var candidateTokens = Tokenize(candidate);
        if (expectedTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0d;
        }

        var overlap = expectedTokens.Intersect(candidateTokens, StringComparer.Ordinal).Count();
        var denominator = Math.Min(expectedTokens.Count, candidateTokens.Count);
        return denominator <= 0 ? 0d : overlap / (double)denominator;
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var normalized = NormalizeToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool ContainsAnyMarker(string text, IEnumerable<string> markers)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var padded = $" {text} ";
        return markers.Any(marker => padded.Contains($" {marker} ", StringComparison.Ordinal));
    }
}

public sealed record CoverLibraryMaintenanceRequest(
    IReadOnlyList<string> RootPaths,
    bool IncludeSubfolders = true,
    int WorkerCount = 8,
    bool UpgradeLowResolutionCovers = true,
    int MinResolution = 500,
    int TargetResolution = 1200,
    int SizeTolerancePercent = 25,
    bool PreserveSourceFormat = false,
    bool ReplaceMissingEmbeddedCovers = true,
    bool SyncExternalCovers = true,
    bool QueueAnimatedArtwork = false,
    string AppleStorefront = "us",
    int AnimatedArtworkMaxResolution = 2160,
    int AnimatedArtworkMaxSizeMb = 10,
    IReadOnlyCollection<string>? AnimatedArtworkFormats = null,
    IReadOnlyCollection<CoverSourceName>? EnabledSources = null,
    string CoverImageTemplate = "cover",
    string AnimatedArtworkSquareFileName = "cover",
    string AnimatedArtworkTallFileName = "cover_tall",
    bool RenameExistingAnimatedArtwork = true,
    bool OverwriteExistingAnimatedArtwork = false,
    bool RemoveOldAnimatedArtwork = false,
    IReadOnlyList<string>? TargetFiles = null,
    bool WriteEmbeddedCover = true,
    bool WriteExternalSidecar = true,
    string LocalArtworkFormat = "jpg",
    bool UseShazamForUntaggedFiles = false);

public sealed record CoverLibraryMaintenanceResult(
    bool Success,
    string Message,
    int AlbumsScanned,
    int AlbumsUpdated,
    int AlbumsSkipped,
    int Errors,
    IReadOnlyList<string> Logs,
    IReadOnlyList<CoverAlbumMaintenanceOutcome>? AlbumResults = null);

public sealed record CoverAlbumMaintenanceOutcome(
    string AlbumDirectory,
    string? RepresentativeFilePath,
    string? Artist,
    string? Album,
    string Status,
    string Message,
    bool StillCoverUpdated = false,
    bool AnimatedArtworkSaved = false,
    bool HasAnimatedArtwork = false,
    bool Updated = false,
    string? CoverPath = null,
    IReadOnlyList<string>? AudioFilePaths = null)
{
    public static CoverAlbumMaintenanceOutcome Skipped(
        string albumDirectory,
        string? representativeFilePath,
        string? artist,
        string? album,
        string message,
        bool HasAnimatedArtwork = false,
        string? CoverPath = null,
        IReadOnlyList<string>? AudioFilePaths = null)
        => new(albumDirectory, representativeFilePath, artist, album, "skipped", message, HasAnimatedArtwork: HasAnimatedArtwork, CoverPath: CoverPath, AudioFilePaths: AudioFilePaths);

    public static CoverAlbumMaintenanceOutcome Error(
        string albumDirectory,
        string? representativeFilePath,
        string? artist,
        string? album,
        string message)
        => new(albumDirectory, representativeFilePath, artist, album, "error", message);
}
