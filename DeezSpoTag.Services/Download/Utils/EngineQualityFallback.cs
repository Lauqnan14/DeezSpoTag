using System;

namespace DeezSpoTag.Services.Download.Utils;

public static class EngineQualityFallback
{
    public static string? GetNextLowerQuality(string engine, string? currentQuality, bool excludeAtmos = false)
    {
        var options = QualityCatalog.GetEngineQualityOptions();
        if (!options.TryGetValue(engine, out var qualities) || qualities.Count == 0)
        {
            return null;
        }

        var ordered = qualities.Select(q => q.Value).ToList();
        if (excludeAtmos)
        {
            ordered = ordered.Where(q => !string.Equals(q, "ATMOS", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (ordered.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(currentQuality))
        {
            return ordered.Count > 1 ? ordered[1] : null;
        }

        var index = ordered.FindIndex(q => string.Equals(q, currentQuality, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return ordered.FirstOrDefault();
        }

        return index + 1 < ordered.Count ? ordered[index + 1] : null;
    }
}
