using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyHomeFeedCollaborators
{
    public required SpotifyPathfinderMetadataClient PathfinderClient { get; init; }
    public required ISettingsService SettingsService { get; init; }
    public required SpotifyBlobService BlobService { get; init; }
    public required PlatformAuthService PlatformAuthService { get; init; }
    public required SpotifyUserAuthStore UserAuthStore { get; init; }
    public required ISpotifyUserContextAccessor UserContextAccessor { get; init; }
}
