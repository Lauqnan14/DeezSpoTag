using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class ExternalSearchControllerHelpers
{
    private static readonly HashSet<string> DefaultAllowedTypes = new(StringComparer.Ordinal)
    {
        "track",
        "album",
        "artist",
        "playlist"
    };

    public static string ComposeTitle(string? title, string? version)
    {
        var first = (title ?? string.Empty).Trim();
        var second = (version ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}".Trim();
    }

    public static string? NormalizeType(string? type, ISet<string>? allowedTypes = null)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var normalized = type.Trim().ToLowerInvariant();
        var candidateSet = allowedTypes ?? DefaultAllowedTypes;
        return candidateSet.Contains(normalized) ? normalized : null;
    }

    public static IActionResult? ValidateQuery(string query)
    {
        return string.IsNullOrWhiteSpace(query)
            ? new BadRequestObjectResult(new { available = false, error = "Query is required." })
            : null;
    }

    public static int NormalizeLimit(int limit, int min = 1, int max = 50)
    {
        return Math.Clamp(limit, min, max);
    }

    public static Dictionary<string, int> BuildTotals(int tracks, int albums, int artists, int playlists)
    {
        return new Dictionary<string, int>
        {
            ["tracks"] = tracks,
            ["albums"] = albums,
            ["artists"] = artists,
            ["playlists"] = playlists
        };
    }

    public static bool TryPrepareSearchRequest(
        string query,
        string? type,
        int limit,
        out string? normalizedType,
        out int normalizedLimit,
        out IActionResult? errorResult,
        ISet<string>? allowedTypes = null)
    {
        errorResult = ValidateQuery(query);
        normalizedType = NormalizeType(type, allowedTypes);
        normalizedLimit = NormalizeLimit(limit);
        return errorResult == null;
    }

    public static async Task<IActionResult> RunSearchAsync(
        string query,
        string? type,
        int limit,
        ILogger logger,
        string failureMessage,
        Func<string?, int, CancellationToken, Task<object>> runSearchAsync,
        CancellationToken cancellationToken)
    {
        if (!TryPrepareSearchRequest(
                query,
                type,
                limit,
                out var normalizedType,
                out var normalizedLimit,
                out var errorResult))
        {
            return errorResult!;
        }

        try
        {
            var payload = await runSearchAsync(normalizedType, normalizedLimit, cancellationToken);
            return new OkObjectResult(payload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "External search failed for query {Query}", query);
            return new ObjectResult(new { available = false, error = failureMessage }) { StatusCode = 500 };
        }
    }
}
