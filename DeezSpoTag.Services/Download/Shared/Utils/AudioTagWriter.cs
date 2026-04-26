using TagLib;

namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class AudioTagWriter
{
    internal readonly record struct AudioTagData(
        string Title,
        string Artist,
        string Album,
        string AlbumArtist,
        string ReleaseDate,
        int TrackNumber,
        int DiscNumber,
        int TotalTracks,
        string? Isrc);

    public static void WriteBasicTags(
        Tag tag,
        AudioTagData data,
        DeezSpoTag.Core.Models.Settings.TagSettings? tagSettings)
    {
        ApplyWhen(tagSettings?.Title ?? true, () => tag.Title = data.Title);
        ApplyWhen(
            tagSettings?.Artist ?? true,
            () => tag.Performers = string.IsNullOrWhiteSpace(data.Artist) ? Array.Empty<string>() : [data.Artist]);
        ApplyWhen(tagSettings?.Album ?? true, () => tag.Album = data.Album);
        ApplyWhen(
            tagSettings?.AlbumArtist ?? true,
            () => tag.AlbumArtists = string.IsNullOrWhiteSpace(data.AlbumArtist) ? Array.Empty<string>() : [data.AlbumArtist]);
        ApplyWhen((tagSettings?.TrackNumber ?? true) && data.TrackNumber > 0, () => tag.Track = (uint)data.TrackNumber);
        ApplyWhen((tagSettings?.TrackTotal ?? false) && data.TotalTracks > 0, () => tag.TrackCount = (uint)data.TotalTracks);
        ApplyWhen((tagSettings?.DiscNumber ?? true) && data.DiscNumber > 0, () => tag.Disc = (uint)data.DiscNumber);

        if (TryResolveReleaseYear(data.ReleaseDate, out var year))
        {
            ApplyWhen(tagSettings?.Year ?? true, () => tag.Year = year);
        }

        ApplyWhen((tagSettings?.Isrc ?? true) && !string.IsNullOrWhiteSpace(data.Isrc), () => tag.ISRC = data.Isrc);
    }

    public static void WriteCover(Tag tag, byte[]? coverData)
    {
        if (coverData is null || coverData.Length == 0)
        {
            return;
        }

        tag.Pictures =
        [
            new Picture(coverData)
            {
                Type = PictureType.FrontCover,
                MimeType = CoverArtMimeTypeResolver.Resolve(null, coverData)
            }
        ];
    }

    private static void ApplyWhen(bool condition, Action action)
    {
        if (condition)
        {
            action();
        }
    }

    private static bool TryResolveReleaseYear(string releaseDate, out uint year)
    {
        year = 0;
        return !string.IsNullOrWhiteSpace(releaseDate)
            && releaseDate.Length >= 4
            && uint.TryParse(releaseDate[..4], out year);
    }
}
