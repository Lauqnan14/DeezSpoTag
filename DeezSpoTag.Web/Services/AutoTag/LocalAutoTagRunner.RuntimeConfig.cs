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

    private static void ApplyRuntimeConfigOverrides(DeezSpoTagSettings settings, AutoTagRunnerConfig config)
    {
        ApplyFolderStructureOverrides(settings, config.FolderStructure);

        if (config.SaveArtwork.HasValue)
        {
            settings.SaveArtwork = config.SaveArtwork.Value;
        }

        if (config.DlAlbumcoverForPlaylist.HasValue)
        {
            settings.DlAlbumcoverForPlaylist = config.DlAlbumcoverForPlaylist.Value;
        }

        if (config.SaveArtworkArtist.HasValue)
        {
            settings.SaveArtworkArtist = config.SaveArtworkArtist.Value;
        }

        if (config.SaveAnimatedArtwork.HasValue)
        {
            settings.SaveAnimatedArtwork = config.SaveAnimatedArtwork.Value;
        }

        if (config.SaveSquareAnimatedArtwork.HasValue)
        {
            settings.SaveSquareAnimatedArtwork = config.SaveSquareAnimatedArtwork.Value;
        }

        if (config.SaveTallAnimatedArtwork.HasValue)
        {
            settings.SaveTallAnimatedArtwork = config.SaveTallAnimatedArtwork.Value;
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkFormats))
        {
            settings.AnimatedArtworkFormats = config.AnimatedArtworkFormats.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.CoverImageTemplate))
        {
            settings.CoverImageTemplate = config.CoverImageTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkSquareFileName))
        {
            settings.AnimatedArtworkSquareFileName = config.AnimatedArtworkSquareFileName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkTallFileName))
        {
            settings.AnimatedArtworkTallFileName = config.AnimatedArtworkTallFileName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.ArtistImageTemplate))
        {
            settings.ArtistImageTemplate = config.ArtistImageTemplate.Trim();
        }

        var normalizedArtworkFormat = NormalizeLocalArtworkFormat(config.LocalArtworkFormat);
        if (!string.IsNullOrWhiteSpace(normalizedArtworkFormat))
        {
            settings.LocalArtworkFormat = normalizedArtworkFormat;
        }

        if (config.EmbedMaxQualityCover.HasValue)
        {
            settings.EmbedMaxQualityCover = config.EmbedMaxQualityCover.Value;
        }

        if (config.AnimatedArtworkMaxSizeMb.HasValue)
        {
            settings.AnimatedArtworkMaxSizeMb = Math.Clamp(config.AnimatedArtworkMaxSizeMb.Value, 1, 200);
        }

        if (config.JpegImageQuality.HasValue)
        {
            settings.JpegImageQuality = Math.Clamp(config.JpegImageQuality.Value, 1, 100);
        }
        if (config.EmbeddedArtworkSize.HasValue)
        {
            settings.EmbeddedArtworkSize = Math.Clamp(config.EmbeddedArtworkSize.Value, 100, 5000);
        }
        if (config.LocalArtworkSize.HasValue)
        {
            settings.LocalArtworkSize = Math.Clamp(config.LocalArtworkSize.Value, 100, 5000);
        }
        if (config.AppleArtworkSize.HasValue)
        {
            settings.AppleArtworkSize = Math.Clamp(config.AppleArtworkSize.Value, 100, 5000);
        }
        if (!string.IsNullOrWhiteSpace(config.AppleArtworkSizeText))
        {
            settings.AppleArtworkSizeText = config.AppleArtworkSizeText.Trim();
        }
    }

    private Dictionary<string, HashSet<SupportedTag>> BuildPlatformSupportedTags()
    {
        var map = (_platformRegistry?.DescribeAll() ?? Array.Empty<AutoTagPlatformDescriptor>())
            .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.Id))
            .GroupBy(descriptor => descriptor.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(descriptor => descriptor.SupportedTags).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);
        map[LyricsPlatform] = new HashSet<SupportedTag>
        {
            SupportedTag.SyncedLyrics,
            SupportedTag.UnsyncedLyrics,
            SupportedTag.TtmlLyrics
        };
        return map;
    }

    private static HashSet<string> BuildConfiguredTagSet(IEnumerable<string>? tags)
    {
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tags == null)
        {
            return configured;
        }

        foreach (var trimmed in tags
            .Select(static rawTag => rawTag?.Trim())
            .Where(static trimmed => !string.IsNullOrWhiteSpace(trimmed)))
        {
            configured.Add(trimmed!);
            var normalized = NormalizeConfiguredTagKey(trimmed!);
            if (!string.Equals(normalized, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                configured.Add(normalized);
            }
        }

        return configured;
    }

    private static string NormalizeConfiguredTagKey(string tag)
    {
        return tag.Trim().ToLowerInvariant() switch
        {
            YearTag => ReleaseDateTag,
            DateTag => ReleaseDateTag,
            LengthTag => DurationTag,
            LyricsTag => UnsyncedLyricsTag,
            CoverTag => AlbumArtTag,
            _ => tag.Trim()
        };
    }

    private static T LoadConfig<T>(JsonObject? custom, string key, T fallback) where T : class, new()
    {
        if (custom == null || !custom.TryGetPropertyValue(key, out var node) || node == null)
        {
            return fallback;
        }

        try
        {
            var parsed = node.Deserialize<T>(CaseInsensitiveJsonOptions);
            return parsed ?? fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fallback;
        }
    }

    private static AutoTagRunnerConfig NormalizeConfig(AutoTagRunnerConfig? raw)
    {
        raw ??= new AutoTagRunnerConfig();
        var effectiveSaveArtwork = raw.SaveArtwork ?? false;
        return new AutoTagRunnerConfig
        {
            Platforms = raw.Platforms ?? new List<string>(),
            DownloadTagSource = raw.DownloadTagSource,
            Path = raw.Path,
            TargetFiles = raw.TargetFiles?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList(),
            PriorityTargetFiles = raw.PriorityTargetFiles?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList(),
            EditionConflictReview = raw.EditionConflictReview,
            Tags = raw.Tags ?? new List<string>(),
            OverwriteTags = raw.OverwriteTags ?? new List<string>(),
            Separators = raw.Separators == null
                ? null
                : new AutoTagSeparators
                {
                    Id3 = raw.Separators.Id3,
                    Vorbis = raw.Separators.Vorbis,
                    Mp4 = raw.Separators.Mp4
            },
            Overwrite = raw.Overwrite,
            MergeGenres = raw.MergeGenres,
            Camelot = raw.Camelot,
            ShortTitle = raw.ShortTitle,
            Strictness = raw.Strictness,
            MatchDuration = raw.MatchDuration,
            MaxDurationDifference = raw.MaxDurationDifference,
            MatchById = raw.MatchById,
            EnableShazam = raw.EnableShazam,
            ForceShazam = raw.ForceShazam,
            EnhancementUntrustedTargets = raw.EnhancementUntrustedTargets,
            ConflictResolution = raw.ConflictResolution,
            SkipTagged = raw.SkipTagged,
            IncludeSubfolders = raw.IncludeSubfolders,
            ParseFilename = raw.ParseFilename,
            Id3v24 = raw.Id3v24,
            TrackNumberLeadingZeroes = raw.TrackNumberLeadingZeroes,
            StylesOptions = raw.StylesOptions,
            MultipleMatches = raw.MultipleMatches,
            TitleRegex = raw.TitleRegex,
            Custom = raw.Custom,
            StylesCustomTag = raw.StylesCustomTag == null
                ? null
                : new AutoTagStylesCustomTag
                {
                    Id3 = raw.StylesCustomTag.Id3,
                    Vorbis = raw.StylesCustomTag.Vorbis,
                    Mp4 = raw.StylesCustomTag.Mp4
                },
            Id3CommLang = raw.Id3CommLang,
            CapitalizeGenres = raw.CapitalizeGenres,
            TracknameTemplate = raw.TracknameTemplate,
            FolderStructure = raw.FolderStructure,
            SaveArtwork = effectiveSaveArtwork,
            DlAlbumcoverForPlaylist = raw.DlAlbumcoverForPlaylist,
            SaveArtworkArtist = raw.SaveArtworkArtist,
            SaveAnimatedArtwork = raw.SaveAnimatedArtwork,
            SaveSquareAnimatedArtwork = raw.SaveSquareAnimatedArtwork,
            SaveTallAnimatedArtwork = raw.SaveTallAnimatedArtwork,
            AnimatedArtworkFormats = raw.AnimatedArtworkFormats,
            CoverImageTemplate = raw.CoverImageTemplate,
            AnimatedArtworkSquareFileName = raw.AnimatedArtworkSquareFileName,
            AnimatedArtworkTallFileName = raw.AnimatedArtworkTallFileName,
            ArtistImageTemplate = raw.ArtistImageTemplate,
            LocalArtworkFormat = raw.LocalArtworkFormat,
            MaterializeToTemplatePath = raw.MaterializeToTemplatePath,
            OrganizeSidecarsIntoTemplateFolders = raw.OrganizeSidecarsIntoTemplateFolders,
            EmbedMaxQualityCover = raw.EmbedMaxQualityCover,
            JpegImageQuality = raw.JpegImageQuality,
            EmbeddedArtworkSize = raw.EmbeddedArtworkSize,
            LocalArtworkSize = raw.LocalArtworkSize,
            AppleArtworkSize = raw.AppleArtworkSize,
            AppleArtworkSizeText = raw.AppleArtworkSizeText,
            AnimatedArtworkMaxSizeMb = raw.AnimatedArtworkMaxSizeMb,
            Technical = raw.Technical,
            ProfileId = raw.ProfileId,
            ProfileName = raw.ProfileName,
            LibraryWideEnhancementBatchSize = raw.LibraryWideEnhancementBatchSize,
            ManualReleasePreference = NormalizeManualReleasePreference(raw.ManualReleasePreference),
            ManualDestinationFolderId = raw.ManualDestinationFolderId,
            DestinationFolderId = raw.DestinationFolderId,
            DestinationFolderScopes = raw.DestinationFolderScopes?
                .Where(scope => scope.Id > 0 && !string.IsNullOrWhiteSpace(scope.RootPath))
                .Select(scope => new AutoTagDestinationFolderScope
                {
                    Id = scope.Id,
                    RootPath = scope.RootPath.Trim()
                })
                .ToList()
        };
    }

    private static TagSettings BuildTagSettings(AutoTagRunnerConfig config, DeezSpoTagSettings runtimeSettings)
    {
        var settings = new TagSettings
        {
            Title = false,
            Artist = false,
            Artists = false,
            Album = false,
            AlbumArtist = false,
            TrackNumber = false,
            TrackTotal = false,
            DiscNumber = false,
            DiscTotal = false,
            Genre = false,
            Label = false,
            Bpm = false,
            Isrc = false,
            Explicit = false,
            Length = false,
            Date = false,
            Year = false,
            Cover = false,
            Barcode = false,
            ReplayGain = false,
            Copyright = false,
            Lyrics = false,
            SyncedLyrics = false,
            Composer = false,
            InvolvedPeople = false,
            Source = false,
            Url = false,
            TrackId = false,
            ReleaseId = false,
            Rating = false,
            SavePlaylistAsCompilation = runtimeSettings.Tags?.SavePlaylistAsCompilation ?? false,
            UseNullSeparator = runtimeSettings.Tags?.UseNullSeparator ?? false,
            SaveID3v1 = runtimeSettings.Tags?.SaveID3v1 ?? true,
            MultiArtistSeparator = runtimeSettings.Tags?.MultiArtistSeparator ?? MultiArtistSeparatorDefault,
            SingleAlbumArtist = runtimeSettings.Tags?.SingleAlbumArtist ?? true,
            CoverDescriptionUTF8 = runtimeSettings.Tags?.CoverDescriptionUTF8 ?? true
        };

        foreach (var tag in config.Tags.Where(tag => TagSettingsAppliers.ContainsKey(tag.Trim())))
        {
            TagSettingsAppliers[tag.Trim()](settings);
        }
        if (WantsArtworkFromSettings(config, runtimeSettings))
        {
            settings.Cover = true;
        }

        return settings;
    }

    private static List<string> SanitizeGenres(
        IEnumerable<string> values,
        IReadOnlyDictionary<string, string>? genreAliasMap = null,
        IEnumerable<string>? genreBlockList = null,
        bool splitComposite = false)
    {
        return GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
            values,
            genreAliasMap,
            splitComposite,
            genreBlockList ?? BlockedGenres);
    }

    private static string SanitizeLogValue(string? value)
    {
        return LogSanitizer.OneLine(value);
    }
}
