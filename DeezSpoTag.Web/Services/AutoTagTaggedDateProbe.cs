using DeezSpoTag.Core.Utils;
using TagLib;

namespace DeezSpoTag.Web.Services;

internal static class AutoTagTaggedDateProbe
{
    private const string TaggedDateTag = "1T_TAGGEDDATE";

    internal static bool HasTaggedDate(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            using var file = TagLib.File.Create(filePath);
            return HasTaggedDate(file, Path.GetExtension(filePath));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    internal static bool HasTaggedDate(TagLib.File file, string extension)
    {
        try
        {
            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
                return id3 != null && TagRawProbe.HasId3Raw(id3, TaggedDateTag);
            }

            if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
            {
                var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
                return vorbis != null && TagRawProbe.HasVorbisRaw(vorbis, TaggedDateTag);
            }

            if (AtlTagHelper.IsMp4Family(extension))
            {
                var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
                return apple != null && TagRawProbe.HasAppleDashBox(apple, Mp4RawTagNameNormalizer.Normalize(TaggedDateTag));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return false;
    }
}
