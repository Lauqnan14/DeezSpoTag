using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Apple;

public static class AppleIdParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex StationIdRegex = new(
        @"ra\.[A-Za-z0-9\-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    public static string? Resolve(string? explicitId, string? appleUrl)
    {
        var resolvedFromUrl = TryExtractFromUrl(appleUrl);
        if (!string.IsNullOrWhiteSpace(resolvedFromUrl))
        {
            return resolvedFromUrl;
        }

        return string.IsNullOrWhiteSpace(explicitId) ? null : explicitId.Trim();
    }

    public static string? TryExtractFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var stationMatch = StationIdRegex.Match(url);
        if (stationMatch.Success)
        {
            return stationMatch.Value;
        }

        try
        {
            return TryExtractFromUri(new Uri(url));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public static string? TryExtractFromUri(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var queryId = query.Get("i");
        if (!string.IsNullOrWhiteSpace(queryId)
            && (long.TryParse(queryId, out _) || queryId.StartsWith("ra.", StringComparison.OrdinalIgnoreCase)))
        {
            return queryId;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            var segment = segments[index];
            if (long.TryParse(segment, out _) || segment.StartsWith("ra.", StringComparison.OrdinalIgnoreCase))
            {
                return segment;
            }
        }

        return null;
    }
}
