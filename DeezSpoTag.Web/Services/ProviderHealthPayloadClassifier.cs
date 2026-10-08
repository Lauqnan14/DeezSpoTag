using System.Text.Json;
using DeezSpoTag.Core.Constants;

namespace DeezSpoTag.Web.Services;

/// <summary>
///     Turns a provider's health-check response into one of the
///     <see cref="ProviderHealthStatus" /> values, or null when the provider is healthy.
/// </summary>
/// <remarks>
///     Amazon, Qobuz and Tidal each carried a byte-identical copy of this classification. It is the
///     decision that decides whether a provider is written to at all, so three copies is three
///     chances to drift: a provider that one registry considered healthy and another considered
///     throttled would be used and skipped inconsistently. There is one implementation here and all
///     three registries call it.
///     <para>
///         Only the classification is shared. What each registry does with the verdict - its own
///         cooldown length, its own failure messages, and Qobuz's extra categories - stays with that
///         registry, because those are genuinely per-provider decisions.
///     </para>
/// </remarks>
internal static class ProviderHealthPayloadClassifier
{
    /// <summary>
    ///     Classifies a parsed health-check body, optionally reading a per-service sub-object first.
    /// </summary>
    /// <param name="root">The parsed response body.</param>
    /// <param name="serviceKey">
    ///     The provider's own key inside a "services" object, when the payload groups by service.
    ///     Null or blank reads the top-level status instead.
    /// </param>
    /// <returns>A <see cref="ProviderHealthStatus" /> value, or null when the provider is healthy.</returns>
    internal static string? Classify(JsonElement root, string? serviceKey)
    {
        if (!string.IsNullOrWhiteSpace(serviceKey)
            && root.TryGetProperty("services", out var services)
            && services.ValueKind == JsonValueKind.Object
            && services.TryGetProperty(serviceKey, out var service))
        {
            if (service.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return ok.GetBoolean() ? null : ProviderHealthStatus.Offline;
            }

            if (service.TryGetProperty("status", out var serviceStatus))
            {
                return ClassifyStatusValue(serviceStatus);
            }
        }

        return root.TryGetProperty("status", out var status) ? ClassifyStatusValue(status) : null;
    }

    /// <summary>
    ///     Classifies a single status value, which is either an HTTP status code or a health word.
    /// </summary>
    /// <remarks>
    ///     The vocabulary a provider reports is not ours, so those words stay raw literals here -
    ///     they are the wire format being parsed, and each appears fewer than three times in this
    ///     one method. The values returned are ours and come from the shared constants.
    /// </remarks>
    internal static string? ClassifyStatusValue(JsonElement status)
    {
        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code))
        {
            return ClassifyHttpStatusCode(code);
        }

        var value = status.GetString()?.Trim().ToLowerInvariant();
        return value switch
        {
            null or "" or "ok" or "up" or ProviderHealthStatus.Online or "healthy" or "operational"
                or "pass" or "passing" => null,
            ProviderHealthStatus.Degraded or "partial" or "warning" or "warn" => ProviderHealthStatus.Transient,
            "down" or ProviderHealthStatus.Offline or "error" or "failed" or "fail" or "unhealthy"
                => ProviderHealthStatus.Offline,
            _ => null,
        };
    }

    private static string? ClassifyHttpStatusCode(int code) => code switch
    {
        >= 200 and < 300 => null,
        429 => ProviderHealthStatus.RateLimited,
        >= 500 => ProviderHealthStatus.Transient,
        _ => ProviderHealthStatus.Offline,
    };
}
