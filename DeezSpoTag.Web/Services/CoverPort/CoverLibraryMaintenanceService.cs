using System.Collections.Concurrent;
using System.Threading;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Settings;
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
    private readonly record struct AlbumArtworkState(
        string ExpectedReleaseType,
        string? ExternalCoverPath,
        (int width, int height)? ExternalSize,
        (int width, int height)? EmbeddedSize,
        bool HasExternal,
        bool HasEmbedded,
        bool HasAnimatedArtwork);
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
        CoverLibraryMaintenanceRequest Request);

    private readonly CoverSearchAndDownloadService _coverSearchService;
    private readonly AppleMusicCatalogService _appleMusicCatalogService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CoverLibraryMaintenanceService> _logger;

    public CoverLibraryMaintenanceService(
        CoverSearchAndDownloadService coverSearchService,
        AppleMusicCatalogService appleMusicCatalogService,
        IHttpClientFactory httpClientFactory,
        ILogger<CoverLibraryMaintenanceService> logger)
    {
        _coverSearchService = coverSearchService;
        _appleMusicCatalogService = appleMusicCatalogService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CoverLibraryMaintenanceResult> RunAsync(
        CoverLibraryMaintenanceRequest request,
        CancellationToken cancellationToken = default)
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
        var albumDirs = CollectAlbumDirectories(rootPaths, request.IncludeSubfolders);
        var scanned = 0;
        var updated = 0;
        var skipped = 0;
        var errors = 0;
        var workerCount = Math.Clamp(request.WorkerCount, 1, 32);
        await Parallel.ForEachAsync(
            albumDirs,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = workerCount
            },
            async (albumDir, ct) =>
            {
                Interlocked.Increment(ref scanned);
                try
                {
                    var updatedDir = await ProcessAlbumDirectoryAsync(albumDir, request, logs, ct);
                    if (updatedDir)
                    {
                        Interlocked.Increment(ref updated);
                    }
                    else
                    {
                        Interlocked.Increment(ref skipped);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref errors);
                    logs.Enqueue($"[error] {albumDir}: {ex.Message}");
                    _logger.LogDebug(ex, "Cover maintenance failed for {AlbumDir}", albumDir);
                }
            });

        return new CoverLibraryMaintenanceResult(
            Success: true,
            Message: $"Cover maintenance finished: {updated} updated, {skipped} skipped, {errors} errors.",
            AlbumsScanned: scanned,
            AlbumsUpdated: updated,
            AlbumsSkipped: skipped,
            Errors: errors,
            Logs: logs.Take(500).ToArray());
    }

    private async Task<bool> ProcessAlbumDirectoryAsync(
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
            logs.Enqueue($"[skip] {albumDir}: no supported audio files.");
            return false;
        }

        if (!TryReadRequiredMetadata(audioFiles, out var metadata))
        {
            logs.Enqueue($"[skip] {albumDir}: missing artist/album tags.");
            return false;
        }
        var artworkState = InspectAlbumArtwork(albumDir, audioFiles[0], audioFiles.Count, metadata);
        var workPlan = BuildWorkPlan(request, artworkState);
        if (!workPlan.RequiresAnyWork)
        {
            return false;
        }

        var updatedAnything = false;
        if (workPlan.RequiresStillCoverUpdate)
        {
            var context = new StillCoverUpdateContext(albumDir, audioFiles, metadata, artworkState, workPlan, request);
            updatedAnything = await TryUpdateStillCoverAsync(
                context,
                logs,
                cancellationToken);
        }

        if (workPlan.NeedsAnimatedArtwork)
        {
            var animatedSaved = await TrySaveAnimatedArtworkAsync(albumDir, metadata, request, logs, cancellationToken);
            updatedAnything = animatedSaved || updatedAnything;
        }

        return updatedAnything;
    }

    private async Task<bool> TryUpdateStillCoverAsync(
        StillCoverUpdateContext context,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        var query = new CoverSearchQuery(context.Metadata.Artist, context.Metadata.Album);
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
            if (context.Request.SyncExternalCovers || !context.ArtworkState.HasExternal || context.WorkPlan.NeedsUpgrade)
            {
                await File.WriteAllBytesAsync(Path.Join(context.AlbumDir, "cover.jpg"), coverBytes, cancellationToken);
            }

            if (context.Request.ReplaceMissingEmbeddedCovers || !context.ArtworkState.HasEmbedded || context.WorkPlan.NeedsUpgrade)
            {
                foreach (var audioPath in context.AudioFiles)
                {
                    EmbedArtwork(audioPath, coverBytes);
                }
            }

            logs.Enqueue($"[ok] {context.AlbumDir}: updated cover from {downloaded.Candidate.Source} ({downloaded.Width}x{downloaded.Height})");
            return true;
        }
        finally
        {
            TryDeleteTemporaryFile(tempCoverPath);
        }
    }

    private async Task<bool> TrySaveAnimatedArtworkAsync(
        string albumDir,
        AlbumMetadata metadata,
        CoverLibraryMaintenanceRequest request,
        ConcurrentQueue<string> logs,
        CancellationToken cancellationToken)
    {
        var baseFileName = BuildAlbumArtworkBaseFileName(metadata, request.CoverImageTemplate);
        var savedAnimated = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
            _appleMusicCatalogService,
            _httpClientFactory,
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                Title = metadata.Title,
                Artist = metadata.Artist,
                Album = metadata.Album,
                BaseFileName = baseFileName,
                Storefront = request.AppleStorefront,
                MaxResolution = request.AnimatedArtworkMaxResolution,
                OutputDir = albumDir,
                Logger = _logger
            },
            cancellationToken);
        if (savedAnimated)
        {
            logs.Enqueue($"[ok] {albumDir}: saved animated artwork.");
            return true;
        }

        logs.Enqueue($"[skip] {albumDir}: animated artwork unavailable.");
        return false;
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

    private static AlbumArtworkState InspectAlbumArtwork(
        string albumDir,
        string firstAudioFile,
        int trackCount,
        AlbumMetadata metadata)
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
            HasAnimatedArtwork: HasAnimatedArtworkFiles(albumDir));
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
        var needsEmbedded = request.ReplaceMissingEmbeddedCovers && !artworkState.HasEmbedded;
        var needsExternal = request.SyncExternalCovers && !artworkState.HasExternal;
        var minResolution = Math.Max(0, request.MinResolution);
        var externalLowRes = artworkState.HasExternal && IsLowResolution(artworkState.ExternalSize!.Value, minResolution);
        var embeddedLowRes = artworkState.HasEmbedded && IsLowResolution(artworkState.EmbeddedSize!.Value, minResolution);
        var needsUpgrade = request.UpgradeLowResolutionCovers && (externalLowRes || embeddedLowRes);
        var noArtworkAtAll = !artworkState.HasExternal && !artworkState.HasEmbedded;
        var needsAnimatedArtwork = request.QueueAnimatedArtwork && !artworkState.HasAnimatedArtwork;
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
            catch (Exception ex) when (ex is not OperationCanceledException) {
                // continue to next file
            }
        }

        return (null, null, null);
    }

    private static bool HasAnimatedArtworkFiles(string albumDir)
    {
        foreach (var path in Directory.EnumerateFiles(albumDir))
        {
            var filename = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(filename))
            {
                continue;
            }

            if (filename.Equals("square_animated_artwork", StringComparison.OrdinalIgnoreCase)
                || filename.Equals("tall_animated_artwork", StringComparison.OrdinalIgnoreCase)
                || filename.EndsWith(" - square_animated_artwork", StringComparison.OrdinalIgnoreCase)
                || filename.EndsWith(" - tall_animated_artwork", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        catch (Exception ex) when (ex is not OperationCanceledException) {
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
        catch (Exception ex) when (ex is not OperationCanceledException) {
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
        catch (Exception ex) when (ex is not OperationCanceledException) {
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
    IReadOnlyCollection<CoverSourceName>? EnabledSources = null,
    string CoverImageTemplate = "cover");

public sealed record CoverLibraryMaintenanceResult(
    bool Success,
    string Message,
    int AlbumsScanned,
    int AlbumsUpdated,
    int AlbumsSkipped,
    int Errors,
    IReadOnlyList<string> Logs);
