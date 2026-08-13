using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Web.Services;

internal static class AnimatedArtworkFileNaming
{
    public static bool IsAnimatedArtworkSidecar(string? path)
        => AnimatedArtworkNaming.IsAnimatedArtworkSidecar(path);

    public static bool IsLegacyStem(string? stem)
        => AnimatedArtworkNaming.IsLegacyStem(stem);
}
