using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Web.Services;

public static class AppDataPaths
{
    public static string GetDataRoot(IWebHostEnvironment environment)
    {
        return NormalizeDataRoot(
            AppDataPathResolver.ResolveDataRootOrDefault(Path.Join(environment.ContentRootPath, "Data")));
    }

    private static string NormalizeDataRoot(string path)
    {
        return AppDataPathResolver.NormalizeConfiguredDataRoot(path)
            ?? Path.GetFullPath(path.Trim());
    }
}
