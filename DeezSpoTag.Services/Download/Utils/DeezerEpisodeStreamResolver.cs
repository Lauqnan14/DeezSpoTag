using Newtonsoft.Json.Linq;

namespace DeezSpoTag.Services.Download.Utils;

public static class DeezerEpisodeStreamResolver
{
    public static string? ResolveStreamUrl(
        JObject showPage,
        string episodeId,
        bool includeLinkFallback,
        bool rejectDeezerEpisodePages)
    {
        var results = showPage["results"] as JObject ?? showPage;
        var episodes = results["EPISODES"] as JObject ?? results["episodes"] as JObject;
        var episodesData = episodes?["data"] as JArray ?? episodes?["DATA"] as JArray;
        if (episodesData == null)
        {
            return null;
        }

        foreach (var episodeToken in episodesData)
        {
            if (episodeToken is not JObject episode)
            {
                continue;
            }

            var id = episode.Value<string>("EPISODE_ID")
                     ?? episode.Value<string>("id")
                     ?? string.Empty;
            if (!string.Equals(id, episodeId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var streamUrl = episode.Value<string>("EPISODE_DIRECT_STREAM_URL")
                            ?? episode.Value<string>("EPISODE_URL");
            if (includeLinkFallback && string.IsNullOrWhiteSpace(streamUrl))
            {
                streamUrl = episode.Value<string>("link");
            }

            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                return null;
            }

            if (rejectDeezerEpisodePages && IsDeezerEpisodePage(streamUrl))
            {
                return null;
            }

            return streamUrl;
        }

        return null;
    }

    private static bool IsDeezerEpisodePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Contains("/episode", StringComparison.OrdinalIgnoreCase);
    }
}
