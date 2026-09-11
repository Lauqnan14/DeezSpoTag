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

    private static Track BuildCoreTrack(
        AutoTagTrack track,
        string? separator,
        bool singleAlbumArtist,
        DeezSpoTagSettings settings)
    {
        var artists = track.Artists.Count == 0 ? new List<string> { UnknownArtist } : track.Artists;
        var albumArtists = track.AlbumArtists.Count == 0 ? artists : track.AlbumArtists;
        var album = new Album(track.Album ?? "")
        {
            TrackTotal = track.TrackTotal ?? 0,
            DiscTotal = null,
            Genre = track.Genres.ToList(),
            Label = track.Label,
            ReleaseDate = track.ReleaseDate
        };

        var primaryAlbumArtist = albumArtists
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?.Trim();
        if (string.IsNullOrWhiteSpace(primaryAlbumArtist))
        {
            primaryAlbumArtist = artists[0];
        }

        var albumMainArtists = singleAlbumArtist
            ? new List<string> { primaryAlbumArtist }
            : albumArtists.ToList();

        album.MainArtist = new DeezSpoTag.Core.Models.Artist(primaryAlbumArtist);
        album.Artists = albumMainArtists.ToList();
        album.Artist["Main"] = albumMainArtists.ToList();

        var coreTrack = new Track
        {
            Title = track.Title,
            Artists = artists.ToList(),
            MainArtist = new DeezSpoTag.Core.Models.Artist(artists[0]),
            Album = album,
            TrackNumber = track.TrackNumber ?? 0,
            DiscNumber = track.DiscNumber ?? 0,
            Bpm = track.Bpm ?? 0,
            Explicit = track.Explicit ?? false,
            ISRC = track.Isrc ?? "",
            Duration = (int?)track.Duration?.TotalSeconds ?? 0
        };

        if (singleAlbumArtist && artists.Count > 1)
        {
            coreTrack.Artist["Main"] = new List<string> { artists[0] };
            coreTrack.Artist["Featured"] = artists.Skip(1).ToList();
            coreTrack.MainArtist = new DeezSpoTag.Core.Models.Artist(artists[0]);
        }
        else
        {
            coreTrack.Artist["Main"] = artists.ToList();
        }

        coreTrack.GenerateMainFeatStrings();
        coreTrack.ArtistString = coreTrack.MainArtist?.Name ?? artists[0];
        coreTrack.ArtistsString = string.IsNullOrWhiteSpace(separator) ? string.Join(", ", artists) : string.Join(separator, artists);

        if (track.ReleaseDate.HasValue)
        {
            coreTrack.Date = CustomDate.FromDateTime(track.ReleaseDate.Value);
            coreTrack.DateString = coreTrack.Date.Format("ymd");
        }

        settings.Tags ??= new TagSettings();
        coreTrack.ApplySettings(settings);

        return coreTrack;
    }

    /// <summary>
    /// Keeps the file's own title wording when the provider returned the same work
    /// with the same variant intent but different variant text ("Song (Live)" vs
    /// "Song (Live at Wembley)", "Song (Remastered)" vs "Song (2011 Remaster)").
    /// Providers must enrich missing tags, not rename tracks the user already titled.
    /// </summary>
    /// <summary>
    /// Keeps the file's own album identity (title, album id, album-artist id) when
    /// the provider matched a different edition of the same album. Returns true when
    /// an edition conflict was detected and preserved.
    /// </summary>
    private static bool PreserveAlbumEditionIdentity(AutoTagAudioInfo sourceInfo, AutoTagTrack track)
    {
        if (track == null || string.IsNullOrWhiteSpace(sourceInfo?.Album))
        {
            return false;
        }

        if (!AlbumTitleNormalizer.IsEditionConflict(sourceInfo.Album, track.Album))
        {
            return false;
        }

        track.Album = sourceInfo.Album.Trim();
        var sourceAlbumId = ReadFirstRawTagValue(sourceInfo, AlbumIdAlbumTagNames);
        if (!string.IsNullOrWhiteSpace(sourceAlbumId))
        {
            track.AlbumId = sourceAlbumId;
            track.ReleaseId = sourceAlbumId;
        }

        var sourceAlbumArtistId = ReadFirstRawTagValue(sourceInfo, AlbumArtistIdAlbumTagNames);
        if (!string.IsNullOrWhiteSpace(sourceAlbumArtistId))
        {
            track.AlbumArtistId = sourceAlbumArtistId;
        }

        return true;
    }

    private static readonly string[] AlbumIdAlbumTagNames =
        ["MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_ALBUM_ID", "ALBUMID", "MB_ALBUM_ID"];
    private static readonly string[] AlbumArtistIdAlbumTagNames =
        ["MUSICBRAINZ_ALBUMARTISTID", "MUSICBRAINZ_ALBUM_ARTIST_ID", "ALBUMARTISTID", "MB_ALBUM_ARTIST_ID"];

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

    private static void PreserveSourceTitleWording(AutoTagAudioInfo sourceInfo, AutoTagTrack track)
    {
        if (track == null || string.IsNullOrWhiteSpace(sourceInfo?.Title))
        {
            return;
        }

        var incomingFullTitle = OneTaggerMatching.FullTitle(track.Title, track.Version);
        if (!TrackTitleMatcher.ShouldPreserveSourceTitleWording(sourceInfo.Title, incomingFullTitle))
        {
            return;
        }

        // The source title already carries its own variant wording, so the incoming
        // version fragment must be dropped — WriteTitleTag re-appends Version in
        // parentheses when ShortTitle is off, which would duplicate the variant.
        track.Title = sourceInfo.Title.Trim();
        track.Version = null;
    }

    private static void PreserveRicherArtistCreditsFromSource(
        AutoTagAudioInfo sourceInfo,
        AutoTagTrack track,
        DeezSpoTagSettings settings)
    {
        IEnumerable<string> sourceArtistValues = sourceInfo.Artists.Count > 0
            ? sourceInfo.Artists
            : Array.Empty<string>();
        if (sourceInfo.Artists.Count == 0 && !string.IsNullOrWhiteSpace(sourceInfo.Artist))
        {
            sourceArtistValues = new[] { sourceInfo.Artist };
        }

        var sourceArtists = SplitArtistCredits(sourceArtistValues);
        var matchedArtists = SplitArtistCredits(track.Artists);

        if (ShouldPreferSourceArtistCredits(sourceArtists, matchedArtists))
        {
            track.Artists = sourceArtists;
        }
        else if (matchedArtists.Count > 0)
        {
            track.Artists = matchedArtists;
        }

        var normalizedAlbumArtists = SplitArtistCredits(track.AlbumArtists);
        if (normalizedAlbumArtists.Count == 0 && track.Artists.Count > 0)
        {
            normalizedAlbumArtists = track.Artists.ToList();
        }

        var singleAlbumArtist = settings.Tags?.SingleAlbumArtist ?? true;
        if (singleAlbumArtist && normalizedAlbumArtists.Count > 1)
        {
            normalizedAlbumArtists = new List<string> { normalizedAlbumArtists[0] };
        }

        track.AlbumArtists = normalizedAlbumArtists;
    }

    private static void ApplyFolderContextGuards(string filePath, string rootPath, AutoTagTrack track)
    {
        var folderArtist = InferArtistFromPath(filePath, rootPath);
        var folderAlbum = InferAlbumFromPath(filePath);
        var hasSpecificFolderArtist = IsSpecificFolderArtist(folderArtist);
        if (!string.IsNullOrWhiteSpace(folderAlbum) && IsWeakMetadataValue(track.Album))
        {
            track.Album = folderAlbum;
        }

        if (!hasSpecificFolderArtist)
        {
            return;
        }

        var normalizedArtists = SplitArtistCredits(track.Artists);
        if (normalizedArtists.Count == 0
            || normalizedArtists.All(IsWeakMetadataValue)
            || normalizedArtists.All(IsVariousArtistsValue))
        {
            track.Artists = new List<string> { folderArtist };
        }

        var normalizedAlbumArtists = SplitArtistCredits(track.AlbumArtists);
        if (normalizedAlbumArtists.Count == 0
            || normalizedAlbumArtists.All(IsWeakMetadataValue)
            || normalizedAlbumArtists.All(IsVariousArtistsValue))
        {
            track.AlbumArtists = new List<string> { folderArtist };
        }
    }

    private static bool ShouldPreferSourceArtistCredits(List<string> sourceArtists, List<string> matchedArtists)
    {
        if (sourceArtists.Count == 0)
        {
            return false;
        }

        if (matchedArtists.Count == 0)
        {
            return true;
        }

        if (sourceArtists.Count <= matchedArtists.Count)
        {
            return false;
        }

        var sourcePrimary = sourceArtists[0];
        var matchedPrimary = matchedArtists[0];
        if (string.IsNullOrWhiteSpace(sourcePrimary) || string.IsNullOrWhiteSpace(matchedPrimary))
        {
            return true;
        }

        if (string.Equals(sourcePrimary, matchedPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return sourcePrimary.Contains(matchedPrimary, StringComparison.OrdinalIgnoreCase)
            || matchedPrimary.Contains(sourcePrimary, StringComparison.OrdinalIgnoreCase);
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

    private static List<string> RewriteCreditList(
        IEnumerable<string>? credits,
        Func<string?, string>? rewriteCredit = null)
    {
        rewriteCredit ??= static value => DeezSpoTag.Services.Library.ArtistAliasGateway.ResolveCredit(value);
        return (credits ?? Array.Empty<string>())
            .Select(credit => rewriteCredit(credit) ?? string.Empty)
            .Select(static credit => credit.Trim())
            .Where(static credit => credit.Length > 0)
            .ToList();
    }

    private static void NormalizeTrackArtistsForTagging(AutoTagTrack track, bool singleAlbumArtist)
    {
        var normalizedArtists = SplitArtistCredits(track.Artists);
        var normalizedAlbumArtists = SplitArtistCredits(track.AlbumArtists);
        if (normalizedArtists.Count > 0 && normalizedArtists.All(IsWeakMetadataValue))
        {
            normalizedArtists.Clear();
        }

        if (normalizedAlbumArtists.Count > 0 && normalizedAlbumArtists.All(IsWeakMetadataValue))
        {
            normalizedAlbumArtists.Clear();
        }

        if (normalizedArtists.Count == 0)
        {
            normalizedArtists = normalizedAlbumArtists.ToList();
        }

        if (normalizedArtists.Count == 0)
        {
            normalizedArtists.Add(UnknownArtist);
        }

        if (normalizedAlbumArtists.Count == 0)
        {
            normalizedAlbumArtists = normalizedArtists.ToList();
        }

        if (singleAlbumArtist && normalizedAlbumArtists.Count > 1)
        {
            normalizedAlbumArtists = new List<string> { normalizedAlbumArtists[0] };
        }

        track.Artists = normalizedArtists;
        track.AlbumArtists = normalizedAlbumArtists;
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

    private static string BuildAtlDashFieldName(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : $"----:com.apple.iTunes:{name.Trim()}";
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

    private static List<string> ResolveArtistValues(Track coreTrack, TagSettings tagSettings)
    {
        var artists = coreTrack.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0)
        {
            return new List<string>();
        }

        if (tagSettings.SingleAlbumArtist)
        {
            var primary = coreTrack.MainArtist?.Name;
            return string.IsNullOrWhiteSpace(primary)
                ? new List<string> { artists[0] }
                : new List<string> { primary.Trim() };
        }

        if (string.Equals(tagSettings.MultiArtistSeparator, MultiArtistSeparatorDefault, StringComparison.OrdinalIgnoreCase))
        {
            return artists;
        }

        if (string.Equals(tagSettings.MultiArtistSeparator, MultiArtistSeparatorNothing, StringComparison.OrdinalIgnoreCase))
        {
            var primary = coreTrack.MainArtist?.Name;
            return string.IsNullOrWhiteSpace(primary)
                ? new List<string> { artists[0] }
                : new List<string> { primary.Trim() };
        }

        var joined = string.IsNullOrWhiteSpace(coreTrack.ArtistsString)
            ? string.Join(", ", artists)
            : coreTrack.ArtistsString;
        return new List<string> { joined };
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

    private static void WriteTitleTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(TitleTag) || !context.EffectiveTagSettings.Title)
        {
            return;
        }

        var titleValue = context.CoreTrack.Title;
        if (!context.Config.ShortTitle
            && !string.IsNullOrWhiteSpace(context.SourceTrack.Version)
            && !titleValue.Contains(context.SourceTrack.Version, StringComparison.OrdinalIgnoreCase))
        {
            titleValue = $"{titleValue} ({context.SourceTrack.Version})";
        }

        SetField(tagWriteContext, new TagFieldBinding("TIT2", TitleUpperTag, "©nam", SupportedTag.Title), new List<string> { titleValue });
    }

    private static void WriteVersionTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(VersionTag) || string.IsNullOrWhiteSpace(context.SourceTrack.Version))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TIT3", "SUBTITLE", "desc", SupportedTag.Version), new List<string> { context.SourceTrack.Version });
    }

    private static void WriteArtistTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ArtistTag) || !context.EffectiveTagSettings.Artist)
        {
            return;
        }

        var artistValues = ResolveArtistValues(context.CoreTrack, context.EffectiveTagSettings);
        if (artistValues.Count == 0)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TPE1", ArtistUpperTag, "©ART", SupportedTag.Artist), artistValues);
    }

    private static void WriteArtistsTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ArtistsTag) || !context.EffectiveTagSettings.Artists)
        {
            return;
        }

        if (string.Equals(
                context.EffectiveTagSettings.MultiArtistSeparator,
                MultiArtistSeparatorDefault,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var artists = context.CoreTrack.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0)
        {
            return;
        }

        var value = context.EffectiveTagSettings.MultiArtistSeparator switch
        {
            MultiArtistSeparatorNothing => context.CoreTrack.MainArtist?.Name ?? artists[0],
            MultiArtistSeparatorDefault => string.Join(", ", artists),
            _ when !string.IsNullOrWhiteSpace(context.CoreTrack.ArtistsString) => context.CoreTrack.ArtistsString,
            _ => string.Join(", ", artists)
        };
        SetRawIfAllowed(tagWriteContext, ArtistsTag, "ARTISTS", new List<string> { value });
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

    private static void WriteAlbumTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(AlbumTag) || !context.EffectiveTagSettings.Album || context.CoreTrack.Album == null)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TALB", AlbumUpperTag, "©alb", SupportedTag.Album), new List<string> { context.CoreTrack.Album.Title });
    }

    private static void WriteKeyTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("key") || string.IsNullOrWhiteSpace(context.SourceTrack.Key))
        {
            return;
        }

        var keyValue = context.Config.Camelot ? ToCamelot(context.SourceTrack.Key) : context.SourceTrack.Key;
        SetField(tagWriteContext, new TagFieldBinding("TKEY", "INITIALKEY", InitialKeyRawTag, SupportedTag.Key), new List<string> { keyValue });
    }

    private static void WriteBpmTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("bpm") || !context.SourceTrack.Bpm.HasValue)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TBPM", "BPM", "tmpo", SupportedTag.BPM), new List<string> { context.SourceTrack.Bpm.Value.ToString() });
    }

    private static void WriteLabelTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LabelTag) || string.IsNullOrWhiteSpace(context.SourceTrack.Label))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TPUB", LabelUpperTag, LabelUpperTag, SupportedTag.Label), new List<string> { context.SourceTrack.Label });
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

    private static void WriteAudioFeatureTag(
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context,
        string enabledTag,
        string rawTag,
        SupportedTag supportedTag,
        double? value)
    {
        if (!context.EnabledTags.Contains(enabledTag) || !value.HasValue)
        {
            return;
        }

        SetRaw(tagWriteContext, rawTag, supportedTag, new List<string> { FormatAudioFeature(value.Value) });
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
}
