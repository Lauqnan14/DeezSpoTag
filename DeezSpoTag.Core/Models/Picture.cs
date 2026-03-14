namespace DeezSpoTag.Core.Models;

/// <summary>
/// Picture model (ported from deezspotag Picture.ts)
/// </summary>
public class Picture
{
    private const string EmptyMd5 = "";

    public string Md5 { get; set; } = EmptyMd5;
    public string Type { get; set; } = EmptyMd5;

    public Picture() { }

    public Picture(string md5, string type)
    {
        Md5 = md5;
        Type = type;
    }

    /// <summary>
    /// Get picture URL for specified size and format (exact port from deezspotag Picture.getURL)
    /// </summary>
    public string GetURL(int size = 1200, string format = "jpg")
    {
        if (string.IsNullOrEmpty(Md5))
        {
            return "";
        }

        var url = $"https://e-cdns-images.dzcdn.net/images/{Type}/{Md5}/{size}x{size}";

        // EXACT PORT from deezspotag Picture.getURL
        if (format.StartsWith("jpg"))
        {
            var quality = 80;
            if (format.Contains('-'))
            {
                var qualityStr = format.Substring(4); // Remove "jpg-" prefix
                if (int.TryParse(qualityStr, out var parsedQuality))
                {
                    quality = parsedQuality;
                }
            }
            return $"{url}-000000-{quality}-0-0.jpg";
        }
        
        if (format == "png")
        {
            return $"{url}-none-100-0-0.png";
        }

        return $"{url}.jpg";
    }

    /// <summary>
    /// Check if this is a static picture (non-Deezer image)
    /// </summary>
    public bool IsStaticPicture()
    {
        return string.IsNullOrEmpty(Md5);
    }
}

/// <summary>
/// Static picture for non-Deezer images
/// </summary>
public class StaticPicture : Picture
{
    public string Url { get; set; }

    public StaticPicture(string url) : base("", "static")
    {
        Url = url;
    }

    public new string GetURL(int size = 1200, string format = "jpg")
    {
        return Url;
    }

}
