using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class PathTemplateCommon
{
    internal static string CollapseRepeatedLeadingArtistPrefix(string filename, string template, string normalizedMainArtist)
    {
        if (string.IsNullOrWhiteSpace(filename)
            || string.IsNullOrWhiteSpace(normalizedMainArtist)
            || !template.Contains("%artist%", StringComparison.OrdinalIgnoreCase)
            || !template.Contains("%title%", StringComparison.OrdinalIgnoreCase))
        {
            return filename;
        }

        var prefix = $"{normalizedMainArtist} - ";
        if (!filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return filename;
        }

        var remainder = filename[prefix.Length..];
        while (remainder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder[prefix.Length..];
        }

        return prefix + remainder;
    }

    internal static bool ShouldCreatePlaylistFolder(Track track, DeezSpoTagSettings settings)
    {
        return settings.CreatePlaylistFolder &&
               track.Playlist != null &&
               !settings.Tags.SavePlaylistAsCompilation;
    }

    internal static bool ShouldCreateArtistFolder(Track track, DeezSpoTagSettings settings)
    {
        return settings.CreateArtistFolder &&
               (track.Playlist == null ||
                settings.Tags.SavePlaylistAsCompilation ||
                settings.CreateStructurePlaylist);
    }

    internal static string ApplyTrackIdTokens(string filename, Track track)
    {
        var value = filename.Replace("%track_id%", track.Id ?? string.Empty);
        return value.Replace("%artist_id%", track.MainArtist?.Id ?? string.Empty);
    }

    internal static string ApplyPlaylistPositionTokens(
        string filename,
        Track track,
        DeezSpoTagSettings settings,
        bool clearPlaylistIdWhenAlbumMissing)
    {
        if (track.Playlist != null)
        {
            var withPlaylist = filename.Replace("%playlist_id%", track.Playlist.Id ?? string.Empty);
            return withPlaylist.Replace("%position%", DownloadUtils.Pad(track.Position ?? 0, track.Playlist.TrackTotal, settings));
        }

        if (track.Album != null)
        {
            var withEmptyPlaylist = filename.Replace("%playlist_id%", string.Empty);
            return withEmptyPlaylist.Replace("%position%", DownloadUtils.Pad(track.TrackNumber, track.Album.TrackTotal, settings));
        }

        return clearPlaylistIdWhenAlbumMissing
            ? filename.Replace("%playlist_id%", string.Empty)
            : filename;
    }
}
