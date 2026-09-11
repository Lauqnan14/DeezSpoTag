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

    private static void AddOtherTagWrites(List<CustomTagWrite> writes, IReadOnlyDictionary<string, List<string>> otherTags)
    {
        foreach (var kvp in otherTags.Where(kvp => kvp.Value.Count > 0 && !IsNonPersistedOtherRawKey(kvp.Key)))
        {
            var isReleaseType = kvp.Key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase);
            writes.Add(new CustomTagWrite(
                isReleaseType ? ReleaseTypeTag : OtherTagsTag,
                isReleaseType ? SupportedTag.ReleaseType : SupportedTag.OtherTags,
                kvp.Key,
                kvp.Value.ToList()));
        }
    }

    private static void AddMetaTagWrite(List<CustomTagWrite> writes, AutoTagRunnerConfig config)
    {
        if (!config.Tags.Any(tag => string.Equals(tag, MetaTagsTag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        writes.Add(new CustomTagWrite(
            MetaTagsTag,
            SupportedTag.MetaTags,
            TaggedDateTag,
            new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}_AT" }));
    }

    private static string ResolveFieldRawName(SupportedTag tag, string format, AutoTagRunnerConfig config)
    {
        return tag switch
        {
            SupportedTag.Key => format switch
            {
                "id3" => "TKEY",
                VorbisFormat => "INITIALKEY",
                _ => InitialKeyRawTag
            },
            SupportedTag.Style => format switch
            {
                "id3" => ResolveStylesTagName(config, ".mp3"),
                VorbisFormat => ResolveStylesTagName(config, FlacExtension),
                _ => ResolveStylesTagName(config, ".mp4")
            },
            SupportedTag.Version => format switch
            {
                "id3" => "TIT3",
                VorbisFormat => "SUBTITLE",
                _ => "desc"
            },
            SupportedTag.Remixer => format switch
            {
                "id3" => "TPE4",
                VorbisFormat => RemixerUpperTag,
                _ => RemixerUpperTag
            },
            SupportedTag.Mood => format switch
            {
                "id3" => "TMOO",
                VorbisFormat => "MOOD",
                _ => "MOOD"
            },
            SupportedTag.Lyricist => format == "id3" ? "TEXT" : LyricistRawTag,
            SupportedTag.Publisher => PublisherRawTag,
            SupportedTag.Description => format == "mp4" ? "ldes" : DescriptionRawTag,
            SupportedTag.Activity => "ACTIVITY",
            SupportedTag.CatalogNumber => CatalogNumberUpperTag,
            _ => tag.ToString()
        };
    }

    private sealed class TagWriteRequest
    {
        public required string FilePath { get; init; }
        public required AutoTagTrack SourceTrack { get; init; }
        public required Track CoreTrack { get; init; }
        public required TagSettings EffectiveTagSettings { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required string PlatformId { get; init; }
        public required string Separator { get; init; }
        public string? TempCoverPath { get; init; }
    }

    private sealed class TagWriteExecutionContext
    {
        public required string FilePath { get; init; }
        public required AutoTagTrack SourceTrack { get; init; }
        public required Track CoreTrack { get; init; }
        public required TagSettings EffectiveTagSettings { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required string PlatformId { get; init; }
        public required string Separator { get; init; }
        public string? TempCoverPath { get; init; }
        public required string Extension { get; init; }
        public required HashSet<string> EnabledTags { get; init; }
        public required IReadOnlyDictionary<string, string> GenreAliasMap { get; init; }
        public required IReadOnlyList<string> GenreBlockList { get; init; }
        public required bool SplitCompositeGenres { get; init; }
        public required bool AllowsLyricsBySettings { get; init; }
        public required bool AllowsSyncedType { get; init; }
        public required bool AllowsUnsyncedType { get; init; }
        public required bool AllowsLrcByFormat { get; init; }
        public required bool AllowsTtmlByFormat { get; init; }
        public required (bool HasAny, bool HasLrc, bool HasTtml, bool HasTxt, string TxtPath) SidecarState { get; init; }
        public required bool ShouldSkipEmbeddedLyrics { get; init; }
        public HashSet<SupportedTag> AttemptedTags { get; } = new();
    }

    private sealed record TagFileWriteResult(HashSet<SupportedTag> AttemptedTags);

    public sealed class LocalAutoTagRunnerCollaborators
    {
        public required ILogger<LocalAutoTagRunner> Logger { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required MusicBrainzMatcher MusicBrainzMatcher { get; init; }
        public required BeatportMatcher BeatportMatcher { get; init; }
        public required DiscogsMatcher DiscogsMatcher { get; init; }
        public required TraxsourceMatcher TraxsourceMatcher { get; init; }
        public required BandcampMatcher BandcampMatcher { get; init; }
        public required BpmSupremeMatcher BpmSupremeMatcher { get; init; }
        public required ItunesMatcher ItunesMatcher { get; init; }
        public required SpotifyMatcher SpotifyMatcher { get; init; }
        public required DeezerMatcher DeezerMatcher { get; init; }
        public required LastFmMatcher LastFmMatcher { get; init; }
        public required BoomplayMatcher BoomplayMatcher { get; init; }
        public required AudiomackMatcher AudiomackMatcher { get; init; }
        public required ShazamMatcher ShazamMatcher { get; init; }
        public required ShazamRecognitionService ShazamRecognitionService { get; init; }
        public required AppleLyricsService AppleLyricsService { get; init; }
        public required AppleMusicCatalogService AppleMusicCatalogService { get; init; }
        public required DownloadLyricsService DownloadLyricsService { get; init; }
        public required DeezSpoTagSettingsService SettingsService { get; init; }
        public required IServiceScopeFactory ServiceScopeFactory { get; init; }
        public required ITrackIdentityResolver TrackIdentityResolver { get; init; }
        public PortedPlatformRegistry? PlatformRegistry { get; init; }

        /// <summary>Optional path of the cross-run album identity store; persistence is disabled when null.</summary>
        public string? AlbumIdentityStorePath { get; init; }
    }

    private readonly record struct TagWriteContext(
        TagLib.File File,
        string Extension,
        AutoTagRunnerConfig Config,
        string Separator,
        string PlatformId,
        bool UseNullSeparator,
        IReadOnlyDictionary<string, string> GenreAliasMap,
        IReadOnlyList<string> GenreBlockList,
        bool SplitCompositeGenres,
        HashSet<SupportedTag> AttemptedTags);

    private readonly record struct TagFieldBinding(
        string Id3Frame,
        string VorbisField,
        string Mp4Field,
        SupportedTag Tag);

    private readonly record struct DateWritePayload(
        DateTime Date,
        bool UseYearOnly,
        string Year,
        string DateString);

    private readonly record struct LyricsSidecarWriteResult(
        bool WroteLrcSidecar,
        bool WroteTtmlSidecar);

    private readonly record struct OverwriteRuleContext(
        HashSet<string> EnabledTags,
        AutoTagRunnerConfig Config,
        TagLib.File File,
        string Extension,
        string PlatformId);

    private sealed record CustomTagWrite(string TagKey, SupportedTag SupportedTag, string RawTagName, List<string> Values);

    private static string ResolveFormatName(string extension)
    {
        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase)) return VorbisFormat;
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return "id3";
        return "mp4";
    }

    private static void SetField(TagWriteContext context, TagFieldBinding binding, List<string> values)
    {
        if (binding.Tag == SupportedTag.Genre)
        {
            values = SanitizeGenres(values, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
            values = PreserveGenreOrderWhenSetEqual(values, context.File.Tag?.Genres);
        }

        if (values.Count == 0)
        {
            return;
        }

        if (IsMp4Family(context.Extension))
        {
            if (Mp4TagHelper.TrySetMp4Field(
                context,
                binding.Tag,
                values))
            {
                context.AttemptedTags.Add(binding.Tag);
                return;
            }

            SetRaw(context, binding.Mp4Field, binding.Tag, values);
            return;
        }

        var raw = ResolveFormatName(context.Extension) switch
        {
            "id3" => binding.Id3Frame,
            VorbisFormat => binding.VorbisField,
            _ => binding.Mp4Field
        };
        SetRaw(context, raw, binding.Tag, values);
    }

    private static void SetRaw(TagWriteContext context, string rawName, SupportedTag tag, List<string> values, bool force = false)
    {
        if (tag == SupportedTag.Genre || IsGenreRawTag(rawName))
        {
            values = SanitizeGenres(values, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
            values = PreserveGenreOrderWhenSetEqual(values, context.File.Tag?.Genres);
            if (values.Count == 0)
            {
                return;
            }
        }
        else if (rawName.Equals(SpotifyUrlTag, StringComparison.OrdinalIgnoreCase))
        {
            values = NormalizeSpotifyTrackUrls(values);
            if (values.Count == 0)
            {
                return;
            }

            var existingValues = ReadRawTagValues(context.File, context.Extension, SpotifyUrlTag);
            force |= existingValues.Any(value => NormalizeSpotifyTrackUrl(value) == null);
        }

        if (!force && !ShouldOverwriteTag(context.Config, tag))
        {
            if (tag == SupportedTag.OtherTags)
            {
                if (HasRawTag(context.File, context.Extension, rawName))
                {
                    context.AttemptedTags.Add(tag);
                    return;
                }
            }
            else if (HasTag(context.File, context.Extension, tag, context.Config, context.PlatformId))
            {
                context.AttemptedTags.Add(tag);
                return;
            }
        }

        WriteRawTagValues(context, rawName, values);
        context.AttemptedTags.Add(tag);
    }

    private static bool HasRawTag(TagLib.File file, string extension, string rawName)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            return id3 != null && TagRawProbe.HasId3Raw(id3, rawName);
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
            return vorbis != null && TagRawProbe.HasVorbisRaw(vorbis, rawName);
        }

        if (IsMp4Family(extension))
        {
            return Mp4TagHelper.HasRaw(file, rawName);
        }

        return false;
    }

    private static void WriteDate(
        TagLib.File file,
        string extension,
        string kind,
        DateTime date,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool useNullSeparator)
    {
        var useYearOnly = IsYearOnlyDateFormat(config.Technical?.DateFormat);
        var payload = new DateWritePayload(
            Date: date,
            UseYearOnly: useYearOnly,
            Year: date.Year.ToString(CultureInfo.InvariantCulture),
            DateString: useYearOnly
                ? date.Year.ToString(CultureInfo.InvariantCulture)
                : date.ToString(IsoDateFormat, CultureInfo.InvariantCulture));

        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            WriteId3Date(file, kind, tag, config, payload, useNullSeparator);
            return;
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            WriteVorbisDate(file, kind, tag, config, payload.DateString);
            return;
        }

        WriteMp4Date(file, extension, kind, tag, config, payload.DateString);
    }

    private static void WriteId3Date(
        TagLib.File file,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        DateWritePayload payload,
        bool useNullSeparator)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (kind == ReleaseDateTag)
        {
            if (ShouldSkipId3ReleaseDate(config, tag, id3, payload.UseYearOnly))
            {
                return;
            }

            if (config.Id3v24)
            {
                SetId3Raw(id3, "TDRC", new List<string> { payload.DateString }, ", ", useNullSeparator);
                return;
            }

            SetId3Raw(id3, "TYER", new List<string> { payload.Year }, ", ", useNullSeparator);
            if (!payload.UseYearOnly)
            {
                SetId3Raw(id3, "TDAT", new List<string> { payload.Date.ToString("ddMM", CultureInfo.InvariantCulture) }, ", ", useNullSeparator);
            }
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasId3Raw(id3, "TDRL"))
        {
            return;
        }

        SetId3Raw(id3, "TDRL", new List<string> { payload.DateString }, ", ", useNullSeparator);
    }

    private static bool ShouldSkipId3ReleaseDate(
        AutoTagRunnerConfig config,
        SupportedTag tag,
        TagLib.Id3v2.Tag id3,
        bool useYearOnly)
    {
        if (ShouldOverwriteTag(config, tag))
        {
            return false;
        }

        if (config.Id3v24 && TagRawProbe.HasId3Raw(id3, "TDRC"))
        {
            return true;
        }

        return !config.Id3v24
            && (TagRawProbe.HasId3Raw(id3, "TYER")
                || (!useYearOnly && TagRawProbe.HasId3Raw(id3, "TDAT")));
    }

    private static void WriteVorbisDate(
        TagLib.File file,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        string dateString)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var field = kind == ReleaseDateTag ? "DATE" : OriginalDateUpperTag;
        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasVorbisRaw(vorbis, field))
        {
            return;
        }

        SetVorbisRaw(vorbis, field, new List<string> { dateString }, "");
    }

    private static void WriteMp4Date(
        TagLib.File file,
        string extension,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        string dateString)
    {
        if (!IsMp4Family(extension))
        {
            return;
        }

        if (kind == ReleaseDateTag)
        {
            if (!ShouldOverwriteTag(config, tag)
                && (Mp4TagHelper.HasRaw(file, "©day")
                    || Mp4TagHelper.HasRaw(file, "DATE")))
            {
                return;
            }

            Mp4TagHelper.SetDate(file, dateString);
            var appleRelease = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            TrySetAppleDashBox(appleRelease, "DATE", new[] { dateString });
            return;
        }

        if (kind != PublishDateTag)
        {
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && Mp4TagHelper.HasRaw(file, OriginalDateUpperTag))
        {
            return;
        }

        var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
        TrySetAppleDashBox(apple, "ORIGINALDATE", new[] { dateString });
    }

    private static bool IsYearOnlyDateFormat(string? dateFormat)
        => string.Equals(dateFormat?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);

    private static void SetTrackNumber(
        TagLib.File file,
        TagWriteExecutionContext context,
        int number,
        int? total,
        SupportedTag tag,
        bool isDisc)
    {
        var numberText = context.Config.TrackNumberLeadingZeroes > 0
            ? number.ToString($"D{context.Config.TrackNumberLeadingZeroes}", CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);

        if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            WriteId3TrackNumber(file, numberText, total, tag, context.Config, context.EffectiveTagSettings.UseNullSeparator, isDisc);
            return;
        }

        if (context.Extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            WriteVorbisTrackNumber(file, numberText, total, tag, context.Config, isDisc);
            return;
        }

        WriteMp4TrackNumber(file, number, total, tag, context.Config, isDisc, context.Extension);
    }

    private static void WriteId3TrackNumber(
        TagLib.File file,
        string numberText,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool useNullSeparator,
        bool isDisc)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (!ShouldOverwriteTag(config, tag) && (isDisc ? id3.Disc > 0 : id3.Track > 0))
        {
            return;
        }

        var value = total.HasValue ? $"{numberText}/{total.Value}" : numberText;
        var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, isDisc ? "TPOS" : "TRCK", true);
        if (useNullSeparator)
        {
            frame.TextEncoding = TagLib.StringType.UTF16;
        }
        frame.Text = new[] { value };
    }

    private static void SetDiscTotal(TagLib.File file, TagWriteExecutionContext context, int total)
    {
        if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
            if (!ShouldOverwriteTag(context.Config, SupportedTag.DiscTotal) && id3.DiscCount > 0)
            {
                return;
            }

            var discNumber = file.Tag.Disc > 0
                ? file.Tag.Disc
                : context.SourceTrack.DiscNumber is > 0
                    ? (uint)context.SourceTrack.DiscNumber.Value
                    : 0;
            var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, "TPOS", true);
            frame.Text = new[] { discNumber > 0 ? $"{discNumber}/{total}" : $"0/{total}" };
            return;
        }

        if (context.Extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
            if (!ShouldOverwriteTag(context.Config, SupportedTag.DiscTotal)
                && TagRawProbe.HasVorbisRaw(vorbis, DiscTotalRawTag))
            {
                return;
            }

            SetVorbisRaw(vorbis, DiscTotalRawTag, new List<string> { total.ToString(CultureInfo.InvariantCulture) }, "");
            return;
        }

        if (IsMp4Family(context.Extension))
        {
            if (!ShouldOverwriteTag(context.Config, SupportedTag.DiscTotal) && file.Tag.DiscCount > 0)
            {
                return;
            }

            file.Tag.DiscCount = (uint)total;
        }
    }

    private static void WriteVorbisTrackNumber(
        TagLib.File file,
        string numberText,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool isDisc)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var field = isDisc ? "DISCNUMBER" : TrackNumberUpperTag;
        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasVorbisRaw(vorbis, field))
        {
            return;
        }

        SetVorbisRaw(vorbis, field, new List<string> { numberText }, "");
        if (isDisc
            && total.HasValue
            && (ShouldOverwriteTag(config, SupportedTag.DiscTotal) || !TagRawProbe.HasVorbisRaw(vorbis, DiscTotalRawTag)))
        {
            SetVorbisRaw(vorbis, DiscTotalRawTag, new List<string> { total.Value.ToString(CultureInfo.InvariantCulture) }, "");
        }
        if (!isDisc
            && total.HasValue
            && (ShouldOverwriteTag(config, SupportedTag.TrackTotal) || !TagRawProbe.HasVorbisRaw(vorbis, TrackTotalRawTag)))
        {
            SetVorbisRaw(vorbis, TrackTotalRawTag, new List<string> { total.Value.ToString(CultureInfo.InvariantCulture) }, "");
        }
    }

    private static void WriteMp4TrackNumber(
        TagLib.File file,
        int number,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool isDisc,
        string extension)
    {
        if (!IsMp4Family(extension))
        {
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && (isDisc ? file.Tag.Disc > 0 : file.Tag.Track > 0))
        {
            return;
        }

        if (!isDisc)
        {
            file.Tag.Track = (uint)number;
            if (total.HasValue
                && (ShouldOverwriteTag(config, SupportedTag.TrackTotal) || file.Tag.TrackCount == 0))
            {
                file.Tag.TrackCount = (uint)total.Value;
            }
            return;
        }

        file.Tag.Disc = (uint)number;
        if (total.HasValue)
        {
            file.Tag.DiscCount = (uint)total.Value;
        }
    }

    private static bool WriteLyrics(TagLib.File file, string extension, AutoTagTrack track, bool synced, AutoTagRunnerConfig config)
    {
        if (!TryResolveLyricsLines(track, synced, out var lyricsLines))
        {
            return false;
        }

        var lyricsText = string.Join(Environment.NewLine, lyricsLines);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return WriteId3Lyrics(file, synced, config, lyricsLines, lyricsText);
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return WriteVorbisLyrics(file, synced, config, lyricsText);
        }

        return WriteGenericLyrics(file, synced, config, lyricsText);
    }

    private static bool TryResolveLyricsLines(AutoTagTrack track, bool synced, out List<string> lyricsLines)
    {
        var key = synced ? SyncedLyricsTag : UnsyncedLyricsTag;
        if (track.Other.TryGetValue(key, out var preferred) && preferred is { Count: > 0 })
        {
            lyricsLines = preferred;
            return true;
        }

        if (track.Other.TryGetValue(LyricsTag, out var fallback) && fallback is { Count: > 0 })
        {
            lyricsLines = fallback;
            return true;
        }

        lyricsLines = new List<string>();
        return false;
    }

    private static bool WriteId3Lyrics(
        TagLib.File file,
        bool synced,
        AutoTagRunnerConfig config,
        IReadOnlyList<string> lyricsLines,
        string lyricsText)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (synced)
        {
            return WriteId3SyncedLyrics(id3, config, lyricsLines);
        }

        return WriteId3UnsyncedLyrics(id3, config, lyricsText);
    }

    private static bool WriteId3SyncedLyrics(TagLib.Id3v2.Tag id3, AutoTagRunnerConfig config, IReadOnlyList<string> lyricsLines)
    {
        if (!ShouldOverwriteTag(config, SupportedTag.SyncedLyrics)
            && id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>("SYLT").Any())
        {
            return true;
        }

        if (!lyricsLines.Any(line => line.StartsWith('[')))
        {
            return false;
        }

        var lang = string.IsNullOrWhiteSpace(config.Id3CommLang) ? "eng" : config.Id3CommLang;
        var frame = new TagLib.Id3v2.SynchronisedLyricsFrame(string.Empty, lang, TagLib.Id3v2.SynchedTextType.Lyrics)
        {
            Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds
        };

        frame.Text = BuildSyncedLyricsItems(lyricsLines).ToArray();
        id3.AddFrame(frame);
        return true;
    }

    private static List<TagLib.Id3v2.SynchedText> BuildSyncedLyricsItems(IReadOnlyList<string> lyricsLines)
    {
        var items = new List<TagLib.Id3v2.SynchedText>();
        foreach (var line in lyricsLines)
        {
            if (!TryParseLrcLine(line, out var timestamp, out var text))
            {
                continue;
            }

            items.Add(new TagLib.Id3v2.SynchedText((long)timestamp.TotalMilliseconds, text));
        }

        return items;
    }

    private static bool WriteId3UnsyncedLyrics(TagLib.Id3v2.Tag id3, AutoTagRunnerConfig config, string lyricsText)
    {
        if (!ShouldOverwriteTag(config, SupportedTag.UnsyncedLyrics)
            && id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>("USLT").Any())
        {
            return true;
        }

        var lang = string.IsNullOrWhiteSpace(config.Id3CommLang) ? "eng" : config.Id3CommLang;
        var frame = TagLib.Id3v2.UnsynchronisedLyricsFrame.Get(id3, string.Empty, lang, true);
        frame.Text = lyricsText;
        return true;
    }

    private static bool WriteVorbisLyrics(TagLib.File file, bool synced, AutoTagRunnerConfig config, string lyricsText)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var supportedTag = synced ? SupportedTag.SyncedLyrics : SupportedTag.UnsyncedLyrics;
        if (!ShouldOverwriteTag(config, supportedTag) && TagRawProbe.HasVorbisRaw(vorbis, LyricsUpperTag))
        {
            return true;
        }

        vorbis.SetField(LyricsUpperTag, lyricsText);
        return true;
    }

    private static bool WriteGenericLyrics(TagLib.File file, bool synced, AutoTagRunnerConfig config, string lyricsText)
    {
        var supportedTag = synced ? SupportedTag.SyncedLyrics : SupportedTag.UnsyncedLyrics;
        if (!ShouldOverwriteTag(config, supportedTag) && !string.IsNullOrWhiteSpace(file.Tag.Lyrics))
        {
            return true;
        }

        file.Tag.Lyrics = lyricsText;
        return true;
    }

    private static bool TryParseLrcLine(string line, out TimeSpan timestamp, out string text)
    {
        timestamp = TimeSpan.Zero;
        text = "";
        if (line.Length < 6 || line[0] != '[')
        {
            return false;
        }

        var end = line.IndexOf(']');
        if (end <= 0)
        {
            return false;
        }

        var ts = line[1..end];
        var parts = ts.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var minutes))
        {
            return false;
        }

        if (!double.TryParse(parts[1], out var seconds))
        {
            return false;
        }

        timestamp = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        text = line[(end + 1)..].Trim();
        return true;
    }

    private static IReadOnlyList<string> ResolveLrcSidecarLines(AutoTagTrack sourceTrack, string filePath, DeezSpoTagSettings settings)
    {
        var syncedPayload = ResolveLyricsPayloadLines(sourceTrack, SyncedLyricsTag);
        var payloadUsable = syncedPayload.Count > 0 && HasLrcSidecarSourceFormat(sourceTrack);
        var timingPreference = LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc);
        var payloadIsWord = payloadUsable && LrcContent.IsWordSynchronized(syncedPayload);

        var existingLrc = ResolveExistingLrcSidecar(filePath);
        if (existingLrc.Count > 0)
        {
            var existingIsWord = LrcContent.IsWordSynchronized(existingLrc);
            if (LrcTimingModes.ImpliesEnhanced(timingPreference)
                && payloadIsWord
                && !existingIsWord)
            {
                return syncedPayload;
            }

            return existingLrc;
        }

        if (timingPreference == LrcTimingModes.WordEnhanced)
        {
            return payloadIsWord ? syncedPayload : Array.Empty<string>();
        }

        return payloadUsable ? syncedPayload : Array.Empty<string>();
    }

    private static bool HasLrcSidecarSourceFormat(AutoTagTrack sourceTrack)
    {
        return sourceTrack.Other.TryGetValue(SyncedLyricsSourceFormatTag, out var values)
            && values.Any(value =>
                string.Equals(value, LyricsSourceFormat.DownloadedLrc.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, LyricsSourceFormat.ProviderSyncedJson.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ResolveExistingLrcSidecar(string filePath)
    {
        var existingLrcPath = Path.ChangeExtension(filePath, ".lrc");
        if (!IOFile.Exists(existingLrcPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            return NormalizeLyricsLines(IOFile.ReadAllLines(existingLrcPath), requireTimestamp: true);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ResolveLyricsPayloadLines(AutoTagTrack sourceTrack, string key)
    {
        if (!sourceTrack.Other.TryGetValue(key, out var payload) || payload.Count == 0)
        {
            return Array.Empty<string>();
        }

        return NormalizeLyricsLines(payload, requireTimestamp: true);
    }

    private static string? ResolveTtmlSidecarPayload(AutoTagTrack sourceTrack, string filePath)
    {
        var existingTtmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        if (IOFile.Exists(existingTtmlPath)
            && AppleLyricsService.IsWordSyncedTtml(ReadFileOrEmpty(existingTtmlPath)))
        {
            return null;
        }

        if (sourceTrack.Other.TryGetValue(TtmlLyricsTag, out var ttmlPayload) && ttmlPayload.Count > 0)
        {
            var existing = ComposeTtmlPayload(ttmlPayload);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        return null;
    }

    private static List<string> NormalizeLyricsLines(IEnumerable<string> lines, bool requireTimestamp)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trimmed in lines
            .Select(static line => line?.Trim())
            .Where(static trimmed => !string.IsNullOrWhiteSpace(trimmed)))
        {
            if (requireTimestamp && !trimmed!.StartsWith('['))
            {
                continue;
            }

            if (seen.Add(trimmed!))
            {
                normalized.Add(trimmed!);
            }
        }

        return normalized;
    }

    private static string? ComposeTtmlPayload(IEnumerable<string> payloadLines)
    {
        var ttml = string.Join(Environment.NewLine, payloadLines.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(ttml) ? null : ttml;
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

    private static class Mp4TagHelper
    {
        public static bool HasField(TagLib.File file, SupportedTag tag)
        {
            return tag switch
            {
                SupportedTag.Title => !string.IsNullOrWhiteSpace(file.Tag.Title),
                SupportedTag.Artist => file.Tag.Performers?.Length > 0,
                SupportedTag.AlbumArtist => file.Tag.AlbumArtists?.Length > 0,
                SupportedTag.Album => !string.IsNullOrWhiteSpace(file.Tag.Album),
                SupportedTag.Genre => file.Tag.Genres?.Length > 0,
                SupportedTag.BPM => file.Tag.BeatsPerMinute > 0,
                SupportedTag.TrackNumber => file.Tag.Track > 0,
                SupportedTag.TrackTotal => file.Tag.TrackCount > 0,
                SupportedTag.DiscNumber => file.Tag.Disc > 0,
                SupportedTag.DiscTotal => file.Tag.DiscCount > 0,
                SupportedTag.UnsyncedLyrics => !string.IsNullOrWhiteSpace(file.Tag.Lyrics),
                SupportedTag.AlbumArt => file.Tag.Pictures?.Length > 0,
                _ => false
            };
        }

        public static bool TrySetMp4Field(
            TagWriteContext context,
            SupportedTag tag,
            List<string> values)
        {
            if (!ShouldOverwriteTag(context.Config, tag) && HasTag(context.File, ".mp4", tag, context.Config, context.PlatformId))
            {
                return true;
            }

            switch (tag)
            {
                case SupportedTag.Title:
                    context.File.Tag.Title = values.FirstOrDefault() ?? "";
                    return true;
                case SupportedTag.Artist:
                    context.File.Tag.Performers = values.ToArray();
                    return true;
                case SupportedTag.AlbumArtist:
                    context.File.Tag.AlbumArtists = values.ToArray();
                    return true;
                case SupportedTag.Album:
                    context.File.Tag.Album = values.FirstOrDefault() ?? "";
                    return true;
                case SupportedTag.Genre:
                    context.File.Tag.Genres = SanitizeGenres(
                        values,
                        context.GenreAliasMap,
                        context.GenreBlockList,
                        context.SplitCompositeGenres).ToArray();
                    return true;
                case SupportedTag.BPM:
                    if (int.TryParse(values.FirstOrDefault(), out var bpm))
                    {
                        context.File.Tag.BeatsPerMinute = (uint)bpm;
                    }
                    return true;
                case SupportedTag.TrackNumber:
                    if (int.TryParse(values.FirstOrDefault(), out var track))
                    {
                        context.File.Tag.Track = (uint)track;
                    }
                    return true;
                case SupportedTag.TrackTotal:
                    if (int.TryParse(values.FirstOrDefault(), out var total))
                    {
                        context.File.Tag.TrackCount = (uint)total;
                    }
                    return true;
                case SupportedTag.DiscNumber:
                    if (int.TryParse(values.FirstOrDefault(), out var disc))
                    {
                        context.File.Tag.Disc = (uint)disc;
                    }
                    return true;
                case SupportedTag.DiscTotal:
                    if (int.TryParse(values.FirstOrDefault(), out var discTotal))
                    {
                        context.File.Tag.DiscCount = (uint)discTotal;
                    }
                    return true;
                default:
                    return false;
            }
        }

        public static void SetMp4Raw(
            TagLib.File file,
            string rawName,
            string[] values,
            IReadOnlyDictionary<string, string> genreAliasMap,
            IReadOnlyList<string> genreBlockList,
            bool splitCompositeGenres)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var output = IsGenreRawTag(normalized) || IsGenreRawTag(rawName)
                ? SanitizeGenres(values, genreAliasMap, genreBlockList, splitCompositeGenres).ToArray()
                : values;
            TrySetAppleDashBox(apple, normalized, output);
        }

        public static bool HasRaw(TagLib.File file, string rawName)
        {
            return HasMp4RawValue(file, rawName);
        }

        private static bool HasMp4RawValue(TagLib.File file, string rawName)
        {
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
            if (apple != null && TagRawProbe.HasAppleDashBox(apple, normalized))
            {
                return true;
            }

            if (ReadMp4AtlRawValues(file.Name, normalized).Count > 0)
            {
                return true;
            }

            return normalized.ToUpperInvariant() switch
            {
                "©NAM" or TitleUpperTag => !string.IsNullOrWhiteSpace(file.Tag.Title),
                "©ART" or ArtistUpperTag or "ARTISTS" => file.Tag.Performers?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "AART" or AlbumArtistUpperTag => file.Tag.AlbumArtists?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "©ALB" or AlbumUpperTag => !string.IsNullOrWhiteSpace(file.Tag.Album),
                "ISRC" => !string.IsNullOrWhiteSpace(file.Tag.ISRC),
                "©GEN" or Mp4GenreTag => file.Tag.Genres?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "TRACK" or "TRKN" => file.Tag.Track > 0 || file.Tag.TrackCount > 0,
                "DISC" or "DISK" => file.Tag.Disc > 0 || file.Tag.DiscCount > 0,
                "LYRICS" or "©LYR" => !string.IsNullOrWhiteSpace(file.Tag.Lyrics),
                _ => false
            };
        }

        public static void SetDate(TagLib.File file, string dateString)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            TrySetAppleDashBox(apple, "©day", new[] { dateString });
        }
    }
}
