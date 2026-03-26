namespace DeezSpoTag.Services.Utils;

public static class DeezSpoTagDataRootResolver
{
    public static string Resolve(string? explicitDataRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataRoot))
        {
            return explicitDataRoot.Trim();
        }

        var configDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return configDir.Trim();
        }

        var dataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return dataDir.Trim();
        }

        return Path.Join(AppContext.BaseDirectory, "Data");
    }
}
