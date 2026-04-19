namespace DeezSpoTag.Services.Download.Shared;

public static class DownloadTagSourceHelper
{
    public const string FollowDownloadEngineSource = "engine";
    public const string DeezerSource = "deezer";
    public const string SpotifySource = "spotify";
    public const string AppleSource = "apple";
    public const string QobuzSource = "qobuz";
    public const string TidalSource = "tidal";
    public const string AmazonSource = "amazon";

    public static string NormalizeStoredSource(string? source, string defaultSource = DeezerSource)
    {
        return NormalizeStoredSourceOrNull(source) ?? NormalizeStoredSourceOrNull(defaultSource) ?? DeezerSource;
    }

    public static string? NormalizeResolvedDownloadTagSource(string? source)
    {
        var normalized = source?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized is "itunes" or "applemusic" or "apple-music" or "apple_music")
        {
            return AppleSource;
        }

        return normalized switch
        {
            DeezerSource => DeezerSource,
            SpotifySource => SpotifySource,
            AppleSource => AppleSource,
            QobuzSource => QobuzSource,
            TidalSource => TidalSource,
            AmazonSource => AmazonSource,
            _ => null
        };
    }

    public static string? ResolveDownloadTagSource(string? storedSource, params string?[] engineCandidates)
    {
        var normalizedStoredSource = NormalizeStoredSourceOrNull(storedSource);
        if (normalizedStoredSource == null)
        {
            return null;
        }

        if (!string.Equals(normalizedStoredSource, FollowDownloadEngineSource, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeResolvedDownloadTagSource(normalizedStoredSource);
        }

        foreach (var candidate in engineCandidates)
        {
            var resolved = NormalizeResolvedDownloadTagSource(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? NormalizeStoredSourceOrNull(string? source)
    {
        if (string.Equals(source?.Trim(), FollowDownloadEngineSource, StringComparison.OrdinalIgnoreCase))
        {
            return FollowDownloadEngineSource;
        }

        return NormalizeResolvedDownloadTagSource(source) switch
        {
            DeezerSource => DeezerSource,
            SpotifySource => SpotifySource,
            AppleSource => AppleSource,
            QobuzSource => QobuzSource,
            TidalSource => TidalSource,
            AmazonSource => AmazonSource,
            _ => null
        };
    }
}
