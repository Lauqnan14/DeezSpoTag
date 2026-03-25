using DeezSpoTag.Services.Utils;

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
        return AppDataPathResolver.NormalizeConfiguredDataRoot(path)
            ?? Path.GetFullPath(path.Trim());
    }
}
