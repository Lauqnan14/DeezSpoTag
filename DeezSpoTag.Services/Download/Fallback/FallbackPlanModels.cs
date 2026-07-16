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
}
