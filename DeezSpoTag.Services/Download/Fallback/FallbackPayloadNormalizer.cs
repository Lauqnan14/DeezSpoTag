using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Fallback;

public static class FallbackPayloadNormalizer
{
    public sealed record CanonicalFallbackState(
        List<string> AutoSources,
        List<FallbackPlanStep> FallbackPlan,
        DownloadSourceOrder.AutoSourceStep FirstStep);

    public static CanonicalFallbackState ResolveCanonicalState(
        DownloadQueueItem item,
        DeezSpoTagSettings settings,
        JsonObject payloadObj)
    {
        var payloadContentType = ReadString(payloadObj, "ContentType");
        var payloadQuality = ReadString(payloadObj, "Quality");
        var payloadAutoSources = ReadStringArray(payloadObj, "AutoSources");
        var payloadFallbackPlan = ReadFallbackPlan(payloadObj);
        var contentType = string.IsNullOrWhiteSpace(item.ContentType) ? payloadContentType : item.ContentType;

        if (IsVideoPayload(contentType, payloadQuality, payloadObj))
        {
            var firstStep = new DownloadSourceOrder.AutoSourceStep("apple", DownloadContentTypes.Video);
            return BuildSingleStepFallback(firstStep, "direct_url");
        }

        if (Shared.DownloadEngineSettingsHelper.IsAtmosOnlyPayload(contentType, payloadQuality))
        {
            var firstStep = new DownloadSourceOrder.AutoSourceStep("apple", "ATMOS");
            return BuildSingleStepFallback(firstStep, "direct_url");
        }

        if (payloadFallbackPlan.Count > 0)
        {
            var normalizedAutoSources = payloadFallbackPlan
                .Where(step => !string.IsNullOrWhiteSpace(step.Engine))
                .Select(step => DownloadSourceOrder.EncodeAutoSource(step.Engine, step.Quality))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedAutoSources.Count > 0)
            {
                var firstStep = DecodeOrDefault(normalizedAutoSources[0], item.Engine, payloadQuality);
                return new CanonicalFallbackState(normalizedAutoSources, payloadFallbackPlan, firstStep);
            }
        }

        if (payloadAutoSources.Count > 0)
        {
            var firstStep = DecodeOrDefault(payloadAutoSources[0], item.Engine, payloadQuality);
            var fallbackPlan = payloadAutoSources
                .Select((source, index) =>
                {
                    var step = DownloadSourceOrder.DecodeAutoSource(source);
                    var engine = string.IsNullOrWhiteSpace(step.Source) ? item.Engine ?? "deezer" : step.Source;
                    return new FallbackPlanStep(
                        StepId: $"step-{index}",
                        Engine: engine,
                        Quality: step.Quality,
                        RequiredInputs: Array.Empty<string>(),
                        ResolutionStrategy: "direct_url");
                })
                .ToList();
            return new CanonicalFallbackState(payloadAutoSources, fallbackPlan, firstStep);
        }

        var resolvedAutoSources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);
        var resolvedFirstStep = resolvedAutoSources.Count > 0
            ? DownloadSourceOrder.DecodeAutoSource(resolvedAutoSources[0])
            : new DownloadSourceOrder.AutoSourceStep(item.Engine ?? "deezer", null);
        var resolvedFallbackPlan = resolvedAutoSources
            .Select((source, index) =>
            {
                var step = DownloadSourceOrder.DecodeAutoSource(source);
                var engine = string.IsNullOrWhiteSpace(step.Source) ? item.Engine ?? "deezer" : step.Source;
                return new FallbackPlanStep(
                    StepId: $"step-{index}",
                    Engine: engine,
                    Quality: step.Quality,
                    RequiredInputs: Array.Empty<string>(),
                    ResolutionStrategy: "direct_url");
            })
            .ToList();
        return new CanonicalFallbackState(resolvedAutoSources, resolvedFallbackPlan, resolvedFirstStep);
    }

    public static bool ApplyCanonicalState(JsonObject payloadObj, CanonicalFallbackState state, bool resetIndexAndHistory)
    {
        var changed = false;
        changed |= SetStringArray(payloadObj, "AutoSources", state.AutoSources);
        changed |= SetFallbackPlan(payloadObj, state.FallbackPlan);

        if (resetIndexAndHistory)
        {
            changed |= SetInt(payloadObj, "AutoIndex", 0);
            changed |= SetString(payloadObj, "Engine", state.FirstStep.Source);
            changed |= SetString(payloadObj, "SourceService", state.FirstStep.Source);
            changed |= SetEmptyArray(payloadObj, "FallbackHistory");
            changed |= SetBool(payloadObj, "FallbackQueuedExternally", false);
            if (!string.IsNullOrWhiteSpace(state.FirstStep.Quality))
            {
                changed |= SetString(payloadObj, "Quality", state.FirstStep.Quality);
            }
        }

        return changed;
    }

    public static List<FallbackPlanStep> ReadFallbackPlan(JsonObject payloadObj)
    {
        if (payloadObj["FallbackPlan"] is not JsonArray planArray)
        {
            return new List<FallbackPlanStep>();
        }

        var steps = new List<FallbackPlanStep>();
        foreach (var node in planArray)
        {
            if (node is not JsonObject stepObj)
            {
                continue;
            }

            var engine = ReadString(stepObj, "Engine");
            if (string.IsNullOrWhiteSpace(engine))
            {
                continue;
            }

            var stepId = ReadString(stepObj, "StepId") ?? $"step-{steps.Count}";
            var quality = ReadString(stepObj, "Quality");
            var resolutionStrategy = ReadString(stepObj, "ResolutionStrategy") ?? "direct_url";
            var requiredInputs = ReadStringArray(stepObj, "RequiredInputs");
            steps.Add(new FallbackPlanStep(stepId, engine, quality, requiredInputs, resolutionStrategy));
        }

        return steps;
    }

    public static List<FallbackPlanStep> BuildDirectUrlPlanFromAutoSources(IReadOnlyList<string> autoSources)
    {
        if (autoSources == null || autoSources.Count == 0)
        {
            return new List<FallbackPlanStep>();
        }

        var steps = new List<FallbackPlanStep>(autoSources.Count);
        for (var index = 0; index < autoSources.Count; index++)
        {
            var decoded = DownloadSourceOrder.DecodeAutoSource(autoSources[index]);
            if (string.IsNullOrWhiteSpace(decoded.Source))
            {
                continue;
            }

            steps.Add(new FallbackPlanStep(
                StepId: $"step-{index}",
                Engine: decoded.Source,
                Quality: decoded.Quality,
                RequiredInputs: Array.Empty<string>(),
                ResolutionStrategy: "direct_url"));
        }

        return steps;
    }

    public static List<string> ReadStringArray(JsonObject payloadObj, string key)
    {
        if (payloadObj[key] is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(static entry => entry?.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToList();
    }

    public static string? ReadString(JsonObject payloadObj, string key)
    {
        if (payloadObj[key] is not JsonNode node)
        {
            return null;
        }

        var value = node.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static CanonicalFallbackState BuildSingleStepFallback(DownloadSourceOrder.AutoSourceStep firstStep, string resolutionStrategy)
    {
        var autoSources = new List<string> { DownloadSourceOrder.EncodeAutoSource(firstStep.Source, firstStep.Quality) };
        var fallbackPlan = new List<FallbackPlanStep>
        {
            new(
                StepId: "step-0",
                Engine: firstStep.Source,
                Quality: firstStep.Quality,
                RequiredInputs: Array.Empty<string>(),
                ResolutionStrategy: resolutionStrategy)
        };
        return new CanonicalFallbackState(autoSources, fallbackPlan, firstStep);
    }

    private static DownloadSourceOrder.AutoSourceStep DecodeOrDefault(string encodedSource, string? engine, string? quality)
    {
        var decoded = DownloadSourceOrder.DecodeAutoSource(encodedSource);
        if (!string.IsNullOrWhiteSpace(decoded.Source))
        {
            return decoded;
        }

        return new DownloadSourceOrder.AutoSourceStep(engine ?? "deezer", quality);
    }

    private static bool IsVideoPayload(string? contentType, string? quality, JsonObject payloadObj)
    {
        if (AppleVideoClassifier.IsVideoContentType(contentType))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(quality)
            && quality.Contains("video", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sourceUrl = ReadString(payloadObj, "SourceUrl") ?? ReadString(payloadObj, "sourceUrl");
        var collectionType = ReadString(payloadObj, "CollectionType") ?? ReadString(payloadObj, "collectionType");
        return AppleVideoClassifier.IsVideo(sourceUrl, collectionType, contentType);
    }

    private static bool SetStringArray(JsonObject payloadObj, string key, IReadOnlyCollection<string> values)
    {
        var current = ReadStringArray(payloadObj, key);
        if (current.SequenceEqual(values))
        {
            return false;
        }

        payloadObj[key] = new JsonArray(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
        return true;
    }

    private static bool SetFallbackPlan(JsonObject payloadObj, IReadOnlyCollection<FallbackPlanStep> steps)
    {
        var current = ReadFallbackPlan(payloadObj);
        if (current.SequenceEqual(steps))
        {
            return false;
        }

        payloadObj["FallbackPlan"] = new JsonArray(
            steps
                .Select(step => (JsonNode)new JsonObject
                {
                    ["StepId"] = step.StepId,
                    ["Engine"] = step.Engine,
                    ["Quality"] = step.Quality,
                    ["RequiredInputs"] = new JsonArray(step.RequiredInputs.Select(input => (JsonNode)JsonValue.Create(input)!).ToArray()),
                    ["ResolutionStrategy"] = step.ResolutionStrategy
                })
                .ToArray());
        return true;
    }

    private static bool SetString(JsonObject payloadObj, string key, string? value)
    {
        var current = ReadString(payloadObj, key);
        if (string.Equals(current, value, StringComparison.Ordinal))
        {
            return false;
        }

        payloadObj[key] = value;
        return true;
    }

    private static bool SetInt(JsonObject payloadObj, string key, int value)
    {
        if (payloadObj[key] is JsonValue jsonValue
            && jsonValue.TryGetValue<int>(out var current)
            && current == value)
        {
            return false;
        }

        payloadObj[key] = value;
        return true;
    }

    private static bool SetBool(JsonObject payloadObj, string key, bool value)
    {
        if (payloadObj[key] is JsonValue jsonValue
            && jsonValue.TryGetValue<bool>(out var current)
            && current == value)
        {
            return false;
        }

        payloadObj[key] = value;
        return true;
    }

    private static bool SetEmptyArray(JsonObject payloadObj, string key)
    {
        if (payloadObj[key] is JsonArray current && current.Count == 0)
        {
            return false;
        }

        payloadObj[key] = new JsonArray();
        return true;
    }
}
