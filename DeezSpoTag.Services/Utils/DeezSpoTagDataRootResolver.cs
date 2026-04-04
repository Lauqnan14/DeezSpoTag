namespace DeezSpoTag.Services.Utils;

public static class DeezSpoTagDataRootResolver
{
    public static string Resolve(string? explicitDataRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataRoot))
        {
            return explicitDataRoot.Trim();
        }

        return AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
    }
}
