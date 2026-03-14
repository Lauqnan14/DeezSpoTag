using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Shared;

internal static class RequestBuilderCommon
{
    private const string PlaylistType = "playlist";
    private const string ArtistType = "artist";
    private const string DefaultFilenameTemplate = "{title} - {artist}";

    public static string ResolveOutputDirectory(
        string baseOutputDir,
        string collectionType,
        string collectionName,
        bool preferAlbumLayoutForPlaylists)
    {
        var outputDir = baseOutputDir;
        var isCollection = string.Equals(collectionType, PlaylistType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(collectionType, ArtistType, StringComparison.OrdinalIgnoreCase);
        var shouldUseCollectionFolder = isCollection
            && !string.IsNullOrWhiteSpace(collectionName)
            && (!string.Equals(collectionType, PlaylistType, StringComparison.OrdinalIgnoreCase)
                || !preferAlbumLayoutForPlaylists);
        if (shouldUseCollectionFolder)
        {
            outputDir = DownloadPathResolver.Combine(outputDir, SanitizePathSegment(collectionName));
        }

        return DownloadPathResolver.ResolveIoPath(outputDir);
    }

    public static string NormalizeFilenameTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return DefaultFilenameTemplate;
        }

        return template
            .Replace("%title%", "{title}", StringComparison.OrdinalIgnoreCase)
            .Replace("%artist%", "{artist}", StringComparison.OrdinalIgnoreCase)
            .Replace("%album%", "{album}", StringComparison.OrdinalIgnoreCase)
            .Replace("%albumartist%", "{album_artist}", StringComparison.OrdinalIgnoreCase)
            .Replace("%album_artist%", "{album_artist}", StringComparison.OrdinalIgnoreCase)
            .Replace("%tracknumber%", "{track}", StringComparison.OrdinalIgnoreCase)
            .Replace("%discnumber%", "{disc}", StringComparison.OrdinalIgnoreCase)
            .Replace("%year%", "{year}", StringComparison.OrdinalIgnoreCase);
    }

    public static void PopulateCommonFields(
        EngineDownloadRequestBase request,
        EngineQueueItemBase item,
        DeezSpoTagSettings settings)
    {
        request.OutputDir = ResolveOutputDirectory(
            settings.DownloadLocation,
            item.CollectionType,
            item.CollectionName,
            settings.PreferAlbumLayoutForPlaylists);
        request.FilenameFormat = NormalizeFilenameTemplate(settings.TracknameTemplate);
        request.IncludeTrackNumber = settings.PadTracks;
        request.Position = item.Position;
        request.UseAlbumTrackNumber = item.UseAlbumTrackNumber;
        request.TrackName = item.Title;
        request.ArtistName = item.Artist;
        request.AlbumName = item.Album;
        request.AlbumArtist = item.AlbumArtist;
        request.ReleaseDate = item.ReleaseDate;
        request.CoverUrl = item.Cover;
        request.Isrc = item.Isrc;
        request.DurationSeconds = item.DurationSeconds;
        request.SpotifyTrackNumber = item.SpotifyTrackNumber;
        request.SpotifyDiscNumber = item.SpotifyDiscNumber;
        request.SpotifyTotalTracks = item.SpotifyTotalTracks;
        request.SpotifyId = item.SpotifyId;
        request.ServiceUrl = item.SourceUrl;
    }

    public static TRequest CreateCommonRequest<TRequest>(EngineQueueItemBase item, DeezSpoTagSettings settings)
        where TRequest : EngineDownloadRequestBase, new()
    {
        var request = new TRequest();
        PopulateCommonFields(request, item, settings);
        return request;
    }

    public static string ResolvePreferredQuality(string? itemQuality, string? settingsQuality, string fallbackQuality)
    {
        if (!string.IsNullOrWhiteSpace(itemQuality))
        {
            return itemQuality;
        }

        if (!string.IsNullOrWhiteSpace(settingsQuality))
        {
            return settingsQuality;
        }

        return fallbackQuality;
    }

    private static string SanitizePathSegment(string value)
    {
        return CjkFilenameSanitizer.SanitizeSegment(
            value,
            fallback: string.Empty,
            replacement: "_",
            collapseWhitespace: true,
            trimTrailingDotsAndSpaces: true);
    }
}
