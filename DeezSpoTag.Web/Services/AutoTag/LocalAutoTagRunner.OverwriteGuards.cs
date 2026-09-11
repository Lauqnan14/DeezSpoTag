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

    private static HashSet<SupportedTag> CapturePresentTags(
        string filePath,
        AutoTagRunnerConfig config,
        string platformId,
        IEnumerable<SupportedTag> tags)
    {
        var present = new HashSet<SupportedTag>();
        using var file = TagLib.File.Create(filePath);
        var extension = Path.GetExtension(filePath);
        foreach (var tag in tags)
        {
            if (HasTag(file, extension, tag, config, platformId))
            {
                present.Add(tag);
            }
        }

        return present;
    }

    private static bool HasTagValue(AutoTagAudioInfo info, params string[] keys)
    {
        return !string.IsNullOrWhiteSpace(ReadFirstTagValue(info.Tags, keys));
    }

    private static bool HasAnyTags(AutoTagRunnerConfig config, params string[] tags)
    {
        if (config.Tags == null || config.Tags.Count == 0)
        {
            return false;
        }

        var configured = BuildConfiguredTagSet(config.Tags);
        return tags.Any(configured.Contains);
    }

    private static bool HasExistingTags(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var extension = Path.GetExtension(filePath);
            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
                if (id3 == null) return false;
                return TagRawProbe.HasId3Raw(id3, TaggedDateTag);
            }

            if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
            {
                var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
                return vorbis != null && TagRawProbe.HasVorbisRaw(vorbis, TaggedDateTag);
            }

            if (IsMp4Family(extension))
            {
                return Mp4TagHelper.HasRaw(file, TaggedDateTag);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool ShouldOverwriteMaterializedFile(DeezSpoTagSettings settings)
        => string.Equals(settings.OverwriteFile, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(settings.OverwriteFile, "overwrite", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldOverwriteTag(AutoTagRunnerConfig config, SupportedTag tag)
    {
        if (config.Overwrite)
        {
            return true;
        }

        return config.OverwriteTags.Any(t => SupportedTagMap.TryGetValue(t.Trim(), out var mapped) && mapped == tag);
    }

    private static bool HasReleaseTypeTagEnabled(HashSet<string> enabledTags)
        => enabledTags.Contains(ReleaseTypeTag) || enabledTags.Contains(OtherTagsTag);

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

    private static void ApplyAlbumLossyOverwriteGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        string? existingAlbum)
    {
        var incomingAlbum = sourceTrack.Album?.Trim();
        var currentAlbum = existingAlbum?.Trim();
        if (!effectiveTagSettings.Album
            || string.IsNullOrWhiteSpace(currentAlbum)
            || string.IsNullOrWhiteSpace(incomingAlbum))
        {
            return;
        }

        var similarity = AutoTagSimilarity.ComputeScore(
            AutoTagSimilarity.NormalizeText(currentAlbum),
            AutoTagSimilarity.NormalizeText(incomingAlbum));
        if (similarity >= 0.90d)
        {
            return;
        }

        sourceTrack.Album = currentAlbum;
        effectiveTagSettings.Album = false;
    }

    private static void ApplyPlatformOverwriteGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        TagLib.File file,
        string platformId,
        ArtistAliasOverwriteDecision aliasOverwrite = default)
    {
        if (!string.Equals(platformId, BoomplayPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existingTitle = file.Tag.Title?.Trim();
        if (effectiveTagSettings.Title
            && !aliasOverwrite.ForcedTitle
            && !string.IsNullOrWhiteSpace(existingTitle)
            && !string.IsNullOrWhiteSpace(sourceTrack.Title))
        {
            var normalizedExistingTitle = AutoTagSimilarity.NormalizeText(existingTitle);
            var normalizedIncomingTitle = AutoTagSimilarity.NormalizeText(sourceTrack.Title);
            var titleSimilarity = AutoTagSimilarity.ComputeScore(normalizedExistingTitle, normalizedIncomingTitle);
            if (!TrackTitleMatcher.HasCompatibleTitleIdentity(existingTitle, sourceTrack.Title)
                || titleSimilarity < 0.90d)
            {
                sourceTrack.Title = existingTitle;
                effectiveTagSettings.Title = false;
            }
        }

        var existingArtists = SplitArtistCredits(file.Tag.Performers?.Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value)).ToList()
            ?? new List<string>());
        var incomingArtists = SplitArtistCredits(sourceTrack.Artists);
        if (effectiveTagSettings.Artist
            && !aliasOverwrite.ForcedArtist
            && existingArtists.Count > 0
            && incomingArtists.Count > 0
            && !AreArtistCreditsEquivalent(existingArtists, incomingArtists))
        {
            sourceTrack.Artists = existingArtists;
            effectiveTagSettings.Artist = false;
        }

        var existingAlbumArtists = SplitArtistCredits(file.Tag.AlbumArtists?.Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value)).ToList()
            ?? new List<string>());
        var incomingAlbumArtists = SplitArtistCredits(sourceTrack.AlbumArtists);
        if (effectiveTagSettings.AlbumArtist
            && !aliasOverwrite.ForcedAlbumArtist
            && existingAlbumArtists.Count > 0
            && incomingAlbumArtists.Count > 0
            && !AreArtistCreditsEquivalent(existingAlbumArtists, incomingAlbumArtists))
        {
            sourceTrack.AlbumArtists = existingAlbumArtists;
            effectiveTagSettings.AlbumArtist = false;
        }

        var existingAlbum = file.Tag.Album?.Trim();
        var incomingAlbum = sourceTrack.Album?.Trim();
        if (effectiveTagSettings.Album
            && !string.IsNullOrWhiteSpace(existingAlbum)
            && !string.IsNullOrWhiteSpace(incomingAlbum))
        {
            var normalizedExistingAlbum = AutoTagSimilarity.NormalizeText(existingAlbum);
            var normalizedIncomingAlbum = AutoTagSimilarity.NormalizeText(incomingAlbum);
            var albumSimilarity = AutoTagSimilarity.ComputeScore(normalizedExistingAlbum, normalizedIncomingAlbum);
            if (albumSimilarity < 0.90d)
            {
                sourceTrack.Album = existingAlbum;
                effectiveTagSettings.Album = false;
            }
        }
    }
}
