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

public sealed partial class LocalAutoTagRunner
{

    private static ProviderTagPlan BuildProviderTagPlan(AutoTagFileRunContext context)
    {
        var configured = context.Plan.Config.Tags
            .Select(tag => tag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => SupportedTagMap.TryGetValue(tag!, out var mapped) ? (SupportedTag?)mapped : null)
            .Where(tag => tag.HasValue)
            .Select(tag => tag!.Value)
            .ToHashSet();

        if (context.Plan.PlatformSupportedTags.TryGetValue(context.Platform, out var supported))
        {
            configured.IntersectWith(supported);
        }

        var retained = new HashSet<SupportedTag>();
        var eligible = new HashSet<SupportedTag>();
        try
        {
            using var file = TagLib.File.Create(context.File);
            var extension = Path.GetExtension(context.File);
            foreach (var tag in configured)
            {
                if (!ShouldOverwriteTag(context.Plan.Config, tag)
                    && HasTag(file, extension, tag, context.Plan.Config, context.Platform))
                {
                    retained.Add(tag);
                }
                else
                {
                    eligible.Add(tag);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            eligible.UnionWith(configured);
        }

        return new ProviderTagPlan(configured, eligible, retained);
    }

    private static void ApplyTechnicalOverrides(DeezSpoTagSettings settings, TechnicalTagSettings? technical)
    {
        if (technical == null)
        {
            return;
        }

        TechnicalLyricsSettingsApplier.Apply(settings, technical);
        settings.Tags ??= new TagSettings();

        settings.DateFormat = technical.DateFormat;
        settings.AlbumVariousArtists = technical.AlbumVariousArtists;
        settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
        settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
        settings.FeaturedToTitle = technical.FeaturedToTitle;
        settings.TitleCasing = technical.TitleCasing;
        settings.ArtistCasing = technical.ArtistCasing;

        settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
        settings.Tags.UseNullSeparator = technical.UseNullSeparator;
        settings.Tags.SaveID3v1 = technical.SaveID3v1;
        settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator;
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
        settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
    }

    private static void ApplyFolderStructureOverrides(DeezSpoTagSettings settings, FolderStructureSettings? folderStructure)
    {
        if (folderStructure == null)
        {
            return;
        }

        settings.CreateArtistFolder = folderStructure.CreateArtistFolder;
        settings.CreateAlbumFolder = folderStructure.CreateAlbumFolder;
        settings.CreateCDFolder = folderStructure.CreateCDFolder;
        settings.CreateStructurePlaylist = folderStructure.CreateStructurePlaylist;
        settings.CreateSingleFolder = folderStructure.CreateSingleFolder;
        settings.CreatePlaylistFolder = folderStructure.CreatePlaylistFolder;

        if (!string.IsNullOrWhiteSpace(folderStructure.ArtistNameTemplate))
        {
            settings.ArtistNameTemplate = folderStructure.ArtistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.AlbumNameTemplate))
        {
            settings.AlbumNameTemplate = folderStructure.AlbumNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.PlaylistNameTemplate))
        {
            settings.PlaylistNameTemplate = folderStructure.PlaylistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.IllegalCharacterReplacer))
        {
            settings.IllegalCharacterReplacer = folderStructure.IllegalCharacterReplacer.Trim();
        }
    }

    private static string? NormalizeManualReleasePreference(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            AutoTagReleaseCategory.Album => AutoTagReleaseCategory.Album,
            AutoTagReleaseCategory.Single => AutoTagReleaseCategory.Single,
            _ => null
        };

    private static int? ParsePositiveInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return int.TryParse(trimmed, out var value) && value > 0 ? value : null;
    }

    private async Task<TagFileWriteResult> TagFileAsync(
        string filePath,
        AutoTagTrack track,
        TagSettings tagSettings,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        string platformId,
        CancellationToken token)
    {
        EnsureReleaseCategory(track);
        var separator = ResolveSeparatorForFormat(config, Path.GetExtension(filePath));
        ApplyArtistAliasPreference(track);
        var effectiveTagSettings = ApplyOverwriteRules(filePath, tagSettings, config, platformId, track, settings);
        NormalizeTrackArtistsForTagging(track, effectiveTagSettings.SingleAlbumArtist);
        var coreTrack = BuildCoreTrack(track, separator, effectiveTagSettings.SingleAlbumArtist, settings);
        string? tempCoverPath = null;
        var shouldPrepareTemplateArtworkSidecar = ShouldPrepareTemplateArtworkSidecar(config);

        if ((effectiveTagSettings.Cover || shouldPrepareTemplateArtworkSidecar) && !string.IsNullOrWhiteSpace(track.Art))
        {
            tempCoverPath = TryResolveExistingCoverSidecar(filePath, track, coreTrack, config, settings)
                ?? await DownloadCoverAsync(track.Art, token);
        }

        if (effectiveTagSettings.Cover &&
            string.IsNullOrWhiteSpace(tempCoverPath) &&
            !TrackHasEmbeddedArtwork(filePath, config, platformId))
        {
            tempCoverPath = TryResolveFolderArtworkPath(filePath);
        }

        var writeResult = await WriteTagsOnetaggerStyleAsync(
            new TagWriteRequest
            {
                FilePath = filePath,
                SourceTrack = track,
                CoreTrack = coreTrack,
                EffectiveTagSettings = effectiveTagSettings,
                Config = config,
                Settings = settings,
                PlatformId = platformId,
                Separator = separator,
                TempCoverPath = tempCoverPath
            },
            token);
        await EnsureTemplateFoldersAndArtworkSidecarAsync(
            track,
            coreTrack,
            config,
            settings,
            filePath,
            tempCoverPath,
            token);
        if (!IsMp4Family(Path.GetExtension(filePath)))
        {
            writeResult.AttemptedTags.UnionWith(await ApplyCustomTagsAsync(
                filePath,
                track,
                config,
                platformId,
                effectiveTagSettings.UseNullSeparator));
        }

        if (!string.IsNullOrWhiteSpace(tempCoverPath) && !string.Equals(Path.GetDirectoryName(tempCoverPath), Path.GetDirectoryName(filePath), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                IOFile.Delete(tempCoverPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // best effort
            }
        }

        return writeResult;
    }

    /// <summary>
    /// Rewrites the track's artist credits, album artists, featured names, and
    /// "feat." strings inside track/album titles so every user-defined alias
    /// becomes its preferred name before tags are written. Applies to every
    /// tagging run — enrichment, enhancement, and merge-triggered runs alike.
    /// </summary>
    private static void ApplyArtistAliasPreference(AutoTagTrack track)
    {
        try
        {
            track.Artists = RewriteCreditList(track.Artists);
            track.AlbumArtists = RewriteCreditList(track.AlbumArtists);
            if (!string.IsNullOrWhiteSpace(track.Title))
            {
                track.Title = DeezSpoTag.Services.Library.ArtistAliasGateway.ResolveCredit(track.Title);
            }

            if (!string.IsNullOrWhiteSpace(track.Album))
            {
                track.Album = DeezSpoTag.Services.Library.ArtistAliasGateway.ResolveCredit(track.Album);
            }

            foreach (var key in track.Other.Keys.ToList())
            {
                track.Other[key] = RewriteCreditList(track.Other[key]);
            }
        }
        catch
        {
            // Alias resolution must never break tagging.
        }
    }

    private async Task<TagFileWriteResult> WriteTagsOnetaggerStyleAsync(
        TagWriteRequest request,
        CancellationToken token)
    {
        var context = BuildTagWriteExecutionContext(request);
        var chapterSnapshot = AtlTagHelper.CaptureChapters(context.FilePath, context.Extension, _logger);

        using var file = TagLib.File.Create(context.FilePath);
        PrepareId3Version(file, context);

        var tagWriteContext = new TagWriteContext(
            file,
            context.Extension,
            context.Config,
            context.Separator,
            context.PlatformId,
            context.EffectiveTagSettings.UseNullSeparator,
            context.GenreAliasMap,
            context.GenreBlockList,
            context.SplitCompositeGenres,
            context.AttemptedTags);
        ApplyPrimaryTagWrites(tagWriteContext, context);
        ApplyAudioFeatureTagWrites(tagWriteContext, context);
        ApplyGenreAndStyleTagWrites(file, tagWriteContext, context);
        ApplyReleaseAndMetadataTagWrites(file, tagWriteContext, context);
        ApplyTrackAndLyricsTagWrites(file, tagWriteContext, context);
        ApplyAlbumArtTagWrite(file, context);
        file.Save();
        RemoveId3v1TagIfDisabled(file, context);

        AtlTagHelper.RestoreChapters(context.FilePath, chapterSnapshot, _logger);

        var sidecarWriteResult = await WriteLyricsSidecarsAsync(context, token);
        CleanupUpgradedTxtSidecar(context, sidecarWriteResult);

        if (sidecarWriteResult.WroteTtmlSidecar)
        {
            context.AttemptedTags.Add(SupportedTag.TtmlLyrics);
        }

        return new TagFileWriteResult(context.AttemptedTags);
    }

    private static TagWriteExecutionContext BuildTagWriteExecutionContext(TagWriteRequest request)
    {
        var extension = Path.GetExtension(request.FilePath);
        var enabledTags = BuildConfiguredTagSet(request.Config.Tags);
        var normalizeGenreTags = request.Settings.NormalizeGenreTags;
        var genreAliasMap = normalizeGenreTags
            ? GenreTagAliasNormalizer.BuildAliasMap(request.Settings.GenreTagAliasRules)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var genreBlockList = GenreTagAliasNormalizer.NormalizeBlockedValues(request.Settings.GenreTagBlockList);
        var allowsSyncedByToggle = request.Settings.SyncedLyrics;
        var allowsUnsyncedByToggle = request.Settings.SaveLyrics;
        var allowsLyricsBySettings = allowsSyncedByToggle || allowsUnsyncedByToggle;
        var selectedLyricsTypes = ParseLyricsTypeSelection(request.Settings.LrcType);
        var allowsSyncedType = allowsSyncedByToggle
            && (selectedLyricsTypes.Contains(LyricsTag) || selectedLyricsTypes.Contains(SyllableLyricsType));
        var allowsUnsyncedType = allowsUnsyncedByToggle && selectedLyricsTypes.Contains(UnsyncedLyricsType);
        var allowsTtmlByFormat = allowsSyncedByToggle
            && selectedLyricsTypes.Contains(TtmlLyricsType)
            && ParseLyricsFormatSelection(request.Settings.LrcFormat).Contains("ttml");
        var allowsLrcByFormat = allowsSyncedByToggle
            && ParseLyricsFormatSelection(request.Settings.LrcFormat).Contains("lrc");
        var sidecarState = GetLyricsSidecarState(request.FilePath);

        return new TagWriteExecutionContext
        {
            FilePath = request.FilePath,
            SourceTrack = request.SourceTrack,
            CoreTrack = request.CoreTrack,
            EffectiveTagSettings = request.EffectiveTagSettings,
            Config = request.Config,
            Settings = request.Settings,
            PlatformId = request.PlatformId,
            Separator = request.Separator,
            TempCoverPath = request.TempCoverPath,
            Extension = extension,
            EnabledTags = enabledTags,
            GenreAliasMap = genreAliasMap,
            GenreBlockList = genreBlockList,
            SplitCompositeGenres = normalizeGenreTags,
            AllowsLyricsBySettings = allowsLyricsBySettings,
            AllowsSyncedType = allowsSyncedType,
            AllowsUnsyncedType = allowsUnsyncedType,
            AllowsLrcByFormat = allowsLrcByFormat,
            AllowsTtmlByFormat = allowsTtmlByFormat,
            SidecarState = sidecarState,
            ShouldSkipEmbeddedLyrics = sidecarState.HasAny
        };
    }

    private static void ApplyPrimaryTagWrites(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteTitleTag(tagWriteContext, context);
        WriteVersionTag(tagWriteContext, context);
        WriteArtistTag(tagWriteContext, context);
        WriteArtistsTag(tagWriteContext, context);
        WriteAlbumArtistTag(tagWriteContext, context);
        WriteAlbumTag(tagWriteContext, context);
        WriteKeyTag(tagWriteContext, context);
        WriteBpmTag(tagWriteContext, context);
        WriteLabelTag(tagWriteContext, context);
    }

    private static void ApplyAudioFeatureTagWrites(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteAudioFeatureTag(tagWriteContext, context, "danceability", DanceabilityTag, SupportedTag.Danceability, context.SourceTrack.Danceability);
        WriteAudioFeatureTag(tagWriteContext, context, "energy", EnergyTag, SupportedTag.Energy, context.SourceTrack.Energy);
        WriteAudioFeatureTag(tagWriteContext, context, "valence", ValenceTag, SupportedTag.Valence, context.SourceTrack.Valence);
        WriteAudioFeatureTag(tagWriteContext, context, "acousticness", AcousticnessTag, SupportedTag.Acousticness, context.SourceTrack.Acousticness);
        WriteAudioFeatureTag(tagWriteContext, context, "instrumentalness", InstrumentalnessTag, SupportedTag.Instrumentalness, context.SourceTrack.Instrumentalness);
        WriteAudioFeatureTag(tagWriteContext, context, "speechiness", SpeechinessTag, SupportedTag.Speechiness, context.SourceTrack.Speechiness);
        WriteAudioFeatureTag(tagWriteContext, context, "loudness", LoudnessTag, SupportedTag.Loudness, context.SourceTrack.Loudness);
        WriteAudioFeatureTag(tagWriteContext, context, "tempo", TempoTag, SupportedTag.Tempo, context.SourceTrack.Tempo);
        WriteAudioFeatureTag(tagWriteContext, context, "liveness", LivenessTag, SupportedTag.Liveness, context.SourceTrack.Liveness);

        if (context.EnabledTags.Contains("timeSignature") && context.SourceTrack.TimeSignature.HasValue)
        {
            SetRaw(
                tagWriteContext,
                TimeSignatureTag,
                SupportedTag.TimeSignature,
                new List<string> { context.SourceTrack.TimeSignature.Value.ToString(CultureInfo.InvariantCulture) });
        }
    }

    private static void ApplyGenreAndStyleTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        var genres = SanitizeGenres(context.CoreTrack.Album?.Genre ?? new List<string>(), context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
        var styles = NormalizeStyleValues(context.SourceTrack.Styles, context.Separator);
        (genres, styles) = ApplyStylesOptions(genres, styles, context.Config.StylesOptions);

        if (context.EnabledTags.Contains(GenreTag) && context.EffectiveTagSettings.Genre && genres.Count > 0)
        {
            var existingGenres = ReadExistingGenre(context.FilePath);
            if (context.Config.MergeGenres)
            {
                var existing = SanitizeGenres(existingGenres, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
                var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);
                genres.AddRange(existing.Where(genreSet.Add));
            }

            genres = SanitizeGenres(genres, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
            if (context.Config.CapitalizeGenres)
            {
                genres = genres.Select(CapitalizeGenre).ToList();
            }
            genres = GenreTagAliasNormalizer.DedupeValues(genres, context.GenreBlockList);

            // Strict no-op: when the merge (after alias/split/capitalize) produced the
            // exact set already on the file — same values, same casing, any order —
            // the genre write is skipped entirely instead of touching the tag block.
            if (!GenreWriteAddsNothing(genres, existingGenres))
            {
                SetField(tagWriteContext, new TagFieldBinding("TCON", Mp4GenreTag, "©gen", SupportedTag.Genre), genres);
            }
        }

        if (!context.EnabledTags.Contains(StyleTag) || styles.Count == 0)
        {
            return;
        }

        var styleTagName = ResolveStylesTagName(context.Config, context.Extension);
        var styleValues = styles;
        if (context.Config.MergeGenres)
        {
            var existingStyles = NormalizeStyleValues(
                ReadExistingRawTag(file, context.Extension, styleTagName),
                context.Separator);
            var existingStyleSet = new HashSet<string>(existingStyles, StringComparer.OrdinalIgnoreCase);
            existingStyles.AddRange(styleValues.Where(existingStyleSet.Add));
            styleValues = existingStyles;
        }

        var rawName = context.Config.StylesOptions.Equals("customTag", StringComparison.OrdinalIgnoreCase)
            ? styleTagName
            : ResolveFieldRawName(SupportedTag.Style, ResolveFormatName(context.Extension), context.Config);
        SetRaw(tagWriteContext, rawName, SupportedTag.Style, styleValues);
    }

    private static (List<string> Genres, List<string> Styles) ApplyStylesOptions(
        List<string> genres,
        List<string> styles,
        string stylesOption)
    {
        switch (stylesOption.ToLowerInvariant())
        {
            case "onlygenres":
                styles = new List<string>();
                break;
            case "onlystyles":
                genres = new List<string>();
                break;
            case "mergetogenres":
                var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);
                genres.AddRange(styles.Where(genreSet.Add));
                break;
            case "mergetostyles":
                var styleSet = new HashSet<string>(styles, StringComparer.OrdinalIgnoreCase);
                styles.AddRange(genres.Where(styleSet.Add));
                break;
            case "stylestogenre":
                genres = styles.ToList();
                break;
            case "genrestostyle":
                styles = genres.ToList();
                break;
        }

        return (genres, styles);
    }

    private static void ApplyReleaseAndMetadataTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        WriteReleaseDateTag(file, context);
        WritePublishDateTag(file, context);
        WriteUrlTag(tagWriteContext, context);
        WriteTrackIdTag(tagWriteContext, context);
        WriteReleaseIdTag(tagWriteContext, context);
        WriteSourceIdentityTags(tagWriteContext, context);
        WriteCatalogNumberTag(tagWriteContext, context);
        WriteDurationTag(tagWriteContext, context);
        WriteRemixerTag(tagWriteContext, context);
        WriteIsrcTag(tagWriteContext, context);
        WriteMoodTag(tagWriteContext, context);
        WriteActivityTag(tagWriteContext, context);
    }

    private static void MarkAttemptedIfPresent(TagWriteExecutionContext context, TagLib.File file, SupportedTag tag)
    {
        if (HasTag(file, context.Extension, tag, context.Config, context.PlatformId))
        {
            context.AttemptedTags.Add(tag);
        }
    }

    private Task<HashSet<SupportedTag>> ApplyCustomTagsAsync(
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        string platformId,
        bool useNullSeparator)
    {
        if (config.Tags.Count == 0)
        {
            return Task.FromResult(new HashSet<SupportedTag>());
        }

        var attemptedTags = new HashSet<SupportedTag>();
        try
        {
            var extension = Path.GetExtension(filePath);
            var chapterSnapshot = AtlTagHelper.CaptureChapters(filePath, extension, _logger);
            using var file = TagLib.File.Create(filePath);
            var enabledTags = BuildConfiguredTagSet(config.Tags);
            var separator = ResolveArtistSeparator(config, filePath);
            var writes = BuildCustomTagWrites(track, config, platformId, extension, file);

            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
                ApplyId3CustomTags(id3, writes, config, separator, useNullSeparator, enabledTags, attemptedTags);
            }
            else if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
            {
                var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
                ApplyVorbisCustomTags(vorbis, writes, config, separator, enabledTags, attemptedTags);
            }
            else if (IsMp4Family(extension))
            {
                var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
                ApplyAppleCustomTags(apple, writes, config, separator, enabledTags, attemptedTags);
            }

            file.Save();
            AtlTagHelper.RestoreChapters(filePath, chapterSnapshot, _logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed applying custom tags for {File}", SanitizeLogValue(filePath));
        }

        return Task.FromResult(attemptedTags);
    }

    private static string[] ApplySeparator(List<string> values, string separator, bool useNullSeparator = false)
    {
        if (values.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (useNullSeparator)
        {
            return values.ToArray();
        }

        if (string.IsNullOrEmpty(separator))
        {
            return values.ToArray();
        }

        return new[] { string.Join(separator, values) };
    }

    private static List<CustomTagWrite> BuildCustomTagWrites(AutoTagTrack track, AutoTagRunnerConfig config, string platformId, string extension, TagLib.File file)
    {
        var writes = new List<CustomTagWrite>();
        var styleTagName = ResolveStylesTagName(config, extension);
        var format = ResolveFormatName(extension);

        if (track.Styles.Count > 0)
        {
            var separator = ResolveSeparatorForFormat(config, extension);
            var styleValues = NormalizeStyleValues(track.Styles, separator);
            if (config.MergeGenres)
            {
                var existing = NormalizeStyleValues(
                    ReadExistingRawTag(file, extension, styleTagName),
                    separator);
                var existingStyleSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                existing.AddRange(styleValues.Where(existingStyleSet.Add));
                styleValues = existing;
            }

            var styleRaw = config.StylesOptions.Equals("customTag", StringComparison.OrdinalIgnoreCase)
                ? styleTagName
                : ResolveFieldRawName(SupportedTag.Style, format, config);

            writes.Add(new CustomTagWrite(StyleTag, SupportedTag.Style, styleRaw, styleValues));
        }

        AddSingleValueCustomTagWrite(
            writes,
            "mood",
            SupportedTag.Mood,
            ResolveFieldRawName(SupportedTag.Mood, format, config),
            track.Mood);
        AddSingleValueCustomTagWrite(
            writes,
            "key",
            SupportedTag.Key,
            ResolveFieldRawName(SupportedTag.Key, format, config),
            track.Key);
        AddSingleValueCustomTagWrite(
            writes,
            VersionTag,
            SupportedTag.Version,
            ResolveFieldRawName(SupportedTag.Version, format, config),
            track.Version);

        if (track.Remixers.Count > 0)
        {
            writes.Add(new CustomTagWrite(RemixerTag, SupportedTag.Remixer, ResolveFieldRawName(SupportedTag.Remixer, format, config), track.Remixers.ToList()));
        }

        AddSingleValueCustomTagWrite(writes, "url", SupportedTag.URL, WwwAudioFileTag, track.Url);
        AddSingleValueCustomTagWrite(
            writes,
            CatalogNumberTag,
            SupportedTag.CatalogNumber,
            ResolveFieldRawName(SupportedTag.CatalogNumber, format, config),
            track.CatalogNumber);
        var platformKey = platformId.ToUpperInvariant();
        AddSingleValueCustomTagWrite(
            writes,
            TrackIdTag,
            SupportedTag.TrackId,
            $"{platformKey}_TRACK_ID",
            track.TrackId);
        AddSingleValueCustomTagWrite(
            writes,
            ReleaseIdTag,
            SupportedTag.ReleaseId,
            $"{platformKey}_RELEASE_ID",
            track.ReleaseId);
        AddSingleValueCustomTagWrite(writes, RecordingIdTag, SupportedTag.RecordingId, RecordingIdRawTag, track.RecordingId);
        AddSingleValueCustomTagWrite(writes, ArtistIdTag, SupportedTag.ArtistId, ArtistIdRawTag, track.ArtistId);
        AddSingleValueCustomTagWrite(writes, AlbumArtistIdTag, SupportedTag.AlbumArtistId, AlbumArtistIdRawTag, track.AlbumArtistId);
        AddSingleValueCustomTagWrite(writes, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, ReleaseGroupIdRawTag, track.ReleaseGroupId);
        AddSingleValueCustomTagWrite(writes, AlbumIdTag, SupportedTag.AlbumId, AlbumIdRawTag, track.AlbumId);
        AddSingleValueCustomTagWrite(writes, ReleaseStatusTag, SupportedTag.ReleaseStatus, ReleaseStatusRawTag, track.ReleaseStatus);
        AddSingleValueCustomTagWrite(writes, ReleaseCountryTag, SupportedTag.ReleaseCountry, ReleaseCountryRawTag, track.ReleaseCountry);
        AddSingleValueCustomTagWrite(writes, BarcodeTag, SupportedTag.Barcode, BarcodeRawTag, track.Barcode);
        AddSingleValueCustomTagWrite(writes, LyricistTag, SupportedTag.Lyricist, ResolveFieldRawName(SupportedTag.Lyricist, format, config), track.Lyricist);
        AddSingleValueCustomTagWrite(writes, PublisherTag, SupportedTag.Publisher, ResolveFieldRawName(SupportedTag.Publisher, format, config), track.Publisher);
        AddSingleValueCustomTagWrite(writes, DescriptionTag, SupportedTag.Description, ResolveFieldRawName(SupportedTag.Description, format, config), track.Description);
        if (track.Media.Count > 0)
        {
            writes.Add(new CustomTagWrite(MediaTag, SupportedTag.Media, MediaRawTag, track.Media.ToList()));
        }
        AddOtherTagWrites(writes, track.Other);
        AddMetaTagWrite(writes, config);

        return writes;
    }

    private static void AddSingleValueCustomTagWrite(
        List<CustomTagWrite> writes,
        string tagKey,
        SupportedTag supportedTag,
        string rawTagName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        writes.Add(new CustomTagWrite(tagKey, supportedTag, rawTagName, new List<string> { value }));
    }

    private static void ApplyId3CustomTags(
        TagLib.Id3v2.Tag tag,
        List<CustomTagWrite> writes,
        AutoTagRunnerConfig config,
        string separator,
        bool useNullSeparator,
        HashSet<string> enabledTags,
        HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasId3Raw(tag, write.RawTagName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            SetId3Raw(tag, write.RawTagName, write.Values, separator, useNullSeparator);
            attemptedTags.Add(write.SupportedTag);
        }
    }

    private static void ApplyVorbisCustomTags(TagLib.Ogg.XiphComment tag, List<CustomTagWrite> writes, AutoTagRunnerConfig config, string separator, HashSet<string> enabledTags, HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasVorbisRaw(tag, write.RawTagName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            SetVorbisRaw(tag, write.RawTagName, write.Values, separator);
            attemptedTags.Add(write.SupportedTag);
        }
    }
}
