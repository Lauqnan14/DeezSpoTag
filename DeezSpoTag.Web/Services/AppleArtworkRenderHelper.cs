namespace DeezSpoTag.Web.Services;

public static class AppleArtworkRenderHelper
{
    private const int UiArtworkMaxSize = 640;
    private const int UiArtworkFallbackSize = 600;

    public static string BuildArtworkUrl(string raw, int? width, int? height)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var resolvedWidth = width.GetValueOrDefault();
        var resolvedHeight = height.GetValueOrDefault();
        if (resolvedWidth <= 0 || resolvedHeight <= 0)
        {
            resolvedWidth = UiArtworkFallbackSize;
            resolvedHeight = UiArtworkFallbackSize;
        }

        var maxDim = Math.Max(resolvedWidth, resolvedHeight);
        var scale = maxDim > UiArtworkMaxSize ? (double)UiArtworkMaxSize / maxDim : 1d;
        var scaledWidth = Math.Max(1, (int)Math.Round(resolvedWidth * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(resolvedHeight * scale));

        return raw.Replace("{w}", scaledWidth.ToString())
            .Replace("{h}", scaledHeight.ToString())
            .Replace("{c}", "cc")
            .Replace("{f}", "jpg");
    }
}
