using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Services.Download.Shared;

public static class EngineFallbackPlanPolicy
{
    public static bool ShouldUseInEngineFallback(EngineQueueItemBase payload, string engineName)
    {
        var normalizedEngine = engineName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEngine))
        {
            return false;
        }

        var plannedSteps = BuildPlannedSteps(payload);
        if (plannedSteps.Count == 0)
        {
            return true;
        }

        var currentIndex = ResolveCurrentStepIndex(payload, plannedSteps, normalizedEngine);
        if (currentIndex >= 0)
        {
            for (var nextIndex = currentIndex + 1; nextIndex < plannedSteps.Count; nextIndex++)
            {
                var nextSource = plannedSteps[nextIndex].Source;
                if (string.IsNullOrWhiteSpace(nextSource))
                {
                    continue;
                }

                return string.Equals(nextSource, normalizedEngine, StringComparison.OrdinalIgnoreCase);
            }
        }

        return plannedSteps.All(step =>
            string.IsNullOrWhiteSpace(step.Source)
            || string.Equals(step.Source, normalizedEngine, StringComparison.OrdinalIgnoreCase));
    }

    private static List<DownloadSourceOrder.AutoSourceStep> BuildPlannedSteps(EngineQueueItemBase payload)
    {
        var steps = new List<DownloadSourceOrder.AutoSourceStep>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendAutoSourceSteps(payload, steps, seen);
        AppendFallbackPlanSteps(payload, steps, seen);
        return steps;
    }

    private static void AppendAutoSourceSteps(
        EngineQueueItemBase payload,
        List<DownloadSourceOrder.AutoSourceStep> steps,
        HashSet<string> seen)
    {
        if (payload.AutoSources != null && payload.AutoSources.Count > 0)
        {
            foreach (var entry in payload.AutoSources)
            {
                var decoded = DownloadSourceOrder.DecodeAutoSource(entry);
                if (string.IsNullOrWhiteSpace(decoded.Source))
                {
                    continue;
                }

                var key = DownloadSourceOrder.EncodeAutoSource(decoded.Source, decoded.Quality);
                if (seen.Add(key))
                {
                    steps.Add(decoded);
                }
            }
        }
    }

    private static void AppendFallbackPlanSteps(
        EngineQueueItemBase payload,
        List<DownloadSourceOrder.AutoSourceStep> steps,
        HashSet<string> seen)
    {
        if (payload.FallbackPlan == null || payload.FallbackPlan.Count == 0)
        {
            return;
        }

        foreach (var step in payload.FallbackPlan)
        {
            if (string.IsNullOrWhiteSpace(step.Engine))
            {
                continue;
            }

            var key = DownloadSourceOrder.EncodeAutoSource(step.Engine, step.Quality);
            if (seen.Add(key))
            {
                steps.Add(new DownloadSourceOrder.AutoSourceStep(step.Engine, step.Quality));
            }
        }
    }

    private static int ResolveCurrentStepIndex(
        EngineQueueItemBase payload,
        IReadOnlyList<DownloadSourceOrder.AutoSourceStep> steps,
        string engineName)
    {
        var indexedMatch = -1;
        if (payload.AutoIndex >= 0 && payload.AutoIndex < steps.Count)
        {
            var indexedStep = steps[payload.AutoIndex];
            if (string.Equals(indexedStep.Source, engineName, StringComparison.OrdinalIgnoreCase))
            {
                indexedMatch = payload.AutoIndex;
            }
        }

        var exactMatch = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!string.Equals(step.Source, engineName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(step.Quality) || string.IsNullOrWhiteSpace(payload.Quality))
            {
                exactMatch = i;
                break;
            }

            if (string.Equals(step.Quality, payload.Quality, StringComparison.OrdinalIgnoreCase))
            {
                exactMatch = i;
                break;
            }
        }

        if (indexedMatch >= 0 && exactMatch >= 0)
        {
            return Math.Max(indexedMatch, exactMatch);
        }

        return indexedMatch >= 0 ? indexedMatch : exactMatch;
    }
}
