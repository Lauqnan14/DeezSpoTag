namespace DeezSpoTag.Services.Download.Shared;

public static class ArtworkSizePolicy
{
    public const int DefaultRequestSize = 1200;

    private const int SpotifyMaxSize = 640;
    private const int DeezerMaxSize = 1000;
    private const int QobuzMaxSize = 999;
    private const int LastFmMaxSize = 500;

    public static int ResolveRequestSize(int desiredSize, string? provider)
    {
        var desired = desiredSize > 0 ? desiredSize : DefaultRequestSize;
        var ceiling = ResolveProviderCeiling(provider);
        return ceiling <= 0 ? desired : Math.Min(desired, ceiling);
    }

    public static int ResolveProviderCeiling(string? provider)
        => provider?.Trim().ToLowerInvariant() switch
        {
            "spotify" => SpotifyMaxSize,
            "deezer" => DeezerMaxSize,
            "qobuz" => QobuzMaxSize,
            "lastfm" or "last.fm" => LastFmMaxSize,
            _ => 0
        };

    public static bool ServesBestAvailable(string? provider)
        => ResolveProviderCeiling(provider) <= 0;
}
