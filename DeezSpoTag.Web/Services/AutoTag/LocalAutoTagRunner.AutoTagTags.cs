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

    private static string ToTagKey(SupportedTag tag)
    {
        return tag switch
        {
            SupportedTag.AlbumArt => AlbumArtTag,
            SupportedTag.BPM => BpmTag,
            SupportedTag.ISRC => IsrcTag,
            SupportedTag.URL => UrlTag,
            SupportedTag.TtmlLyrics => TtmlLyricsTag,
            _ => char.ToLowerInvariant(tag.ToString()[0]) + tag.ToString()[1..]
        };
    }

    private static string? TryGetFirstOtherValue(Dictionary<string, List<string>> other, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!other.TryGetValue(key, out var values) || values == null)
            {
                continue;
            }

            var value = values.FirstOrDefault(static raw => !string.IsNullOrWhiteSpace(raw))?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

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

    private static bool HasOtherKey(IEnumerable<string> keys, string target)
        => keys.Any(key => key.Equals(target, StringComparison.OrdinalIgnoreCase));

    private static bool HasAnyOtherKey(IEnumerable<string> keys, string first, string second)
        => HasOtherKey(keys, first) || HasOtherKey(keys, second);

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

        if (GenericIdentityCompatibilityFields.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return key.EndsWith("_TRACK_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_TRACKID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_RELEASE_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ALBUM_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ALBUMID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ALBUM_ARTIST_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ARTIST_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ARTISTID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_RECORDING_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_URL", StringComparison.OrdinalIgnoreCase);
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
}
