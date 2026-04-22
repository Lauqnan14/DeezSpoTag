namespace DeezSpoTag.Services.Download.Utils;

public static class DeezerImageUrlParser
{
    public static string? ExtractImageMd5(string? imageUrl, string imageType)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var marker = $"/images/{imageType}/";
        var start = imageUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = imageUrl.IndexOf('/', start);
        if (end <= start)
        {
            return null;
        }

        return imageUrl.Substring(start, end - start);
    }
}
