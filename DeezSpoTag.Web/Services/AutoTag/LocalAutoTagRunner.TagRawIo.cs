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

    /// <summary>
    /// Reads the ordering metadata for one file: the alphabetically-first main artist
    /// (multi-artist credits sort under their first artist), the album title, and the
    /// track number. Files that cannot be read sort as unknown-identity material.
    /// </summary>
    private static ArtistSortMeta ReadArtistSortMeta(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var artists = (tag.Performers ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            var albumArtist = !string.IsNullOrWhiteSpace(tag.FirstAlbumArtist)
                ? tag.FirstAlbumArtist
                : tag.FirstAlbumArtistSort ?? tag.JoinedAlbumArtists;
            if (artists.Count == 0 && !string.IsNullOrWhiteSpace(tag.FirstPerformer))
            {
                artists.Add(tag.FirstPerformer);
            }

            // Sort by album/main artist so featured credits do not pull a track
            // under another name. Fall back to track artists when album artist is empty.
            var artistKey = ArtistOrderKey.ResolveMainArtistKey(
                string.IsNullOrWhiteSpace(albumArtist) ? artists : new[] { albumArtist },
                artists.FirstOrDefault());
            var album = string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album;
            var weakIdentity = TrackIdentityTrust.IsWeakMetadataValue(artists.FirstOrDefault() ?? albumArtist)
                || TrackIdentityTrust.IsWeakMetadataValue(album);
            return new ArtistSortMeta(
                artistKey,
                AlbumTitleNormalizer.CoreTitle(album),
                tag.Track > 0 ? (int)tag.Track : null,
                weakIdentity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ArtistSortMeta(string.Empty, string.Empty, null, WeakIdentity: true);
        }
    }

    private static void AddLookupUrl(Dictionary<string, string> urls, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
        {
            urls[key] = value.Trim();
        }
    }

    private static string? ReadFirstTagValue(Dictionary<string, List<string>> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !tags.TryGetValue(key, out var values) || values.Count == 0)
            {
                continue;
            }

            var value = values.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static void AddTagIfAny(Dictionary<string, List<string>> tags, string key, List<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        tags[key] = values;
    }

    private static List<string> ReadRawTagValuesAny(TagLib.File file, string extension, params string[] rawNames)
    {
        var values = new List<string>();
        foreach (var value in rawNames
                     .SelectMany(rawName => ReadRawTagValues(file, extension, rawName))
                     .Where(value => !values.Contains(value, StringComparer.OrdinalIgnoreCase)))
        {
            values.Add(value);
        }

        return values;
    }

    private static void SetOtherValue(AutoTagTrack track, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        track.Other[key.Trim()] = new List<string> { value.Trim() };
    }

    private static string? ReadFirstRawTagValue(AutoTagAudioInfo info, string[] tagNames)
    {
        foreach (var tagName in tagNames)
        {
            if (info.Tags.TryGetValue(tagName, out var values) && values is { Count: > 0 })
            {
                var value = values.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static string BuildAtlDashFieldName(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : $"----:com.apple.iTunes:{name.Trim()}";
    }

    private static void PrepareId3Version(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        id3.Version = context.Config.Id3v24 ? (byte)4 : (byte)3;
    }

    private static void RemoveId3v1TagIfDisabled(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || context.EffectiveTagSettings.SaveID3v1)
        {
            return;
        }

        file.RemoveTags(TagTypes.Id3v1);
        file.Save();
    }

    private static void WriteSingleRawTag(
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context,
        string tagKey,
        SupportedTag supportedTag,
        string rawTagName,
        string? value)
    {
        if (!context.EnabledTags.Contains(tagKey) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        SetRaw(tagWriteContext, rawTagName, supportedTag, new List<string> { value });
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

    private static void SetId3Raw(TagLib.Id3v2.Tag tag, string name, List<string> values, string separator, bool useNullSeparator = false)
    {
        var output = ApplySeparator(values, separator, useNullSeparator);
        if (name.Length == 4)
        {
            var frame = TagLib.Id3v2.TextInformationFrame.Get(tag, name, true);
            if (useNullSeparator)
            {
                frame.TextEncoding = TagLib.StringType.UTF16;
            }
            frame.Text = output;
            return;
        }

        var user = TagLib.Id3v2.UserTextInformationFrame.Get(tag, name, true);
        if (useNullSeparator)
        {
            user.TextEncoding = TagLib.StringType.UTF16;
        }
        user.Text = output;
    }

    private static void SetVorbisRaw(TagLib.Ogg.XiphComment tag, string name, List<string> values, string separator)
    {
        var output = ApplySeparator(values, separator);
        tag.SetField(name, output);
    }

    private static List<string> ReadExistingRawTag(TagLib.File file, string extension, string name)
    {
        return ReadRawTagValuesCore(
            file,
            extension,
            name,
            static (apple, rawName) => TagRawProbe.HasAppleDashBox(apple, rawName)
                ? new List<string> { rawName }
                : new List<string>());
    }

    private static List<string> ReadRawTagValues(TagLib.File file, string extension, string name)
    {
        return ReadRawTagValuesCore(file, extension, name, ReadAppleDashBox);
    }

    private static List<string> ReadMp4AtlRawValues(string filePath, string rawName)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !IOFile.Exists(filePath))
        {
            return new List<string>();
        }

        try
        {
            var atlTrack = new ATL.Track(filePath);
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var values = new List<string>();

            AddMp4AtlNativeRawValues(values, atlTrack, normalized);

            if (atlTrack.AdditionalFields != null)
            {
                var additional = new Dictionary<string, string>(atlTrack.AdditionalFields, StringComparer.OrdinalIgnoreCase);
                AddIfPresent(values, ResolveAtlAdditionalValue(additional, normalized));
                AddIfPresent(values, ResolveAtlAdditionalValue(additional, BuildAtlDashFieldName(normalized)));
            }

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return new List<string>();
        }
    }

    private static void AddMp4AtlNativeRawValues(List<string> values, ATL.Track atlTrack, string normalized)
    {
        switch (normalized.ToUpperInvariant())
        {
            case "©NAM":
            case TitleUpperTag:
                AddIfPresent(values, atlTrack.Title);
                break;
            case "©ART":
            case ArtistUpperTag:
            case "ARTISTS":
                AddIfPresent(values, atlTrack.Artist);
                break;
            case "©ALB":
            case AlbumUpperTag:
                AddIfPresent(values, atlTrack.Album);
                break;
            case "AART":
            case AlbumArtistUpperTag:
            case "ALBUM ARTIST":
                AddIfPresent(values, atlTrack.AlbumArtist);
                break;
            case "©WRT":
            case ComposerUpperTag:
                AddIfPresent(values, atlTrack.Composer);
                break;
            case "©GEN":
            case Mp4GenreTag:
                AddIfPresent(values, atlTrack.Genre);
                break;
            case "ISRC":
                AddIfPresent(values, atlTrack.ISRC);
                break;
            case "DATE":
            case "YEAR":
            case "©DAY":
                AddMp4AtlDateValue(values, atlTrack);
                break;
            case "BPM":
            case "TMPO":
                AddMp4AtlPositiveNumberValue(values, atlTrack.BPM);
                break;
            case "TRACK":
            case "TRKN":
                AddMp4AtlPositiveNumberValue(values, atlTrack.TrackNumber);
                break;
            case "DISC":
            case "DISK":
                AddMp4AtlPositiveNumberValue(values, atlTrack.DiscNumber);
                break;
            case "LYRICS":
            case "©LYR":
                AddMp4AtlLyricsValues(values, atlTrack);
                break;
        }
    }

    private static void AddMp4AtlDateValue(List<string> values, ATL.Track atlTrack)
    {
        if (atlTrack.Date.HasValue)
        {
            AddIfPresent(values, atlTrack.Date.Value.ToString(IsoDateFormat));
        }
    }

    private static void AddMp4AtlPositiveNumberValue(List<string> values, double? value)
    {
        if (value is > 0)
        {
            AddIfPresent(values, value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddIfPresent(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static void SetRawIfAllowed(
        TagWriteContext context,
        string configTagKey,
        string rawName,
        List<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        if (!ShouldOverwriteRawTag(context.File, context.Extension, context.Config, configTagKey, rawName))
        {
            if (SupportedTagMap.TryGetValue(configTagKey, out var retainedTag))
            {
                context.AttemptedTags.Add(retainedTag);
            }
            return;
        }

        WriteRawTagValues(context, rawName, values);
        if (SupportedTagMap.TryGetValue(configTagKey, out var supportedTag))
        {
            context.AttemptedTags.Add(supportedTag);
        }
    }

    private static void WriteRawTagValues(TagWriteContext context, string rawName, List<string> values)
    {
        if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag)context.File.GetTag(TagTypes.Id3v2, true);
            SetId3Raw(id3, rawName, values, context.Separator, context.UseNullSeparator);
            return;
        }

        if (context.Extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment)context.File.GetTag(TagTypes.Xiph, true);
            SetVorbisRaw(vorbis, rawName, values, context.Separator);
            return;
        }

        if (IsMp4Family(context.Extension))
        {
            Mp4TagHelper.SetMp4Raw(
                context.File,
                rawName,
                ApplySeparator(values, context.Separator),
                context.GenreAliasMap,
                context.GenreBlockList,
                context.SplitCompositeGenres);
        }
    }

    private static void RemoveRawTagValues(TagWriteContext context, string rawName)
    {
        if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)context.File.GetTag(TagTypes.Id3v2, false);
            if (id3 == null)
            {
                return;
            }

            if (rawName.Length == 4)
            {
                id3.RemoveFrames(rawName);
                return;
            }

            foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>("TXXX")
                         .Where(frame => string.Equals(frame.Description, rawName, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                id3.RemoveFrame(frame);
            }
            return;
        }

        if (context.Extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)context.File.GetTag(TagTypes.Xiph, false);
            vorbis?.RemoveField(rawName);
            return;
        }

        if (IsMp4Family(context.Extension))
        {
            var apple = (TagLib.Mpeg4.AppleTag?)context.File.GetTag(TagTypes.Apple, false);
            AppleDashBoxReflectionHelper.TryClearValues(apple, Mp4RawTagNameNormalizer.Normalize(rawName));
        }
    }

    private static IEnumerable<string> SplitCompositeRawValues(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split([';', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
