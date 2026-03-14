namespace DeezSpoTag.Web.Services;

internal static class ImageFileExtensionResolver
{
    public static string NormalizeStandardImageExtension(string? extension)
    {
        var value = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            ".png" => ".png",
            ".webp" => ".webp",
            ".bmp" => ".bmp",
            ".jpeg" => ".jpg",
            ".jpg" => ".jpg",
            _ => ".jpg"
        };
    }

    public static string ResolveStandardImageExtension(string? contentType, string? url)
    {
        var fromType = contentType?.Trim().ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(fromType))
        {
            return fromType;
        }

        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return NormalizeStandardImageExtension(Path.GetExtension(uri.AbsolutePath));
        }

        return ".jpg";
    }
}
