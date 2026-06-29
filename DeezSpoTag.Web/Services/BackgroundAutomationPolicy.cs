using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.Web.Services;

public static class BackgroundAutomationPolicy
{
    public static bool IsEnabled(IConfiguration configuration, string featureName)
    {
        if (TryReadBoolean(Environment.GetEnvironmentVariable(ToFeatureEnvVar(featureName)), out var featureEnv))
        {
            return featureEnv;
        }

        var featureConfig = configuration[$"BackgroundAutomation:{featureName}:Enabled"];
        if (TryReadBoolean(featureConfig, out var featureEnabled))
        {
            return featureEnabled;
        }

        return false;
    }

    private static string ToFeatureEnvVar(string featureName)
    {
        var normalized = new string(featureName
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_')
            .ToArray());
        return $"DEEZSPOTAG_BACKGROUND_AUTOMATION_{normalized}_ENABLED";
    }

    private static bool TryReadBoolean(string? value, out bool enabled)
    {
        enabled = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (bool.TryParse(trimmed, out enabled))
        {
            return true;
        }

        if (string.Equals(trimmed, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }

        if (string.Equals(trimmed, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            return true;
        }

        return false;
    }
}
