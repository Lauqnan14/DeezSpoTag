using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class PlexUserIdResolver
{
    public static async Task<long?> ResolveAsync(
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        CancellationToken cancellationToken)
    {
        var state = await authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null)
        {
            return null;
        }

        var username = !string.IsNullOrWhiteSpace(plex.Username)
            ? plex.Username
            : plex.ServerName;

        return await libraryRepository.EnsurePlexUserAsync(
            username,
            plex.Username,
            plex.Url,
            plex.MachineIdentifier,
            cancellationToken);
    }
}
