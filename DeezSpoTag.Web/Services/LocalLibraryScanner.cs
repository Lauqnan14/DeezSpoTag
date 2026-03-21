using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using TagLib;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

public sealed class LocalLibraryScanner
{
    private const string SyncedStatus = "synced";
    private const string UnsyncedStatus = "unsynced";
    private const string FlacExtension = ".flac";
    private const string AiffExtension = ".aiff";
    private const string AlacExtension = ".alac";
    public sealed record ScanProgress(
        int ProcessedFiles,
        int TotalFiles,
        int ErrorCount,
        string? CurrentFile,
        int ArtistsDetected,
        int AlbumsDetected,
        int TracksDetected);

    private sealed class ScanContext
    {
        public required LibraryConfigStore.LocalLibrarySnapshot Snapshot { get; init; }
        public required Dictionary<string, LibraryConfigStore.LibraryArtist> ArtistIndex { get; init; }
        public required Dictionary<string, LibraryConfigStore.LibraryAlbum> AlbumIndex { get; init; }
        public required Dictionary<string, HashSet<string>> ArtistGenres { get; init; }
        public required bool UsePrimaryArtistFolders { get; init; }
        public required bool EnableSignalAnalysis { get; init; }
        public required IProgress<ScanProgress>? Progress { get; init; }
        public required Action<LibraryConfigStore.LocalLibrarySnapshot>? SnapshotPublished { get; init; }
        public int ProcessedFiles { get; set; }
        public int TotalFiles { get; set; }
        public int ErrorCount { get; set; }
        public int ReportEvery { get; init; } = 1;
    }

    private sealed record FolderScanBaseline(int ArtistCount, int AlbumCount, int TrackCount);

    private sealed class TrackScanData
    {
        public required string FilePath { get; init; }
        public required string TrackTitle { get; set; }
        public string? TagTitle { get; set; }
        public string? TagArtist { get; set; }
        public string? TagAlbum { get; set; }
        public string? TagAlbumArtist { get; set; }
        public string? TagVersion { get; set; }
        public string? TagLabel { get; set; }
        public string? TagCatalogNumber { get; set; }
        public int? TagBpm { get; set; }
        public string? TagKey { get; set; }
        public int? TagTrackTotal { get; set; }
        public int? TagDurationMs { get; set; }
        public int? TagYear { get; set; }
        public int? TagTrackNo { get; set; }
        public int? TagDisc { get; set; }
        public string? TagGenre { get; set; }
        public string? TagIsrc { get; set; }
        public string? TagReleaseDate { get; set; }
        public string? TagPublishDate { get; set; }
        public string? TagUrl { get; set; }
        public string? TagReleaseId { get; set; }
        public string? TagTrackId { get; set; }
        public string? TagMetaTaggedDate { get; set; }
        public string? LyricsUnsynced { get; set; }
        public string? LyricsSynced { get; set; }
        public List<string> TagGenres { get; set; } = new();
        public List<string> TagStyles { get; set; } = new();
        public List<string> TagMoods { get; set; } = new();
        public List<string> TagRemixers { get; set; } = new();
        public List<LocalTrackOtherTag> TagOtherTags { get; set; } = new();
        public int? TrackNo { get; set; }
        public int? Disc { get; set; }
        public int? DurationMs { get; set; }
        public string? LyricsStatus { get; set; }
        public string? LyricsType { get; set; }
        public string? Codec { get; set; }
        public int? BitrateKbps { get; set; }
        public int? SampleRateHz { get; set; }
        public int? BitsPerSample { get; set; }
        public int? Channels { get; set; }
        public int? QualityRank { get; set; }
        public string? AudioVariant { get; set; }
        public string? DeezerTrackId { get; set; }
        public string? Source { get; set; }
        public string? SourceId { get; set; }
        public string? Isrc { get; set; }
        public TrackSourceIds TrackIds { get; set; } = new(null, null);
        public AlbumSourceIds AlbumIds { get; set; } = new(null, null, null);
        public ArtistSourceIds ArtistIds { get; set; } = new(null, null, null);
        public string[]? Genres { get; set; }
    }

    private readonly LibraryConfigStore _configStore;
    private readonly ILogger<LocalLibraryScanner> _logger;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly AudioQualitySignalAnalyzer _signalAnalyzer;

    private static readonly string[] AudioExtensions = new[]
    {
        ".mp3", FlacExtension, ".m4a", ".m4b", ".wav", ".ogg", AiffExtension, AlacExtension, ".aac"
    };

    private static readonly string[] ImageExtensions = new[]
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp"
    };

    private static readonly Regex SyncedLyricsRegex = new(
        @"\[\d{1,2}:\d{2}([.:]\d{1,2})?\]",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex FileNameTrackPrefixRegex = new(
        @"^\s*(?<track>\d{1,3})\s*[-._]\s*(?<title>.+?)\s*$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
    private static readonly string[] LyricsSidecarExtensions = new[] { ".lrc", ".ttml" };
    private static readonly char[] TagValueSeparators = [';', ',', '/', '|'];
    private static readonly char[] DeezerUrlTerminatorChars = ['?', '/', '#'];
    private static readonly string[] WindowsFfprobeCandidates = ["ffprobe.exe", "ffprobe"];
    private static readonly string[] UnixFfprobeCandidates = ["/usr/bin/ffprobe", "/usr/local/bin/ffprobe", "/bin/ffprobe", "ffprobe"];
    private static readonly Lazy<string?> FfprobePath = new(ResolveFfprobePath);
    private static readonly bool LibraryPrecountEnabled = ReadBooleanEnvironmentVariable("DEEZSPOTAG_LIBRARY_PRECOUNT", defaultValue: false);
    private static readonly bool LegacyAggressiveFfprobeEnabled = ReadBooleanEnvironmentVariable("DEEZSPOTAG_LIBRARY_AGGRESSIVE_FFPROBE", defaultValue: false);
    private static readonly bool DefaultSignalAnalysisDisabled = ResolveSignalAnalysisDisabled();

    public LocalLibraryScanner(
        LibraryConfigStore configStore,
        DeezSpoTagSettingsService settingsService,
        AudioQualitySignalAnalyzer signalAnalyzer,
        ILogger<LocalLibraryScanner> logger)
    {
        _configStore = configStore;
        _settingsService = settingsService;
        _signalAnalyzer = signalAnalyzer;
        _logger = logger;
    }

    public LibraryConfigStore.LocalLibrarySnapshot Scan(IEnumerable<DeezSpoTag.Services.Library.FolderDto> folders)
    {
        return Scan(folders, progress: null, snapshotPublished: null, cancellationToken: CancellationToken.None);
    }

    public LibraryConfigStore.LocalLibrarySnapshot Scan(
        IEnumerable<DeezSpoTag.Services.Library.FolderDto> folders,
        IProgress<ScanProgress>? progress,
        Action<LibraryConfigStore.LocalLibrarySnapshot>? snapshotPublished,
        CancellationToken cancellationToken)
    {
        var context = CreateScanContext(progress, snapshotPublished);
        var (excludedFolders, scannableFolders) = SplitScannableFolders(folders);
        LogExcludedFolders(excludedFolders);
        InitializeProgressState(context, scannableFolders, cancellationToken);

        foreach (var folder in scannableFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanFolder(folder, context, cancellationToken);
        }

        ReportProgress(context, currentFile: null, force: true);
        context.Snapshot.ArtistGenres = context.ArtistGenres.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        PublishSnapshot(context);
        return context.Snapshot;
    }

    private ScanContext CreateScanContext(
        IProgress<ScanProgress>? progress,
        Action<LibraryConfigStore.LocalLibrarySnapshot>? snapshotPublished)
    {
        return new ScanContext
        {
            Snapshot = new LibraryConfigStore.LocalLibrarySnapshot(),
            ArtistIndex = new Dictionary<string, LibraryConfigStore.LibraryArtist>(StringComparer.OrdinalIgnoreCase),
            AlbumIndex = new Dictionary<string, LibraryConfigStore.LibraryAlbum>(StringComparer.OrdinalIgnoreCase),
            ArtistGenres = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
            UsePrimaryArtistFolders = ResolveUsePrimaryArtistFolders(),
            EnableSignalAnalysis = ResolveEnableSignalAnalysis(),
            Progress = progress,
            SnapshotPublished = snapshotPublished
        };
    }

    private bool ResolveEnableSignalAnalysis()
    {
        try
        {
            var settings = _configStore.GetSettings();
            return settings.EnableSignalAnalysis;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load library settings for signal analysis. Falling back to default scanner behavior.");
            return !DefaultSignalAnalysisDisabled;
        }
    }

    private bool ResolveUsePrimaryArtistFolders()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            return settings.Tags?.SingleAlbumArtist != false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load settings for artist folder handling. Falling back to main artist mode.");
            return true;
        }
    }

    private static (List<FolderDto> ExcludedFolders, List<FolderDto> ScannableFolders) SplitScannableFolders(IEnumerable<FolderDto> folders)
    {
        var enabledFolders = folders.Where(folder => folder.Enabled).ToList();
        var excluded = enabledFolders.Where(IsExcludedFromLibraryScan).ToList();
        var scannable = enabledFolders.Where(folder => !IsExcludedFromLibraryScan(folder)).ToList();
        return (excluded, scannable);
    }

    private void LogExcludedFolders(IEnumerable<FolderDto> excludedFolders)
    {
        foreach (var excludedFolder in excludedFolders)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Skipping non-library folder: {excludedFolder.DisplayName} ({excludedFolder.DesiredQuality})."));
        }
    }

    private static void InitializeProgressState(ScanContext context, IEnumerable<FolderDto> scannableFolders, CancellationToken cancellationToken)
    {
        if (context.Progress == null)
        {
            return;
        }

        if (LibraryPrecountEnabled)
        {
            context.TotalFiles = CountScannableAudioFiles(scannableFolders, cancellationToken);
        }

        ReportProgress(context, currentFile: null, force: true);
    }

    private static int CountScannableAudioFiles(IEnumerable<FolderDto> scannableFolders, CancellationToken cancellationToken)
    {
        var totalFiles = 0;
        foreach (var rootPath in scannableFolders.Select(static folder => folder.RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            foreach (var artistDir in Directory.GetDirectories(rootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var albumDir in Directory.GetDirectories(artistDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalFiles += Directory.EnumerateFiles(albumDir, "*.*", SearchOption.AllDirectories)
                        .Count(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
                }
            }
        }

        return totalFiles;
    }

    private void ScanFolder(FolderDto folder, ScanContext context, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder.RootPath))
        {
            _logger.LogDebug("Library folder missing: {DisplayName} -> {RootPath}", folder.DisplayName, folder.RootPath);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Library folder missing: {folder.DisplayName} -> {folder.RootPath}"));
            return;
        }

        var baseline = new FolderScanBaseline(
            context.Snapshot.Artists.Count,
            context.Snapshot.Albums.Count,
            context.Snapshot.Tracks.Count);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Scanning library folder: {folder.DisplayName} -> {folder.RootPath}"));
        _logger.LogDebug("Scanning library folder {DisplayName} -> {RootPath}", folder.DisplayName, folder.RootPath);

        foreach (var artistDir in Directory.GetDirectories(folder.RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanArtistDirectory(folder, artistDir, context, cancellationToken);
        }

        LogFolderSummary(folder.DisplayName, baseline, context);
    }

    private void ScanArtistDirectory(FolderDto folder, string artistDir, ScanContext context, CancellationToken cancellationToken)
    {
        var artistNameRaw = Path.GetFileName(artistDir);
        var artistName = NormalizePrimaryArtistName(artistNameRaw, context.UsePrimaryArtistFolders);
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        var artist = GetOrCreateArtist(context, artistName, artistDir);
        foreach (var albumDir in Directory.GetDirectories(artistDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanAlbumDirectory(folder, artistName, artist, albumDir, context, cancellationToken);
        }
    }

    private static LibraryConfigStore.LibraryArtist GetOrCreateArtist(ScanContext context, string artistName, string artistDir)
    {
        var artistImageCandidate = FindFirstImage(artistDir);
        if (!context.ArtistIndex.TryGetValue(artistName, out var artist))
        {
            artist = new LibraryConfigStore.LibraryArtist(
                ComputeStableId($"artist|{artistName}"),
                artistName,
                artistImageCandidate,
                null);
            context.ArtistIndex[artistName] = artist;
            context.Snapshot.Artists.Add(artist);
            return artist;
        }

        var updatedImage = ImagePathPreference.ChooseBetterImage(artist.ImagePath, artistImageCandidate);
        if (!string.Equals(updatedImage, artist.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            artist = artist with { ImagePath = updatedImage };
            context.ArtistIndex[artistName] = artist;
            var artistIndexInSnapshot = context.Snapshot.Artists.FindIndex(item => item.Id == artist.Id);
            if (artistIndexInSnapshot >= 0)
            {
                context.Snapshot.Artists[artistIndexInSnapshot] = artist;
            }
        }

        return artist;
    }

    private void ScanAlbumDirectory(
        FolderDto folder,
        string artistName,
        LibraryConfigStore.LibraryArtist artist,
        string albumDir,
        ScanContext context,
        CancellationToken cancellationToken)
    {
        var albumTitle = Path.GetFileName(albumDir);
        if (string.IsNullOrWhiteSpace(albumTitle))
        {
            return;
        }

        var album = GetOrCreateAlbum(context, artist, albumTitle, albumDir, folder.DisplayName);
        foreach (var file in EnumerateAudioFiles(albumDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanTrackFile(file, artistName, album, context);
        }

        PublishSnapshot(context);
    }

    private static IEnumerable<string> EnumerateAudioFiles(string albumDir)
    {
        return Directory.EnumerateFiles(albumDir, "*.*", SearchOption.AllDirectories)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static LibraryConfigStore.LibraryAlbum GetOrCreateAlbum(
        ScanContext context,
        LibraryConfigStore.LibraryArtist artist,
        string albumTitle,
        string albumDir,
        string folderDisplayName)
    {
        var albumKey = $"{artist.Id}|{albumTitle}";
        var albumCoverCandidate = FindFirstImage(albumDir);
        var albumHasAnimatedArtwork = Directory.EnumerateFiles(albumDir, "*.*", SearchOption.AllDirectories)
            .Any(static path => IsAnimatedArtworkFileName(Path.GetFileNameWithoutExtension(path)));

        if (!context.AlbumIndex.TryGetValue(albumKey, out var album))
        {
            album = new LibraryConfigStore.LibraryAlbum(
                ComputeStableId($"album|{artist.Id}|{albumTitle}"),
                artist.Id,
                albumTitle,
                albumCoverCandidate,
                new List<string> { folderDisplayName },
                albumHasAnimatedArtwork);
            context.AlbumIndex[albumKey] = album;
            context.Snapshot.Albums.Add(album);
            return album;
        }

        var localFolders = album.LocalFolders.ToList();
        if (!localFolders.Contains(folderDisplayName, StringComparer.OrdinalIgnoreCase))
        {
            localFolders.Add(folderDisplayName);
            album = album with { LocalFolders = localFolders };
        }

        var updatedCover = ImagePathPreference.ChooseBetterImage(album.PreferredCoverPath, albumCoverCandidate);
        if (!string.Equals(updatedCover, album.PreferredCoverPath, StringComparison.OrdinalIgnoreCase))
        {
            album = album with { PreferredCoverPath = updatedCover };
        }

        if (albumHasAnimatedArtwork && !album.HasAnimatedArtwork)
        {
            album = album with { HasAnimatedArtwork = true };
        }

        context.AlbumIndex[albumKey] = album;
        var albumIndexInSnapshot = context.Snapshot.Albums.FindIndex(item => item.Id == album.Id);
        if (albumIndexInSnapshot >= 0)
        {
            context.Snapshot.Albums[albumIndexInSnapshot] = album;
        }

        return album;
    }

    private void ScanTrackFile(
        string file,
        string artistName,
        LibraryConfigStore.LibraryAlbum album,
        ScanContext context)
    {
        var trackData = CreateInitialTrackData(file);
        PopulateTrackDataFromTags(file, trackData, context);
        MergeSidecarLyrics(file, trackData);
        RefreshTrackProbeData(file, trackData);
        ApplyFragmentedMp4Fallback(file, trackData);
        FinalizeTrackScanData(trackData, artistName, album.Title);
        CollectArtistGenres(context.ArtistGenres, artistName, trackData.Genres);
        AddTrackToSnapshot(context.Snapshot, artistName, album, trackData);
        ReportProgress(context, file, force: false);
    }

    private static TrackScanData CreateInitialTrackData(string file)
    {
        var rawFileTrackTitle = Path.GetFileNameWithoutExtension(file);
        var trackTitle = NormalizeTrackTitle(rawFileTrackTitle, out var inferredTrackNoFromFileName);
        return new TrackScanData
        {
            FilePath = file,
            TrackTitle = trackTitle,
            TrackNo = inferredTrackNoFromFileName
        };
    }

    private void PopulateTrackDataFromTags(string file, TrackScanData trackData, ScanContext context)
    {
        try
        {
            using var tagFile = TagLib.File.Create(file);
            ApplyPrimaryTagValues(tagFile, trackData);
            ApplyAudioProperties(tagFile, trackData);
            ApplyDerivedTagValues(file, tagFile, trackData, context);
            ApplySourceValues(tagFile, trackData);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.ErrorCount++;
            _logger.LogDebug(ex, "Tag parsing failed for {FilePath}", file);
        }
    }

    private static void ApplyPrimaryTagValues(TagLib.File tagFile, TrackScanData trackData)
    {
        var tag = tagFile.Tag;
        if (!string.IsNullOrWhiteSpace(tag.Title))
        {
            trackData.TrackTitle = tag.Title;
        }

        trackData.TagTitle = NormalizeTagValue(tag.Title);
        trackData.TagArtist = JoinTagValues(tag.Performers);
        trackData.TagAlbum = NormalizeTagValue(tag.Album);
        trackData.TagAlbumArtist = JoinTagValues(tag.AlbumArtists);
        trackData.TagVersion = NormalizeTagValue(GetCustomValue(tagFile, "VERSION") ?? GetCustomValue(tagFile, "MIXNAME"));
        trackData.TagLabel = NormalizeTagValue(GetCustomValue(tagFile, "LABEL") ?? GetCustomValue(tagFile, "PUBLISHER"));
        trackData.TagCatalogNumber = NormalizeTagValue(GetCustomValue(tagFile, "CATALOGNUMBER") ?? GetCustomValue(tagFile, "CATALOG_NUMBER"));
        trackData.TagBpm = PositiveOrNull(tag.BeatsPerMinute);
        trackData.TagKey = NormalizeTagValue(GetCustomValue(tagFile, "KEY"));
        trackData.TagTrackTotal = PositiveOrNull(tag.TrackCount);
        trackData.TagDurationMs = (int)tagFile.Properties.Duration.TotalMilliseconds;
        trackData.TagYear = PositiveOrNull(tag.Year);
        trackData.TagTrackNo = PositiveOrNull(tag.Track);
        trackData.TagDisc = PositiveOrNull(tag.Disc);
        trackData.TagGenre = JoinTagValues(tag.Genres);
        trackData.TagReleaseDate = NormalizeTagValue(GetCustomValue(tagFile, "RELEASEDATE") ?? GetCustomValue(tagFile, "RELEASE_DATE"));
        trackData.TagPublishDate = NormalizeTagValue(GetCustomValue(tagFile, "PUBLISHDATE") ?? GetCustomValue(tagFile, "PUBLISH_DATE"));
        trackData.TagUrl = NormalizeTagValue(GetCustomValue(tagFile, "URL"));
        trackData.TagReleaseId = NormalizeTagValue(GetCustomValue(tagFile, "RELEASE_ID") ?? GetCustomValue(tagFile, "RELEASEID") ?? GetCustomValue(tagFile, "MUSICBRAINZ_RELEASEID") ?? GetCustomValue(tagFile, "MUSICBRAINZ_RELEASE_ID"));
        trackData.TagTrackId = NormalizeTagValue(GetCustomValue(tagFile, "TRACK_ID") ?? GetCustomValue(tagFile, "TRACKID") ?? GetCustomValue(tagFile, "MUSICBRAINZ_TRACKID") ?? GetCustomValue(tagFile, "MUSICBRAINZ_TRACK_ID"));
        trackData.TagMetaTaggedDate = NormalizeTagValue(GetCustomValue(tagFile, "1T_TAGGEDDATE"));

        if (tag.Track > 0)
        {
            trackData.TrackNo = (int)tag.Track;
        }

        if (tag.Disc > 0)
        {
            trackData.Disc = (int)tag.Disc;
        }
    }

    private static void ApplyAudioProperties(TagLib.File tagFile, TrackScanData trackData)
    {
        trackData.DurationMs = (int)tagFile.Properties.Duration.TotalMilliseconds;
        trackData.Codec = tagFile.Properties.Codecs.FirstOrDefault()?.Description;
        trackData.BitrateKbps = PositiveOrNull(tagFile.Properties.AudioBitrate);
        trackData.SampleRateHz = PositiveOrNull(tagFile.Properties.AudioSampleRate);
        trackData.BitsPerSample = PositiveOrNull(tagFile.Properties.BitsPerSample);
        trackData.Channels = PositiveOrNull(tagFile.Properties.AudioChannels);
        trackData.Genres = tagFile.Tag.Genres;
    }

    private void ApplyDerivedTagValues(string file, TagLib.File tagFile, TrackScanData trackData, ScanContext context)
    {
        MergeEmbeddedLyrics(tagFile.Tag.Lyrics, trackData);
        trackData.TagBpm ??= TryParseInt(GetCustomValue(tagFile, "BPM"));
        trackData.TagTrackTotal ??= TryParseInt(GetCustomValue(tagFile, "TRACKTOTAL") ?? GetCustomValue(tagFile, "TRACK_TOTAL"));
        trackData.QualityRank = EstimateTrackQualityRank(file, trackData, context);
        trackData.TagIsrc = NormalizeSourceId(tagFile.Tag.ISRC);
        trackData.Isrc = trackData.TagIsrc;
        trackData.TagGenres = SplitTagValues(tagFile.Tag.Genres);
        trackData.TagStyles = SplitTagValues(GetCustomValue(tagFile, "STYLE") ?? GetCustomValue(tagFile, "SUBGENRE"));
        trackData.TagMoods = SplitTagValues(GetCustomValue(tagFile, "MOOD"));
        trackData.TagRemixers = SplitTagValues(GetCustomValue(tagFile, "REMIXER") ?? GetCustomValue(tagFile, "REMIXERS"));
        trackData.TagOtherTags = ExtractOtherTags(tagFile);
    }

    private int? EstimateTrackQualityRank(string file, TrackScanData trackData, ScanContext context)
    {
        SignalQualityAnalysis? signalAnalysis = null;
        if (context.EnableSignalAnalysis
            && ShouldRunSignalAnalysis(file, trackData.Codec, trackData.BitrateKbps, trackData.BitsPerSample))
        {
            signalAnalysis = _signalAnalyzer.Analyze(file, trackData.Codec, trackData.SampleRateHz, trackData.BitrateKbps);
        }

        return EstimateQualityRank(file, trackData.Codec, trackData.BitrateKbps, trackData.BitsPerSample, trackData.SampleRateHz, signalAnalysis);
    }

    private static void ApplySourceValues(TagLib.File tagFile, TrackScanData trackData)
    {
        var sourceInfo = ExtractSourceId(tagFile);
        trackData.DeezerTrackId = sourceInfo.DeezerId;
        trackData.Source = sourceInfo.Source;
        trackData.SourceId = sourceInfo.SourceId;
        trackData.TrackIds = ExtractTrackSourceIds(tagFile);
        trackData.AlbumIds = ExtractAlbumSourceIds(tagFile);
        trackData.ArtistIds = ExtractArtistSourceIds(tagFile);
        if (string.Equals(trackData.Source, "deezer", StringComparison.OrdinalIgnoreCase))
        {
            trackData.Source = null;
            trackData.SourceId = null;
        }
    }

    private static string? JoinTagValues(string[]? values)
    {
        if (values is not { Length: > 0 })
        {
            return null;
        }

        var normalized = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return normalized.Length == 0 ? null : string.Join("; ", normalized);
    }

    private static int? PositiveOrNull(uint value)
        => value > 0 ? (int)value : null;

    private static int? PositiveOrNull(int value)
        => value > 0 ? value : null;

    private static void MergeEmbeddedLyrics(string? lyrics, TrackScanData trackData)
    {
        if (string.IsNullOrWhiteSpace(lyrics))
        {
            return;
        }

        trackData.LyricsStatus = SyncedLyricsRegex.IsMatch(lyrics) ? SyncedStatus : UnsyncedStatus;
        trackData.LyricsType = "embedded";
        if (string.Equals(trackData.LyricsStatus, SyncedStatus, StringComparison.OrdinalIgnoreCase))
        {
            trackData.LyricsSynced = lyrics;
            return;
        }

        trackData.LyricsUnsynced = lyrics;
    }

    private static void MergeSidecarLyrics(string file, TrackScanData trackData)
    {
        var sidecarLyrics = ReadLyricsSidecars(file);
        if (!string.IsNullOrWhiteSpace(sidecarLyrics.LyricsSynced) && string.IsNullOrWhiteSpace(trackData.LyricsSynced))
        {
            trackData.LyricsSynced = sidecarLyrics.LyricsSynced;
        }

        if (!string.IsNullOrWhiteSpace(sidecarLyrics.LyricsUnsynced) && string.IsNullOrWhiteSpace(trackData.LyricsUnsynced))
        {
            trackData.LyricsUnsynced = sidecarLyrics.LyricsUnsynced;
        }

        trackData.LyricsStatus = MergeLyricsStatus(trackData.LyricsStatus, sidecarLyrics.LyricsStatus);
        trackData.LyricsType = MergeLyricsTypes(trackData.LyricsType, sidecarLyrics.LyricsType);
    }

    private void RefreshTrackProbeData(string file, TrackScanData trackData)
    {
        if (!NeedsFfprobeRefresh(file, trackData.Codec, trackData.DurationMs, trackData.BitrateKbps, trackData.SampleRateHz, trackData.Channels))
        {
            return;
        }

        var probe = TryProbeAudioInfo(file);
        if (probe is null)
        {
            return;
        }

        trackData.DurationMs = PreferProbedDuration(trackData.DurationMs, probe.DurationMs);
        trackData.TagDurationMs = PreferProbedDuration(trackData.TagDurationMs, probe.DurationMs);
        trackData.SampleRateHz = PreferPositiveProbeValue(trackData.SampleRateHz, probe.SampleRateHz);
        trackData.Channels = PreferPositiveProbeValue(trackData.Channels, probe.Channels);
        trackData.BitrateKbps = PreferProbedBitrate(trackData.BitrateKbps, probe.BitrateKbps);
        trackData.Codec = PreferProbedCodec(trackData.Codec, probe.Codec);
    }

    private static int? PreferProbedDuration(int? current, int? probeValue)
    {
        if (probeValue is not { } candidate || candidate <= 0)
        {
            return current;
        }

        if (!current.HasValue || current.Value <= 0 || Math.Abs(current.Value - candidate) > 1000)
        {
            return candidate;
        }

        return current;
    }

    private static int? PreferPositiveProbeValue(int? current, int? probeValue)
    {
        if (probeValue is not { } candidate || candidate <= 0)
        {
            return current;
        }

        if (!current.HasValue || current.Value <= 0 || current.Value != candidate)
        {
            return candidate;
        }

        return current;
    }

    private static int? PreferProbedBitrate(int? current, int? probeValue)
    {
        if (probeValue is not { } candidate || candidate <= 0)
        {
            return current;
        }

        if (!current.HasValue || current.Value <= 0 || Math.Abs(current.Value - candidate) > 32)
        {
            return candidate;
        }

        return current;
    }

    private static string? PreferProbedCodec(string? current, string? probeValue)
    {
        if (string.IsNullOrWhiteSpace(probeValue))
        {
            return current;
        }

        if (IsGenericCodecDescription(current)
            || string.IsNullOrWhiteSpace(current)
            || !string.Equals(current, probeValue, StringComparison.OrdinalIgnoreCase))
        {
            return probeValue;
        }

        return current;
    }

    private static void ApplyFragmentedMp4Fallback(string file, TrackScanData trackData)
    {
        if (!IsFragmentedMp4Candidate(file, trackData.DurationMs, trackData.BitrateKbps))
        {
            return;
        }

        var fmp4 = FragmentedMp4DurationReader.TryRead(file);
        if (fmp4 is null)
        {
            return;
        }

        var fmp4Ms = (int)Math.Round(fmp4.DurationSeconds * 1000);
        if (fmp4Ms > (trackData.DurationMs ?? 0))
        {
            trackData.DurationMs = fmp4Ms;
            trackData.TagDurationMs = fmp4Ms;
        }

        if (fmp4.SampleRateHz > 0 && (!trackData.SampleRateHz.HasValue || trackData.SampleRateHz.Value <= 0))
        {
            trackData.SampleRateHz = fmp4.SampleRateHz;
        }
    }

    private static void FinalizeTrackScanData(TrackScanData trackData, string artistName, string albumTitle)
    {
        trackData.TrackTitle = NormalizeTrackTitle(trackData.TrackTitle, out var inferredTrackNoFromTitle);
        if (!trackData.TrackNo.HasValue && inferredTrackNoFromTitle.HasValue)
        {
            trackData.TrackNo = inferredTrackNoFromTitle;
        }

        if (!trackData.TagTrackNo.HasValue && trackData.TrackNo.HasValue)
        {
            trackData.TagTrackNo = trackData.TrackNo;
        }

        if (trackData.DurationMs.HasValue && trackData.DurationMs.Value <= 0)
        {
            trackData.DurationMs = null;
        }

        if (trackData.TagDurationMs.HasValue && trackData.TagDurationMs.Value <= 0)
        {
            trackData.TagDurationMs = null;
        }

        trackData.AudioVariant = ResolveAudioVariant(trackData.FilePath, trackData.Codec, trackData.Channels);
        trackData.TagTitle ??= trackData.TrackTitle;
        trackData.TagArtist ??= artistName;
        trackData.TagAlbum ??= albumTitle;
        trackData.TagAlbumArtist = string.IsNullOrWhiteSpace(trackData.TagAlbumArtist) ? trackData.TagArtist : trackData.TagAlbumArtist;
        trackData.TagReleaseDate ??= trackData.TagYear.HasValue ? trackData.TagYear.Value.ToString(CultureInfo.InvariantCulture) : null;
        trackData.TagGenre ??= trackData.TagGenres.FirstOrDefault();
    }

    private static void CollectArtistGenres(
        Dictionary<string, HashSet<string>> artistGenres,
        string artistName,
        string[]? genres)
    {
        if (genres is not { Length: > 0 })
        {
            return;
        }

        if (!artistGenres.TryGetValue(artistName, out var genreSet))
        {
            genreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            artistGenres[artistName] = genreSet;
        }

        foreach (var genre in genres
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim()))
        {
            genreSet.Add(genre);
        }
    }

    private static void AddTrackToSnapshot(
        LibraryConfigStore.LocalLibrarySnapshot snapshot,
        string artistName,
        LibraryConfigStore.LibraryAlbum album,
        TrackScanData trackData)
    {
        var trackId = ComputeStableId($"track|{album.Id}|{trackData.FilePath}");
        snapshot.Tracks.Add(new LibraryConfigStore.LibraryTrack(
            trackId,
            album.Id,
            true,
            new LocalTrackScanDto(
                artistName,
                album.Title,
                trackData.TrackTitle,
                trackData.FilePath,
                trackData.TagTitle,
                trackData.TagArtist,
                trackData.TagAlbum,
                trackData.TagAlbumArtist,
                trackData.TagVersion,
                trackData.TagLabel,
                trackData.TagCatalogNumber,
                trackData.TagBpm,
                trackData.TagKey,
                trackData.TagTrackTotal,
                trackData.TagDurationMs,
                trackData.TagYear,
                trackData.TagTrackNo,
                trackData.TagDisc,
                trackData.TagGenre,
                trackData.TagIsrc,
                trackData.TagReleaseDate,
                trackData.TagPublishDate,
                trackData.TagUrl,
                trackData.TagReleaseId,
                trackData.TagTrackId,
                trackData.TagMetaTaggedDate,
                trackData.LyricsUnsynced,
                trackData.LyricsSynced,
                trackData.TagGenres,
                trackData.TagStyles,
                trackData.TagMoods,
                trackData.TagRemixers,
                trackData.TagOtherTags,
                trackData.TrackNo,
                trackData.Disc,
                trackData.DurationMs,
                trackData.LyricsStatus,
                trackData.LyricsType,
                trackData.Codec,
                trackData.BitrateKbps,
                trackData.SampleRateHz,
                trackData.BitsPerSample,
                trackData.Channels,
                trackData.QualityRank,
                trackData.AudioVariant,
                trackData.DeezerTrackId,
                trackData.Isrc,
                trackData.AlbumIds.DeezerAlbumId,
                trackData.ArtistIds.DeezerArtistId,
                trackData.TrackIds.SpotifyTrackId,
                trackData.AlbumIds.SpotifyAlbumId,
                trackData.ArtistIds.SpotifyArtistId,
                trackData.TrackIds.AppleTrackId,
                trackData.AlbumIds.AppleAlbumId,
                trackData.ArtistIds.AppleArtistId,
                trackData.Source,
                trackData.SourceId)));
    }

    private static void ReportProgress(ScanContext context, string? currentFile, bool force)
    {
        if (context.Progress == null)
        {
            return;
        }

        if (!force)
        {
            context.ProcessedFiles++;
            var reportThresholdHit = context.ProcessedFiles % context.ReportEvery == 0;
            var reachedEnd = context.TotalFiles > 0 && context.ProcessedFiles >= context.TotalFiles;
            if (!reportThresholdHit && !reachedEnd)
            {
                return;
            }
        }

        context.Progress.Report(new ScanProgress(
            context.ProcessedFiles,
            context.TotalFiles,
            context.ErrorCount,
            currentFile,
            context.Snapshot.Artists.Count,
            context.Snapshot.Albums.Count,
            context.Snapshot.Tracks.Count));
    }

    private static void PublishSnapshot(ScanContext context)
    {
        context.SnapshotPublished?.Invoke(CloneSnapshot(context.Snapshot));
    }

    private static LibraryConfigStore.LocalLibrarySnapshot CloneSnapshot(LibraryConfigStore.LocalLibrarySnapshot snapshot)
    {
        return new LibraryConfigStore.LocalLibrarySnapshot
        {
            Artists = snapshot.Artists.ToList(),
            Albums = snapshot.Albums
                .Select(album => album with { LocalFolders = album.LocalFolders.ToList() })
                .ToList(),
            Tracks = snapshot.Tracks.ToList(),
            ArtistGenres = snapshot.ArtistGenres.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private void LogFolderSummary(string folderDisplayName, FolderScanBaseline baseline, ScanContext context)
    {
        var artistsAdded = context.Snapshot.Artists.Count - baseline.ArtistCount;
        var albumsAdded = context.Snapshot.Albums.Count - baseline.AlbumCount;
        var tracksAdded = context.Snapshot.Tracks.Count - baseline.TrackCount;

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Finished folder scan: {folderDisplayName} (artists={artistsAdded}, albums={albumsAdded}, tracks={tracksAdded})."));
        _logger.LogDebug(
            "Finished folder scan {DisplayName} artists={Artists} albums={Albums} tracks={Tracks}",
            folderDisplayName,
            artistsAdded,
            albumsAdded,
            tracksAdded);
    }

    private static bool IsExcludedFromLibraryScan(FolderDto folder)
    {
        var desiredQuality = folder.DesiredQuality?.Trim();
        return string.Equals(desiredQuality, "video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(desiredQuality, "podcast", StringComparison.OrdinalIgnoreCase);
    }

    private static SourceInfo ExtractSourceId(TagLib.File file)
    {
        var source = NormalizeSourceId(GetCustomValue(file, "SOURCE"));
        var sourceId = NormalizeSourceId(GetCustomValue(file, "SOURCEID"));
        var deezerId = string.Equals(source, "deezer", StringComparison.OrdinalIgnoreCase)
            ? ParseDeezerId(sourceId)
            : null;

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            var deezerRaw = GetCustomValue(file, "DEEZER_TRACK_ID")
                ?? GetCustomValue(file, "DEEZERID")
                ?? GetCustomValue(file, "DEEZER_ID");
            deezerId = ParseDeezerId(deezerRaw ?? sourceId);
        }

        return new SourceInfo(
            source?.ToLowerInvariant(),
            string.IsNullOrWhiteSpace(deezerId) ? NormalizeSourceId(sourceId) : null,
            deezerId);
    }

    private static TrackSourceIds ExtractTrackSourceIds(TagLib.File file)
    {
        return new TrackSourceIds(
            NormalizeSourceId(GetCustomValue(file, "SPOTIFY_TRACK_ID") ?? GetCustomValue(file, "SPOTIFY_TRACKID")),
            NormalizeSourceId(GetCustomValue(file, "APPLE_TRACK_ID") ?? GetCustomValue(file, "APPLE_TRACKID") ?? GetCustomValue(file, "ITUNES_TRACK_ID"))
        );
    }

    private static AlbumSourceIds ExtractAlbumSourceIds(TagLib.File file)
    {
        return new AlbumSourceIds(
            NormalizeSourceId(GetCustomValue(file, "DEEZER_ALBUM_ID") ?? GetCustomValue(file, "DEEZER_ALBUMID") ?? GetCustomValue(file, "DEEZERALBUMID")),
            NormalizeSourceId(GetCustomValue(file, "SPOTIFY_ALBUM_ID") ?? GetCustomValue(file, "SPOTIFY_ALBUMID") ?? GetCustomValue(file, "SPOTIFYALBUMID")),
            NormalizeSourceId(GetCustomValue(file, "APPLE_ALBUM_ID") ?? GetCustomValue(file, "APPLE_ALBUMID") ?? GetCustomValue(file, "ITUNES_ALBUM_ID"))
        );
    }

    private static ArtistSourceIds ExtractArtistSourceIds(TagLib.File file)
    {
        return new ArtistSourceIds(
            NormalizeSourceId(GetCustomValue(file, "DEEZER_ARTIST_ID") ?? GetCustomValue(file, "DEEZER_ARTISTID") ?? GetCustomValue(file, "DEEZERARTISTID")),
            NormalizeSourceId(GetCustomValue(file, "SPOTIFY_ARTIST_ID") ?? GetCustomValue(file, "SPOTIFY_ARTISTID") ?? GetCustomValue(file, "SPOTIFYARTISTID")),
            NormalizeSourceId(GetCustomValue(file, "APPLE_ARTIST_ID") ?? GetCustomValue(file, "APPLE_ARTISTID") ?? GetCustomValue(file, "ITUNES_ARTIST_ID"))
        );
    }

    private static string? GetCustomValue(TagLib.File file, string key)
    {
        var id3 = file.GetTag(TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
        if (id3 != null)
        {
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false);
            if (frame?.Text != null && frame.Text.Length > 0 && !string.IsNullOrWhiteSpace(frame.Text[0]))
            {
                return frame.Text[0];
            }
        }

        var xiph = file.GetTag(TagTypes.Xiph, false) as TagLib.Ogg.XiphComment;
        if (xiph != null)
        {
            var values = xiph.GetField(key);
            if (values != null && values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]))
            {
                return values[0];
            }
        }

        var apple = file.GetTag(TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag;
        if (apple != null)
        {
            var dashValue = TryGetAppleDashBox(apple, key);
            if (!string.IsNullOrWhiteSpace(dashValue))
            {
                return dashValue;
            }
        }

        return null;
    }

    private static string? NormalizeTagValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? TryParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static List<string> SplitTagValues(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw
            .Split(TagValueSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> SplitTagValues(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return new List<string>();
        }

        return values
            .SelectMany(value => SplitTagValues(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<LocalTrackOtherTag> ExtractOtherTags(TagLib.File file)
    {
        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SOURCE", "SOURCEID", "DEEZER_TRACK_ID", "DEEZERID", "DEEZER_ID",
            "DEEZER_ALBUM_ID", "DEEZER_ALBUMID", "DEEZERALBUMID",
            "DEEZER_ARTIST_ID", "DEEZER_ARTISTID", "DEEZERARTISTID",
            "SPOTIFY_TRACK_ID", "SPOTIFY_TRACKID",
            "SPOTIFY_ALBUM_ID", "SPOTIFY_ALBUMID", "SPOTIFYALBUMID",
            "SPOTIFY_ARTIST_ID", "SPOTIFY_ARTISTID", "SPOTIFYARTISTID",
            "APPLE_TRACK_ID", "APPLE_TRACKID", "ITUNES_TRACK_ID",
            "APPLE_ALBUM_ID", "APPLE_ALBUMID", "ITUNES_ALBUM_ID",
            "APPLE_ARTIST_ID", "APPLE_ARTISTID", "ITUNES_ARTIST_ID",
            "ISRC", "VERSION", "MIXNAME", "LABEL", "PUBLISHER", "CATALOGNUMBER", "CATALOG_NUMBER",
            "BPM", "KEY", "TRACKTOTAL", "TRACK_TOTAL", "DISCNUMBER", "DISC_NUMBER",
            "STYLE", "SUBGENRE", "MOOD", "REMIXER", "REMIXERS",
            "RELEASEDATE", "RELEASE_DATE", "PUBLISHDATE", "PUBLISH_DATE",
            "URL", "RELEASE_ID", "RELEASEID", "TRACK_ID", "TRACKID",
            "MUSICBRAINZ_RELEASEID", "MUSICBRAINZ_RELEASE_ID", "MUSICBRAINZ_TRACKID", "MUSICBRAINZ_TRACK_ID",
            "1T_TAGGEDDATE"
        };

        var results = new List<LocalTrackOtherTag>();
        var id3 = file.GetTag(TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
        if (id3 != null)
        {
            var frames = id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>();
            foreach (var frame in frames)
            {
                var key = frame.Description;
                if (string.IsNullOrWhiteSpace(key) || knownKeys.Contains(key))
                {
                    continue;
                }

                foreach (var normalized in (frame.Text ?? Array.Empty<string>())
                    .Select(NormalizeTagValue)
                    .OfType<string>()
                    .Where(static normalized => !string.IsNullOrWhiteSpace(normalized)))
                {
                    results.Add(new LocalTrackOtherTag(key, normalized));
                }
            }
        }

        return results;
    }

    private sealed record TrackSourceIds(string? SpotifyTrackId, string? AppleTrackId);
    private sealed record AlbumSourceIds(string? DeezerAlbumId, string? SpotifyAlbumId, string? AppleAlbumId);
    private sealed record ArtistSourceIds(string? DeezerArtistId, string? SpotifyArtistId, string? AppleArtistId);

    private static string? TryGetAppleDashBox(TagLib.Mpeg4.AppleTag tag, string name)
    {
        try
        {
            var tagType = tag.GetType();
            var methods = tagType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, "GetDashBox", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 2 ||
                    parameters[0].ParameterType != typeof(string) ||
                    parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                var meanValue = "com.apple.iTunes";
                var result = method.Invoke(tag, new object[] { meanValue, name });
                return result switch
                {
                    string str => str,
                    string[] values => values.FirstOrDefault(),
                    IEnumerable<string> enumerable => enumerable.FirstOrDefault(),
                    _ => null
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }

        return null;
    }

    private static string? ParseDeezerId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        const string urlMarker = "/track/";
        var urlIndex = value.IndexOf(urlMarker, StringComparison.OrdinalIgnoreCase);
        if (urlIndex >= 0)
        {
            var start = urlIndex + urlMarker.Length;
            var end = value.IndexOfAny(DeezerUrlTerminatorChars, start);
            var id = end >= 0 ? value[start..end] : value[start..];
            return NormalizeSourceId(id);
        }

        if (value.All(char.IsDigit))
        {
            return value;
        }

        return NormalizeSourceId(value);
    }

    private static string? NormalizeSourceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed record SourceInfo(string? Source, string? SourceId, string? DeezerId);

    private static string? FindFirstImage(string folderPath)
    {
        var images = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));

        return images.FirstOrDefault();
    }

    private static long ComputeStableId(string input)
    {
        // Deterministic non-cryptographic identifier only; value is not used for security decisions.
#pragma warning disable S4790
        var hash = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(input));
#pragma warning restore S4790
        var raw = Math.Abs(BitConverter.ToInt64(hash, 0));
        // Keep IDs within JS safe integer range to avoid precision loss in the UI.
        return raw & ((1L << 53) - 1);
    }

    private static int? EstimateQualityRank(
        string filePath,
        string? codec,
        int? bitrateKbps,
        int? bitsPerSample,
        int? sampleRateHz,
        SignalQualityAnalysis? signalAnalysis)
    {
        var extension = NormalizeAudioExtension(filePath);
        var codecText = NormalizeCodec(codec);

        if (IsLosslessAudio(extension, codecText))
        {
            return EstimateLosslessQualityRank(bitsPerSample, sampleRateHz, signalAnalysis);
        }

        var lossyRank = EstimateLossyQualityRank(bitrateKbps, sampleRateHz, signalAnalysis, extension, codecText);
        if (lossyRank.HasValue)
        {
            return lossyRank;
        }

        return EstimateBitDepthRank(bitsPerSample);
    }

    private static string NormalizeAudioExtension(string filePath)
        => Path.GetExtension(filePath)?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeCodec(string? codec)
        => codec?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool IsLosslessAudio(string extension, string codecText)
        => extension is FlacExtension or AlacExtension or ".wav" or AiffExtension or ".aif"
            || codecText.Contains("flac", StringComparison.Ordinal)
            || codecText.Contains("alac", StringComparison.Ordinal)
            || codecText.Contains("lossless", StringComparison.Ordinal)
            || codecText.Contains("pcm", StringComparison.Ordinal)
            || codecText.Contains("wave", StringComparison.Ordinal);

    private static bool IsLossyAudio(string extension, string codecText)
        => extension is ".mp3" or ".m4a" or ".m4b" or ".aac" or ".ogg" or ".opus"
            || codecText.Contains("aac", StringComparison.Ordinal)
            || codecText.Contains("mp3", StringComparison.Ordinal)
            || codecText.Contains("mpeg", StringComparison.Ordinal)
            || codecText.Contains("vorbis", StringComparison.Ordinal)
            || codecText.Contains("opus", StringComparison.Ordinal)
            || codecText.Contains("mp4a", StringComparison.Ordinal);

    private static int EstimateLosslessQualityRank(
        int? bitsPerSample,
        int? sampleRateHz,
        SignalQualityAnalysis? signalAnalysis)
    {
        if (signalAnalysis is not null
            && signalAnalysis.IsLosslessCodecContainer
            && !signalAnalysis.IsTrueLossless
            && signalAnalysis.EquivalentBitrateKbps.HasValue)
        {
            return signalAnalysis.EquivalentBitrateKbps.Value >= 192 ? 2 : 1;
        }

        var bitDepthRank = EstimateBitDepthRank(bitsPerSample);
        if (bitDepthRank.HasValue)
        {
            return bitDepthRank.Value;
        }

        if (sampleRateHz.HasValue && sampleRateHz.Value > 48000)
        {
            return 4;
        }

        return 3;
    }

    private static int? EstimateLossyQualityRank(
        int? bitrateKbps,
        int? sampleRateHz,
        SignalQualityAnalysis? signalAnalysis,
        string extension,
        string codecText)
    {
        if (bitrateKbps.HasValue)
        {
            if (bitrateKbps.Value >= 192)
            {
                return 2;
            }

            if (bitrateKbps.Value > 0)
            {
                return 1;
            }

            return null;
        }

        if (signalAnalysis?.EquivalentBitrateKbps is int estimatedLossyBitrate)
        {
            return estimatedLossyBitrate >= 192 ? 2 : 1;
        }

        if (!IsLossyAudio(extension, codecText))
        {
            return null;
        }

        return sampleRateHz.HasValue && sampleRateHz.Value >= 44100 ? 2 : 1;
    }

    private static int? EstimateBitDepthRank(int? bitsPerSample)
    {
        if (!bitsPerSample.HasValue)
        {
            return null;
        }

        if (bitsPerSample.Value >= 24)
        {
            return 4;
        }

        return bitsPerSample.Value >= 16 ? 3 : null;
    }

    private static string ResolveAudioVariant(string filePath, string? codec, int? channels)
    {
        return AudioVariantResolver.ResolveAudioVariant(
            storedVariant: null,
            channels: channels,
            filePath: filePath,
            codec: codec,
            extension: Path.GetExtension(filePath));
    }

    private static bool IsGenericCodecDescription(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return true;
        }

        return codec.Contains("mpeg-4 audio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFragmentedMp4Candidate(string filePath, int? durationMs, int? bitrateKbps)
    {
        var extension = Path.GetExtension(filePath)?.Trim().ToLowerInvariant() ?? string.Empty;
        if (extension is not (".m4a" or ".m4b" or ".ec3" or ".ac3"))
        {
            return false;
        }

        if (!durationMs.HasValue || durationMs.Value <= 0)
        {
            return true;
        }

        if (!bitrateKbps.HasValue || bitrateKbps.Value <= 0)
        {
            return true;
        }

        // Check if file size is implausibly large for the reported duration.
        // If filesize implies a much longer duration than reported, it's likely
        // a fragmented MP4 where only the first fragment was parsed.
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists && fileInfo.Length > 0)
            {
                var expectedDurationMs = fileInfo.Length * 8.0 / bitrateKbps.Value;
                if (expectedDurationMs > durationMs.Value * 4d)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Ignore file access errors
        }

        return false;
    }

    private static bool NeedsFfprobeRefresh(
        string filePath,
        string? codec,
        int? durationMs,
        int? bitrateKbps,
        int? sampleRateHz,
        int? channels)
    {
        var extension = Path.GetExtension(filePath)?.Trim().ToLowerInvariant() ?? string.Empty;
        if (LegacyAggressiveFfprobeEnabled
            && extension is ".m4a" or ".m4b" or ".aac" or ".ac3" or ".ec3")
        {
            return true;
        }

        if (extension is ".m4a" or ".m4b" or ".aac" or ".ac3" or ".ec3")
        {
            // These containers are sometimes under-reported by TagLib; keep probing
            // only when metadata is missing or the file shape looks fragmented.
            if (IsFragmentedMp4Candidate(filePath, durationMs, bitrateKbps))
            {
                return true;
            }
        }

        if (!durationMs.HasValue || durationMs.Value <= 0)
        {
            return true;
        }

        if (!sampleRateHz.HasValue || sampleRateHz.Value <= 0)
        {
            return true;
        }

        if (!channels.HasValue || channels.Value <= 0)
        {
            return true;
        }

        return IsGenericCodecDescription(codec);
    }

    private static bool ReadBooleanEnvironmentVariable(string variableName, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static bool ResolveSignalAnalysisDisabled()
    {
        var explicitDisable = ReadNullableBooleanEnvironmentVariable("DEEZSPOTAG_LIBRARY_DISABLE_SIGNAL_ANALYSIS");
        if (explicitDisable.HasValue)
        {
            return explicitDisable.Value;
        }

        var explicitEnable = ReadNullableBooleanEnvironmentVariable("DEEZSPOTAG_LIBRARY_ENABLE_SIGNAL_ANALYSIS");
        if (explicitEnable.HasValue)
        {
            return !explicitEnable.Value;
        }

        // Keep library scan fast by default; expensive audio-content analysis belongs
        // in an explicit quality-analysis pass rather than the baseline filesystem scan.
        return true;
    }

    private static bool? ReadNullableBooleanEnvironmentVariable(string variableName)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static string? ResolveFfprobePath()
    {
        foreach (var candidate in GetFfprobeCandidates())
        {
            if (TryResolveFfprobeCandidate(candidate, out var resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static string[] GetFfprobeCandidates()
        => OperatingSystem.IsWindows() ? WindowsFfprobeCandidates : UnixFfprobeCandidates;

    private static bool TryResolveFfprobeCandidate(string candidate, out string? resolved)
    {
        resolved = null;
        try
        {
            if (IsAbsoluteOrRelativePathCandidate(candidate))
            {
                if (!System.IO.File.Exists(candidate))
                {
                    return false;
                }

                resolved = candidate;
                return true;
            }

            if (!TryRunVersionProbe(candidate))
            {
                return false;
            }

            resolved = candidate;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Error probing ffprobe candidate '{0}': {1}", candidate, ex.Message);
            return false;
        }
    }

    private static bool IsAbsoluteOrRelativePathCandidate(string candidate)
        => candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar);

    private static bool TryRunVersionProbe(string candidate)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = candidate,
            Arguments = "-version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
        {
            return false;
        }

        if (!process.WaitForExit(1500))
        {
            TryKillProbeProcess(process, candidate);
            return false;
        }

        return process.ExitCode == 0;
    }

    private static void TryKillProbeProcess(Process process, string candidate)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex)
        {
            System.Diagnostics.Trace.TraceWarning("ffprobe process exited before termination for '{0}': {1}", candidate, ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Failed to terminate ffprobe process for '{0}': {1}", candidate, ex.Message);
        }
    }

    private FfprobeAudioInfo? TryProbeAudioInfo(string filePath)
    {
        var ffprobePath = FfprobePath.Value;
        if (!CanProbeAudio(filePath, ffprobePath))
        {
            return null;
        }

        var startInfo = CreateFfprobeStartInfo(ffprobePath!, filePath);
        if (!TryExecuteFfprobe(startInfo, filePath, out var output))
        {
            return null;
        }

        return TryParseFfprobeOutput(output.StandardOutput, filePath);
    }

    private static bool CanProbeAudio(string filePath, string? ffprobePath)
        => !string.IsNullOrWhiteSpace(ffprobePath) && System.IO.File.Exists(filePath);

    private static ProcessStartInfo CreateFfprobeStartInfo(string ffprobePath, string filePath)
        => new()
        {
            FileName = ffprobePath,
            Arguments = $"-v error -show_entries stream=codec_type,codec_name,codec_tag_string,profile,sample_rate,channels,bit_rate -show_entries format=duration -of json \"{filePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

    private bool TryExecuteFfprobe(ProcessStartInfo startInfo, string filePath, out FfprobeRawOutput output)
    {
        output = new FfprobeRawOutput(string.Empty, string.Empty);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            output = new FfprobeRawOutput(stdout, stderr);
            if (!process.WaitForExit(10000))
            {
                TryKillTimedOutFfprobe(process, filePath);
                return false;
            }

            if (process.ExitCode != 0)
            {
                LogFfprobeError(filePath, stderr);
                return false;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                LogFfprobeError(filePath, stderr);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ffprobe execution failed for {FilePath}", filePath);
            return false;
        }
    }

    private void TryKillTimedOutFfprobe(Process process, string filePath)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "ffprobe process already exited before forced termination");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogDebug(ex, "Failed to terminate ffprobe process for {FilePath}", filePath);
        }
    }

    private void LogFfprobeError(string filePath, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogDebug("ffprobe failed for {FilePath}: {Error}", filePath, stderr.Trim());
        }
    }

    private FfprobeAudioInfo? TryParseFfprobeOutput(string stdout, string filePath)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var streamInfo = ParseFfprobeStream(root);
            var durationMs = ParseFfprobeDuration(root);

            if (IsEmptyFfprobeResult(durationMs, streamInfo))
            {
                return null;
            }

            return new FfprobeAudioInfo(
                durationMs,
                streamInfo.SampleRateHz,
                streamInfo.Channels,
                streamInfo.BitrateKbps,
                streamInfo.Codec);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ffprobe parse failed for {FilePath}", filePath);
            return null;
        }
    }

    private static FfprobeStreamInfo ParseFfprobeStream(JsonElement root)
    {
        var stream = SelectAudioStream(root);
        if (stream.ValueKind == JsonValueKind.Undefined)
        {
            return new FfprobeStreamInfo(null, null, null, null);
        }

        var codecName = ReadTrimmedString(stream, "codec_name");
        var profile = ReadTrimmedString(stream, "profile");
        var codec = ComposeCodec(codecName, profile);
        var sampleRateHz = ReadIntFromString(stream, "sample_rate");
        var channels = ReadInt(stream, "channels");
        var bitrateKbps = ReadBitrateKbps(stream, "bit_rate");
        return new FfprobeStreamInfo(sampleRateHz, channels, bitrateKbps, codec);
    }

    private static JsonElement SelectAudioStream(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array
            || streams.GetArrayLength() == 0)
        {
            return default;
        }

        var audioStream = streams.EnumerateArray()
            .FirstOrDefault(static element =>
                element.TryGetProperty("codec_type", out var codecTypeElement)
                && string.Equals(codecTypeElement.GetString(), "audio", StringComparison.OrdinalIgnoreCase));
        return audioStream.ValueKind == JsonValueKind.Undefined ? streams[0] : audioStream;
    }

    private static string? ReadTrimmedString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? ReadIntFromString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (!int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        return parsed > 0 ? parsed : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var parsed))
        {
            return null;
        }

        return parsed > 0 ? parsed : null;
    }

    private static int? ReadBitrateKbps(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (!long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            return null;
        }

        return (int)Math.Round(parsed / 1000d);
    }

    private static string? ComposeCodec(string? codecName, string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return codecName;
        }

        return string.IsNullOrWhiteSpace(codecName)
            ? profile
            : $"{codecName} ({profile})";
    }

    private static int? ParseFfprobeDuration(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.Object
            || !format.TryGetProperty("duration", out var durationElement))
        {
            return null;
        }

        if (!double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDurationSeconds)
            || parsedDurationSeconds <= 0)
        {
            return null;
        }

        return (int)Math.Round(parsedDurationSeconds * 1000d);
    }

    private static bool IsEmptyFfprobeResult(int? durationMs, FfprobeStreamInfo streamInfo)
        => !durationMs.HasValue
            && !streamInfo.SampleRateHz.HasValue
            && !streamInfo.Channels.HasValue
            && !streamInfo.BitrateKbps.HasValue
            && string.IsNullOrWhiteSpace(streamInfo.Codec);

    private sealed record FfprobeAudioInfo(
        int? DurationMs,
        int? SampleRateHz,
        int? Channels,
        int? BitrateKbps,
        string? Codec);
    private sealed record FfprobeRawOutput(string StandardOutput, string StandardError);
    private sealed record FfprobeStreamInfo(int? SampleRateHz, int? Channels, int? BitrateKbps, string? Codec);

    private sealed record LyricsScanResult(
        string? LyricsStatus,
        string? LyricsType,
        string? LyricsUnsynced,
        string? LyricsSynced);

    private static string NormalizeTrackTitle(string rawTitle, out int? inferredTrackNo)
    {
        inferredTrackNo = null;
        var title = (rawTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Unknown Title";
        }

        var match = FileNameTrackPrefixRegex.Match(title);
        if (!match.Success)
        {
            return title;
        }

        if (int.TryParse(match.Groups["track"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTrackNo)
            && parsedTrackNo > 0)
        {
            inferredTrackNo = parsedTrackNo;
        }

        var normalizedTitle = match.Groups["title"].Value.Trim();
        return string.IsNullOrWhiteSpace(normalizedTitle) ? title : normalizedTitle;
    }

    private static bool ShouldRunSignalAnalysis(
        string filePath,
        string? codec,
        int? bitrateKbps,
        int? bitsPerSample)
    {
        var extension = Path.GetExtension(filePath)?.Trim().ToLowerInvariant() ?? string.Empty;
        var codecText = codec?.Trim().ToLowerInvariant() ?? string.Empty;

        var isLikelyLosslessContainer =
            extension is FlacExtension or AlacExtension or ".wav" or AiffExtension or ".aif"
            || codecText.Contains("flac", StringComparison.Ordinal)
            || codecText.Contains("alac", StringComparison.Ordinal)
            || codecText.Contains("lossless", StringComparison.Ordinal)
            || codecText.Contains("pcm", StringComparison.Ordinal)
            || codecText.Contains("wave", StringComparison.Ordinal);

        if (isLikelyLosslessContainer)
        {
            return true;
        }

        if (!bitrateKbps.HasValue)
        {
            return true;
        }

        if (bitsPerSample.HasValue && bitsPerSample.Value >= 24)
        {
            return true;
        }

        return bitrateKbps.Value is >= 160 and <= 300;
    }

    private static LyricsScanResult ReadLyricsSidecars(string audioFilePath)
    {
        var basePath = Path.Combine(
            Path.GetDirectoryName(audioFilePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(audioFilePath));
        var hasSynced = false;
        var hasUnsynced = false;
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? synced = null;
        string? unsynced = null;

        foreach (var extension in LyricsSidecarExtensions)
        {
            var path = basePath + extension;
            if (!System.IO.File.Exists(path))
            {
                continue;
            }

            string content;
            try
            {
                content = System.IO.File.ReadAllText(path);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            types.Add(extension.TrimStart('.').ToLowerInvariant());
            var isSynced = extension.Equals(".ttml", StringComparison.OrdinalIgnoreCase)
                || SyncedLyricsRegex.IsMatch(content)
                || content.Contains("<tt", StringComparison.OrdinalIgnoreCase);
            if (isSynced)
            {
                hasSynced = true;
                synced ??= content;
            }
            else
            {
                hasUnsynced = true;
                unsynced ??= content;
            }
        }

        return new LyricsScanResult(
            ComposeLyricsStatus(hasSynced, hasUnsynced),
            ComposeLyricsType(types),
            unsynced,
            synced);
    }

    private static string? MergeLyricsStatus(string? existing, string? sidecar)
    {
        var hasSynced = string.Equals(existing, SyncedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing, "both", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sidecar, SyncedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sidecar, "both", StringComparison.OrdinalIgnoreCase);
        var hasUnsynced = string.Equals(existing, UnsyncedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing, "both", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sidecar, UnsyncedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sidecar, "both", StringComparison.OrdinalIgnoreCase);
        return ComposeLyricsStatus(hasSynced, hasUnsynced);
    }

    private static string? ComposeLyricsStatus(bool hasSynced, bool hasUnsynced)
    {
        if (hasSynced && hasUnsynced)
        {
            return "both";
        }

        if (hasSynced)
        {
            return SyncedStatus;
        }

        if (hasUnsynced)
        {
            return UnsyncedStatus;
        }

        return null;
    }

    private static string? MergeLyricsTypes(string? embeddedType, string? sidecarType)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLyricsTypeParts(parts, embeddedType);
        AddLyricsTypeParts(parts, sidecarType);
        return ComposeLyricsType(parts);
    }

    private static void AddLyricsTypeParts(HashSet<string> parts, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var part in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            parts.Add(part);
        }
    }

    private static string? ComposeLyricsType(IEnumerable<string> parts)
    {
        var ordered = parts
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static part => part switch
            {
                "embedded" => 0,
                "lrc" => 1,
                "ttml" => 2,
                _ => 9
            })
            .ToList();
        return ordered.Count == 0 ? null : string.Join('+', ordered);
    }

    private static bool IsAnimatedArtworkFileName(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return false;
        }

        return filename.Equals("square_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.Equals("tall_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(" - square_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(" - tall_animated_artwork", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePrimaryArtistName(string? rawArtist, bool usePrimaryArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist))
        {
            return string.Empty;
        }

        if (!usePrimaryArtist)
        {
            return rawArtist.Trim();
        }

        return ArtistNameNormalizer.ExtractPrimaryArtist(rawArtist);
    }
}
