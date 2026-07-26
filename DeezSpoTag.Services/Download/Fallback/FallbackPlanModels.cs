namespace DeezSpoTag.Services.Download.Fallback;

using System.Text.Json;
using System.Text.Json.Nodes;

public sealed record FallbackPlanStep(
    string StepId,
    string Engine,
    string? Quality,
    IReadOnlyList<string> RequiredInputs,
    string ResolutionStrategy);

public sealed record FallbackAttempt(
    string StepId,
    string Status,
    string ErrorClass,
    string Detail);

public static class DownloadExecutionPlan
{
    public static List<FallbackPlanStep> FromEncodedSources(IEnumerable<string> sources)
        => sources
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .Where(static step => !string.IsNullOrWhiteSpace(step.Source))
            .Select((step, index) => new FallbackPlanStep(
                $"step-{index}",
                step.Source,
                step.Quality,
                Array.Empty<string>(),
                "direct_url"))
            .ToList();

    public static List<FallbackPlanStep> Read(JsonObject payload)
    {
        var node = payload["FallbackPlan"] ?? payload["fallbackPlan"];
        if (node is not JsonArray)
        {
            return [];
        }

        try
        {
            return node.Deserialize<List<FallbackPlanStep>>() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static List<FallbackPlanStep> NormalizeForRequest(
        IEnumerable<FallbackPlanStep> plan,
        string? contentType,
        string? quality)
    {
        var atmosRequest = IsAtmosRequest(contentType, quality);
        return plan
            .Where(step => IsAtmosStep(step.Engine, step.Quality) == atmosRequest)
            .Select((step, index) => step with { StepId = $"step-{index}" })
            .ToList();
    }

    public static bool NormalizePersistedRetryPlan(
        JsonObject payload,
        out List<FallbackPlanStep> plan)
    {
        var storedPlan = Read(payload);
        var contentType = ReadString(payload, "ContentType", "contentType");
        var quality = ReadString(payload, "Quality", "quality");
        plan = NormalizeForRequest(storedPlan, contentType, quality);
        if (plan.Count == 0 || PlansMatch(storedPlan, plan))
        {
            return false;
        }

        var firstStep = plan[0];
        payload["FallbackPlan"] = JsonSerializer.SerializeToNode(plan);
        payload.Remove("fallbackPlan");
        payload["Engine"] = firstStep.Engine;
        payload["SourceService"] = firstStep.Engine;
        payload["Quality"] = firstStep.Quality ?? string.Empty;
        payload["AutoIndex"] = 0;
        payload["ResolvedAutoIndex"] = 0;
        payload["ResolvedEngine"] = string.Empty;
        payload["ResolvedQuality"] = string.Empty;
        payload["ResolvedSourceUrl"] = string.Empty;
        payload["ResolutionStatus"] = "pending";
        payload["ResolutionError"] = string.Empty;
        return true;
    }

    public static bool IsAtmosRequest(string? contentType, string? quality)
    {
        var normalizedContentType = contentType?.Trim();
        if (string.Equals(normalizedContentType, "atmos", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalizedContentType, "stereo", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsAtmosQuality(quality);
    }

    private static bool IsAtmosStep(string? engine, string? quality)
        => (string.Equals(engine?.Trim(), "apple", StringComparison.OrdinalIgnoreCase)
                && string.Equals(quality?.Trim(), "ATMOS", StringComparison.OrdinalIgnoreCase))
           || ((string.Equals(engine?.Trim(), "tidal", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(engine?.Trim(), "amazon", StringComparison.OrdinalIgnoreCase))
                && string.Equals(quality?.Trim(), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase));

    private static bool IsAtmosQuality(string? quality)
        => string.Equals(quality?.Trim(), "ATMOS", StringComparison.OrdinalIgnoreCase)
           || string.Equals(quality?.Trim(), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonObject payload, string canonicalName, string legacyName)
        => payload[canonicalName]?.GetValue<string>()
           ?? payload[legacyName]?.GetValue<string>();

    private static bool PlansMatch(
        IReadOnlyList<FallbackPlanStep> stored,
        IReadOnlyList<FallbackPlanStep> normalized)
    {
        if (stored.Count != normalized.Count)
        {
            return false;
        }

        for (var index = 0; index < stored.Count; index++)
        {
            if (!string.Equals(stored[index].StepId, normalized[index].StepId, StringComparison.Ordinal)
                || !string.Equals(stored[index].Engine, normalized[index].Engine, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(stored[index].Quality, normalized[index].Quality, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
