using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using TagLib;
using IOFile = System.IO.File;
using DownloadLyricsService = DeezSpoTag.Services.Download.Utils.LyricsService;
using LyricsProviderRegistry = DeezSpoTag.Services.Download.Utils.LyricsProviderRegistry;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed partial class LocalAutoTagRunner : IAutoTagRunner
{

    private static bool WantsArtworkFromSettings(AutoTagRunnerConfig config, DeezSpoTagSettings settings)
        => HasAnyTags(config, AlbumArtTag)
           || settings.SaveArtwork
           || settings.EmbedMaxQualityCover;

    /// <summary>The identity a native payload confirmed for one provider on this file,
    /// or null when the provider never confirmed anything for it.</summary>
    private static ProviderIdentityPayload? ConfirmedProviderIdentity(
        AutoTagFileRunContext context,
        string providerId)
        => context.Plan.TryGetConfirmedProviderIdentity(context.FileIndex, providerId, out var confirmed)
            ? confirmed
            : null;

    private async Task EnsureManualArtistArtworkAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo identity,
        AutoTagTrack track)
    {
        if (!context.Plan.Settings.SaveArtworkArtist)
        {
            return;
        }

        var coreTrack = BuildCoreTrack(
            track,
            ResolveArtistSeparator(context.Plan.Config, context.File),
            context.Plan.TagSettings.SingleAlbumArtist,
            context.Plan.Settings);
        var artistPath = BuildTemplatePathInfo(coreTrack, context.Plan.Settings).ArtistPath;
        if (string.IsNullOrWhiteSpace(artistPath))
        {
            return;
        }

        var artworkKey = Path.GetFullPath(artistPath);
        if (!context.Plan.AttemptedArtistArtworkPaths.Add(artworkKey))
        {
            return;
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        var artist = track.Artists.FirstOrDefault();
        // Artwork providers are only fed identities confirmed by a native payload; raw
        // tags and mutated track values are never trusted provenance.
        var appleConfirmed = ConfirmedProviderIdentity(context, "itunes");
        var deezerConfirmed = ConfirmedProviderIdentity(context, "deezer");
        var spotifyConfirmed = ConfirmedProviderIdentity(context, "spotify");
        var artistArtwork = await DownloadEngineArtworkHelper.ResolveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                _appleMusicCatalogService,
                _httpClientFactory,
                context.Plan.Settings,
                provider.GetService<DeezSpoTag.Integrations.Deezer.DeezerClient>(),
                provider.GetService<DeezSpoTag.Services.Download.ISpotifyArtworkResolver>(),
                provider.GetService<DeezSpoTag.Services.Download.ILastFmArtistImageResolver>(),
                appleConfirmed?.TrackId,
                deezerConfirmed?.TrackId,
                spotifyConfirmed?.TrackId,
                artist,
                _logger)
            {
                AppleArtistId = appleConfirmed?.ArtistId,
                DeezerArtistId = deezerConfirmed?.ArtistId,
                SpotifyArtistId = spotifyConfirmed?.ArtistId
            },
            context.Token);
        if (artistArtwork == null)
        {
            return;
        }

        _logger.LogInformation(
            "Artist artwork resolved from {Provider} using {ResolutionMethod} for {Artist}",
            artistArtwork.Provider,
            artistArtwork.ResolutionMethod,
            artist);

        _ = await DownloadEngineArtworkHelper.SaveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.SaveArtistArtworkRequest(
                provider.GetRequiredService<ImageDownloader>(),
                provider.GetRequiredService<EnhancedPathTemplateProcessor>(),
                artistPath,
                artistArtwork.Url,
                context.Plan.Settings,
                coreTrack,
                AppleQueueHelpers.GetAppleArtworkSize(context.Plan.Settings),
                context.Plan.Settings.EmbedMaxQualityCover,
                _logger),
            context.Token);
    }

    private async Task EnsureArtworkFallbackAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        AutoTagTrack track,
        CancellationToken token)
    {
        if (!WantsArtworkFromSettings(context.Plan.Config, context.Plan.Settings)
            || !string.IsNullOrWhiteSpace(track.Art))
        {
            return;
        }

        var providerOrder = ArtworkFallbackHelper.ResolveOrder(context.Plan.Settings);
        if (providerOrder.Count == 0)
        {
            return;
        }

        foreach (var platform in providerOrder
            .Select(ResolveArtworkFallbackPlatform)
            .Where(platform => !string.IsNullOrWhiteSpace(platform)
                && !string.Equals(platform, context.Platform, StringComparison.OrdinalIgnoreCase)))
        {
            AutoTagMatchResult? fallbackMatch;
            try
            {
                fallbackMatch = await MatchPlatformAsync(
                    platform!,
                    info,
                    new PlatformMatchContext
                    {
                        FilePath = context.File,
                        Config = context.Plan.Config,
                        Settings = context.Plan.Settings,
                        MatchingConfig = context.Plan.MatchingConfig,
                        ShazamCache = context.Plan.ShazamCache,
                        IsManualEnrichment = IsManualEnrichment(context.Plan.Config)
                    },
                    token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogArtworkFallbackMatchFailure(ex, context.File, platform!);
                continue;
            }

            var fallbackArt = fallbackMatch?.Track?.Art;
            if (string.IsNullOrWhiteSpace(fallbackArt))
            {
                continue;
            }
            if (IsManualEnrichment(context.Plan.Config)
                && (!AutoTagReleaseCategory.MatchesPreference(
                        fallbackMatch!.Track.ReleaseType,
                        fallbackMatch.Track.TrackTotal,
                        context.Plan.Config.ManualReleasePreference)
                    || (context.Plan.FrozenManualReleases.TryGetValue(context.FileIndex, out var frozenRelease)
                        && !AlbumsReferToSameRelease(frozenRelease.Album, fallbackMatch.Track.Album))))
            {
                continue;
            }

            track.Art = fallbackArt;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Artwork fallback resolved for {File} via {Platform}.", SanitizeLogValue(context.File), SanitizeLogValue(platform));
            }

            return;
        }
    }

    private void LogArtworkFallbackMatchFailure(Exception ex, string filePath, string platform)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(ex, "Artwork fallback match failed for {File} using {Platform}.", SanitizeLogValue(filePath), SanitizeLogValue(platform));
        }
    }

    private static string? ResolveArtworkFallbackPlatform(string provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "apple" => ItunesPlatform,
            "deezer" => DeezerPlatform,
            SpotifyPlatform => SpotifyPlatform,
            _ => null
        };
    }

    private static string? NormalizeLocalArtworkFormat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "jpg",
            "png"
        };

        var normalized = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => allowed.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => value.ToLowerInvariant())
            .ToList();

        return normalized.Count == 0 ? null : string.Join(",", normalized);
    }

    private static string BuildAlbumArtworkBaseFileName(AutoTagTrack track, DeezSpoTagSettings settings)
    {
        var albumTitle = string.IsNullOrWhiteSpace(track.Album) ? "Unknown Album" : track.Album.Trim();
        var primaryArtist = track.Artists.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            primaryArtist = UnknownArtist;
        }

        var albumModel = new DeezSpoTag.Core.Models.Album(albumTitle)
        {
            MainArtist = new DeezSpoTag.Core.Models.Artist(primaryArtist),
            Artists = new List<string> { primaryArtist }
        };

        return PathTemplateGenerator.GenerateAlbumName(
            settings.CoverImageTemplate,
            albumModel,
            settings,
            playlist: null);
    }

    private static List<string> ResolveAnimatedArtworkBadges(string filePath, DeezSpoTagSettings settings)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new List<string>();
        }

        return Directory.EnumerateFiles(directory)
            .Any(path => AnimatedArtworkNaming.IsAlbumAnimatedArtworkSidecar(
                path,
                settings.AnimatedArtworkSquareFileName,
                settings.AnimatedArtworkTallFileName))
            ? new List<string> { "animated-artwork" }
            : new List<string>();
    }

    private static bool TrySetMissingAlbumArtists(
        TagLib.Tag tag,
        List<string> artistCredits,
        bool singleAlbumArtist)
    {
        var hasAlbumArtist = tag.AlbumArtists != null && tag.AlbumArtists.Any(value => !string.IsNullOrWhiteSpace(value));
        if (hasAlbumArtist || artistCredits.Count == 0)
        {
            return false;
        }

        tag.AlbumArtists = singleAlbumArtist
            ? new[] { artistCredits[0] }
            : artistCredits.ToArray();
        return true;
    }

    private static List<string> ResolveAlbumArtistValues(Track coreTrack)
    {
        var primary = coreTrack.Album?.MainArtist?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return new List<string> { primary };
        }

        var mainArtists = coreTrack.Artist.GetValueOrDefault("Main", new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mainArtists.Count > 0)
        {
            return new List<string> { mainArtists[0] };
        }

        var artists = coreTrack.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count > 0)
        {
            return new List<string> { artists[0] };
        }

        return new List<string> { UnknownArtist };
    }

    private static void WriteAlbumArtistTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(AlbumArtistTag) || !context.EffectiveTagSettings.AlbumArtist)
        {
            return;
        }

        var albumArtistValues = ResolveAlbumArtistValues(context.CoreTrack);
        SetField(
            tagWriteContext,
            new TagFieldBinding("TPE2", AlbumArtistUpperTag, "aART", SupportedTag.AlbumArtist),
            albumArtistValues);
    }

    private static void ApplyAlbumArtTagWrite(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(AlbumArtTag) || !context.EffectiveTagSettings.Cover || string.IsNullOrWhiteSpace(context.TempCoverPath))
        {
            return;
        }

        var tempCoverPath = context.TempCoverPath;
        if (ShouldOverwriteTag(context.Config, SupportedTag.AlbumArt)
            || !HasTag(file, context.Extension, SupportedTag.AlbumArt, context.Config, context.PlatformId))
        {
            ApplyAlbumArt(file, tempCoverPath, context.EffectiveTagSettings.CoverDescriptionUTF8);
        }

        MarkAttemptedIfPresent(context, file, SupportedTag.AlbumArt);
    }

    private static TrackPathInfo BuildTemplatePathInfo(
        Track coreTrack,
        DeezSpoTagSettings settings)
    {
        var downloadType = string.IsNullOrWhiteSpace(coreTrack.Album?.Title) ? "track" : "album";
        var pathInfo = PathTemplateGenerator.GeneratePath(coreTrack, downloadType, settings);
        if (!string.IsNullOrWhiteSpace(pathInfo.CoverPath)
            || !settings.CreateAlbumFolder
            || string.IsNullOrWhiteSpace(coreTrack.Album?.Title))
        {
            return pathInfo;
        }

        var albumParentPath = !string.IsNullOrWhiteSpace(pathInfo.ArtistPath)
            ? pathInfo.ArtistPath
            : settings.DownloadLocation ?? ".";
        var albumName = PathTemplateGenerator.GenerateAlbumName(
            settings.AlbumNameTemplate,
            coreTrack.Album,
            settings,
            coreTrack.Playlist);
        if (string.IsNullOrWhiteSpace(albumName))
        {
            return pathInfo;
        }

        pathInfo.CoverPath = Path.Join(albumParentPath, albumName);
        return pathInfo;
    }

    private static string MaterializeFileToTemplatePath(
        string sourcePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        TagSettings tagSettings,
        string? establishedAlbumRoot = null)
    {
        if (config.MaterializeToTemplatePath != true)
        {
            return sourcePath;
        }

        var separator = ResolveArtistSeparator(config, sourcePath);
        var coreTrack = BuildCoreTrack(track, separator, tagSettings.SingleAlbumArtist, settings);
        var pathInfo = BuildTemplatePathInfo(coreTrack, settings);
        if (string.IsNullOrWhiteSpace(pathInfo.FilePath)
            || string.IsNullOrWhiteSpace(pathInfo.Filename))
        {
            return sourcePath;
        }

        var destinationDirectory = pathInfo.FilePath;
        if (!string.IsNullOrWhiteSpace(establishedAlbumRoot))
        {
            var discRelativePath = string.IsNullOrWhiteSpace(pathInfo.CoverPath)
                ? "."
                : Path.GetRelativePath(pathInfo.CoverPath, pathInfo.FilePath);
            if (discRelativePath == "."
                || (!Path.IsPathRooted(discRelativePath)
                    && discRelativePath != ".."
                    && !discRelativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                destinationDirectory = discRelativePath == "."
                    ? establishedAlbumRoot
                    : Path.Combine(establishedAlbumRoot, discRelativePath);
            }
        }

        var destinationPath = Path.Join(destinationDirectory, $"{pathInfo.Filename}{Path.GetExtension(sourcePath)}");
        if (PathsReferToSameFile(sourcePath, destinationPath))
        {
            return sourcePath;
        }

        Directory.CreateDirectory(destinationDirectory);
        destinationPath = ResolveTemplateMaterializationDestination(sourcePath, destinationPath, settings);
        FileMoveFallbackHelper.MoveWithFallback(sourcePath, destinationPath);
        MoveAdjacentSidecars(sourcePath, destinationPath);
        return destinationPath;
    }

    private static bool ShouldWriteArtworkSidecar(AutoTagRunnerConfig config)
        => config.SaveArtwork ?? false;

    private static bool ShouldPrepareTemplateArtworkSidecar(AutoTagRunnerConfig config)
        => config.OrganizeSidecarsIntoTemplateFolders == true && ShouldWriteArtworkSidecar(config);

    private static string? TryResolveExistingCoverSidecar(
        string filePath,
        AutoTagTrack track,
        Track coreTrack,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings)
    {
        if (!ShouldWriteArtworkSidecar(config))
        {
            return null;
        }

        var outputDirectory = config.OrganizeSidecarsIntoTemplateFolders == true
            ? BuildTemplatePathInfo(coreTrack, settings).CoverPath
            : Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return null;
        }

        var baseFileName = BuildAlbumArtworkBaseFileName(track, settings);
        if (string.IsNullOrWhiteSpace(baseFileName))
        {
            baseFileName = CoverTag;
        }

        return ResolveLocalArtworkFormats(settings.LocalArtworkFormat)
            .Select(format => Path.Join(outputDirectory, $"{baseFileName}.{format}"))
            .FirstOrDefault(IOFile.Exists);
    }

    private static async Task EnsureTemplateFoldersAndArtworkSidecarAsync(
        AutoTagTrack sourceTrack,
        Track coreTrack,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        string filePath,
        string? tempCoverPath,
        CancellationToken token)
    {
        var pathInfo = config.OrganizeSidecarsIntoTemplateFolders == true
            ? BuildTemplatePathInfo(coreTrack, settings)
            : new TrackPathInfo
            {
                ArtistPath = Path.GetDirectoryName(filePath),
                CoverPath = Path.GetDirectoryName(filePath)
            };
        if (!string.IsNullOrWhiteSpace(pathInfo.ArtistPath))
        {
            Directory.CreateDirectory(pathInfo.ArtistPath);
        }

        if (!string.IsNullOrWhiteSpace(pathInfo.CoverPath))
        {
            Directory.CreateDirectory(pathInfo.CoverPath);
        }

        if (!ShouldWriteArtworkSidecar(config)
            || string.IsNullOrWhiteSpace(pathInfo.CoverPath)
            || string.IsNullOrWhiteSpace(tempCoverPath)
            || !IOFile.Exists(tempCoverPath))
        {
            return;
        }

        var baseFileName = BuildAlbumArtworkBaseFileName(sourceTrack, settings);
        if (string.IsNullOrWhiteSpace(baseFileName))
        {
            baseFileName = CoverTag;
        }

        var formats = ResolveLocalArtworkFormats(settings.LocalArtworkFormat);
        using var image = await Image.LoadAsync(tempCoverPath, token);
        foreach (var format in formats)
        {
            var coverPath = Path.Join(pathInfo.CoverPath, $"{baseFileName}.{format}");
            if (IOFile.Exists(coverPath))
            {
                continue;
            }

            if (format == "png")
            {
                await image.SaveAsPngAsync(coverPath, new PngEncoder(), token);
            }
            else
            {
                await image.SaveAsJpegAsync(
                    coverPath,
                    new JpegEncoder { Quality = Math.Clamp(settings.JpegImageQuality, 1, 100) },
                    token);
            }
        }
    }

    private static IReadOnlyList<string> ResolveLocalArtworkFormats(string? configured)
    {
        var formats = (configured ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.TrimStart('.').ToLowerInvariant())
            .Where(value => value is "jpg" or "jpeg" or "png")
            .Select(value => value == "jpeg" ? "jpg" : value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return formats.Count == 0 ? ["jpg"] : formats;
    }

    private static bool TrackHasEmbeddedArtwork(string filePath, AutoTagRunnerConfig config, string platformId)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var extension = Path.GetExtension(filePath);
            return HasTag(file, extension, SupportedTag.AlbumArt, config, platformId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string? TryResolveFolderArtworkPath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var preferredNames = new[]
        {
            CoverTag,
            "folder",
            "front",
            AlbumTag,
            "albumart",
            "artwork"
        };
        var preferredExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };

        return preferredNames
            .SelectMany(name => preferredExtensions.Select(ext => Path.Join(directory, name + ext)))
            .FirstOrDefault(IOFile.Exists);
    }

    private async Task<string?> DownloadCoverAsync(string url, CancellationToken token)
    {
        try
        {
            var tempPath = Path.Join(Path.GetTempPath(), $"autotag-cover-{Guid.NewGuid():N}.jpg");
            using var scope = _serviceScopeFactory.CreateScope();
            var imageDownloader = scope.ServiceProvider.GetRequiredService<ImageDownloader>();
            return await imageDownloader.DownloadImageAsync(
                url,
                tempPath,
                overwrite: "y",
                preferMaxQuality: true,
                cancellationToken: token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download cover art.");
            return null;
        }
    }

    private static void ApplyAlbumArt(TagLib.File file, string imagePath, bool coverDescriptionUtf8)
    {
        if (!IOFile.Exists(imagePath))
        {
            return;
        }

        var data = IOFile.ReadAllBytes(imagePath);
        var picture = new TagLib.Picture
        {
            Data = data,
            Type = TagLib.PictureType.FrontCover,
            MimeType = CoverArtMimeTypeResolver.Resolve(imagePath, data),
            Description = "Cover"
        };

        var extension = Path.GetExtension(file.Name);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
            id3.RemoveFrames("APIC");
            var apic = new TagLib.Id3v2.AttachmentFrame(picture)
            {
                TextEncoding = coverDescriptionUtf8 ? TagLib.StringType.UTF8 : TagLib.StringType.Latin1
            };
            id3.AddFrame(apic);
        }

        file.Tag.Pictures = new[] { picture };
    }

    private static void ApplyAlbumArtistGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        DeezSpoTagSettings runtimeSettings,
        List<string> normalizedExistingArtists,
        List<string> existingAlbumArtists,
        bool keepSingleArtistOnly)
    {
        var singleAlbumArtist = runtimeSettings.Tags?.SingleAlbumArtist ?? true;
        var normalizedExistingAlbumArtists = SplitArtistCredits(existingAlbumArtists);
        var normalizedIncomingAlbumArtists = SplitArtistCredits(sourceTrack.AlbumArtists);
        if (singleAlbumArtist)
        {
            ApplySingleAlbumArtistGuard(
                effectiveTagSettings,
                sourceTrack,
                normalizedExistingArtists,
                normalizedExistingAlbumArtists);
            return;
        }

        var albumArtistsMatchOrPreferred = normalizedExistingAlbumArtists.Count > 0
            && (AreArtistCreditsEquivalent(normalizedExistingAlbumArtists, normalizedIncomingAlbumArtists)
                || (!keepSingleArtistOnly
                    && ShouldPreferSourceArtistCredits(normalizedExistingAlbumArtists, normalizedIncomingAlbumArtists)));
        if (!albumArtistsMatchOrPreferred)
        {
            return;
        }

        sourceTrack.AlbumArtists = normalizedExistingAlbumArtists.ToList();
        if (effectiveTagSettings.AlbumArtist)
        {
            effectiveTagSettings.AlbumArtist = false;
        }
    }

    private static void ApplySingleAlbumArtistGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        List<string> normalizedExistingArtists,
        List<string> normalizedExistingAlbumArtists)
    {
        string? preferredAlbumArtist = null;
        for (var i = 0; i < normalizedExistingAlbumArtists.Count; i++)
        {
            var candidate = normalizedExistingAlbumArtists[i];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                preferredAlbumArtist = candidate;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(preferredAlbumArtist) && normalizedExistingArtists.Count > 0)
        {
            preferredAlbumArtist = normalizedExistingArtists[0];
        }
        if (string.IsNullOrWhiteSpace(preferredAlbumArtist))
        {
            return;
        }

        sourceTrack.AlbumArtists = new List<string> { preferredAlbumArtist };
        if (effectiveTagSettings.AlbumArtist
            && normalizedExistingAlbumArtists.Count > 0
            && AreArtistPrimaryCompatible(normalizedExistingAlbumArtists[0], preferredAlbumArtist))
        {
            effectiveTagSettings.AlbumArtist = false;
        }
    }
}
