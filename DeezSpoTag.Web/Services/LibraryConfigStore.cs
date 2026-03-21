using System.Globalization;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryConfigStore
{
    private readonly LibraryRepository _repository;
    private readonly ILogger<LibraryConfigStore> _logger;
    private readonly object _logLock = new();
    private readonly string _activityLogPath;

    public LibraryConfigStore(
        LibraryRepository repository,
        ILogger<LibraryConfigStore> logger,
        IHostEnvironment environment)
    {
        _repository = repository;
        _logger = logger;
        var dataRoot = AppDataPathResolver.ResolveDataRootOrDefault(Path.Join(environment.ContentRootPath, "Data"));
        var logDir = Path.Join(dataRoot, "logs");
        Directory.CreateDirectory(logDir);
        _activityLogPath = Path.Join(logDir, "activities.log");
    }

    public LibrarySettingsDto GetSettings()
    {
        if (!_repository.IsConfigured)
        {
            return new LibrarySettingsDto(0.85m, true, false, false);
        }

        return _repository.GetSettingsAsync().GetAwaiter().GetResult();
    }

    public LibrarySettingsDto SaveSettings(LibrarySettingsDto settings)
    {
        if (!_repository.IsConfigured)
        {
            return settings;
        }

        return _repository.UpdateSettingsAsync(settings).GetAwaiter().GetResult();
    }

    public IReadOnlyList<FolderDto> GetFolders()
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<FolderDto>();
        }

        return _repository.GetFoldersAsync().GetAwaiter().GetResult();
    }

    public IReadOnlyList<LibraryArtist> GetLocalArtists()
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<LibraryArtist>();
        }

        var artists = _repository.GetArtistsAsync("all").GetAwaiter().GetResult();
        return artists
            .Select(artist => new LibraryArtist(artist.Id, artist.Name, artist.PreferredImagePath, artist.PreferredBackgroundPath))
            .OrderBy(artist => artist.Name)
            .ToList();
    }

    public IReadOnlyList<LibraryAlbum> GetLocalAlbums(long artistId)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<LibraryAlbum>();
        }

        var albums = _repository.GetArtistAlbumsAsync(artistId).GetAwaiter().GetResult();
        return albums
            .Select(album => new LibraryAlbum(
                album.Id,
                album.ArtistId,
                album.Title,
                album.PreferredCoverPath,
                album.LocalFolders,
                false,
                album.HasStereoVariant,
                album.HasAtmosVariant,
                album.LocalTrackCount,
                album.LocalStereoTrackCount,
                album.LocalAtmosTrackCount))
            .OrderBy(album => album.Title)
            .ToList();
    }

    public LibraryAlbum? GetLocalAlbum(long albumId)
    {
        if (!_repository.IsConfigured)
        {
            return null;
        }

        var album = _repository.GetAlbumAsync(albumId).GetAwaiter().GetResult();
        return album is null
            ? null
            : new LibraryAlbum(
                album.Id,
                album.ArtistId,
                album.Title,
                album.PreferredCoverPath,
                album.LocalFolders,
                false,
                false,
                false,
                0,
                0,
                0);
    }

    public IReadOnlyList<LibraryTrack> GetLocalTracks(long albumId)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<LibraryTrack>();
        }

        var tracks = _repository.GetAlbumTracksAsync(albumId).GetAwaiter().GetResult();
        return tracks
            .Select(track => new LibraryTrack(
                track.Id,
                track.AlbumId,
                track.AvailableLocally,
                new LocalTrackScanDto(
                    ArtistName: string.Empty,
                    AlbumTitle: string.Empty,
                    Title: track.Title,
                    FilePath: string.Empty,
                    TagTitle: null,
                    TagArtist: null,
                    TagAlbum: null,
                    TagAlbumArtist: null,
                    TagVersion: null,
                    TagLabel: null,
                    TagCatalogNumber: null,
                    TagBpm: null,
                    TagKey: null,
                    TagTrackTotal: null,
                    TagDurationMs: null,
                    TagYear: null,
                    TagTrackNo: null,
                    TagDisc: null,
                    TagGenre: null,
                    TagIsrc: null,
                    TagReleaseDate: null,
                    TagPublishDate: null,
                    TagUrl: null,
                    TagReleaseId: null,
                    TagTrackId: null,
                    TagMetaTaggedDate: null,
                    LyricsUnsynced: null,
                    LyricsSynced: null,
                    TagGenres: Array.Empty<string>(),
                    TagStyles: Array.Empty<string>(),
                    TagMoods: Array.Empty<string>(),
                    TagRemixers: Array.Empty<string>(),
                    TagOtherTags: Array.Empty<LocalTrackOtherTag>(),
                    TrackNo: track.TrackNo,
                    Disc: track.Disc,
                    DurationMs: track.DurationMs,
                    LyricsStatus: track.LyricsStatus,
                    LyricsType: null,
                    Codec: null,
                    BitrateKbps: null,
                    SampleRateHz: null,
                    BitsPerSample: null,
                    Channels: null,
                    QualityRank: null,
                    AudioVariant: null,
                    DeezerTrackId: null,
                    Isrc: null,
                    DeezerAlbumId: null,
                    DeezerArtistId: null,
                    SpotifyTrackId: null,
                    SpotifyAlbumId: null,
                    SpotifyArtistId: null,
                    AppleTrackId: null,
                    AppleAlbumId: null,
                    AppleArtistId: null,
                    Source: null,
                    SourceId: null)))
            .OrderBy(track => track.Scan.TrackNo ?? 0)
            .ThenBy(track => track.Scan.Title)
            .ToList();
    }

    public void SaveLocalLibrary(LocalLibrarySnapshot snapshot)
    {
        if (!_repository.IsConfigured)
        {
            _logger.LogWarning("Library DB not configured; local library snapshot was not persisted.");
        }
    }

    public void SaveLastScanInfo(LastScanInfo info)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var scanInfo = new LibraryScanInfo(info.LastRunUtc, info.ArtistCount, info.AlbumCount, info.TrackCount);
        _repository.SaveScanInfoAsync(scanInfo).GetAwaiter().GetResult();
    }

    public LastScanInfo GetLastScanInfo()
    {
        if (!_repository.IsConfigured)
        {
            return new LastScanInfo(null, 0, 0, 0);
        }

        var info = _repository.GetScanInfoAsync().GetAwaiter().GetResult();
        return new LastScanInfo(info.LastRunUtc, info.ArtistCount, info.AlbumCount, info.TrackCount);
    }

    public void AddLog(LibraryLogEntry entry)
    {
        try
        {
            AppendLogToFile(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist activity log entry.");
        }
    }

    public IReadOnlyList<LibraryLogEntry> GetLogs()
    {
        try
        {
            return ReadLogsFromFile();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read activity log file.");
            return Array.Empty<LibraryLogEntry>();
        }
    }

    public void ClearLogs()
    {
        try
        {
            lock (_logLock)
            {
                if (File.Exists(_activityLogPath))
                {
                    File.WriteAllText(_activityLogPath, string.Empty);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to clear activity log file.");
        }
    }

    private void AppendLogToFile(LibraryLogEntry entry)
    {
        var line = $"{entry.TimestampUtc:O}|{entry.Level}|{entry.Message}";
        lock (_logLock)
        {
            File.AppendAllText(_activityLogPath, line + Environment.NewLine);
        }
    }

    private IReadOnlyList<LibraryLogEntry> ReadLogsFromFile()
    {
        if (!File.Exists(_activityLogPath))
        {
            return Array.Empty<LibraryLogEntry>();
        }

        var lines = File.ReadAllLines(_activityLogPath);
        var logs = new List<LibraryLogEntry>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var first = line.IndexOf('|');
            if (first <= 0)
            {
                continue;
            }

            var second = line.IndexOf('|', first + 1);
            if (second <= first)
            {
                continue;
            }

            var timestampText = line[..first];
            var level = line.Substring(first + 1, second - first - 1);
            var message = line[(second + 1)..];
            if (!DateTimeOffset.TryParse(
                    timestampText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var timestamp))
            {
                continue;
            }

            logs.Add(new LibraryLogEntry(timestamp, level, message));
        }

        return logs;
    }


    public string? GetArtistSourceId(long artistId, string source)
    {
        if (!_repository.IsConfigured)
        {
            return null;
        }

        return _repository.GetArtistSourceIdAsync(artistId, source).GetAwaiter().GetResult();
    }


    public bool HasLocalLibraryData()
    {
        if (!_repository.IsConfigured)
        {
            return false;
        }

        return _repository.HasLocalLibraryDataAsync().GetAwaiter().GetResult();
    }

    public IReadOnlyList<OfflineTrackSearchDto> SearchTracksAsync(string likeQuery, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<OfflineTrackSearchDto>();
        }

        return _repository.SearchTracksAsync(likeQuery, cancellationToken).GetAwaiter().GetResult();
    }

    public IReadOnlyList<OfflineAlbumSearchDto> SearchAlbumsAsync(string likeQuery, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<OfflineAlbumSearchDto>();
        }

        return _repository.SearchAlbumsAsync(likeQuery, cancellationToken).GetAwaiter().GetResult();
    }

    public IReadOnlyList<OfflineArtistSearchDto> SearchArtistsAsync(string likeQuery, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<OfflineArtistSearchDto>();
        }

        return _repository.SearchArtistsAsync(likeQuery, cancellationToken).GetAwaiter().GetResult();
    }

    public FolderDto AddFolder(FolderUpsertRequest request)
    {
        if (!_repository.IsConfigured)
        {
            throw new InvalidOperationException("Library DB not configured.");
        }

        return _repository
            .AddFolderAsync(new LibraryRepository.FolderUpsertInput(
                request.RootPath,
                request.DisplayName,
                request.Enabled,
                request.LibraryName,
                request.DesiredQuality,
                request.ConvertEnabled,
                request.ConvertFormat,
                request.ConvertBitrate))
            .GetAwaiter()
            .GetResult();
    }

    public FolderDto? UpdateFolder(long id, FolderUpsertRequest request)
    {
        if (!_repository.IsConfigured)
        {
            return null;
        }

        return _repository
            .UpdateFolderAsync(id, new LibraryRepository.FolderUpsertInput(
                request.RootPath,
                request.DisplayName,
                request.Enabled,
                request.LibraryName,
                request.DesiredQuality,
                request.ConvertEnabled,
                request.ConvertFormat,
                request.ConvertBitrate))
            .GetAwaiter()
            .GetResult();
    }

    public bool DeleteFolder(long id)
    {
        if (!_repository.IsConfigured)
        {
            return false;
        }

        return _repository.DeleteFolderAsync(id).GetAwaiter().GetResult();
    }

    public IReadOnlyList<FolderAliasDto> GetAliases(long folderId)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<FolderAliasDto>();
        }

        return _repository.GetFolderAliasesAsync(folderId).GetAwaiter().GetResult();
    }

    public FolderAliasDto AddAlias(long folderId, string aliasName)
    {
        if (!_repository.IsConfigured)
        {
            throw new InvalidOperationException("Library DB not configured.");
        }

        return _repository.AddFolderAliasAsync(folderId, aliasName).GetAwaiter().GetResult();
    }

    public bool DeleteAlias(long aliasId)
    {
        if (!_repository.IsConfigured)
        {
            return false;
        }

        return _repository.DeleteFolderAliasAsync(aliasId).GetAwaiter().GetResult();
    }

    public sealed record LibraryArtist(long Id, string Name, string? ImagePath, string? BackgroundImagePath);
    public sealed record LibraryAlbum(
        long Id,
        long ArtistId,
        string Title,
        string? PreferredCoverPath,
        IReadOnlyList<string> LocalFolders,
        bool HasAnimatedArtwork = false,
        bool HasStereoVariant = false,
        bool HasAtmosVariant = false,
        int LocalTrackCount = 0,
        int LocalStereoTrackCount = 0,
        int LocalAtmosTrackCount = 0);
    public sealed record LibraryTrack(
        long Id,
        long AlbumId,
        bool AvailableLocally,
        LocalTrackScanDto Scan);

    public sealed record LastScanInfo(DateTimeOffset? LastRunUtc, int ArtistCount, int AlbumCount, int TrackCount);

    public sealed record LibraryLogEntry(DateTimeOffset TimestampUtc, string Level, string Message);
    public sealed record FolderUpsertRequest(
        string RootPath,
        string DisplayName,
        bool Enabled,
        string? LibraryName,
        string DesiredQuality,
        bool ConvertEnabled,
        string? ConvertFormat,
        string? ConvertBitrate);

    public sealed class LocalLibrarySnapshot
    {
        public List<LibraryArtist> Artists { get; set; } = new();
        public List<LibraryAlbum> Albums { get; set; } = new();
        public List<LibraryTrack> Tracks { get; set; } = new();
        public Dictionary<string, List<string>> ArtistGenres { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

}
