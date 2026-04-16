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

    public static async Task<string?> ResolveAndApplyProfileAsync(
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
                    return null;
                }

                throw new InvalidOperationException("Destination music folder requires a valid AutoTag profile.");
            }

            return ApplyResolvedProfileToSettings(settings, unwrappedProfile, currentEngine: options.CurrentEngine);
        }

        try
        {
            var profile = await resolver.ResolveProfileAsync(destinationFolderId, cancellationToken);
            if (profile == null)
            {
                if (!options.RequireProfile)
                {
                    return null;
                }

                throw new InvalidOperationException("Destination music folder requires a valid AutoTag profile.");
            }

            return ApplyResolvedProfileToSettings(settings, profile, currentEngine: options.CurrentEngine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to resolve download tag profile for folder {FolderId}", destinationFolderId);            }
            if (ex is InvalidOperationException invalidOperationException
                && invalidOperationException.Message.StartsWith("Download profile source resolution failed:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(invalidOperationException.Message, ex);
            }

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

        var normalizedRoot = settings.DownloadLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var terminalSegment = Path.GetFileName(normalizedRoot);
        if (!string.IsNullOrWhiteSpace(terminalSegment)
            && string.Equals(terminalSegment, suffix, StringComparison.OrdinalIgnoreCase))
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

    public static string ApplyResolvedProfileToSettings(
        DeezSpoTagSettings settings,
        DownloadTagProfileSettings profile,
        string? currentEngine = null)
    {
        settings.Tags = TagSettingsMerge.UseProfileOnly(profile.TagSettings);
        var normalizedSource = DownloadTagSourceHelper.ResolveDownloadTagSource(
            profile.DownloadTagSource,
            currentEngine,
            settings.Service);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            var configuredSource = profile.DownloadTagSource?.Trim();
            throw new InvalidOperationException(
                $"Download profile source resolution failed: downloadTagSource '{configuredSource}' is invalid for engine '{currentEngine ?? settings.Service ?? "unknown"}'.");
        }
        TechnicalLyricsSettingsApplier.Apply(settings, profile.Technical);

        var folder = profile.FolderStructure;
        if (folder == null)
        {
            return normalizedSource;
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

        return normalizedSource;
    }

}
