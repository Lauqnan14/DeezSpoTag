namespace DeezSpoTag.Services.Download.Shared;

public static class DownloadTagSourceHelper
{
    public const string FollowDownloadEngineSource = "engine";
    public const string DeezerSource = "deezer";
    public const string SpotifySource = "spotify";
    public const string QobuzSource = "qobuz";

    public static string NormalizeStoredSource(string? source, string defaultSource = DeezerSource)
    {
        return NormalizeStoredSourceOrNull(source) ?? NormalizeStoredSourceOrNull(defaultSource) ?? DeezerSource;
    }

    public static string? NormalizeResolvedDownloadTagSource(string? source)
    {
        return source?.Trim().ToLowerInvariant() switch
        {
            DeezerSource => DeezerSource,
            SpotifySource => SpotifySource,
            QobuzSource => QobuzSource,
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
        return source?.Trim().ToLowerInvariant() switch
        {
            FollowDownloadEngineSource => FollowDownloadEngineSource,
            DeezerSource => DeezerSource,
            SpotifySource => SpotifySource,
            QobuzSource => QobuzSource,
            _ => null
        };
    }
}
