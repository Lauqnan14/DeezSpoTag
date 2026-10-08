using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Library;

/// <summary>
/// The content role a library folder plays. A folder is exactly one of these, and the role is
/// read off the folder's desired quality: a folder typed Atmos on the Folder tab is an Atmos
/// folder, and every other music folder is a stereo folder.
/// </summary>
public enum FolderContentRole
{
    Stereo,
    Atmos,
    Video,
    Podcast
}

/// <summary>
/// Answers "which folder should this kind of download go to?" from the folders themselves.
///
/// A folder already knows what it is, so most of the time the answer is obvious. When exactly
/// one enabled folder can serve a role there is nothing to ask the user about, so that folder is
/// the default. When there are none, or more than one, the user has a real choice to make and
/// this returns nothing so the caller keeps asking.
///
/// A folder the user already chose always wins. This only ever fills a gap; it never overrides
/// a configured destination, and it never writes anything back.
/// </summary>
public static class FolderContentTypeResolver
{
    // The legacy numeric rank Atmos folders were stored with before the value column carried
    // "atmos". QualityScannerService.IsAtmosDestinationFolder accepts it too, and the download
    // router and the Folder tab both write the string form, so both are treated as Atmos here.
    private const string LegacyAtmosRank = "5";

    // Lowercase because the folder value is compared after being lowercased.
    private const string VideoMode = "video";
    private const string PodcastMode = "podcast";

    public static FolderContentRole ResolveRole(FolderDto? folder)
    {
        var desiredQuality = folder?.DesiredQuality?.Trim().ToLowerInvariant() ?? string.Empty;

        if (desiredQuality.Contains(VideoMode, StringComparison.Ordinal))
        {
            return FolderContentRole.Video;
        }

        if (desiredQuality.Contains(PodcastMode, StringComparison.Ordinal))
        {
            return FolderContentRole.Podcast;
        }

        return IsAtmosDesiredQuality(desiredQuality)
            ? FolderContentRole.Atmos
            : FolderContentRole.Stereo;
    }

    public static bool IsRole(FolderDto? folder, FolderContentRole role)
        => ResolveRole(folder) == role;

    /// <summary>
    /// Enabled folders that can serve the role. Disabled folders are never download destinations.
    /// </summary>
    public static IReadOnlyList<FolderDto> SelectEnabledByRole(
        IEnumerable<FolderDto>? folders,
        FolderContentRole role)
    {
        if (folders is null)
        {
            return Array.Empty<FolderDto>();
        }

        return folders
            .Where(folder => folder is { Enabled: true } && IsRole(folder, role))
            .ToList();
    }

    /// <summary>
    /// The destination folder id for a role, or null when the user still has to choose.
    /// </summary>
    /// <param name="configuredFolderId">
    /// The destination the user already saved, if any. Honoured when it still points at an
    /// enabled folder of the requested role.
    /// </param>
    public static long? ResolveDefaultFolderId(
        IEnumerable<FolderDto>? folders,
        FolderContentRole role,
        long? configuredFolderId = null)
    {
        if (folders is null)
        {
            return null;
        }

        var enabledFolders = folders.Where(folder => folder is { Enabled: true }).ToList();

        if (configuredFolderId.HasValue
            && enabledFolders.Any(folder => folder.Id == configuredFolderId.Value && IsRole(folder, role)))
        {
            return configuredFolderId.Value;
        }

        // One folder of this kind means there is nothing to ask. Zero or several means the user
        // has a genuine choice, so no default is offered and the caller keeps prompting.
        var candidates = enabledFolders.Where(folder => IsRole(folder, role)).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0].Id : null;
    }

    public static long? ResolveDefaultFolderId(
        IEnumerable<FolderDto>? folders,
        string? contentType,
        long? configuredFolderId = null)
        => ResolveDefaultFolderId(folders, ParseRole(contentType), configuredFolderId);

    public static FolderContentRole ParseRole(string? contentType)
    {
        var normalized = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            DownloadContentTypes.Atmos => FolderContentRole.Atmos,
            DownloadContentTypes.Video => FolderContentRole.Video,
            DownloadContentTypes.Podcast => FolderContentRole.Podcast,
            _ => FolderContentRole.Stereo
        };
    }

    private static bool IsAtmosDesiredQuality(string normalizedDesiredQuality)
        => normalizedDesiredQuality.Contains(DownloadContentTypes.Atmos, StringComparison.Ordinal)
            || string.Equals(normalizedDesiredQuality, LegacyAtmosRank, StringComparison.Ordinal);
}
