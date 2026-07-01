using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Fallback;

public static class FallbackPayloadNormalizer
{
    private const string QualityKey = "Quality";
    private const string DirectUrlResolution = "direct_url";
    private const string DefaultEngine = "deezer";

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
        var payloadQuality = ReadString(payloadObj, QualityKey);
        var payloadAutoSources = ReadStringArray(payloadObj, "AutoSources");
        var payloadFallbackPlan = ReadFallbackPlan(payloadObj);
        var contentType = string.IsNullOrWhiteSpace(item.ContentType) ? payloadContentType : item.ContentType;

        if (IsVideoPayload(contentType, payloadQuality, payloadObj))
        {
            var firstStep = new DownloadSourceOrder.AutoSourceStep("apple", DownloadContentTypes.Video);
            return BuildSingleStepFallback(firstStep, DirectUrlResolution);
        }

        if (Shared.DownloadEngineSettingsHelper.IsAtmosOnlyPayload(contentType, payloadQuality))
        {
            return ResolveAtmosOnlyState(item, payloadQuality, payloadAutoSources, payloadFallbackPlan);
        }

        if (TryResolveAutoSources(item, payloadQuality, payloadAutoSources, payloadFallbackPlan, out var autoSourceState))
        {
            return autoSourceState;
        }

        if (TryResolveFallbackPlan(item, payloadQuality, payloadFallbackPlan, out var planState))
        {
            return planState;
        }

        var effectiveSettings = ResolveFallbackSettings(settings, payloadObj);
        var resolvedAutoSources = DownloadSourceOrder.ResolveQualityAutoSources(effectiveSettings, includeDeezer: true, targetQuality: null);
        var resolvedFirstStep = resolvedAutoSources.Count > 0
            ? DownloadSourceOrder.DecodeAutoSource(resolvedAutoSources[0])
            : new DownloadSourceOrder.AutoSourceStep(item.Engine ?? DefaultEngine, null);
        var resolvedFallbackPlan = resolvedAutoSources
            .Select((source, index) =>
            {
                var step = DownloadSourceOrder.DecodeAutoSource(source);
                var engine = string.IsNullOrWhiteSpace(step.Source) ? item.Engine ?? DefaultEngine : step.Source;
                return new FallbackPlanStep(
                    StepId: $"step-{index}",
                    Engine: engine,
                    Quality: step.Quality,
                    RequiredInputs: Array.Empty<string>(),
                    ResolutionStrategy: DirectUrlResolution);
            })
            .ToList();
        return new CanonicalFallbackState(resolvedAutoSources, resolvedFallbackPlan, resolvedFirstStep);
    }

    private static bool TryResolveAutoSources(
        DownloadQueueItem item,
        string? payloadQuality,
        List<string> payloadAutoSources,
        List<FallbackPlanStep> payloadFallbackPlan,
        out CanonicalFallbackState state)
    {
        state = default!;
        if (payloadAutoSources.Count == 0)
        {
            return false;
        }

        var normalizedAutoSources = NormalizeEncodedSources(payloadAutoSources);
        if (normalizedAutoSources.Count == 0)
        {
            return false;
        }

        var firstStep = DecodeOrDefault(normalizedAutoSources[0], item.Engine, payloadQuality);
        var normalizedFallbackPlan = ShouldReuseFallbackPlan(payloadFallbackPlan, normalizedAutoSources)
            ? payloadFallbackPlan
            : BuildDirectUrlPlanFromAutoSources(normalizedAutoSources);
        state = new CanonicalFallbackState(normalizedAutoSources, normalizedFallbackPlan, firstStep);
        return true;
    }

    private static bool TryResolveFallbackPlan(
        DownloadQueueItem item,
        string? payloadQuality,
        List<FallbackPlanStep> payloadFallbackPlan,
        out CanonicalFallbackState state)
    {
        state = default!;
        if (payloadFallbackPlan.Count == 0)
        {
            return false;
        }

        var normalizedAutoSources = NormalizePlanSources(payloadFallbackPlan);
        if (normalizedAutoSources.Count == 0)
        {
            return false;
        }

        var firstStep = DecodeOrDefault(normalizedAutoSources[0], item.Engine, payloadQuality);
        state = new CanonicalFallbackState(normalizedAutoSources, payloadFallbackPlan, firstStep);
        return true;
    }

    private static CanonicalFallbackState ResolveAtmosOnlyState(
        DownloadQueueItem item,
        string? payloadQuality,
        List<string> payloadAutoSources,
        List<FallbackPlanStep> payloadFallbackPlan)
    {
        var normalizedAutoSources = NormalizeEncodedSources(payloadAutoSources)
            .Where(IsEncodedAtmosSource)
            .ToList();

        var normalizedFallbackPlan = payloadFallbackPlan
            .Where(step => IsAtmosStep(step.Engine, step.Quality))
            .ToList();

        if (normalizedAutoSources.Count == 0 && normalizedFallbackPlan.Count > 0)
        {
            normalizedAutoSources = NormalizePlanSources(normalizedFallbackPlan)
                .Where(IsEncodedAtmosSource)
                .ToList();
        }

        if (normalizedAutoSources.Count == 0)
        {
            var engine = IsAtmosEngine(item.Engine) ? item.Engine! : "apple";
            var quality = ResolveAtmosQuality(engine, payloadQuality);
            var firstStep = new DownloadSourceOrder.AutoSourceStep(engine, quality);
            return BuildSingleStepFallback(firstStep, DirectUrlResolution);
        }

        if (normalizedFallbackPlan.Count == 0 || !ShouldReuseFallbackPlan(normalizedFallbackPlan, normalizedAutoSources))
        {
            normalizedFallbackPlan = BuildDirectUrlPlanFromAutoSources(normalizedAutoSources);
        }

        var firstAtmosStep = DecodeOrDefault(normalizedAutoSources[0], item.Engine, payloadQuality);
        return new CanonicalFallbackState(normalizedAutoSources, normalizedFallbackPlan, firstAtmosStep);
    }

    private static string ResolveAtmosQuality(string engine, string? payloadQuality)
    {
        if (!string.IsNullOrWhiteSpace(payloadQuality))
        {
            return payloadQuality;
        }

        return string.Equals(engine, "tidal", StringComparison.OrdinalIgnoreCase)
               || string.Equals(engine, "amazon", StringComparison.OrdinalIgnoreCase)
            ? "DOLBY_ATMOS"
            : "ATMOS";
    }

    private static DeezSpoTagSettings ResolveFallbackSettings(DeezSpoTagSettings settings, JsonObject payloadObj)
    {
        var snapshot = QueueSourceSettingsSnapshot.ReadFromPayload(payloadObj);
        return snapshot?.ApplyTo(settings) ?? settings;
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
                changed |= SetString(payloadObj, QualityKey, state.FirstStep.Quality);
            }
        }

        return changed;
    }

    private static bool IsAtmosEngine(string? engine)
        => string.Equals(engine, "apple", StringComparison.OrdinalIgnoreCase)
           || string.Equals(engine, "tidal", StringComparison.OrdinalIgnoreCase)
           || string.Equals(engine, "amazon", StringComparison.OrdinalIgnoreCase);

    private static bool IsEncodedAtmosSource(string encodedSource)
    {
        var step = DownloadSourceOrder.DecodeAutoSource(encodedSource);
        return IsAtmosStep(step.Source, step.Quality);
    }

    private static bool IsAtmosStep(string? engine, string? quality)
        => (string.Equals(engine, "apple", StringComparison.OrdinalIgnoreCase)
                && string.Equals(quality?.Trim(), "ATMOS", StringComparison.OrdinalIgnoreCase))
           || (string.Equals(engine, "tidal", StringComparison.OrdinalIgnoreCase)
                && string.Equals(quality?.Trim(), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase))
           || (string.Equals(engine, "amazon", StringComparison.OrdinalIgnoreCase)
                && string.Equals(quality?.Trim(), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase));

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
            var quality = ReadString(stepObj, QualityKey);
            var resolutionStrategy = ReadString(stepObj, "ResolutionStrategy") ?? DirectUrlResolution;
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
                ResolutionStrategy: DirectUrlResolution));
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

        return new DownloadSourceOrder.AutoSourceStep(engine ?? DefaultEngine, quality);
    }

    private static bool ShouldReuseFallbackPlan(List<FallbackPlanStep> fallbackPlan, IReadOnlyList<string> autoSources)
    {
        if (fallbackPlan == null || fallbackPlan.Count == 0)
        {
            return false;
        }

        var normalizedPlanSources = NormalizePlanSources(fallbackPlan);
        if (normalizedPlanSources.Count == 0)
        {
            return false;
        }

        var normalizedAutoSources = NormalizeEncodedSources(autoSources);
        return normalizedPlanSources.SequenceEqual(normalizedAutoSources, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> NormalizePlanSources(IReadOnlyList<FallbackPlanStep> fallbackPlan)
    {
        return NormalizeEncodedSources(
            fallbackPlan
                .Where(step => !string.IsNullOrWhiteSpace(step.Engine))
                .Select(step => DownloadSourceOrder.EncodeAutoSource(step.Engine, step.Quality))
                .ToList());
    }

    private static List<string> NormalizeEncodedSources(IReadOnlyList<string> sources)
    {
        if (sources == null || sources.Count == 0)
        {
            return new List<string>();
        }

        return DownloadSourceOrder.CollapseAutoSourcesByService(
            sources
                .Select(DownloadSourceOrder.DecodeAutoSource)
                .Where(step => !string.IsNullOrWhiteSpace(step.Source))
                .Select(step => DownloadSourceOrder.EncodeAutoSource(step.Source, step.Quality))
                .ToList());
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
                    [QualityKey] = step.Quality,
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
