using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

internal static class TaggingProfileSettingsMapper
{
    public static void ApplyProfileToSettings(
        DeezSpoTagSettings settings,
        TaggingProfile profile,
        string defaultTitleCasing = "nothing",
        string defaultArtistCasing = "nothing",
        string defaultArtworkFallbackOrder = "apple,deezer,spotify")
    {
        TaggingProfileSettingsOverlay.ApplyProfileToSettings(
            settings,
            profile,
            defaultTitleCasing,
            defaultArtistCasing,
            defaultArtworkFallbackOrder);
    }
}
