namespace DeezSpoTag.Web.Services;

public static class AppDataPaths
{
    public static string GetDataRoot(IWebHostEnvironment environment)
    {
        var configDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return NormalizeDataRoot(configDir);
        }

        var deezspotagDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(deezspotagDataDir))
        {
            return NormalizeDataRoot(deezspotagDataDir);
        }

        return NormalizeDataRoot(Path.Join(environment.ContentRootPath, "Data"));
    }

    private static string NormalizeDataRoot(string path)
    {
        var normalized = Path.GetFullPath(path.Trim());
        while (string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized)),
            "deezspotag",
            StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(normalized))?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            normalized = parent;
        }

        return normalized;
    }
}
