using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

public static class DownloadEngineSettingsHelper
{
    public readonly record struct ProfileResolutionOptions(
        string? CurrentEngine = null,
        bool WrapResolutionExceptions = true,
        bool RequireProfile = true);

    public static async Task ResolveAndApplyProfileAsync(
        IDownloadTagSettingsResolver resolver,
        DeezSpoTagSettings settings,
        long? destinationFolderId,
        ILogger logger,
        CancellationToken cancellationToken,
        ProfileResolutionOptions options = default)
    {
        if (!options.WrapResolutionExceptions)
        {
            var unwrappedProfile = await resolver.ResolveProfileAsync(destinationFolderId, cancellationToken);
            if (unwrappedProfile == null)
            {
                if (!options.RequireProfile)
                {
                    return;
                }

                throw new InvalidOperationException("Destination music folder requires a valid AutoTag profile.");
            }

            ApplyResolvedProfileToSettings(settings, unwrappedProfile, currentEngine: options.CurrentEngine);
            return;
        }

        try
        {
            var profile = await resolver.ResolveProfileAsync(destinationFolderId, cancellationToken);
            if (profile == null)
            {
                if (!options.RequireProfile)
                {
                    return;
                }

                throw new InvalidOperationException("Destination music folder requires a valid AutoTag profile.");
            }

            ApplyResolvedProfileToSettings(settings, profile, currentEngine: options.CurrentEngine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to resolve download tag profile for folder {FolderId}", destinationFolderId);            }
            throw new InvalidOperationException("Failed to apply destination profile settings.", ex);
        }
    }

    public static void ApplyQualityBucketToSettings(DeezSpoTagSettings settings, string? qualityBucket)
    {
        if (string.IsNullOrWhiteSpace(settings.DownloadLocation))
        {
            return;
        }

        var normalized = qualityBucket?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var suffix = normalized switch
        {
            "atmos" => "Atmos",
            "stereo" => "Stereo",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(suffix))
        {
            return;
        }

        settings.DownloadLocation = Path.Join(settings.DownloadLocation, suffix);
    }

    public static bool IsAtmosOnlyPayload(string? contentType, string? quality)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && string.Equals(contentType.Trim(), DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(quality)
            && quality.Contains("atmos", StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyResolvedProfileToSettings(
        DeezSpoTagSettings settings,
        DownloadTagProfileSettings profile,
        string? metadataSourceOverride = null,
        string? currentEngine = null)
    {
        settings.Tags = TagSettingsMerge.UseProfileOnly(profile.TagSettings);
        var normalizedSource = metadataSourceOverride != null
            ? DownloadTagSourceHelper.NormalizeMetadataResolverSource(metadataSourceOverride)
            : DownloadTagSourceHelper.ResolveMetadataSource(profile.DownloadTagSource, currentEngine, settings.Service);
        settings.MetadataSource = normalizedSource
            ?? DownloadTagSourceHelper.NormalizeMetadataResolverSource(settings.MetadataSource)
            ?? string.Empty;
        TechnicalLyricsSettingsApplier.Apply(settings, profile.Technical);

        var folder = profile.FolderStructure;
        if (folder == null)
        {
            return;
        }

        settings.CreateArtistFolder = folder.CreateArtistFolder;
        settings.CreateAlbumFolder = folder.CreateAlbumFolder;
        settings.CreateCDFolder = folder.CreateCDFolder;
        settings.CreateStructurePlaylist = folder.CreateStructurePlaylist;
        settings.CreateSingleFolder = folder.CreateSingleFolder;
        settings.CreatePlaylistFolder = folder.CreatePlaylistFolder;

        if (!string.IsNullOrWhiteSpace(folder.ArtistNameTemplate))
        {
            settings.ArtistNameTemplate = folder.ArtistNameTemplate;
        }

        if (!string.IsNullOrWhiteSpace(folder.AlbumNameTemplate))
        {
            settings.AlbumNameTemplate = folder.AlbumNameTemplate;
        }

        if (!string.IsNullOrWhiteSpace(folder.PlaylistNameTemplate))
        {
            settings.PlaylistNameTemplate = folder.PlaylistNameTemplate;
        }

        if (!string.IsNullOrWhiteSpace(folder.IllegalCharacterReplacer))
        {
            settings.IllegalCharacterReplacer = folder.IllegalCharacterReplacer;
        }
    }

}
