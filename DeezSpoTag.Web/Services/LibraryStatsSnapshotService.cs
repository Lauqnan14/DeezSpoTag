using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryStatsSnapshotService
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;

    public LibraryStatsSnapshotService(
        LibraryRepository repository,
        LibraryConfigStore configStore)
    {
        _repository = repository;
        _configStore = configStore;
    }

    public async Task<object> BuildStatsPayloadAsync(long? folderId, CancellationToken cancellationToken)
    {
        LibraryScanInfo scanInfo;
        LibraryStatsDto stats;

        if (_repository.IsConfigured)
        {
            scanInfo = await _repository.GetScanInfoAsync(cancellationToken);
            if (folderId.HasValue)
            {
                var totals = await _repository.GetFolderStatsTotalsAsync(folderId.Value, cancellationToken);
                stats = new LibraryStatsDto(
                    totals.Artists,
                    totals.Albums,
                    totals.Tracks,
                    Array.Empty<LibraryStatsLibraryDto>(),
                    totals.VideoItems,
                    totals.PodcastItems,
                    null);
            }
            else
            {
                stats = await _repository.GetLibraryStatsAsync(cancellationToken);
            }
        }
        else
        {
            var lastScan = _configStore.GetLastScanInfo();
            scanInfo = new LibraryScanInfo(lastScan.LastRunUtc, lastScan.ArtistCount, lastScan.AlbumCount, lastScan.TrackCount);
            stats = new LibraryStatsDto(0, 0, 0, Array.Empty<LibraryStatsLibraryDto>(), 0, 0, null);
        }

        return new
        {
            totals = new
            {
                artists = stats.TotalArtists,
                albums = stats.TotalAlbums,
                tracks = stats.TotalTracks,
                videoItems = stats.TotalVideoItems,
                podcastItems = stats.TotalPodcastItems
            },
            lastRunUtc = scanInfo.LastRunUtc,
            libraries = stats.Libraries.Select(library => new
            {
                id = library.LibraryId,
                name = library.Name,
                artists = library.ArtistCount,
                albums = library.AlbumCount,
                tracks = library.TrackCount,
                videoItems = library.VideoItemCount,
                podcastItems = library.PodcastItemCount,
                unmetQuality = library.UnmetQualityCount,
                noLyrics = library.NoLyricsCount,
                folderMix = new
                {
                    music = library.MusicFolderCount,
                    video = library.VideoFolderCount,
                    podcast = library.PodcastFolderCount
                },
                contentType = ResolveLibraryContentType(library)
            }),
            detail = stats.Detail is null ? null : new
            {
                tracksWithLyrics = stats.Detail.TracksWithLyrics,
                tracksWithSyncedLyrics = stats.Detail.TracksWithSyncedLyrics,
                tracksWithUnsyncedLyrics = stats.Detail.TracksWithUnsyncedLyrics,
                tracksWithBothLyrics = stats.Detail.TracksWithBothLyrics,
                albumsWithAnimatedArtwork = stats.Detail.AlbumsWithAnimatedArtwork,
                sourceCoverage = new
                {
                    deezerTrackIds = stats.Detail.SourceCoverage.DeezerTrackIds,
                    spotifyTrackIds = stats.Detail.SourceCoverage.SpotifyTrackIds,
                    appleTrackIds = stats.Detail.SourceCoverage.AppleTrackIds,
                    deezerUrls = stats.Detail.SourceCoverage.DeezerUrls,
                    spotifyUrls = stats.Detail.SourceCoverage.SpotifyUrls,
                    appleUrls = stats.Detail.SourceCoverage.AppleUrls
                },
                breakdowns = new
                {
                    extensions = stats.Detail.Extensions.Select(item => new { value = item.Value, count = item.Count }),
                    bitDepths = stats.Detail.BitDepths.Select(item => new { value = item.Value, count = item.Count }),
                    sampleRates = stats.Detail.SampleRates.Select(item => new { value = item.Value, count = item.Count }),
                    technicalProfiles = stats.Detail.TechnicalProfiles.Select(item => new { value = item.Value, count = item.Count }),
                    lyricsTypes = stats.Detail.LyricsTypes.Select(item => new { value = item.Value, count = item.Count })
                }
            }
        };
    }

    private static string ResolveLibraryContentType(LibraryStatsLibraryDto library)
    {
        var hasMusic = library.TrackCount > 0 || library.MusicFolderCount > 0;
        var hasVideo = library.VideoItemCount > 0 || library.VideoFolderCount > 0;
        var hasPodcast = library.PodcastItemCount > 0 || library.PodcastFolderCount > 0;
        var categories = (hasMusic ? 1 : 0) + (hasVideo ? 1 : 0) + (hasPodcast ? 1 : 0);

        if (categories == 0)
        {
            return "empty";
        }

        if (categories > 1)
        {
            return "mixed";
        }

        if (hasVideo)
        {
            return "video";
        }

        if (hasPodcast)
        {
            return "podcast";
        }

        return "music";
    }
}
