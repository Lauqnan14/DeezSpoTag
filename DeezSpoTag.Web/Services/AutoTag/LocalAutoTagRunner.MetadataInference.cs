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

    private static bool IsMp4Family(string extension)
    {
        return AtlTagHelper.IsMp4Family(extension);
    }

    private static bool IsManualEnrichment(AutoTagRunnerConfig config)
        => !string.IsNullOrWhiteSpace(config.ManualReleasePreference)
           && config.ManualDestinationFolderId is > 0;

    private static HashSet<string> BuildNormalizedPathSet(IEnumerable<string>? paths)
        => paths?
            .Select(NormalizeOrderPath)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
           ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string? TryGetJsonString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static IEnumerable<string> EnumerateAudioFiles(string rootPath, bool includeSubfolders)
    {
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(rootPath, "*.*", option)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path))
                && !AnimatedArtworkFileNaming.IsAnimatedArtworkSidecar(path));
    }

    private static AutoTagAudioInfo CloneAudioInfo(AutoTagAudioInfo source)
    {
        return new AutoTagAudioInfo
        {
            Title = source.Title,
            Artist = source.Artist,
            Artists = source.Artists.ToList(),
            Album = source.Album,
            DurationSeconds = source.DurationSeconds,
            Isrc = source.Isrc,
            TrackNumber = source.TrackNumber,
            FilePath = source.FilePath,
            Tags = source.Tags.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            HasEmbeddedTitle = source.HasEmbeddedTitle,
            HasEmbeddedArtist = source.HasEmbeddedArtist
        };
    }

    private static bool IsRawCoreMetadata(AutoTagAudioInfo info)
    {
        return !info.HasEmbeddedTitle
            && !info.HasEmbeddedArtist
            && string.IsNullOrWhiteSpace(info.Isrc);
    }

    private static bool IsLikelyNoisyCoreMetadata(AutoTagAudioInfo info)
        => TrackIdentityTrust.IsWeakMetadataValue(info.Title)
            || TrackIdentityTrust.IsWeakMetadataValue(info.Artist);

    private AutoTagAudioInfo BuildAudioInfo(string filePath, string rootPath, bool parseFilename, string? tracknameTemplate, string? titleRegex)
    {
        var extension = Path.GetExtension(filePath);
        try
        {
            using var file = TagLib.File.Create(filePath);
            var draft = BuildAudioInfoDraft(file, filePath);

            PopulateAudioInfoTagMap(file, extension, draft.Tags);
            ApplyDraftTagFallbacks(draft);
            ApplyTracknameTemplateFallbacks(draft, filePath, parseFilename, tracknameTemplate);
            EnsureArtistFallbacks(draft, filePath, rootPath);
            draft.Title = ResolveTitleWithFallback(draft.Title, filePath, titleRegex);

            return CreateAudioInfoFromDraft(
                draft,
                filePath,
                extension,
                (int?)file.Properties.Duration.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed reading tags for {File}", SanitizeLogValue(filePath));
            return BuildAudioInfoFallback(filePath, rootPath, parseFilename, tracknameTemplate, titleRegex);
        }
    }

    private static AudioInfoDraft BuildAudioInfoDraft(TagLib.File file, string filePath)
    {
        var performerCredits = file.Tag.Performers?
            .Where(credit => !string.IsNullOrWhiteSpace(credit))
            .ToList()
            ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(file.Tag.FirstPerformer))
        {
            performerCredits.Add(file.Tag.FirstPerformer!);
        }

        var artists = SplitArtistCredits(performerCredits);
        if (artists.Count > 0 && artists.All(IsWeakMetadataValue))
        {
            artists.Clear();
        }

        var firstPerformer = IsWeakMetadataValue(file.Tag.FirstPerformer)
            ? string.Empty
            : file.Tag.FirstPerformer ?? string.Empty;
        var title = IsWeakMetadataValue(file.Tag.Title) ? string.Empty : file.Tag.Title ?? string.Empty;
        var album = IsWeakMetadataValue(file.Tag.Album)
            ? InferAlbumFromPath(filePath)
            : file.Tag.Album;
        return new AudioInfoDraft
        {
            Title = title,
            Artist = artists.FirstOrDefault() ?? firstPerformer,
            Artists = artists,
            Album = string.IsNullOrWhiteSpace(album)
                ? InferAlbumFromPath(filePath)
                : album,
            Isrc = file.Tag.ISRC,
            TrackNumber = file.Tag.Track > 0 ? (int?)file.Tag.Track : null,
            HasEmbeddedTitle = !string.IsNullOrWhiteSpace(title),
            HasEmbeddedArtist = artists.Count > 0,
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void PopulateAudioInfoTagMap(TagLib.File file, string extension, Dictionary<string, List<string>> tags)
    {
        AddTagIfAny(tags, "BEATPORT_TRACK_ID", ReadRawTagValues(file, extension, "BEATPORT_TRACK_ID"));
        AddTagIfAny(tags, "DISCOGS_RELEASE_ID", ReadRawTagValues(file, extension, "DISCOGS_RELEASE_ID"));
        AddTagIfAny(tags, AutoTagIdentityTags.ItunesTrackId, ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleTrackIdAliases));
        AddTagIfAny(tags, AutoTagIdentityTags.AppleTrackId, ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleTrackIdAliases));
        AddTagIfAny(tags, "ITUNES_RELEASE_ID", ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleReleaseIdAliases));
        AddTagIfAny(tags, "ITUNES_ARTIST_ID", ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleArtistIdAliases));
        AddTagIfAny(tags, DeezerTrackIdTag, ReadRawTagValuesAny(file, extension, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID"));
        AddTagIfAny(tags, "DEEZER_RELEASE_ID", ReadRawTagValuesAny(file, extension, "DEEZER_RELEASE_ID"));
        AddTagIfAny(tags, SpotifyTrackIdTag, ReadRawTagValuesAny(file, extension, SpotifyTrackIdTag, SpotifyTrackIdLegacyTag, SpotifyIdLegacyTag, SpotifyIdUnderscoreLegacyTag));
        AddTagIfAny(tags, SpotifyUrlTag, NormalizeSpotifyTrackUrls(ReadRawTagValuesAny(file, extension, SpotifyUrlTag, "SPOTIFYURI", "SPOTIFY_URI")));
        AddTagIfAny(tags, "URL", ReadRawTagValuesAny(file, extension, "URL"));
        AddTagIfAny(tags, WwwAudioFileTag, ReadRawTagValuesAny(file, extension, WwwAudioFileTag));
        AddTagIfAny(tags, "MUSICBRAINZ_RECORDING_ID", ReadRawTagValuesAny(file, extension, "MUSICBRAINZ_RECORDING_ID", "MUSICBRAINZ_RECORDINGID", "MUSICBRAINZ_TRACK_ID", "MUSICBRAINZ_TRACKID"));
        AddTagIfAny(tags, RecordingIdRawTag, ReadRawTagValuesAny(file, extension, RecordingIdRawTag));
        AddTagIfAny(tags, ArtistIdRawTag, ReadRawTagValuesAny(file, extension, ArtistIdRawTag));
        AddTagIfAny(tags, AlbumArtistIdRawTag, ReadRawTagValuesAny(file, extension, AlbumArtistIdRawTag));
        AddTagIfAny(tags, ReleaseGroupIdRawTag, ReadRawTagValuesAny(file, extension, ReleaseGroupIdRawTag));
        AddTagIfAny(tags, AlbumIdRawTag, ReadRawTagValuesAny(file, extension, AlbumIdRawTag));
        AddTagIfAny(tags, ReleaseStatusRawTag, ReadRawTagValuesAny(file, extension, ReleaseStatusRawTag));
        AddTagIfAny(tags, ReleaseCountryRawTag, ReadRawTagValuesAny(file, extension, ReleaseCountryRawTag));
        AddTagIfAny(tags, MediaRawTag, ReadRawTagValuesAny(file, extension, MediaRawTag));

        foreach (var shazamTag in ShazamRawTagHints)
        {
            AddTagIfAny(tags, shazamTag, ReadRawTagValuesAny(file, extension, shazamTag));
        }
    }

    private static void ApplyDraftTagFallbacks(AudioInfoDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Isrc))
        {
            draft.Isrc = ReadFirstTagValue(draft.Tags, "SHAZAM_ISRC", "ISRC");
        }

        if (IsWeakMetadataValue(draft.Album))
        {
            draft.Album = ReadFirstTagValue(draft.Tags, "SHAZAM_ALBUM", AlbumUpperTag);
        }

        if (!draft.TrackNumber.HasValue)
        {
            draft.TrackNumber = ParsePositiveInt(ReadFirstTagValue(draft.Tags, "SHAZAM_TRACK_NUMBER", TrackNumberUpperTag));
        }
    }

    private static void ApplyTracknameTemplateFallbacks(
        AudioInfoDraft draft,
        string filePath,
        bool parseFilename,
        string? tracknameTemplate)
    {
        if (!parseFilename || (!IsWeakMetadataValue(draft.Title) && !IsWeakMetadataValue(draft.Artist)))
        {
            return;
        }

        var template = OneTaggerMatching.ParseFilenameTemplate(tracknameTemplate);
        if (!TryParseFilename(Path.GetFileName(filePath), template, out var parsedArtist, out var parsedTitle))
        {
            return;
        }

        if (IsWeakMetadataValue(draft.Artist))
        {
            draft.Artist = parsedArtist;
        }

        if (IsWeakMetadataValue(draft.Title))
        {
            draft.Title = parsedTitle;
        }

        if (draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(parsedArtist))
        {
            draft.Artists = SplitArtistCredits(new[] { parsedArtist });
        }
    }

    private static void EnsureArtistFallbacks(AudioInfoDraft draft, string filePath, string rootPath)
    {
        if (draft.Artists.Count > 0 && draft.Artists.All(IsWeakMetadataValue))
        {
            draft.Artists.Clear();
        }

        if (draft.Artists.Count == 0 && !IsWeakMetadataValue(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
            draft.Artist = draft.Artists.FirstOrDefault() ?? draft.Artist;
        }

        if (!IsWeakMetadataValue(draft.Artist))
        {
            return;
        }

        draft.Artist = InferArtistFromPath(filePath, rootPath);
        if (draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
            draft.Artist = draft.Artists.FirstOrDefault() ?? draft.Artist;
        }
    }

    private static string ResolveTitleWithFallback(string title, string filePath, string? titleRegex)
    {
        var resolved = IsWeakMetadataValue(title)
            ? InferTitleFromFilename(filePath)
            : title;

        return ApplyTitleRegexFilter(resolved, titleRegex);
    }

    private static AutoTagAudioInfo CreateAudioInfoFromDraft(
        AudioInfoDraft draft,
        string filePath,
        string extension,
        int? durationSeconds)
    {
        var normalizedDurationSeconds = NormalizeDurationSeconds(filePath, extension, durationSeconds)
            ?? ResolveDurationSecondsFromTags(draft.Tags)
            ?? ResolveDurationSecondsWithFfprobe(filePath, extension);

        return new AutoTagAudioInfo
        {
            Title = draft.Title,
            Artist = draft.Artist,
            Artists = draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(draft.Artist)
                ? new List<string> { draft.Artist }
                : draft.Artists,
            Album = string.IsNullOrWhiteSpace(draft.Album) ? null : draft.Album,
            DurationSeconds = normalizedDurationSeconds,
            Isrc = string.IsNullOrWhiteSpace(draft.Isrc) ? null : draft.Isrc,
            TrackNumber = draft.TrackNumber,
            FilePath = filePath,
            Tags = draft.Tags,
            HasEmbeddedTitle = draft.HasEmbeddedTitle,
            HasEmbeddedArtist = draft.HasEmbeddedArtist
        };
    }

    private static AutoTagAudioInfo BuildAudioInfoFallback(
        string filePath,
        string rootPath,
        bool parseFilename,
        string? tracknameTemplate,
        string? titleRegex)
    {
        var draft = new AudioInfoDraft
        {
            Title = InferTitleFromFilename(filePath),
            Artist = InferArtistFromPath(filePath, rootPath),
            Album = InferAlbumFromPath(filePath),
            Artists = new List<string>(),
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
        if (!string.IsNullOrWhiteSpace(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
        }

        ApplyTracknameTemplateFallbacks(draft, filePath, parseFilename, tracknameTemplate);
        draft.Title = ApplyTitleRegexFilter(draft.Title, titleRegex);

        return new AutoTagAudioInfo
        {
            Title = draft.Title,
            Artist = draft.Artist,
            Artists = draft.Artists,
            Album = string.IsNullOrWhiteSpace(draft.Album) ? null : draft.Album,
            FilePath = filePath,
            HasEmbeddedTitle = false,
            HasEmbeddedArtist = false
        };
    }

    private static string InferArtistFromPath(string filePath, string rootPath)
    {
        try
        {
            var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrWhiteSpace(fileDir))
            {
                return string.Empty;
            }

            var rootFull = Path.GetFullPath(rootPath);
            var relativeDir = Path.GetRelativePath(rootFull, fileDir);
            if (!string.IsNullOrWhiteSpace(relativeDir) &&
                !relativeDir.StartsWith("..", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relativeDir))
            {
                var parts = relativeDir.Split(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .ToArray();
                if (parts.Length >= 2)
                {
                    return parts[0].Trim();
                }
            }

            var parent = Directory.GetParent(fileDir);
            return parent?.Name?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string InferAlbumFromPath(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return string.IsNullOrWhiteSpace(dir) ? string.Empty : Path.GetFileName(dir).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static bool IsSpecificFolderArtist(string? value)
        => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value);

    private static bool IsWeakMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().Trim('[', ']');
        return WeakMetadataValues.Contains(normalized);
    }

    private static bool IsVariousArtistsValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Trim('[', ']').ToLowerInvariant();
        return normalized is "various artists" or "various" or "va" or "v/a";
    }

    private static string InferTitleFromFilename(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return string.Empty;
        }

        var cleaned = LeadingTrackNumberRegex.Replace(baseName, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? baseName.Trim() : cleaned;
    }

    private static Task EnsureCoreTagsFromPathAsync(
        string filePath,
        string rootPath,
        bool singleAlbumArtist,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var artist = InferArtistFromPath(filePath, rootPath);
        var artistCredits = string.IsNullOrWhiteSpace(artist)
            ? new List<string>()
            : SplitArtistCredits(new[] { artist });
        var album = InferAlbumFromPath(filePath);
        var title = InferTitleFromFilename(filePath);

        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album) && string.IsNullOrWhiteSpace(title))
        {
            return Task.CompletedTask;
        }

        try
        {
            var extension = Path.GetExtension(filePath);
            var chapterSnapshot = AtlTagHelper.CaptureChapters(filePath, extension);
            using var file = TagLib.File.Create(filePath);
            var changed = false;
            changed |= TrySetMissingTitle(file.Tag, title);
            changed |= TrySetMissingPerformers(file.Tag, artistCredits);
            changed |= TrySetMissingAlbumArtists(file.Tag, artistCredits, singleAlbumArtist);
            changed |= TrySetMissingAlbum(file.Tag, album);

            if (changed)
            {
                file.Save();
                AtlTagHelper.RestoreChapters(filePath, chapterSnapshot);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort only
        }

        return Task.CompletedTask;
    }

    private static bool TrySetMissingTitle(TagLib.Tag tag, string? title)
    {
        if (!string.IsNullOrWhiteSpace(tag.Title) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        tag.Title = title;
        return true;
    }

    private static bool TrySetMissingPerformers(TagLib.Tag tag, List<string> artistCredits)
    {
        var hasPerformer = tag.Performers != null && tag.Performers.Any(value => !string.IsNullOrWhiteSpace(value));
        if (hasPerformer || artistCredits.Count == 0)
        {
            return false;
        }

        tag.Performers = artistCredits.ToArray();
        return true;
    }

    private static bool TrySetMissingAlbum(TagLib.Tag tag, string? album)
    {
        if (!string.IsNullOrWhiteSpace(tag.Album) || string.IsNullOrWhiteSpace(album))
        {
            return false;
        }

        tag.Album = album;
        return true;
    }

    private static bool TryParseFilename(string filename, Regex? template, out string artist, out string title)
    {
        artist = "";
        title = "";
        if (template != null)
        {
            var match = template.Match(filename);
            if (match.Success)
            {
                var titleGroup = match.Groups[TitleTag];
                if (titleGroup.Success)
                {
                    title = titleGroup.Value.Trim();
                }
                var artistGroup = match.Groups["artists"];
                if (artistGroup.Success)
                {
                    artist = artistGroup.Value.Trim();
                }
                return !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist);
            }
        }

        return false;
    }

    private static bool IsYearOnlyDateFormat(string? dateFormat)
        => string.Equals(dateFormat?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);

    private static bool IsNearMissAlternativeTitle(string existingTitle, string incomingTitle)
    {
        if (TrackTitleMatcher.HasCompatibleTitleIdentity(existingTitle, incomingTitle))
        {
            return false;
        }

        var existingNormalized = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(existingTitle));
        var incomingNormalized = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(incomingTitle));
        if (string.IsNullOrWhiteSpace(existingNormalized) || string.IsNullOrWhiteSpace(incomingNormalized))
        {
            return false;
        }

        // Old MusicBrainz/OneTagger fuzzy thresholds treated scores around 0.86 as the same work.
        return AutoTagSimilarity.ComputeScore(existingNormalized, incomingNormalized) >= 0.80d;
    }

    private static string NormalizeLooseTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return LooseTitleNormalizationRegex.Replace(value.ToLowerInvariant(), string.Empty);
    }
}
