using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Apple;

public static class AppleVideoClassifier
{
    public static bool IsVideoUrl(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        return sourceUrl.Contains("/music-video/", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains("/music-videos/", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains("/video/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoCollectionType(string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(collectionType))
        {
            return false;
        }

        return collectionType.Equals("music-video", StringComparison.OrdinalIgnoreCase)
            || collectionType.Equals("music-videos", StringComparison.OrdinalIgnoreCase)
            || collectionType.Equals("video", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        return contentType.Equals(DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
            || contentType.Equals("video", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideo(string? sourceUrl, string? collectionType = null, string? contentType = null, bool explicitFlag = false)
    {
        return explicitFlag
            || IsVideoContentType(contentType)
            || IsVideoCollectionType(collectionType)
            || IsVideoUrl(sourceUrl);
    }
}
