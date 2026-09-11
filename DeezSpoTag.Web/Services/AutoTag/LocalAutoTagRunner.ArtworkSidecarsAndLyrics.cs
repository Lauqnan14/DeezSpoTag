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

public partial class LocalAutoTagRunner
{

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

    private static async Task<LyricsSidecarWriteResult> WriteLyricsSidecarsAsync(
        TagWriteExecutionContext context,
        CancellationToken token)
    {
        var wroteLrcSidecar = false;
        var wroteTtmlSidecar = false;
        var sidecarLrcLines = ResolveLrcSidecarLines(context.SourceTrack, context.FilePath, context.Settings);
        if (context.AllowsLyricsBySettings
            && context.AllowsLrcByFormat
            && (context.AllowsSyncedType || context.AllowsUnsyncedType)
            && sidecarLrcLines.Count > 0)
        {
            var lrcPath = BuildLyricsSidecarPath(context, ".lrc");
            if (!IOFile.Exists(lrcPath) || ShouldUpgradeLrcSidecarToWordTiming(context, lrcPath, sidecarLrcLines))
            {
                await IOFile.WriteAllLinesAsync(lrcPath, sidecarLrcLines, token);
                wroteLrcSidecar = true;
            }
        }

        var sidecarTtml = ResolveTtmlSidecarPayload(context.SourceTrack, context.FilePath);
        if (context.EnabledTags.Contains(TtmlLyricsTag)
            && context.AllowsLyricsBySettings
            && context.AllowsTtmlByFormat
            && AppleLyricsService.IsWordSyncedTtml(sidecarTtml))
        {
            var ttmlPath = BuildLyricsSidecarPath(context, TtmlExtension);
            if (!IOFile.Exists(ttmlPath) || ShouldUpgradeTtmlSidecarToWordTiming(ttmlPath, sidecarTtml))
            {
                await IOFile.WriteAllTextAsync(ttmlPath, sidecarTtml, token);
                wroteTtmlSidecar = true;
            }
        }

        return new LyricsSidecarWriteResult(wroteLrcSidecar, wroteTtmlSidecar);
    }

    private static bool ShouldUpgradeTtmlSidecarToWordTiming(string ttmlPath, string? incomingTtml)
    {
        try
        {
            var existing = IOFile.ReadAllText(ttmlPath);
            if (!AppleLyricsService.IsWordSyncedTtml(existing))
            {
                return true;
            }

            return AppleLyricsService.IsAppleNativeTtml(incomingTtml)
                && !AppleLyricsService.IsAppleNativeTtml(existing);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool ShouldUpgradeLrcSidecarToWordTiming(
        TagWriteExecutionContext context,
        string lrcPath,
        IReadOnlyList<string> sidecarLrcLines)
    {
        if (!context.Settings.PreferEnhancedLrc || !LrcContent.IsWordSynchronized(sidecarLrcLines))
        {
            return false;
        }

        try
        {
            return !LrcContent.IsWordSynchronized(IOFile.ReadAllLines(lrcPath));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static string BuildLyricsSidecarPath(TagWriteExecutionContext context, string extension)
    {
        if (context.Config.OrganizeSidecarsIntoTemplateFolders == true)
        {
            var pathInfo = BuildTemplatePathInfo(context.CoreTrack, context.Settings);
            if (!string.IsNullOrWhiteSpace(pathInfo.FilePath)
                && !string.IsNullOrWhiteSpace(pathInfo.Filename))
            {
                Directory.CreateDirectory(pathInfo.FilePath);
                return Path.Join(pathInfo.FilePath, $"{pathInfo.Filename}{extension}");
            }
        }

        return Path.ChangeExtension(context.FilePath, extension);
    }

    private void CleanupUpgradedTxtSidecar(TagWriteExecutionContext context, LyricsSidecarWriteResult sidecarWriteResult)
    {
        if (!context.SidecarState.HasTxt
            || (!context.SidecarState.HasLrc
                && !context.SidecarState.HasTtml
                && !sidecarWriteResult.WroteLrcSidecar
                && !sidecarWriteResult.WroteTtmlSidecar))
        {
            return;
        }

        try
        {
            IOFile.Delete(context.SidecarState.TxtPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to remove upgraded TXT lyrics sidecar {Path}", SanitizeLogValue(context.SidecarState.TxtPath));
            }
        }
    }

    private static bool ShouldAllowLyricsOtherTagKey(
        string key,
        bool allowsLyricsBySettings,
        bool allowsSyncedType,
        bool allowsUnsyncedType,
        bool allowsTtmlByFormat,
        bool allowLyricsPayloadWrites)
    {
        if (!IsLyricsPayloadKey(key))
        {
            return true;
        }

        if (!allowsLyricsBySettings || !allowLyricsPayloadWrites)
        {
            return false;
        }

        if (key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsSyncedType;
        }

        if (key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsUnsyncedType;
        }

        if (key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsSyncedType && allowsTtmlByFormat;
        }

        return allowsSyncedType || allowsUnsyncedType;
    }

    private static bool IsLyricsPayloadKey(string key)
    {
        return key.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(SyncedLyricsSourceFormatTag, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool HasAny, bool HasLrc, bool HasTtml, bool HasTxt, string TxtPath) GetLyricsSidecarState(string filePath)
    {
        var lrcPath = Path.ChangeExtension(filePath, ".lrc");
        var ttmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        var txtPath = Path.ChangeExtension(filePath, ".txt");
        var hasLrc = IOFile.Exists(lrcPath);
        var hasTtml = HasTimedTtmlSidecar(ttmlPath);
        var hasTxt = IOFile.Exists(txtPath);
        return (hasLrc || hasTtml || hasTxt, hasLrc, hasTtml, hasTxt, txtPath);
    }

    private static bool HasTimedTtmlSidecar(string path)
    {
        if (!IOFile.Exists(path))
        {
            return false;
        }

        try
        {
            return AppleLyricsService.IsWordSyncedTtml(IOFile.ReadAllText(path));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
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

    private static string ResolveStylesTagName(AutoTagRunnerConfig config, string extension)
    {
        if (config.StylesCustomTag == null)
        {
            return StyleUpperTag;
        }

        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Id3) ? StyleUpperTag : config.StylesCustomTag.Id3;
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Vorbis) ? StyleUpperTag : config.StylesCustomTag.Vorbis;
        }

        if (IsMp4Family(extension))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Mp4) ? StyleUpperTag : config.StylesCustomTag.Mp4;
        }

        return StyleUpperTag;
    }

    private static readonly Dictionary<string, SupportedTag> SupportedTagMap = CreateSupportedTagMap();

    private static Dictionary<string, SupportedTag> CreateSupportedTagMap()
    {
        var map = new Dictionary<string, SupportedTag>(StringComparer.OrdinalIgnoreCase)
        {
            [TitleTag] = SupportedTag.Title,
            [ArtistTag] = SupportedTag.Artist,
            [ArtistsTag] = SupportedTag.Artist,
            [AlbumArtistTag] = SupportedTag.AlbumArtist,
            [AlbumTag] = SupportedTag.Album,
            [AlbumArtTag] = SupportedTag.AlbumArt,
            [VersionTag] = SupportedTag.Version,
            [RemixerTag] = SupportedTag.Remixer,
            [GenreTag] = SupportedTag.Genre,
            [StyleTag] = SupportedTag.Style,
            [LabelTag] = SupportedTag.Label,
            [ReleaseIdTag] = SupportedTag.ReleaseId,
            [TrackIdTag] = SupportedTag.TrackId,
            [RecordingIdTag] = SupportedTag.RecordingId,
            [ArtistIdTag] = SupportedTag.ArtistId,
            [AlbumArtistIdTag] = SupportedTag.AlbumArtistId,
            [ReleaseGroupIdTag] = SupportedTag.ReleaseGroupId,
            [AlbumIdTag] = SupportedTag.AlbumId,
            [ReleaseStatusTag] = SupportedTag.ReleaseStatus,
            [ReleaseCountryTag] = SupportedTag.ReleaseCountry,
            [BarcodeTag] = SupportedTag.Barcode,
            [MediaTag] = SupportedTag.Media,
            [CopyrightTag] = SupportedTag.Copyright,
            [ComposerTag] = SupportedTag.Composer,
            [LyricistTag] = SupportedTag.Lyricist,
            [InvolvedPeopleTag] = SupportedTag.InvolvedPeople,
            [PublisherTag] = SupportedTag.Publisher,
            [DescriptionTag] = SupportedTag.Description,
            [ReplayGainTag] = SupportedTag.ReplayGain,
            [SourceTag] = SupportedTag.Source,
            [RatingTag] = SupportedTag.Rating,
            [LanguageTag] = SupportedTag.Language
        };

        SupportedTagFeatureMappings.AddAudioFeatureTags(map);

        map[CatalogNumberTag] = SupportedTag.CatalogNumber;
        map[TrackNumberTag] = SupportedTag.TrackNumber;
        map[DiscNumberTag] = SupportedTag.DiscNumber;
        map[DurationTag] = SupportedTag.Duration;
        map[TrackTotalTag] = SupportedTag.TrackTotal;
        map[ReleaseTypeTag] = SupportedTag.ReleaseType;
        map[DiscTotalTag] = SupportedTag.DiscTotal;
        map["isrc"] = SupportedTag.ISRC;
        map[PublishDateTag] = SupportedTag.PublishDate;
        map[ReleaseDateTag] = SupportedTag.ReleaseDate;
        map[YearTag] = SupportedTag.ReleaseDate;
        map[DateTag] = SupportedTag.ReleaseDate;
        map[LengthTag] = SupportedTag.Duration;
        map[CoverTag] = SupportedTag.AlbumArt;
        map[LyricsTag] = SupportedTag.UnsyncedLyrics;
        map["url"] = SupportedTag.URL;
        map[OtherTagsTag] = SupportedTag.OtherTags;
        map[MetaTagsTag] = SupportedTag.MetaTags;
        map[UnsyncedLyricsTag] = SupportedTag.UnsyncedLyrics;
        map[SyncedLyricsTag] = SupportedTag.SyncedLyrics;
        map[TtmlLyricsTag] = SupportedTag.TtmlLyrics;
        map[ExplicitTag] = SupportedTag.Explicit;
        return map;
    }

    private static bool ShouldOverwriteTag(AutoTagRunnerConfig config, SupportedTag tag)
    {
        if (config.Overwrite)
        {
            return true;
        }

        return config.OverwriteTags.Any(t => SupportedTagMap.TryGetValue(t.Trim(), out var mapped) && mapped == tag);
    }

    private static string ResolveSeparatorForFormat(AutoTagRunnerConfig config, string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return config.Separators?.Id3 ?? ", ";
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return config.Separators?.Vorbis ?? "";
        }

        if (IsMp4Family(extension))
        {
            return config.Separators?.Mp4 ?? ", ";
        }

        return ", ";
    }

    private static List<string> CollectAutoTagTags(AutoTagTrack track)
    {
        var tags = new List<string>();
        void Add(string tag, bool condition)
        {
            if (!condition || tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            tags.Add(tag);
        }

        AddAutoTagMetadataTags(track, Add);
        AddAutoTagFeatureTags(track, Add);
        AddAutoTagNumericAndDateTags(track, Add);
        AddAutoTagOtherMappedTags(track, Add);
        AddAutoTagLyricsAndOtherTags(track, Add);

        return tags;
    }

    private static void AddAutoTagMetadataTags(AutoTagTrack track, Action<string, bool> add)
    {
        add(TitleTag, !string.IsNullOrWhiteSpace(track.Title));
        add(ArtistTag, track.Artists.Count > 0);
        add(AlbumArtistTag, track.AlbumArtists.Count > 0);
        add(AlbumTag, !string.IsNullOrWhiteSpace(track.Album));
        add(AlbumArtTag, !string.IsNullOrWhiteSpace(track.Art));
        add(VersionTag, !string.IsNullOrWhiteSpace(track.Version));
        add(RemixerTag, track.Remixers.Count > 0);
        add(GenreTag, track.Genres.Count > 0);
        add(StyleTag, track.Styles.Count > 0);
        add(LabelTag, !string.IsNullOrWhiteSpace(track.Label));
        add(ReleaseIdTag, !string.IsNullOrWhiteSpace(track.ReleaseId));
        add(TrackIdTag, !string.IsNullOrWhiteSpace(track.TrackId));
        add(RecordingIdTag, !string.IsNullOrWhiteSpace(track.RecordingId));
        add(ArtistIdTag, !string.IsNullOrWhiteSpace(track.ArtistId));
        add(AlbumArtistIdTag, !string.IsNullOrWhiteSpace(track.AlbumArtistId));
        add(ReleaseGroupIdTag, !string.IsNullOrWhiteSpace(track.ReleaseGroupId));
        add(AlbumIdTag, !string.IsNullOrWhiteSpace(track.AlbumId));
        add(ReleaseStatusTag, !string.IsNullOrWhiteSpace(track.ReleaseStatus));
        add(ReleaseCountryTag, !string.IsNullOrWhiteSpace(track.ReleaseCountry));
        add(BarcodeTag, !string.IsNullOrWhiteSpace(track.Barcode));
        add(MediaTag, track.Media.Count > 0);
        add(LyricistTag, !string.IsNullOrWhiteSpace(track.Lyricist));
        add(PublisherTag, !string.IsNullOrWhiteSpace(track.Publisher));
        add(DescriptionTag, !string.IsNullOrWhiteSpace(track.Description));
    }

    private static void AddAutoTagFeatureTags(AutoTagTrack track, Action<string, bool> add)
    {
        add(BpmTag, track.Bpm.HasValue && track.Bpm.Value > 0);
        add("danceability", track.Danceability.HasValue);
        add("energy", track.Energy.HasValue);
        add("valence", track.Valence.HasValue);
        add("acousticness", track.Acousticness.HasValue);
        add("instrumentalness", track.Instrumentalness.HasValue);
        add("speechiness", track.Speechiness.HasValue);
        add("loudness", track.Loudness.HasValue);
        add("tempo", track.Tempo.HasValue);
        add("timeSignature", track.TimeSignature.HasValue);
        add("liveness", track.Liveness.HasValue);
        add("key", !string.IsNullOrWhiteSpace(track.Key));
        add("mood", !string.IsNullOrWhiteSpace(track.Mood));
        add("activity", !string.IsNullOrWhiteSpace(track.Activity));
    }

    private static void AddAutoTagNumericAndDateTags(AutoTagTrack track, Action<string, bool> add)
    {
        add(CatalogNumberTag, !string.IsNullOrWhiteSpace(track.CatalogNumber));
        add(TrackNumberTag, track.TrackNumber.HasValue && track.TrackNumber.Value > 0);
        add(TrackTotalTag, track.TrackTotal.HasValue && track.TrackTotal.Value > 0);
        add(ReleaseTypeTag, !string.IsNullOrWhiteSpace(AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal)));
        add(DiscTotalTag, track.DiscTotal.HasValue && track.DiscTotal.Value > 0 || HasOtherTagValues(track, DiscTotalTag));
        add(DiscNumberTag, track.DiscNumber.HasValue && track.DiscNumber.Value > 0);
        add(DurationTag, track.Duration.HasValue && track.Duration.Value.TotalSeconds > 0);
        add(IsrcTag, !string.IsNullOrWhiteSpace(track.Isrc));
        add(PublishDateTag, track.PublishDate.HasValue);
        add(ReleaseDateTag, track.ReleaseDate.HasValue);
        add(UrlTag, !string.IsNullOrWhiteSpace(track.Url));
        add(ExplicitTag, track.Explicit.HasValue);
    }

    private static void AddAutoTagOtherMappedTags(AutoTagTrack track, Action<string, bool> add)
    {
        add(BarcodeTag, HasOtherTagValues(track, BarcodeTag));
        add(ReplayGainTag, HasOtherTagValues(track, ReplayGainTag));
        add(CopyrightTag, HasOtherTagValues(track, CopyrightTag));
        add(ComposerTag, HasOtherTagValues(track, ComposerTag));
        add(LyricistTag, HasOtherTagValues(track, LyricistTag) || HasOtherTagValues(track, LyricistRawTag) || HasOtherTagValues(track, "TEXT"));
        add(InvolvedPeopleTag, HasOtherTagValues(track, InvolvedPeopleTag));
        add(PublisherTag, HasOtherTagValues(track, PublisherTag) || HasOtherTagValues(track, PublisherRawTag));
        add(DescriptionTag, HasOtherTagValues(track, DescriptionTag) || HasOtherTagValues(track, DescriptionRawTag) || HasOtherTagValues(track, CommentRawTag));
        add(SourceTag, HasOtherTagValues(track, SourceTag));
        add(RatingTag, HasOtherTagValues(track, RatingTag));
        add(LanguageTag, HasOtherTagValues(track, LanguageTag));
    }

    private static void AddAutoTagLyricsAndOtherTags(AutoTagTrack track, Action<string, bool> add)
    {
        var otherKeys = track.Other.Keys.ToList();
        var hasSyncedLyrics = HasOtherKey(otherKeys, SyncedLyricsTag);
        var hasUnsyncedLyrics = HasAnyOtherKey(otherKeys, UnsyncedLyricsTag, LyricsTag);
        var hasTtmlLyrics = HasOtherKey(otherKeys, TtmlLyricsTag);
        add(SyncedLyricsTag, hasSyncedLyrics);
        add(UnsyncedLyricsTag, hasUnsyncedLyrics);
        add(TtmlLyricsTag, hasTtmlLyrics);

        var hasOtherTags = HasNonLyricsOtherTag(otherKeys);
        add(OtherTagsTag, hasOtherTags);
    }

    private static bool HasOtherKey(IEnumerable<string> keys, string target)
        => keys.Any(key => key.Equals(target, StringComparison.OrdinalIgnoreCase));

    private static bool HasAnyOtherKey(IEnumerable<string> keys, string first, string second)
        => HasOtherKey(keys, first) || HasOtherKey(keys, second);

    private static bool HasNonLyricsOtherTag(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(SyncedLyricsSourceFormatTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase)
                || IsNonPersistedOtherRawKey(key))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool HasOtherTagValues(AutoTagTrack track, string key)
    {
        return track.Other.TryGetValue(key, out var values) && values.Count > 0;
    }

    private static bool IsFirstClassOtherRawKey(string key)
    {
        if (FirstClassRawOtherTags.Contains(key))
        {
            return true;
        }

        return key.EndsWith("_TRACK_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_RELEASE_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ALBUM_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ARTIST_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_URL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeMatchMetadataKey(string key)
    {
        return key.StartsWith("SHAZAM_MATCH_", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_SIMILARITY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_DURATION_DIFF_SECONDS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_TITLE_SIMILARITY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_ARTIST_SIMILARITY", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonPersistedOtherRawKey(string key)
        => IsFirstClassOtherRawKey(key) || IsRuntimeMatchMetadataKey(key);

    private static bool ShouldPersistOtherRawKey(string key)
    {
        return !string.IsNullOrWhiteSpace(key)
            && !IsNonPersistedOtherRawKey(key)
            && !key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase)
            && !IsLyricsPayloadKey(key);
    }

    private static bool HasReleaseTypeTagEnabled(HashSet<string> enabledTags)
        => enabledTags.Contains(ReleaseTypeTag) || enabledTags.Contains(OtherTagsTag);

    private static void EnsureReleaseCategory(AutoTagTrack track)
    {
        var releaseType = AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal);
        if (string.IsNullOrWhiteSpace(releaseType))
        {
            return;
        }

        track.ReleaseType = releaseType;
        track.Other[ReleaseTypeRawTag] = new List<string> { releaseType };
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

    private static string FormatAudioFeature(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool HasTag(TagLib.File file, string extension, SupportedTag tag, AutoTagRunnerConfig config, string platformId)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            if (id3 == null) return false;
            return HasId3Tag(id3, tag, config, platformId);
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
            if (vorbis == null) return false;
            return HasVorbisTag(vorbis, tag, config, platformId);
        }

        if (IsMp4Family(extension))
        {
            return HasMp4Tag(file, tag, config, platformId);
        }

        return false;
    }

    private static bool HasId3Tag(TagLib.Id3v2.Tag tag, SupportedTag supportedTag, AutoTagRunnerConfig config, string platformId)
    {
        return supportedTag switch
        {
            SupportedTag.Title => !string.IsNullOrWhiteSpace(tag.Title),
            SupportedTag.Artist => tag.Performers?.Length > 0,
            SupportedTag.AlbumArtist => tag.AlbumArtists?.Length > 0,
            SupportedTag.Album => !string.IsNullOrWhiteSpace(tag.Album),
            SupportedTag.Key => TagRawProbe.HasId3Raw(tag, "TKEY"),
            SupportedTag.BPM => TagRawProbe.HasId3Raw(tag, "TBPM"),
            SupportedTag.Danceability => TagRawProbe.HasId3Raw(tag, DanceabilityTag),
            SupportedTag.Energy => TagRawProbe.HasId3Raw(tag, EnergyTag),
            SupportedTag.Valence => TagRawProbe.HasId3Raw(tag, ValenceTag),
            SupportedTag.Acousticness => TagRawProbe.HasId3Raw(tag, AcousticnessTag),
            SupportedTag.Instrumentalness => TagRawProbe.HasId3Raw(tag, InstrumentalnessTag),
            SupportedTag.Speechiness => TagRawProbe.HasId3Raw(tag, SpeechinessTag),
            SupportedTag.Loudness => TagRawProbe.HasId3Raw(tag, LoudnessTag),
            SupportedTag.Tempo => TagRawProbe.HasId3Raw(tag, TempoTag),
            SupportedTag.TimeSignature => TagRawProbe.HasId3Raw(tag, TimeSignatureTag),
            SupportedTag.Liveness => TagRawProbe.HasId3Raw(tag, LivenessTag),
            SupportedTag.Genre => tag.Genres?.Length > 0,
            SupportedTag.Style => TagRawProbe.HasId3Raw(tag, ResolveStylesTagName(config, ".mp3")),
            SupportedTag.Label => TagRawProbe.HasId3Raw(tag, "TPUB"),
            SupportedTag.Copyright => TagRawProbe.HasId3Raw(tag, CopyrightRawTag),
            SupportedTag.Composer => TagRawProbe.HasId3Raw(tag, "TCOM"),
            SupportedTag.Lyricist => TagRawProbe.HasId3Raw(tag, "TEXT") || TagRawProbe.HasId3Raw(tag, LyricistRawTag),
            SupportedTag.InvolvedPeople => TagRawProbe.HasId3Raw(tag, InvolvedPeopleRawTag),
            SupportedTag.Publisher => TagRawProbe.HasId3Raw(tag, PublisherRawTag),
            SupportedTag.Description => TagRawProbe.HasId3Raw(tag, DescriptionRawTag) || TagRawProbe.HasId3Raw(tag, CommentRawTag) || !string.IsNullOrWhiteSpace(tag.Comment),
            SupportedTag.ReplayGain => TagRawProbe.HasId3Raw(tag, ReplayGainRawTag),
            SupportedTag.Source => TagRawProbe.HasId3Raw(tag, SourceRawTag),
            SupportedTag.Rating => TagRawProbe.HasId3Raw(tag, RatingRawTag),
            SupportedTag.Language => TagRawProbe.HasId3Raw(tag, LanguageRawTag),
            SupportedTag.ISRC => TagRawProbe.HasId3Raw(tag, "TSRC"),
            SupportedTag.CatalogNumber => TagRawProbe.HasId3Raw(tag, CatalogNumberUpperTag),
            SupportedTag.Version => TagRawProbe.HasId3Raw(tag, "TIT3"),
            SupportedTag.TrackNumber => tag.Track > 0,
            SupportedTag.TrackTotal => tag.TrackCount > 0,
            SupportedTag.ReleaseType => TagRawProbe.HasId3Raw(tag, ReleaseTypeRawTag),
            SupportedTag.DiscNumber => tag.Disc > 0,
            SupportedTag.DiscTotal => tag.DiscCount > 0,
            SupportedTag.Duration => TagRawProbe.HasId3Raw(tag, "TLEN"),
            SupportedTag.Remixer => TagRawProbe.HasId3Raw(tag, "TPE4"),
            SupportedTag.Mood => TagRawProbe.HasId3Raw(tag, "TMOO"),
            SupportedTag.Activity => TagRawProbe.HasId3Raw(tag, "ACTIVITY"),
            SupportedTag.ReleaseDate => TagRawProbe.HasId3Raw(tag, config.Id3v24 ? "TDRC" : "TYER"),
            SupportedTag.PublishDate => TagRawProbe.HasId3Raw(tag, "TDRL"),
            SupportedTag.URL => TagRawProbe.HasId3Raw(tag, WwwAudioFileTag),
            SupportedTag.TrackId => TagRawProbe.HasId3Raw(tag, $"{platformId.ToUpperInvariant()}_TRACK_ID"),
            SupportedTag.ReleaseId => TagRawProbe.HasId3Raw(tag, $"{platformId.ToUpperInvariant()}_RELEASE_ID"),
            SupportedTag.RecordingId => TagRawProbe.HasId3Raw(tag, RecordingIdRawTag),
            SupportedTag.ArtistId => TagRawProbe.HasId3Raw(tag, ArtistIdRawTag),
            SupportedTag.AlbumArtistId => TagRawProbe.HasId3Raw(tag, AlbumArtistIdRawTag),
            SupportedTag.ReleaseGroupId => TagRawProbe.HasId3Raw(tag, ReleaseGroupIdRawTag),
            SupportedTag.AlbumId => TagRawProbe.HasId3Raw(tag, AlbumIdRawTag),
            SupportedTag.ReleaseStatus => TagRawProbe.HasId3Raw(tag, ReleaseStatusRawTag),
            SupportedTag.ReleaseCountry => TagRawProbe.HasId3Raw(tag, ReleaseCountryRawTag),
            SupportedTag.Barcode => TagRawProbe.HasId3Raw(tag, BarcodeRawTag),
            SupportedTag.Media => TagRawProbe.HasId3Raw(tag, MediaRawTag),
            SupportedTag.OtherTags => false,
            SupportedTag.MetaTags => TagRawProbe.HasId3Raw(tag, TaggedDateTag),
            SupportedTag.SyncedLyrics => tag.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>("SYLT").Any(),
            SupportedTag.UnsyncedLyrics => !string.IsNullOrWhiteSpace(tag.Lyrics),
            SupportedTag.AlbumArt => tag.Pictures?.Length > 0,
            SupportedTag.Explicit => TagRawProbe.HasId3Raw(tag, ItunesAdvisoryTag),
            _ => false
        };
    }

    private static bool HasVorbisTag(TagLib.Ogg.XiphComment tag, SupportedTag supportedTag, AutoTagRunnerConfig config, string platformId)
    {
        return supportedTag switch
        {
            SupportedTag.Title => tag.GetField(TitleUpperTag).Length > 0,
            SupportedTag.Artist => tag.GetField(ArtistUpperTag).Length > 0,
            SupportedTag.AlbumArtist => tag.GetField(AlbumArtistUpperTag).Length > 0,
            SupportedTag.Album => tag.GetField(AlbumUpperTag).Length > 0,
            SupportedTag.Key => tag.GetField("INITIALKEY").Length > 0,
            SupportedTag.BPM => tag.GetField("BPM").Length > 0,
            SupportedTag.Danceability => TagRawProbe.HasVorbisRaw(tag, DanceabilityTag),
            SupportedTag.Energy => TagRawProbe.HasVorbisRaw(tag, EnergyTag),
            SupportedTag.Valence => TagRawProbe.HasVorbisRaw(tag, ValenceTag),
            SupportedTag.Acousticness => TagRawProbe.HasVorbisRaw(tag, AcousticnessTag),
            SupportedTag.Instrumentalness => TagRawProbe.HasVorbisRaw(tag, InstrumentalnessTag),
            SupportedTag.Speechiness => TagRawProbe.HasVorbisRaw(tag, SpeechinessTag),
            SupportedTag.Loudness => TagRawProbe.HasVorbisRaw(tag, LoudnessTag),
            SupportedTag.Tempo => TagRawProbe.HasVorbisRaw(tag, TempoTag),
            SupportedTag.TimeSignature => TagRawProbe.HasVorbisRaw(tag, TimeSignatureTag),
            SupportedTag.Liveness => TagRawProbe.HasVorbisRaw(tag, LivenessTag),
            SupportedTag.Genre => tag.GetField(Mp4GenreTag).Length > 0,
            SupportedTag.Style => tag.GetField(ResolveStylesTagName(config, FlacExtension)).Length > 0,
            SupportedTag.Label => tag.GetField(LabelUpperTag).Length > 0,
            SupportedTag.Copyright => tag.GetField(CopyrightRawTag).Length > 0,
            SupportedTag.Composer => tag.GetField(ComposerUpperTag).Length > 0,
            SupportedTag.Lyricist => tag.GetField(LyricistRawTag).Length > 0,
            SupportedTag.InvolvedPeople => tag.GetField(InvolvedPeopleRawTag).Length > 0,
            SupportedTag.Publisher => tag.GetField(PublisherRawTag).Length > 0,
            SupportedTag.Description => tag.GetField(DescriptionRawTag).Length > 0 || tag.GetField(CommentRawTag).Length > 0,
            SupportedTag.ReplayGain => tag.GetField(ReplayGainRawTag).Length > 0,
            SupportedTag.Source => tag.GetField(SourceRawTag).Length > 0,
            SupportedTag.Rating => tag.GetField(RatingRawTag).Length > 0,
            SupportedTag.Language => tag.GetField(LanguageRawTag).Length > 0,
            SupportedTag.ISRC => tag.GetField("ISRC").Length > 0,
            SupportedTag.CatalogNumber => tag.GetField(CatalogNumberUpperTag).Length > 0,
            SupportedTag.Version => tag.GetField("SUBTITLE").Length > 0,
            SupportedTag.TrackNumber => tag.GetField(TrackNumberUpperTag).Length > 0,
            SupportedTag.TrackTotal => tag.GetField(TrackTotalRawTag).Length > 0,
            SupportedTag.ReleaseType => tag.GetField(ReleaseTypeRawTag).Length > 0,
            SupportedTag.DiscNumber => tag.GetField("DISCNUMBER").Length > 0,
            SupportedTag.DiscTotal => tag.GetField(DiscTotalRawTag).Length > 0,
            SupportedTag.Duration => tag.GetField(LengthUpperTag).Length > 0,
            SupportedTag.Remixer => tag.GetField(RemixerUpperTag).Length > 0,
            SupportedTag.Mood => tag.GetField("MOOD").Length > 0,
            SupportedTag.Activity => tag.GetField("ACTIVITY").Length > 0,
            SupportedTag.ReleaseDate => tag.GetField("DATE").Length > 0,
            SupportedTag.PublishDate => tag.GetField(OriginalDateUpperTag).Length > 0,
            SupportedTag.URL => tag.GetField(WwwAudioFileTag).Length > 0,
            SupportedTag.TrackId => tag.GetField($"{platformId.ToUpperInvariant()}_TRACK_ID").Length > 0,
            SupportedTag.ReleaseId => tag.GetField($"{platformId.ToUpperInvariant()}_RELEASE_ID").Length > 0,
            SupportedTag.RecordingId => tag.GetField(RecordingIdRawTag).Length > 0,
            SupportedTag.ArtistId => tag.GetField(ArtistIdRawTag).Length > 0,
            SupportedTag.AlbumArtistId => tag.GetField(AlbumArtistIdRawTag).Length > 0,
            SupportedTag.ReleaseGroupId => tag.GetField(ReleaseGroupIdRawTag).Length > 0,
            SupportedTag.AlbumId => tag.GetField(AlbumIdRawTag).Length > 0,
            SupportedTag.ReleaseStatus => tag.GetField(ReleaseStatusRawTag).Length > 0,
            SupportedTag.ReleaseCountry => tag.GetField(ReleaseCountryRawTag).Length > 0,
            SupportedTag.Barcode => tag.GetField(BarcodeRawTag).Length > 0,
            SupportedTag.Media => tag.GetField(MediaRawTag).Length > 0,
            SupportedTag.MetaTags => tag.GetField(TaggedDateTag).Length > 0,
            SupportedTag.UnsyncedLyrics => tag.GetField(LyricsUpperTag).Any(value => !string.IsNullOrWhiteSpace(value)),
            SupportedTag.SyncedLyrics =>
                tag.GetField(LyricsSyncedTag).Any(value => !string.IsNullOrWhiteSpace(value))
                || HasTimestampedLyricsPayload(tag.GetField(LyricsUpperTag)),
            SupportedTag.AlbumArt => tag.Pictures?.Length > 0,
            SupportedTag.Explicit => tag.GetField(ItunesAdvisoryTag).Length > 0
                || tag.GetField("COMMENT").Any(v => string.Equals(v, "Explicit", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static bool HasMp4Tag(TagLib.File file, SupportedTag supportedTag, AutoTagRunnerConfig config, string platformId)
    {
        return supportedTag switch
        {
            SupportedTag.Title => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Artist => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.AlbumArtist => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Album => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.BPM => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Genre => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Style => Mp4TagHelper.HasRaw(file, ResolveStylesTagName(config, ".mp4")),
            SupportedTag.Danceability => Mp4TagHelper.HasRaw(file, DanceabilityTag),
            SupportedTag.Energy => Mp4TagHelper.HasRaw(file, EnergyTag),
            SupportedTag.Valence => Mp4TagHelper.HasRaw(file, ValenceTag),
            SupportedTag.Acousticness => Mp4TagHelper.HasRaw(file, AcousticnessTag),
            SupportedTag.Instrumentalness => Mp4TagHelper.HasRaw(file, InstrumentalnessTag),
            SupportedTag.Speechiness => Mp4TagHelper.HasRaw(file, SpeechinessTag),
            SupportedTag.Loudness => Mp4TagHelper.HasRaw(file, LoudnessTag),
            SupportedTag.Tempo => Mp4TagHelper.HasRaw(file, TempoTag),
            SupportedTag.TimeSignature => Mp4TagHelper.HasRaw(file, TimeSignatureTag),
            SupportedTag.Liveness => Mp4TagHelper.HasRaw(file, LivenessTag),
            SupportedTag.Label => Mp4TagHelper.HasRaw(file, LabelUpperTag),
            SupportedTag.Copyright => Mp4TagHelper.HasRaw(file, CopyrightRawTag),
            SupportedTag.Composer => Mp4TagHelper.HasRaw(file, "©wrt"),
            SupportedTag.Lyricist => Mp4TagHelper.HasRaw(file, LyricistRawTag),
            SupportedTag.InvolvedPeople => Mp4TagHelper.HasRaw(file, InvolvedPeopleRawTag),
            SupportedTag.Publisher => Mp4TagHelper.HasRaw(file, PublisherRawTag),
            SupportedTag.Description => Mp4TagHelper.HasRaw(file, "ldes") || Mp4TagHelper.HasRaw(file, DescriptionRawTag),
            SupportedTag.ReplayGain => Mp4TagHelper.HasRaw(file, ReplayGainRawTag),
            SupportedTag.Source => Mp4TagHelper.HasRaw(file, SourceRawTag),
            SupportedTag.Rating => Mp4TagHelper.HasRaw(file, RatingRawTag),
            SupportedTag.Language => Mp4TagHelper.HasRaw(file, LanguageRawTag),
            SupportedTag.ISRC => Mp4TagHelper.HasRaw(file, "ISRC"),
            SupportedTag.CatalogNumber => Mp4TagHelper.HasRaw(file, CatalogNumberUpperTag),
            SupportedTag.Version => Mp4TagHelper.HasRaw(file, "desc"),
            SupportedTag.TrackNumber => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.TrackTotal => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.ReleaseType => Mp4TagHelper.HasRaw(file, ReleaseTypeRawTag),
            SupportedTag.DiscNumber => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.DiscTotal => file.Tag.DiscCount > 0,
            SupportedTag.Duration => Mp4TagHelper.HasRaw(file, LengthUpperTag),
            SupportedTag.Remixer => Mp4TagHelper.HasRaw(file, RemixerUpperTag),
            SupportedTag.Mood => Mp4TagHelper.HasRaw(file, "MOOD"),
            SupportedTag.Activity => Mp4TagHelper.HasRaw(file, "ACTIVITY"),
            SupportedTag.Key => Mp4TagHelper.HasRaw(file, InitialKeyRawTag),
            SupportedTag.ReleaseDate =>
                Mp4TagHelper.HasRaw(file, "©day")
                || Mp4TagHelper.HasRaw(file, "DATE"),
            SupportedTag.PublishDate => Mp4TagHelper.HasRaw(file, "ORIGINALDATE"),
            SupportedTag.URL => Mp4TagHelper.HasRaw(file, WwwAudioFileTag),
            SupportedTag.TrackId => Mp4TagHelper.HasRaw(file, $"{platformId.ToUpperInvariant()}_TRACK_ID"),
            SupportedTag.ReleaseId => Mp4TagHelper.HasRaw(file, $"{platformId.ToUpperInvariant()}_RELEASE_ID"),
            SupportedTag.RecordingId => Mp4TagHelper.HasRaw(file, RecordingIdRawTag),
            SupportedTag.ArtistId => Mp4TagHelper.HasRaw(file, ArtistIdRawTag),
            SupportedTag.AlbumArtistId => Mp4TagHelper.HasRaw(file, AlbumArtistIdRawTag),
            SupportedTag.ReleaseGroupId => Mp4TagHelper.HasRaw(file, ReleaseGroupIdRawTag),
            SupportedTag.AlbumId => Mp4TagHelper.HasRaw(file, AlbumIdRawTag),
            SupportedTag.ReleaseStatus => Mp4TagHelper.HasRaw(file, ReleaseStatusRawTag),
            SupportedTag.ReleaseCountry => Mp4TagHelper.HasRaw(file, ReleaseCountryRawTag),
            SupportedTag.Barcode => Mp4TagHelper.HasRaw(file, BarcodeRawTag),
            SupportedTag.Media => Mp4TagHelper.HasRaw(file, MediaRawTag),
            SupportedTag.MetaTags => Mp4TagHelper.HasRaw(file, TaggedDateTag),
            SupportedTag.UnsyncedLyrics => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.SyncedLyrics =>
                Mp4TagHelper.HasRaw(file, LyricsSyncedTag)
                || ContainsTimestampedLyrics(file.Tag.Lyrics),
            SupportedTag.AlbumArt => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Explicit => Mp4TagHelper.HasRaw(file, ItunesAdvisoryTag),
            _ => false
        };
    }

    private static bool HasTimestampedLyricsPayload(IEnumerable<string> values)
    {
        return values.Any(ContainsTimestampedLyrics);
    }

    private static bool ContainsTimestampedLyrics(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics))
        {
            return false;
        }

        return rawLyrics
            .Split(LyricsLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => TryParseLrcLine(line, out _, out _));
    }

    private static List<string> ReadExistingGenre(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            return SanitizeGenres(file.Tag.Genres ?? Array.Empty<string>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new List<string>();
        }
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
}
