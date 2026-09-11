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

    /// <summary>
    /// True when the final genre list equals the file's current genres exactly
    /// (same values, same casing, order-insensitive). Casing-only changes such as
    /// R&b → R&B are real repairs and must be written.
    /// </summary>
    private static bool GenreWriteAddsNothing(List<string> genres, List<string> existingGenres)
    {
        if (genres.Count != existingGenres.Count)
        {
            return false;
        }

        return genres
            .Select(value => value?.Trim() ?? string.Empty)
            .OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(
                existingGenres.Select(value => value?.Trim() ?? string.Empty).OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static List<string> NormalizeStyleValues(IEnumerable<string> values, string separator)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var effectiveSeparator = string.IsNullOrEmpty(separator) ? "," : separator;
            var parts = value.Split(
                effectiveSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    normalized.Add(trimmed);
                }
            }
        }

        return normalized;
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

    private static void WriteReleaseDateTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ReleaseDateTag) || !context.SourceTrack.ReleaseDate.HasValue)
        {
            return;
        }

        WriteDate(
            file,
            context.Extension,
            ReleaseDateTag,
            context.SourceTrack.ReleaseDate.Value,
            SupportedTag.ReleaseDate,
            context.Config,
            context.EffectiveTagSettings.UseNullSeparator);
        MarkAttemptedIfPresent(context, file, SupportedTag.ReleaseDate);
    }

    private static void WritePublishDateTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(PublishDateTag) || !context.SourceTrack.PublishDate.HasValue)
        {
            return;
        }

        WriteDate(
            file,
            context.Extension,
            PublishDateTag,
            context.SourceTrack.PublishDate.Value,
            SupportedTag.PublishDate,
            context.Config,
            context.EffectiveTagSettings.UseNullSeparator);
        MarkAttemptedIfPresent(context, file, SupportedTag.PublishDate);
    }

    private static void WriteUrlTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("url") || string.IsNullOrWhiteSpace(context.SourceTrack.Url))
        {
            return;
        }

        var url = context.SourceTrack.Url;
        if (string.Equals(context.PlatformId, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            url = NormalizeSpotifyTrackUrl(url);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }
        }

        SetRaw(tagWriteContext, WwwAudioFileTag, SupportedTag.URL, new List<string> { url });

        if (!string.Equals(context.PlatformId, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existingSpotifyUrls = ReadRawTagValues(tagWriteContext.File, context.Extension, SpotifyUrlTag);
        var shouldWriteSpotifyUrl = ShouldOverwriteTag(context.Config, SupportedTag.URL)
            || existingSpotifyUrls.Count == 0
            || existingSpotifyUrls.Any(existing => NormalizeSpotifyTrackUrl(existing) == null);
        if (shouldWriteSpotifyUrl)
        {
            SetRaw(tagWriteContext, SpotifyUrlTag, SupportedTag.URL, new List<string> { url }, force: true);
        }
    }

    private static void WriteTrackIdTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(TrackIdTag) || string.IsNullOrWhiteSpace(context.SourceTrack.TrackId))
        {
            return;
        }

        SetRaw(
            tagWriteContext,
            $"{context.PlatformId.ToUpperInvariant()}_TRACK_ID",
            SupportedTag.TrackId,
            new List<string> { context.SourceTrack.TrackId });
    }

    private static void WriteReleaseIdTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ReleaseIdTag) || string.IsNullOrWhiteSpace(context.SourceTrack.ReleaseId))
        {
            return;
        }

        var releaseId = context.SourceTrack.ReleaseId.Trim();
        // Namespace guard: a value from another platform's id family (e.g. an Audiomack
        // numeric album id) must never be written into this platform's release-id tag.
        if (!IsPlatformReleaseIdShapeValid(context.PlatformId, releaseId))
        {
            return;
        }

        SetRaw(
            tagWriteContext,
            $"{context.PlatformId.ToUpperInvariant()}_RELEASE_ID",
            SupportedTag.ReleaseId,
            new List<string> { releaseId });
    }

    /// <summary>
    /// Per-platform release-id shape contract: MusicBrainz ids are GUIDs, Spotify ids
    /// are 22-character base62 strings, and the known numeric catalog platforms
    /// (Deezer, Apple/iTunes, Audiomack, Shazam, Boomplay, Amazon, Discogs) use digit
    /// strings. Unknown platforms are not restricted.
    /// </summary>
    internal static bool IsPlatformReleaseIdShapeValid(string? platformId, string value)
    {
        if (string.IsNullOrWhiteSpace(platformId) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalizedValue = value.Trim();
        return platformId.Trim().ToLowerInvariant() switch
        {
            "musicbrainz" => Guid.TryParse(normalizedValue, out _),
            "spotify" => normalizedValue.Length == 22 && normalizedValue.All(char.IsLetterOrDigit),
            "deezer" or "apple" or "itunes" or "audiomack" or "shazam" or "boomplay" or "amazon" or "discogs" => normalizedValue.All(char.IsDigit),
            _ => true
        };
    }

    private static void WriteSourceIdentityTags(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteSingleRawTag(tagWriteContext, context, RecordingIdTag, SupportedTag.RecordingId, RecordingIdRawTag, context.SourceTrack.RecordingId);
        WriteSingleRawTag(tagWriteContext, context, ArtistIdTag, SupportedTag.ArtistId, ArtistIdRawTag, context.SourceTrack.ArtistId);
        // The generic ALBUMARTISTID/ALBUMID tags are one shared namespace: only
        // GUID-shaped (MusicBrainz) values may be written there. Platform-local ids
        // (Deezer numeric ids, Spotify ids, …) keep their own <PLATFORM>_… tags, so
        // files of one album cannot end up with mixed id namespaces.
        WriteSingleRawTag(tagWriteContext, context, AlbumArtistIdTag, SupportedTag.AlbumArtistId, AlbumArtistIdRawTag, ToMusicBrainzShapedId(context.SourceTrack.AlbumArtistId));
        WriteSingleRawTag(tagWriteContext, context, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, ReleaseGroupIdRawTag, ToMusicBrainzShapedId(context.SourceTrack.ReleaseGroupId));
        WriteSingleRawTag(tagWriteContext, context, AlbumIdTag, SupportedTag.AlbumId, AlbumIdRawTag, ToMusicBrainzShapedId(context.SourceTrack.AlbumId));
        WriteSingleRawTag(tagWriteContext, context, ReleaseStatusTag, SupportedTag.ReleaseStatus, ReleaseStatusRawTag, context.SourceTrack.ReleaseStatus);
        WriteSingleRawTag(tagWriteContext, context, ReleaseCountryTag, SupportedTag.ReleaseCountry, ReleaseCountryRawTag, context.SourceTrack.ReleaseCountry);
        WriteSingleRawTag(tagWriteContext, context, BarcodeTag, SupportedTag.Barcode, BarcodeRawTag, context.SourceTrack.Barcode);
        if (context.EnabledTags.Contains(MediaTag) && context.SourceTrack.Media.Count > 0)
        {
            SetRaw(tagWriteContext, MediaRawTag, SupportedTag.Media, context.SourceTrack.Media);
        }
    }

    private static string? ToMusicBrainzShapedId(string? value)
        => Guid.TryParse(value, out _) ? value : null;

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

    private static void WriteCatalogNumberTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(CatalogNumberTag) || string.IsNullOrWhiteSpace(context.SourceTrack.CatalogNumber))
        {
            return;
        }

        SetField(
            tagWriteContext,
            new TagFieldBinding(CatalogNumberUpperTag, CatalogNumberUpperTag, CatalogNumberUpperTag, SupportedTag.CatalogNumber),
            new List<string> { context.SourceTrack.CatalogNumber });
    }

    private static void WriteDurationTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DurationTag) || !context.SourceTrack.Duration.HasValue)
        {
            return;
        }

        var totalMilliseconds = ((int)Math.Round(context.SourceTrack.Duration.Value.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);
        SetField(
            tagWriteContext,
            new TagFieldBinding("TLEN", LengthUpperTag, LengthUpperTag, SupportedTag.Duration),
            new List<string> { totalMilliseconds });
    }

    private static void WriteRemixerTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(RemixerTag) || context.SourceTrack.Remixers.Count == 0)
        {
            return;
        }

        SetField(
            tagWriteContext,
            new TagFieldBinding("TPE4", RemixerUpperTag, RemixerUpperTag, SupportedTag.Remixer),
            context.SourceTrack.Remixers.ToList());
    }

    private static void WriteIsrcTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("isrc") || string.IsNullOrWhiteSpace(context.SourceTrack.Isrc))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TSRC", "ISRC", "ISRC", SupportedTag.ISRC), new List<string> { context.SourceTrack.Isrc });
    }

    private static void WriteMoodTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("mood") || string.IsNullOrWhiteSpace(context.SourceTrack.Mood))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TMOO", "MOOD", "MOOD", SupportedTag.Mood), new List<string> { context.SourceTrack.Mood });
    }

    private static void WriteActivityTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("activity") || string.IsNullOrWhiteSpace(context.SourceTrack.Activity))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("ACTIVITY", "ACTIVITY", "ACTIVITY", SupportedTag.Activity), new List<string> { context.SourceTrack.Activity });
    }

    private static void ApplyTrackAndLyricsTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        WriteDiscNumberTag(file, context);
        WriteDiscTotalTag(file, context);
        WriteTrackNumberTag(file, context);
        WriteBarcodeTag(tagWriteContext, context);
        WriteReplayGainTag(tagWriteContext, context);
        WriteCopyrightTag(tagWriteContext, context);
        WriteComposerTag(tagWriteContext, context);
        WriteLyricistTag(tagWriteContext, context);
        WriteInvolvedPeopleTag(tagWriteContext, context);
        WritePublisherTag(tagWriteContext, context);
        WriteDescriptionTag(tagWriteContext, context);
        WriteSourceTag(tagWriteContext, context);
        WriteRatingTag(tagWriteContext, context);
        WriteLanguageTag(tagWriteContext, context);
        WriteSyncedLyrics(file, context);
        WriteUnsyncedLyrics(file, context);
        WriteExplicitTag(tagWriteContext, context);
        WriteOtherTags(tagWriteContext, context);
        WriteMetaTag(tagWriteContext, context);
    }

    private static void WriteDiscNumberTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DiscNumberTag)
            || !context.EffectiveTagSettings.DiscNumber
            || !context.SourceTrack.DiscNumber.HasValue)
        {
            return;
        }

        SetTrackNumber(
            file,
            context,
            context.SourceTrack.DiscNumber.Value,
            ResolveFirstPositiveInt(context.SourceTrack, DiscTotalTag, DiscTotalRawTag),
            SupportedTag.DiscNumber,
            isDisc: true);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscNumber);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscTotal);
    }

    private static void WriteDiscTotalTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DiscTotalTag)
            || !context.EffectiveTagSettings.DiscTotal)
        {
            return;
        }

        var total = context.SourceTrack.DiscTotal is > 0
            ? context.SourceTrack.DiscTotal
            : ResolveFirstPositiveInt(context.SourceTrack, DiscTotalTag, DiscTotalRawTag);
        if (!total.HasValue)
        {
            return;
        }

        SetDiscTotal(file, context, total.Value);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscTotal);
    }

    private static void WriteTrackNumberTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(TrackNumberTag)
            || !context.EffectiveTagSettings.TrackNumber
            || !context.SourceTrack.TrackNumber.HasValue)
        {
            return;
        }

        var total = context.EnabledTags.Contains(TrackTotalTag)
            && context.EffectiveTagSettings.TrackTotal
            && context.SourceTrack.TrackTotal is > 0
            ? context.SourceTrack.TrackTotal
            : null;
        SetTrackNumber(
            file,
            context,
            context.SourceTrack.TrackNumber.Value,
            total,
            SupportedTag.TrackNumber,
            isDisc: false);
        MarkAttemptedIfPresent(context, file, SupportedTag.TrackNumber);
        MarkAttemptedIfPresent(context, file, SupportedTag.TrackTotal);
    }

    private static void WriteBarcodeTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(BarcodeTag) || !context.EffectiveTagSettings.Barcode)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, BarcodeTag, "upc", BarcodeRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, BarcodeTag, BarcodeRawTag, values);
    }

    private static void WriteReplayGainTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ReplayGainTag) || !context.EffectiveTagSettings.ReplayGain)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, ReplayGainTag, ReplayGainRawTag, "gain");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, ReplayGainTag, ReplayGainRawTag, values);
    }

    private static void WriteCopyrightTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(CopyrightTag) || !context.EffectiveTagSettings.Copyright)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, CopyrightTag, CopyrightRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, CopyrightTag, CopyrightRawTag, values);
    }

    private static void WriteComposerTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ComposerTag) || !context.EffectiveTagSettings.Composer)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, ComposerTag, ComposerUpperTag, "TCOM");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, ComposerTag, ResolveComposerRawName(context.Extension), values);
    }

    private static void WriteLyricistTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LyricistTag) || !context.EffectiveTagSettings.Lyricist)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Lyricist, context.SourceTrack, LyricistTag, LyricistRawTag, "TEXT");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, LyricistTag, ResolveLyricistRawName(context.Extension), values);
    }

    private static void WriteInvolvedPeopleTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(InvolvedPeopleTag) || !context.EffectiveTagSettings.InvolvedPeople)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, InvolvedPeopleTag, InvolvedPeopleRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, InvolvedPeopleTag, InvolvedPeopleRawTag, values);
    }

    private static void WritePublisherTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(PublisherTag) || !context.EffectiveTagSettings.Publisher)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Publisher, context.SourceTrack, PublisherTag, PublisherRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, PublisherTag, PublisherRawTag, values);
    }

    private static void WriteDescriptionTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DescriptionTag) || !context.EffectiveTagSettings.Description)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Description, context.SourceTrack, DescriptionTag, DescriptionRawTag, CommentRawTag);
        if (values.Count == 0)
        {
            return;
        }

        var rawName = IsMp4Family(context.Extension) ? "ldes" : DescriptionRawTag;
        SetRawIfAllowed(tagWriteContext, DescriptionTag, rawName, values);
    }

    private static void WriteSourceTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(SourceTag) || !context.EffectiveTagSettings.Source)
        {
            return;
        }

        if (string.Equals(context.PlatformId, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceValues = ResolveOtherValues(context.SourceTrack, SourceTag);
        if (sourceValues.Count == 0)
        {
            sourceValues.Add(context.PlatformId.ToUpperInvariant());
        }

        SetRawIfAllowed(tagWriteContext, SourceTag, SourceRawTag, sourceValues);

        var sourceIdValues = ResolveOtherValues(context.SourceTrack, "sourceId", "SOURCE_ID", SourceIdRawTag);
        if (sourceIdValues.Count == 0 && !string.IsNullOrWhiteSpace(context.SourceTrack.TrackId))
        {
            sourceIdValues.Add(context.SourceTrack.TrackId);
        }
        if (sourceIdValues.Count > 0)
        {
            SetRawIfAllowed(tagWriteContext, SourceTag, SourceIdRawTag, sourceIdValues);
        }
    }

    private static void WriteRatingTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(RatingTag) || !context.EffectiveTagSettings.Rating)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, RatingTag, RatingRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, RatingTag, RatingRawTag, values);
    }

    private static void WriteLanguageTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LanguageTag))
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, LanguageTag, LanguageRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, LanguageTag, LanguageRawTag, values);
    }

    private static void WriteSyncedLyrics(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!ShouldWriteSyncedLyrics(context))
        {
            return;
        }

        if (WriteLyrics(file, context.Extension, context.SourceTrack, true, context.Config))
        {
            context.AttemptedTags.Add(SupportedTag.SyncedLyrics);
        }
    }

    private static void WriteUnsyncedLyrics(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!ShouldWriteUnsyncedLyrics(context))
        {
            return;
        }

        if (WriteLyrics(file, context.Extension, context.SourceTrack, false, context.Config))
        {
            context.AttemptedTags.Add(SupportedTag.UnsyncedLyrics);
        }
    }

    private static void WriteExplicitTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ExplicitTag) || !context.SourceTrack.Explicit.HasValue)
        {
            return;
        }

        SetRaw(
            tagWriteContext,
            ItunesAdvisoryTag,
            SupportedTag.Explicit,
            new List<string> { context.SourceTrack.Explicit.Value ? "1" : "0" });
    }

    private static void WriteOtherTags(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(OtherTagsTag)
            && !context.EnabledTags.Contains(ReleaseTypeTag))
        {
            return;
        }

        if (context.EnabledTags.Contains(OtherTagsTag))
        {
            foreach (var rawName in context.SourceTrack.RawTagsToRemove)
            {
                RemoveRawTagValues(tagWriteContext, rawName);
            }
        }

        if (context.SourceTrack.Other.Count == 0)
        {
            return;
        }

        foreach (var kvp in context.SourceTrack.Other)
        {
            if (IsNonPersistedOtherRawKey(kvp.Key))
            {
                continue;
            }

            var isReleaseType = kvp.Key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase);
            if (isReleaseType && !HasReleaseTypeTagEnabled(context.EnabledTags))
            {
                continue;
            }

            if (!isReleaseType && !context.EnabledTags.Contains(OtherTagsTag))
            {
                continue;
            }

            if (!ShouldAllowLyricsOtherTagKey(
                    kvp.Key,
                    context.AllowsLyricsBySettings,
                    context.AllowsSyncedType,
                    context.AllowsUnsyncedType,
                    context.AllowsTtmlByFormat,
                    !context.ShouldSkipEmbeddedLyrics))
            {
                continue;
            }

            SetRaw(tagWriteContext, kvp.Key, isReleaseType ? SupportedTag.ReleaseType : SupportedTag.OtherTags, kvp.Value.ToList());
        }
    }

    private static void WriteMetaTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(MetaTagsTag))
        {
            return;
        }

        SetRaw(tagWriteContext, TaggedDateTag, SupportedTag.MetaTags, new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}_AT" });
    }

    private static bool ShouldWriteSyncedLyrics(TagWriteExecutionContext context)
    {
        return context.EnabledTags.Contains(SyncedLyricsTag)
            && context.EffectiveTagSettings.SyncedLyrics
            && context.AllowsLyricsBySettings
            && context.AllowsSyncedType
            && !context.ShouldSkipEmbeddedLyrics;
    }

    private static bool ShouldWriteUnsyncedLyrics(TagWriteExecutionContext context)
    {
        return context.EnabledTags.Contains(UnsyncedLyricsTag)
            && context.EffectiveTagSettings.Lyrics
            && context.AllowsLyricsBySettings
            && context.AllowsUnsyncedType
            && !context.ShouldSkipEmbeddedLyrics;
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

    private static void MarkAttemptedIfPresent(TagWriteExecutionContext context, TagLib.File file, SupportedTag tag)
    {
        if (HasTag(file, context.Extension, tag, context.Config, context.PlatformId))
        {
            context.AttemptedTags.Add(tag);
        }
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
        TagSettings tagSettings)
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

        var destinationPath = Path.Join(pathInfo.FilePath, $"{pathInfo.Filename}{Path.GetExtension(sourcePath)}");
        if (PathsReferToSameFile(sourcePath, destinationPath))
        {
            return sourcePath;
        }

        Directory.CreateDirectory(pathInfo.FilePath);
        destinationPath = ResolveTemplateMaterializationDestination(sourcePath, destinationPath, settings);
        FileMoveFallbackHelper.MoveWithFallback(sourcePath, destinationPath);
        MoveAdjacentSidecars(sourcePath, destinationPath);
        return destinationPath;
    }

    private static string ResolveTemplateMaterializationDestination(
        string sourcePath,
        string destinationPath,
        DeezSpoTagSettings settings)
    {
        if (!IOFile.Exists(destinationPath) || ShouldOverwriteMaterializedFile(settings))
        {
            return destinationPath;
        }

        var directory = Path.GetDirectoryName(destinationPath) ?? "";
        var filename = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Join(directory, $"{filename} ({index}){extension}");
            if (!IOFile.Exists(candidate) && !PathsReferToSameFile(sourcePath, candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not allocate a unique manual enrichment destination for {destinationPath}.");
    }

    private static bool ShouldOverwriteMaterializedFile(DeezSpoTagSettings settings)
        => string.Equals(settings.OverwriteFile, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(settings.OverwriteFile, "overwrite", StringComparison.OrdinalIgnoreCase);

    private static void MoveAdjacentSidecars(string sourcePath, string destinationPath)
    {
        foreach (var extension in new[] { ".lrc", TtmlExtension, ".txt" })
        {
            var sourceSidecar = Path.ChangeExtension(sourcePath, extension);
            if (!IOFile.Exists(sourceSidecar))
            {
                continue;
            }

            var destinationSidecar = Path.ChangeExtension(destinationPath, extension);
            if (PathsReferToSameFile(sourceSidecar, destinationSidecar) || IOFile.Exists(destinationSidecar))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationSidecar) ?? "");
            FileMoveFallbackHelper.MoveWithFallback(sourceSidecar, destinationSidecar);
        }
    }

    private static bool PathsReferToSameFile(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void PersistManualMaterializedTargetPath(
        AutoTagRunPlan plan,
        string previousPath,
        string materializedPath)
    {
        if (PathsReferToSameFile(previousPath, materializedPath))
        {
            return;
        }

        var runtimeDirectory = Path.GetDirectoryName(plan.ConfigPath);
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
        {
            return;
        }

        var configPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plan.ConfigPath };
        if (!string.IsNullOrWhiteSpace(plan.JobId))
        {
            foreach (var path in Directory.EnumerateFiles(runtimeDirectory, $"autotag-{plan.JobId}-*.json"))
            {
                configPaths.Add(path);
            }
        }

        foreach (var configPath in configPaths)
        {
            ReplaceTargetPathInRuntimeConfig(configPath, previousPath, materializedPath);
        }
    }

    private static void ReplaceTargetPathInRuntimeConfig(
        string configPath,
        string previousPath,
        string materializedPath)
    {
        try
        {
            var root = JsonNode.Parse(IOFile.ReadAllText(configPath)) as JsonObject;
            if (root?["targetFiles"] is not JsonArray targets)
            {
                return;
            }

            var changed = false;
            for (var index = 0; index < targets.Count; index++)
            {
                var existing = targets[index]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(existing) || !PathsReferToSameFile(existing, previousPath))
                {
                    continue;
                }

                targets[index] = materializedPath;
                changed = true;
            }

            if (changed)
            {
                IOFile.WriteAllText(configPath, root.ToJsonString(CaseInsensitiveJsonOptions), new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new IOException($"Failed to persist manual enrichment staging path '{materializedPath}'.", ex);
        }
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
}
