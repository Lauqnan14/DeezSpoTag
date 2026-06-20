using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public static class WatchlistDestinationFolderResolver
{
    public static async Task<HashSet<long>> GetValidFolderIdsAsync(
        AutoTagProfileResolutionService profileResolutionService,
        CancellationToken cancellationToken)
    {
        var state = await profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        return state.FoldersById.Values
            .Where(folder => IsMusicDestinationFolder(folder)
                && AutoTagProfileResolutionService.ResolveFolderProfile(state, folder.Id, folder.AutoTagProfileId) != null)
            .Select(folder => folder.Id)
            .ToHashSet();
    }

    public static bool IsMusicDestinationFolder(FolderDto folder)
    {
        if (!folder.Enabled || string.IsNullOrWhiteSpace(folder.RootPath))
        {
            return false;
        }

        var desiredQuality = folder.DesiredQuality?.Trim().ToLowerInvariant() ?? string.Empty;
        return !desiredQuality.Contains("video", StringComparison.Ordinal)
            && !desiredQuality.Contains("podcast", StringComparison.Ordinal);
    }
}
