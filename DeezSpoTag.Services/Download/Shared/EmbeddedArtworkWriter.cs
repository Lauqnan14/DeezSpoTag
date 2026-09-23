using ATL;
using DeezSpoTag.Services.Download.Shared.Utils;
using SixLabors.ImageSharp;
using TagLib;
using AtlPictureInfo = ATL.PictureInfo;
using AtlTagType = ATL.AudioData.MetaDataIOFactory.TagType;

namespace DeezSpoTag.Services.Download.Shared;

public static class EmbeddedArtworkWriter
{
    public static void WriteAndVerify(string audioPath, ArtworkVariant artwork)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentNullException.ThrowIfNull(artwork);
        using (var image = Image.Load(artwork.Bytes))
        {
            if (image.Width != artwork.Width || image.Height != artwork.Height)
                throw new InvalidDataException("Prepared artwork dimensions do not match its payload.");
        }

        var extension = Path.GetExtension(audioPath);
        if (extension is ".m4a" or ".m4b" or ".mp4")
        {
            var track = new ATL.Track(audioPath);
            track.EmbeddedPictures.Clear();
            track.EmbeddedPictures.Add(AtlPictureInfo.fromBinaryData(
                artwork.Bytes, AtlPictureInfo.PIC_TYPE.Front, AtlTagType.NATIVE, null, 0));
            if (!track.Save(AtlTagType.NATIVE)) throw new IOException("ATL failed to save embedded artwork.");
        }
        else
        {
            using var file = TagLib.File.Create(audioPath);
            file.Tag.Pictures =
            [
                new Picture(artwork.Bytes)
                {
                    Type = PictureType.FrontCover,
                    MimeType = CoverArtMimeTypeResolver.Resolve(null, artwork.Bytes),
                    Description = "Cover"
                }
            ];
            file.Save();
        }

        var persisted = Read(audioPath);
        if (persisted is not { Length: > 0 }) throw new IOException("Embedded artwork was not persisted.");
        using var verified = Image.Load(persisted);
        if (verified.Width != artwork.Width || verified.Height != artwork.Height)
            throw new IOException("Embedded artwork dimensions changed during persistence.");
    }

    private static byte[]? Read(string audioPath)
    {
        var extension = Path.GetExtension(audioPath);
        if (extension is ".m4a" or ".m4b" or ".mp4")
            return new ATL.Track(audioPath).EmbeddedPictures.FirstOrDefault()?.PictureData;
        using var file = TagLib.File.Create(audioPath);
        return file.Tag.Pictures?.FirstOrDefault()?.Data?.Data;
    }
}
