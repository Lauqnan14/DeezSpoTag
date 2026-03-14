using DeezSpoTag.Core.Models;
using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;
using CoreTrack = DeezSpoTag.Core.Models.Track;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class TrackTokenRefreshHelper
{
    public static async Task<bool> RefreshTrackTokenAsync(
        CoreTrack track,
        DeezerClient deezerClient,
        ILogger logger,
        bool includeFileSizes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(track.Id) || track.Id == "0")
            {
                return false;
            }

            var gwTracks = await deezerClient.GetTracksAsync(new List<string> { track.Id });
            var gwTrack = gwTracks.FirstOrDefault();
            if (gwTrack == null || string.IsNullOrWhiteSpace(gwTrack.TrackToken))
            {
                return false;
            }

            track.TrackToken = gwTrack.TrackToken;
            track.TrackTokenExpiration = gwTrack.TrackTokenExpire;
            track.TrackTokenExpire = gwTrack.TrackTokenExpire;
            track.MD5 = gwTrack.Md5Origin;
            track.MediaVersion = gwTrack.MediaVersion.ToString();

            if (includeFileSizes)
            {
                track.FileSizes ??= new Dictionary<string, int>();
                UpdateFileSize(track.FileSizes, "mp3_128", gwTrack.FilesizeMp3128);
                UpdateFileSize(track.FileSizes, "mp3_320", gwTrack.FilesizeMp3320);
                UpdateFileSize(track.FileSizes, "flac", gwTrack.FilesizeFlac);
                UpdateFileSize(track.FileSizes, "mp4_ra1", gwTrack.FilesizeMp4Ra1);
                UpdateFileSize(track.FileSizes, "mp4_ra2", gwTrack.FilesizeMp4Ra2);
                UpdateFileSize(track.FileSizes, "mp4_ra3", gwTrack.FilesizeMp4Ra3);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to refresh track token for track {TrackId}", track.Id);
            return false;
        }
    }

    private static void UpdateFileSize(IDictionary<string, int> fileSizes, string key, int? value)
    {
        if (value.HasValue && value.Value > 0)
        {
            fileSizes[key] = value.Value;
        }
    }
}
