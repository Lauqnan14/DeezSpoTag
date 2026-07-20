using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Web.Services;

public interface IAppDataRootOverride
{
    string? AppDataRoot { get; }
}

public static class AppDataPaths
{
    public static string GetDataRoot(IWebHostEnvironment environment)
    {
        if (environment is IAppDataRootOverride { AppDataRoot: { } overrideRoot }
            && !string.IsNullOrWhiteSpace(overrideRoot))
        {
            return NormalizeDataRoot(overrideRoot);
        }

        return NormalizeDataRoot(
            AppDataPathResolver.ResolveDataRootOrDefault(Path.Join(environment.ContentRootPath, "Data")));
    }

    private static string NormalizeDataRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath("Data");
        }

        return AppDataPathResolver.NormalizeConfiguredDataRoot(path)
            ?? Path.GetFullPath(path);
    }
}
