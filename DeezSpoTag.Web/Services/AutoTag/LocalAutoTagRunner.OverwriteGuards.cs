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

    internal static string CapitalizeGenre(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var chars = words[i].ToCharArray();
            if (chars.Length == 0)
            {
                continue;
            }

            // Capitalize word starts without flattening the remaining casing:
            // R&B, HipHop and EDM must survive capitalization untouched.
            chars[0] = char.ToUpperInvariant(chars[0]);
            for (var c = 1; c < chars.Length; c++)
            {
                if (chars[c - 1] == '&' && char.IsLetter(chars[c]))
                {
                    chars[c] = char.ToUpperInvariant(chars[c]);
                }
            }

            words[i] = new string(chars);
        }

        return string.Join(' ', words);
    }

    private static bool IsGenreRawTag(string rawName)
    {
        var normalized = rawName.Trim();
        var mp4Normalized = Mp4RawTagNameNormalizer.Normalize(normalized);
        return normalized.Equals("TCON", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(Mp4GenreTag, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("©gen", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"----:com.apple.iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"com.apple.iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || mp4Normalized.Equals(Mp4GenreTag, StringComparison.OrdinalIgnoreCase)
            || mp4Normalized.Equals("©gen", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Keeps the file's existing genre order when the sanitized values are the same
    /// set (case-insensitive) as the tags already on the file. Platform payloads
    /// reorder genres between runs, and with genre in overwriteTags every run rewrote
    /// them in that platform's order — producing order-only diffs (HipHop, Rap →
    /// Rap, HipHop) with no content change. The returned list keeps the file's order
    /// while adopting the sanitized values' casing; any real set change keeps the
    /// platform order.
    /// </summary>
    internal static List<string> PreserveGenreOrderWhenSetEqual(List<string> values, IEnumerable<string?>? existingGenres)
    {
        if (values.Count == 0 || existingGenres == null)
        {
            return values;
        }

        var existingList = existingGenres
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();
        if (existingList.Count != values.Count)
        {
            return values;
        }

        var sanitizedByNormalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = value?.Trim() ?? string.Empty;
            if (key.Length == 0 || !sanitizedByNormalized.TryAdd(key, value ?? string.Empty))
            {
                return values;
            }
        }

        var ordered = new List<string>(values.Count);
        foreach (var existing in existingList)
        {
            if (!sanitizedByNormalized.TryGetValue(existing, out var sanitized))
            {
                return values;
            }

            ordered.Add(sanitized);
        }

        return ordered;
    }

    private static string ToCamelot(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        foreach (var (original, camelot) in CamelotNotes)
        {
            if (string.Equals(original, key, StringComparison.OrdinalIgnoreCase))
            {
                return camelot;
            }
        }
        return key;
    }

    private static readonly (string Original, string Camelot)[] CamelotNotes =
    {
        ("Abm", "1A"),
        ("G#m", "1A"),
        ("B", "1B"),
        ("D#m", "2A"),
        ("Ebm", "2A"),
        ("Gb", "2B"),
        ("F#", "2B"),
        ("A#m", "3A"),
        ("Bbm", "3A"),
        ("C#", "3B"),
        ("Db", "3B"),
        ("Dd", "3B"),
        ("Fm", "4A"),
        ("G#", "4B"),
        ("Ab", "4B"),
        ("Cm", "5A"),
        ("D#", "5B"),
        ("Eb", "5B"),
        ("Gm", "6A"),
        ("A#", "6B"),
        ("Bb", "6B"),
        ("Dm", "7A"),
        ("F", "7B"),
        ("Am", "8A"),
        ("C", "8B"),
        ("Em", "9A"),
        ("G", "9B"),
        ("Bm", "10A"),
        ("D", "10B"),
        ("Gbm", "11A"),
        ("F#m", "11A"),
        ("A", "11B"),
        ("C#m", "12A"),
        ("Dbm", "12A"),
        ("E", "12B")
    };

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

    private static List<string> ReadRawTagValuesCore(
        TagLib.File file,
        string extension,
        string name,
        Func<TagLib.Mpeg4.AppleTag, string, List<string>> readAppleValues)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            if (id3 == null) return new List<string>();
            if (name.Length == 4)
            {
                var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, name, false);
                return frame?.Text?.ToList() ?? new List<string>();
            }

            var user = TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, false);
            return user?.Text?.ToList() ?? new List<string>();
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
            return vorbis?.GetField(name).ToList() ?? new List<string>();
        }

        if (IsMp4Family(extension))
        {
            var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
            var normalizedName = Mp4RawTagNameNormalizer.Normalize(name);
            if (apple != null)
            {
                var dashValues = readAppleValues(apple, normalizedName);
                if (dashValues.Count > 0)
                {
                    return dashValues;
                }
            }

            return ReadMp4AtlRawValues(file.Name, normalizedName);
        }

        return new List<string>();
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

    private static void AddMp4AtlLyricsValues(List<string> values, ATL.Track atlTrack)
    {
        if (atlTrack.Lyrics == null || atlTrack.Lyrics.Count == 0)
        {
            return;
        }

        foreach (var line in atlTrack.Lyrics)
        {
            AddIfPresent(values, line?.UnsynchronizedLyrics);
        }
    }

    private static string ResolveAtlAdditionalValue(Dictionary<string, string> additional, string key)
    {
        return additional.TryGetValue(key, out var value)
            ? value
            : string.Empty;
    }

    private static void AddIfPresent(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
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

    private static void ApplyAppleCustomTags(TagLib.Mpeg4.AppleTag tag, List<CustomTagWrite> writes, AutoTagRunnerConfig config, string separator, HashSet<string> enabledTags, HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            var rawName = Mp4RawTagNameNormalizer.Normalize(write.RawTagName);
            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasAppleDashBox(tag, rawName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            TrySetAppleDashBox(tag, rawName, ApplySeparator(write.Values, separator));
            attemptedTags.Add(write.SupportedTag);
        }
    }

    private static TagSettings ApplyOverwriteRules(
        string filePath,
        TagSettings baseSettings,
        AutoTagRunnerConfig config,
        string platformId,
        AutoTagTrack? sourceTrack = null,
        DeezSpoTagSettings? runtimeSettings = null)
    {
        var copy = CloneTagSettings(baseSettings);
        if (config.Tags.Count == 0)
        {
            return copy;
        }

        try
        {
            using var file = TagLib.File.Create(filePath);
            var extension = Path.GetExtension(filePath);
            var enabled = BuildConfiguredTagSet(config.Tags);
            var context = new OverwriteRuleContext(enabled, config, file, extension, platformId);

            ApplyOverwriteRule(copy, context, TitleTag, SupportedTag.Title, static c => c.Title = false);
            ApplyOverwriteRule(copy, context, ArtistTag, SupportedTag.Artist, static c => c.Artist = false);
            ApplyOverwriteRule(copy, context, AlbumArtistTag, SupportedTag.AlbumArtist, static c => c.AlbumArtist = false);
            ApplyOverwriteRule(copy, context, AlbumTag, SupportedTag.Album, static c => c.Album = false);
            ApplyOverwriteRule(copy, context, GenreTag, SupportedTag.Genre, static c => c.Genre = false);
            ApplyOverwriteRule(copy, context, LabelTag, SupportedTag.Label, static c => c.Label = false);
            ApplyOverwriteRule(copy, context, "bpm", SupportedTag.BPM, static c => c.Bpm = false);
            ApplyOverwriteRule(copy, context, "isrc", SupportedTag.ISRC, static c => c.Isrc = false);
            ApplyOverwriteRule(copy, context, DurationTag, SupportedTag.Duration, static c => c.Length = false);
            ApplyOverwriteRule(copy, context, DiscNumberTag, SupportedTag.DiscNumber, static c => c.DiscNumber = false);
            ApplyOverwriteRule(copy, context, AlbumArtTag, SupportedTag.AlbumArt, static c => c.Cover = false);
            ApplyOverwriteRule(copy, context, UnsyncedLyricsTag, SupportedTag.UnsyncedLyrics, static c => c.Lyrics = false);
            ApplyOverwriteRule(copy, context, SyncedLyricsTag, SupportedTag.SyncedLyrics, static c => c.SyncedLyrics = false);

            ApplyReleaseDateOverwriteRule(copy, context);
            ApplyTrackNumberOverwriteRule(copy, context);
            ApplyTrackTotalOverwriteRule(copy, context);
            ApplyPreferenceAwareOverwriteGuards(copy, sourceTrack, runtimeSettings, file, platformId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return copy;
        }

        return copy;
    }

    private static TagSettings CloneTagSettings(TagSettings baseSettings)
    {
        return new TagSettings
        {
            Title = baseSettings.Title,
            Artist = baseSettings.Artist,
            Artists = baseSettings.Artists,
            Album = baseSettings.Album,
            Cover = baseSettings.Cover,
            TrackNumber = baseSettings.TrackNumber,
            TrackTotal = baseSettings.TrackTotal,
            DiscNumber = baseSettings.DiscNumber,
            DiscTotal = baseSettings.DiscTotal,
            AlbumArtist = baseSettings.AlbumArtist,
            Genre = baseSettings.Genre,
            Year = baseSettings.Year,
            Date = baseSettings.Date,
            Explicit = baseSettings.Explicit,
            Isrc = baseSettings.Isrc,
            Barcode = baseSettings.Barcode,
            Length = baseSettings.Length,
            Bpm = baseSettings.Bpm,
            ReplayGain = baseSettings.ReplayGain,
            Label = baseSettings.Label,
            Copyright = baseSettings.Copyright,
            Lyrics = baseSettings.Lyrics,
            SyncedLyrics = baseSettings.SyncedLyrics,
            Composer = baseSettings.Composer,
            InvolvedPeople = baseSettings.InvolvedPeople,
            Source = baseSettings.Source,
            Rating = baseSettings.Rating,
            SavePlaylistAsCompilation = baseSettings.SavePlaylistAsCompilation,
            UseNullSeparator = baseSettings.UseNullSeparator,
            SaveID3v1 = baseSettings.SaveID3v1,
            Url = baseSettings.Url,
            TrackId = baseSettings.TrackId,
            ReleaseId = baseSettings.ReleaseId,
            MultiArtistSeparator = baseSettings.MultiArtistSeparator,
            SingleAlbumArtist = baseSettings.SingleAlbumArtist,
            CoverDescriptionUTF8 = baseSettings.CoverDescriptionUTF8
        };
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

    private static bool ShouldOverwriteRawTag(
        TagLib.File file,
        string extension,
        AutoTagRunnerConfig config,
        string configTagKey,
        string rawName)
    {
        if (config.Overwrite || config.OverwriteTags.Any(tag => string.Equals(tag?.Trim(), configTagKey, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !HasRawTag(file, extension, rawName);
    }

    private static List<string> ResolveOtherValues(AutoTagTrack track, params string[] keys)
    {
        var values = new List<string>();
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !track.Other.TryGetValue(key, out var keyValues))
            {
                continue;
            }

            values = values
                .Concat(keyValues.SelectMany(SplitCompositeRawValues))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return values;
    }

    private static List<string> ResolveFirstClassOrOtherValues(string? firstClassValue, AutoTagTrack track, params string[] keys)
    {
        var values = ResolveOtherValues(track, keys);
        if (!string.IsNullOrWhiteSpace(firstClassValue))
        {
            values.Insert(0, firstClassValue.Trim());
        }

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SplitCompositeRawValues(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split([';', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int? ResolveFirstPositiveInt(AutoTagTrack track, params string[] keys)
    {
        return ResolveOtherValues(track, keys)
            .Select(raw => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null)
            .FirstOrDefault(parsed => parsed > 0);
    }

    private static string ResolveComposerRawName(string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return "TCOM";
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return ComposerUpperTag;
        }

        return "©wrt";
    }

    private static string ResolveLyricistRawName(string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return "TEXT";
        }

        return LyricistRawTag;
    }

    private static void ApplyOverwriteRule(
        TagSettings settings,
        OverwriteRuleContext context,
        string tagKey,
        SupportedTag supportedTag,
        Action<TagSettings> disableAction)
    {
        if (!context.EnabledTags.Contains(tagKey))
        {
            return;
        }

        if (ShouldOverwriteTag(context.Config, supportedTag))
        {
            return;
        }

        if (!HasTag(context.File, context.Extension, supportedTag, context.Config, context.PlatformId))
        {
            return;
        }

        disableAction(settings);
    }

    private static void ApplyReleaseDateOverwriteRule(TagSettings settings, OverwriteRuleContext context)
    {
        if (!context.EnabledTags.Contains(ReleaseDateTag))
        {
            return;
        }

        if (ShouldOverwriteTag(context.Config, SupportedTag.ReleaseDate))
        {
            return;
        }

        if (!HasTag(context.File, context.Extension, SupportedTag.ReleaseDate, context.Config, context.PlatformId))
        {
            return;
        }

        settings.Date = false;
        settings.Year = false;
    }

    private static void ApplyTrackNumberOverwriteRule(TagSettings settings, OverwriteRuleContext context)
    {
        if (!context.EnabledTags.Contains(TrackNumberTag))
        {
            return;
        }

        if (ShouldOverwriteTag(context.Config, SupportedTag.TrackNumber))
        {
            return;
        }

        if (!HasTag(context.File, context.Extension, SupportedTag.TrackNumber, context.Config, context.PlatformId))
        {
            return;
        }

        settings.TrackNumber = false;
        settings.TrackTotal = false;
    }

    private static void ApplyTrackTotalOverwriteRule(TagSettings settings, OverwriteRuleContext context)
    {
        if (context.EnabledTags.Contains(TrackTotalTag) && !settings.TrackNumber)
        {
            settings.TrackTotal = false;
        }
    }

    private static void ApplyPreferenceAwareOverwriteGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack? sourceTrack,
        DeezSpoTagSettings? runtimeSettings,
        TagLib.File file,
        string platformId)
    {
        if (sourceTrack == null
            || runtimeSettings == null
            || (!effectiveTagSettings.Artist && !effectiveTagSettings.AlbumArtist && !effectiveTagSettings.Title))
        {
            return;
        }

        var existingTitle = file.Tag.Title;
        ApplyTitleLossyOverwriteGuard(effectiveTagSettings, sourceTrack, existingTitle, platformId);

        var existingArtistCredits = file.Tag.Performers?
            .Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value))
            .ToList() ?? new List<string>();
        if (!IsWeakMetadataValue(file.Tag.FirstPerformer) && !IsVariousArtistsValue(file.Tag.FirstPerformer))
        {
            existingArtistCredits.Add(file.Tag.FirstPerformer!);
        }

        var existingAlbumArtistCredits = file.Tag.AlbumArtists?
            .Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value))
            .ToList() ?? new List<string>();

        var aliasOverwrite = ApplyPreferredArtistAliasToExistingCredits(
            effectiveTagSettings,
            sourceTrack,
            existingArtistCredits,
            existingAlbumArtistCredits,
            existingTitle);

        if (!aliasOverwrite.ForcedArtist && !aliasOverwrite.ForcedAlbumArtist)
        {
            ApplyPreferenceAwareArtistGuards(
                effectiveTagSettings,
                sourceTrack,
                runtimeSettings,
                existingArtistCredits,
                existingAlbumArtistCredits,
                aliasOverwrite.RewrittenTitle ?? existingTitle);
        }
        else
        {
            PreserveRicherCreditsWithoutBlockingPreferredWrite(
                sourceTrack,
                existingArtistCredits,
                existingAlbumArtistCredits);
            if (!aliasOverwrite.ForcedTitle)
            {
                ApplyTitleFeaturedGuard(
                    effectiveTagSettings,
                    sourceTrack,
                    runtimeSettings,
                    SplitArtistCredits(existingArtistCredits),
                    existingTitle);
            }
        }

        ApplyAlbumLossyOverwriteGuard(effectiveTagSettings, sourceTrack, file.Tag.Album);
        ApplyPlatformOverwriteGuards(
            effectiveTagSettings,
            sourceTrack,
            file,
            platformId,
            aliasOverwrite);
    }
}
