namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class CoverArtMimeTypeResolver
{
    private const string DefaultMimeType = "image/jpeg";

    public static string Resolve(string? imagePath, byte[]? data)
    {
        var fromPath = ResolveFromPath(imagePath);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        var fromData = ResolveFromData(data);
        return string.IsNullOrWhiteSpace(fromData) ? DefaultMimeType : fromData;
    }

    private static string? ResolveFromPath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var extension = Path.GetExtension(imagePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => null
        };
    }

    private static string? ResolveFromData(byte[]? data)
    {
        if (data is not { Length: > 3 })
        {
            return null;
        }

        if (HasPrefix(data, 0xFF, 0xD8, 0xFF))
        {
            return "image/jpeg";
        }

        if (HasPrefix(data, 0x89, 0x50, 0x4E, 0x47))
        {
            return "image/png";
        }

        if (HasPrefix(data, 0x47, 0x49, 0x46, 0x38))
        {
            return "image/gif";
        }

        if (HasPrefix(data, 0x42, 0x4D))
        {
            return "image/bmp";
        }

        if (HasPrefix(data, 0x49, 0x49, 0x2A, 0x00) || HasPrefix(data, 0x4D, 0x4D, 0x00, 0x2A))
        {
            return "image/tiff";
        }

        if (HasPrefix(data, 0x52, 0x49, 0x46, 0x46) && data.Length > 11
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    private static bool HasPrefix(byte[] data, params byte[] prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
