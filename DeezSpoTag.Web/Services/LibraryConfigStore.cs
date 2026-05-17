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

    public async Task<LibrarySettingsDto> GetSettingsAsync()
    {
        if (!_repository.IsConfigured)
        {
            return new LibrarySettingsDto(false, false);
        }

        return await _repository.GetSettingsAsync();
    }

    public async Task<LibrarySettingsDto> SaveSettingsAsync(LibrarySettingsDto settings)
    {
        if (!_repository.IsConfigured)
        {
            return settings;
        }

        return await _repository.UpdateSettingsAsync(settings);
    }

    public async Task<IReadOnlyList<FolderDto>> GetFoldersAsync()
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<FolderDto>();
        }

        return await _repository.GetFoldersAsync();
    }

    public async Task<IReadOnlyList<LibraryArtist>> GetLocalArtistsAsync()
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<LibraryArtist>();
        }

        var artists = await _repository.GetArtistsAsync("all");
        return artists
            .Select(artist => new LibraryArtist(artist.Id, artist.Name, artist.PreferredImagePath, artist.PreferredBackgroundPath))
            .OrderBy(artist => artist.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<LibraryAlbum>> GetLocalAlbumsAsync(long artistId)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<LibraryAlbum>();
        }

        var albums = await _repository.GetArtistAlbumsAsync(artistId);
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

    public async Task<LibraryAlbum?> GetLocalAlbumAsync(long albumId)
    {
        if (!_repository.IsConfigured)
        {
            return null;
        }

        var album = await _repository.GetAlbumAsync(albumId);
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

    public async Task<IReadOnlyList<LibraryTrack>> GetLocalTracksAsync(long albumId)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<LibraryTrack>();
        }

        var tracks = await _repository.GetAlbumTracksAsync(albumId);
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

    public async Task SaveLastScanInfoAsync(LastScanInfo info)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var scanInfo = new LibraryScanInfo(info.LastRunUtc, info.ArtistCount, info.AlbumCount, info.TrackCount);
        await _repository.SaveScanInfoAsync(scanInfo);
    }

    public async Task<LastScanInfo> GetLastScanInfoAsync()
    {
        if (!_repository.IsConfigured)
        {
            return new LastScanInfo(null, 0, 0, 0);
        }

        var info = await _repository.GetScanInfoAsync();
        return new LastScanInfo(info.LastRunUtc, info.ArtistCount, info.AlbumCount, info.TrackCount);
    }

    // Compatibility shims for synchronous consumers; migrate callsites to async progressively.
    public LibrarySettingsDto GetSettings() => GetSettingsAsync().GetAwaiter().GetResult();
    public LibrarySettingsDto SaveSettings(LibrarySettingsDto settings) => SaveSettingsAsync(settings).GetAwaiter().GetResult();
    public IReadOnlyList<FolderDto> GetFolders() => GetFoldersAsync().GetAwaiter().GetResult();
    public IReadOnlyList<LibraryArtist> GetLocalArtists() => GetLocalArtistsAsync().GetAwaiter().GetResult();
    public IReadOnlyList<LibraryAlbum> GetLocalAlbums(long artistId) => GetLocalAlbumsAsync(artistId).GetAwaiter().GetResult();
    public LibraryAlbum? GetLocalAlbum(long albumId) => GetLocalAlbumAsync(albumId).GetAwaiter().GetResult();
    public IReadOnlyList<LibraryTrack> GetLocalTracks(long albumId) => GetLocalTracksAsync(albumId).GetAwaiter().GetResult();
    public void SaveLastScanInfo(LastScanInfo info) => SaveLastScanInfoAsync(info).GetAwaiter().GetResult();
    public LastScanInfo GetLastScanInfo() => GetLastScanInfoAsync().GetAwaiter().GetResult();

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


    public async Task<string?> GetArtistSourceIdAsync(long artistId, string source)
    {
        if (!_repository.IsConfigured)
        {
            return null;
        }

        return await _repository.GetArtistSourceIdAsync(artistId, source);
    }


    public async Task<bool> HasLocalLibraryDataAsync()
    {
        if (!_repository.IsConfigured)
        {
            return false;
        }

        return await _repository.HasLocalLibraryDataAsync();
    }

    public async Task<IReadOnlyList<OfflineTrackSearchDto>> SearchTracksAsync(string likeQuery, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<OfflineTrackSearchDto>();
        }

        return await _repository.SearchTracksAsync(likeQuery, cancellationToken);
    }

    public async Task<IReadOnlyList<OfflineAlbumSearchDto>> SearchAlbumsAsync(string likeQuery, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<OfflineAlbumSearchDto>();
        }

        return await _repository.SearchAlbumsAsync(likeQuery, cancellationToken);
    }

    public async Task<IReadOnlyList<OfflineArtistSearchDto>> SearchArtistsAsync(string likeQuery, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<OfflineArtistSearchDto>();
        }

        return await _repository.SearchArtistsAsync(likeQuery, cancellationToken);
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

    public async Task<bool> DeleteFolderAsync(long id)
    {
        if (!_repository.IsConfigured)
        {
            return false;
        }

        return await _repository.DeleteFolderAsync(id);
    }

    public async Task<IReadOnlyList<FolderAliasDto>> GetAliasesAsync(long folderId)
    {
        if (!_repository.IsConfigured)
        {
            return Array.Empty<FolderAliasDto>();
        }

        return await _repository.GetFolderAliasesAsync(folderId);
    }

    public async Task<FolderAliasDto> AddAliasAsync(long folderId, string aliasName)
    {
        if (!_repository.IsConfigured)
        {
            throw new InvalidOperationException("Library DB not configured.");
        }

        return await _repository.AddFolderAliasAsync(folderId, aliasName);
    }

    public async Task<bool> DeleteAliasAsync(long aliasId)
    {
        if (!_repository.IsConfigured)
        {
            return false;
        }

        return await _repository.DeleteFolderAliasAsync(aliasId);
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
