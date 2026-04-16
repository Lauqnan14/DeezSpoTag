using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class AudioFileTaggingHelper
{
    internal readonly record struct AudioTagDataInput(
        string Title,
        string Artist,
        string Album,
        string AlbumArtist,
        string ReleaseDate,
        int TrackNumber,
        int DiscNumber,
        int TotalTracks,
        string? Isrc);

    internal readonly record struct AudioTaggingRequest(
        ILogger Logger,
        string EngineName,
        HttpClient HttpClient,
        string FilePath,
        AudioTagWriter.AudioTagData TagData,
        string CoverUrl,
        bool EmbedMaxQualityCover,
        DeezSpoTag.Core.Models.Settings.TagSettings? TagSettings);

    public static AudioTagWriter.AudioTagData CreateTagData(AudioTagDataInput input)
    {
        return new AudioTagWriter.AudioTagData(
            input.Title,
            input.Artist,
            input.Album,
            input.AlbumArtist,
            input.ReleaseDate,
            input.TrackNumber,
            input.DiscNumber,
            input.TotalTracks,
            input.Isrc);
    }

    public static async Task<bool> TryTagAsync(AudioTaggingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var file = TagLib.File.Create(request.FilePath);
            var tag = file.Tag;

            AudioTagWriter.WriteBasicTags(tag, request.TagData, request.TagSettings);

            if (request.TagSettings?.Cover ?? true)
            {
                var coverData = await CoverArtDownloader.TryDownloadAsync(
                    request.HttpClient,
                    request.CoverUrl,
                    request.EmbedMaxQualityCover,
                    cancellationToken);
                if (coverData is { Length: > 0 })
                {
                    AudioTagWriter.WriteCover(tag, coverData);
                }
            }

            file.Save();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogWarning(ex, "Failed to tag {Engine} download at {Path}", request.EngineName, request.FilePath);
            return false;
        }
    }
}
