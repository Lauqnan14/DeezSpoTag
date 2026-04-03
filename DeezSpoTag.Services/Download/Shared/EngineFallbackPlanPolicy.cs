using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Services.Download.Shared;

public static class EngineFallbackPlanPolicy
{
    public static bool ShouldUseInEngineFallback(EngineQueueItemBase payload, string engineName)
    {
        IEnumerable<string> plannedEngines = Enumerable.Empty<string>();
        if (payload.FallbackPlan != null && payload.FallbackPlan.Count > 0)
        {
            plannedEngines = payload.FallbackPlan
                .Select(step => step.Engine)
                .Where(engine => !string.IsNullOrWhiteSpace(engine));
        }
        else if (payload.AutoSources != null && payload.AutoSources.Count > 0)
        {
            plannedEngines = payload.AutoSources
                .Select(DownloadSourceOrder.DecodeAutoSource)
                .Select(step => step.Source)
                .Where(engine => !string.IsNullOrWhiteSpace(engine));
        }

        return !plannedEngines.Any(engine => !string.Equals(engine, engineName, StringComparison.OrdinalIgnoreCase));
    }
}
