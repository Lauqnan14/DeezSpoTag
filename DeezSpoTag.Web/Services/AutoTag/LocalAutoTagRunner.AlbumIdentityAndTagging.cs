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

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort timeout cleanup
        }
    }

    private static List<string> SplitArtistCredits(IEnumerable<string> rawCredits)
    {
        return ArtistNameNormalizer.ExpandArtistNames(rawCredits);
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

    private static int? ParsePositiveInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return int.TryParse(trimmed, out var value) && value > 0 ? value : null;
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

    private static bool TrySetMissingAlbumArtists(
        TagLib.Tag tag,
        List<string> artistCredits,
        bool singleAlbumArtist)
    {
        var hasAlbumArtist = tag.AlbumArtists != null && tag.AlbumArtists.Any(value => !string.IsNullOrWhiteSpace(value));
        if (hasAlbumArtist || artistCredits.Count == 0)
        {
            return false;
        }

        tag.AlbumArtists = singleAlbumArtist
            ? new[] { artistCredits[0] }
            : artistCredits.ToArray();
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

    private static TagSettings BuildTagSettings(AutoTagRunnerConfig config, DeezSpoTagSettings runtimeSettings)
    {
        var settings = new TagSettings
        {
            Title = false,
            Artist = false,
            Artists = false,
            Album = false,
            AlbumArtist = false,
            TrackNumber = false,
            TrackTotal = false,
            DiscNumber = false,
            DiscTotal = false,
            Genre = false,
            Label = false,
            Bpm = false,
            Isrc = false,
            Explicit = false,
            Length = false,
            Date = false,
            Year = false,
            Cover = false,
            Barcode = false,
            ReplayGain = false,
            Copyright = false,
            Lyrics = false,
            SyncedLyrics = false,
            Composer = false,
            InvolvedPeople = false,
            Source = false,
            Url = false,
            TrackId = false,
            ReleaseId = false,
            Rating = false,
            SavePlaylistAsCompilation = runtimeSettings.Tags?.SavePlaylistAsCompilation ?? false,
            UseNullSeparator = runtimeSettings.Tags?.UseNullSeparator ?? false,
            SaveID3v1 = runtimeSettings.Tags?.SaveID3v1 ?? true,
            MultiArtistSeparator = runtimeSettings.Tags?.MultiArtistSeparator ?? MultiArtistSeparatorDefault,
            SingleAlbumArtist = runtimeSettings.Tags?.SingleAlbumArtist ?? true,
            CoverDescriptionUTF8 = runtimeSettings.Tags?.CoverDescriptionUTF8 ?? true
        };

        foreach (var tag in config.Tags.Where(tag => TagSettingsAppliers.ContainsKey(tag.Trim())))
        {
            TagSettingsAppliers[tag.Trim()](settings);
        }
        if (WantsArtworkFromSettings(config, runtimeSettings))
        {
            settings.Cover = true;
        }

        return settings;
    }

    private static readonly string[] AlbumIdentitySeedExtensions =
        [".flac", ".mp3", ".m4a", ".mp4", ".aac", ".alac", ".ogg", ".opus", ".wav"];

    private static readonly string[] AlbumIdentityDateRawNames = ["DATE", "TDRC", "TDRL", "TYER"];
    private static readonly string[] AlbumIdentityAlbumIdRawNames =
        [AlbumIdRawTag, "MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_ALBUM_ID", "MUSICBRAINZ_RELEASE_ID"];
    private static readonly string[] AlbumIdentityAlbumArtistIdRawNames =
        [AlbumArtistIdRawTag, "MUSICBRAINZ_ALBUMARTISTID", "MUSICBRAINZ_ALBUM_ARTIST_ID"];
    private static readonly string[] AlbumIdentityReleaseGroupIdRawNames =
        [ReleaseGroupIdRawTag, "MUSICBRAINZ_RELEASEGROUPID", "MUSICBRAINZ_RELEASE_GROUP_ID"];
    private static readonly string[] PlatformReleaseIdRawNames =
        ["DEEZER_RELEASE_ID", "SPOTIFY_RELEASE_ID", "ITUNES_RELEASE_ID", "APPLE_RELEASE_ID", "APPLE_ALBUM_ID"];

    private AlbumIdentityStore? _albumIdentityStore;
    private readonly string? _albumIdentityStorePath;

    /// <summary>
    /// Loads the cross-run album identity store so a track downloaded months after
    /// the rest of its album converges onto the same album id and release date as
    /// the earlier tracks.
    /// </summary>
    private void LoadPersistedAlbumIdentities()
    {
        var storePath = _albumIdentityStorePath;
        if (string.IsNullOrWhiteSpace(storePath))
        {
            return;
        }

        try
        {
            _albumIdentityStore = IOFile.Exists(storePath)
                ? AlbumIdentityStore.Load(storePath)
                : new AlbumIdentityStore();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load the album identity store.");
            _albumIdentityStore = new AlbumIdentityStore();
        }
    }

    private void SeedPlanAlbumIdentities(AutoTagRunPlan plan)
    {
        if (_albumIdentityStore == null)
        {
            return;
        }

        foreach (var (key, identity, updatedAt) in _albumIdentityStore.Entries)
        {
            plan.AlbumIdentities.Seed(key, identity, updatedAt);
        }
    }

    /// <summary>Merges the run's established album identities back into the cross-run store.</summary>
    private void PersistAlbumIdentities(AutoTagRunPlan plan)
    {
        var storePath = _albumIdentityStorePath;
        if (string.IsNullOrWhiteSpace(storePath) || _albumIdentityStore == null || !plan.AlbumIdentities.IsDirty)
        {
            return;
        }

        try
        {
            _albumIdentityStore.Merge(plan.AlbumIdentities.Snapshot());
            _albumIdentityStore.Save(storePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist the album identity store.");
        }
    }

    private sealed record FolderAlbumIdentity(
        string AlbumTitle,
        string? AlbumArtist,
        AlbumIdentity Identity);

    /// <summary>
    /// Folder-keyed album identity: every file in the same album folder adopts ONE
    /// established identity — album title wording, album artist, release date and
    /// album ids — no matter which platform matched it. This is what keeps
    /// Navidrome-visible tags identical across files, platforms and sessions.
    /// </summary>
    private void ApplyAlbumIdentityConsensus(AutoTagFileRunContext context, AutoTagAudioInfo sourceInfo, AutoTagTrack track)
    {
        var albumArtist = track.AlbumArtists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? track.Artists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        // Edition-aware key: standard vs deluxe stay separate identities; different
        // wordings of the same edition ("Deluxe" vs "Deluxe Edition") share one key.
        var key = AlbumIdentity.BuildEditionAwareKey(albumArtist, track.Album);
        if (key is null)
        {
            return;
        }

        AlbumIdentity? seed = null;
        if (context.Plan.SeededAlbumIdentityKeys.Add(key))
        {
            seed = TryReadAlbumIdentityFromSiblings(
                context.File,
                TryResolveProspectiveAlbumDirectory(context, track));
        }

        var candidate = BuildAlbumIdentityCandidate(track, context.Platform);
        var established = context.Plan.AlbumIdentities.Establish(key, candidate, seed);
        if (established.IsEmpty)
        {
            return;
        }

        ApplyEstablishedAlbumIdentity(track, established, context.Platform);

        ApplyFolderAlbumIdentity(context, sourceInfo, track, albumArtist, established);
    }

    /// <summary>
    /// Folder-level flattening: once one platform establishes the album wording for a
    /// folder, every later platform pass for files in that folder adopts it (same
    /// core + edition), so the last-matching platform can no longer vary the album
    /// title, album artist or date per file.
    /// </summary>
    private void ApplyFolderAlbumIdentity(
        AutoTagFileRunContext context,
        AutoTagAudioInfo sourceInfo,
        AutoTagTrack track,
        string? candidateAlbumArtist,
        AlbumIdentity establishedIdentity)
    {
        var folderKey = ResolveAlbumFolderKey(context, track);
        if (string.IsNullOrWhiteSpace(folderKey))
        {
            return;
        }

        if (!context.Plan.AlbumFolderIdentities.TryGetValue(folderKey, out var establishedFolder))
        {
            // First matched file for this folder: seed from the file's own existing
            // tags (download source wording) and fall back to the candidate wording.
            var sourceAlbum = string.IsNullOrWhiteSpace(sourceInfo?.Album) ? null : sourceInfo.Album.Trim();
            var sourceAlbumArtist = sourceInfo?.Tags.TryGetValue("ALBUMARTIST", out var albumArtistValues) == true
                ? albumArtistValues.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
                : null;
            establishedFolder = new FolderAlbumIdentity(
                sourceAlbum ?? track.Album ?? string.Empty,
                sourceAlbumArtist ?? candidateAlbumArtist,
                establishedIdentity);
            context.Plan.AlbumFolderIdentities[folderKey] = establishedFolder;
            return;
        }

        // Same album and same edition: adopt the folder's established wording and
        // identity so every platform writes identical values.
        if (AlbumTitleNormalizer.IsSameEdition(establishedFolder.AlbumTitle, track.Album))
        {
            if (!string.IsNullOrWhiteSpace(establishedFolder.AlbumTitle))
            {
                track.Album = establishedFolder.AlbumTitle;
            }

            if (!string.IsNullOrWhiteSpace(establishedFolder.AlbumArtist))
            {
                track.AlbumArtists = new List<string> { establishedFolder.AlbumArtist };
            }

            ApplyEstablishedAlbumIdentity(track, establishedFolder.Identity, context.Platform);
        }
        else if (AlbumTitleNormalizer.IsEditionConflict(establishedFolder.AlbumTitle, track.Album))
        {
            // A later platform matched a different edition of the same album: keep the
            // folder's established edition entirely.
            track.Album = establishedFolder.AlbumTitle;
            if (!string.IsNullOrWhiteSpace(establishedFolder.AlbumArtist))
            {
                track.AlbumArtists = new List<string> { establishedFolder.AlbumArtist };
            }

            ApplyEstablishedAlbumIdentity(track, establishedFolder.Identity, context.Platform);
        }
    }

    private static AlbumIdentity BuildAlbumIdentityCandidate(AutoTagTrack track, string platformId)
    {
        return new AlbumIdentity(
            AlbumIdentity.FormatReleaseDate(track.ReleaseDate),
            track.AlbumId,
            track.AlbumArtistId,
            track.ReleaseGroupId,
            track.ReleaseStatus,
            track.ReleaseCountry,
            track.Barcode,
            track.ReleaseType,
            BuildPlatformReleaseIds(track, platformId));
    }

    private static IReadOnlyDictionary<string, string>? BuildPlatformReleaseIds(AutoTagTrack track, string platformId)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPlatformReleaseId(values, PlatformReleaseIdRawName(platformId), track.ReleaseId);
        foreach (var rawName in PlatformReleaseIdRawNames)
        {
            AddPlatformReleaseId(values, rawName, ResolveOtherValues(track, rawName).FirstOrDefault());
        }

        return values.Count == 0 ? null : values;
    }

    private static void AddPlatformReleaseId(IDictionary<string, string> target, string? rawName, string? value)
    {
        if (string.IsNullOrWhiteSpace(rawName) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Shape contract on intake: a value from another platform's id family must not
        // enter the album identity (previously-corrupted tags would otherwise re-seed
        // the identity and re-propagate the foreign id on every platform pass).
        var platformId = rawName.Trim().EndsWith("_RELEASE_ID", StringComparison.OrdinalIgnoreCase)
            ? rawName.Trim()[..^"_RELEASE_ID".Length]
            : rawName.Trim();
        if (!IsPlatformReleaseIdShapeValid(platformId, value))
        {
            return;
        }

        target.TryAdd(rawName.Trim().ToUpperInvariant(), value.Trim());
    }

    private static string? PlatformReleaseIdRawName(string? platformId)
        => string.IsNullOrWhiteSpace(platformId) ? null : $"{platformId.Trim().ToUpperInvariant()}_RELEASE_ID";

    private static void ApplyEstablishedAlbumIdentity(AutoTagTrack track, AlbumIdentity identity, string platformId)
    {
        var establishedDate = AlbumIdentity.ParseReleaseDate(identity.ReleaseDate);
        if (establishedDate.HasValue)
        {
            track.ReleaseDate = establishedDate;
            SetOtherValue(track, "RELEASEDATE", identity.ReleaseDate);
        }

        if (!string.IsNullOrWhiteSpace(identity.AlbumId))
        {
            track.AlbumId = identity.AlbumId;
            // The shared AlbumId slot is platform-agnostic (the folder's majority id);
            // ReleaseId is platform-scoped and is set from the identity's own
            // per-platform entry below. Stamping the establishing platform's album id
            // into ReleaseId made every platform write that foreign id into its own
            // <PLATFORM>_RELEASE_ID tag (e.g. an Audiomack numeric id inside
            // SPOTIFY_RELEASE_ID).
            var musicBrainzAlbumId = ToMusicBrainzShapedId(identity.AlbumId);
            if (!string.IsNullOrWhiteSpace(musicBrainzAlbumId))
            {
                SetOtherValue(track, AlbumIdRawTag, musicBrainzAlbumId);
                SetOtherValue(track, "MUSICBRAINZ_ALBUMID", musicBrainzAlbumId);
                SetOtherValue(track, "MUSICBRAINZ_RELEASE_ID", musicBrainzAlbumId);
            }
        }

        if (!string.IsNullOrWhiteSpace(identity.ReleaseGroupId))
        {
            track.ReleaseGroupId = identity.ReleaseGroupId;
            SetOtherValue(track, ReleaseGroupIdRawTag, identity.ReleaseGroupId);
            SetOtherValue(track, "MUSICBRAINZ_RELEASEGROUPID", identity.ReleaseGroupId);
        }

        if (!string.IsNullOrWhiteSpace(identity.AlbumArtistId))
        {
            track.AlbumArtistId = identity.AlbumArtistId;
            SetOtherValue(track, AlbumArtistIdRawTag, identity.AlbumArtistId);
            SetOtherValue(track, "MUSICBRAINZ_ALBUMARTISTID", identity.AlbumArtistId);
        }

        if (!string.IsNullOrWhiteSpace(identity.ReleaseStatus))
        {
            track.ReleaseStatus = identity.ReleaseStatus;
            SetOtherValue(track, ReleaseStatusRawTag, identity.ReleaseStatus);
        }

        if (!string.IsNullOrWhiteSpace(identity.ReleaseCountry))
        {
            track.ReleaseCountry = identity.ReleaseCountry;
            SetOtherValue(track, ReleaseCountryRawTag, identity.ReleaseCountry);
        }

        if (!string.IsNullOrWhiteSpace(identity.Barcode))
        {
            track.Barcode = identity.Barcode;
            SetOtherValue(track, BarcodeRawTag, identity.Barcode);
            SetOtherValue(track, BarcodeTag, identity.Barcode);
        }

        if (!string.IsNullOrWhiteSpace(identity.ReleaseType))
        {
            track.ReleaseType = identity.ReleaseType;
            SetOtherValue(track, ReleaseTypeRawTag, identity.ReleaseType);
        }

        var platformReleaseIdName = PlatformReleaseIdRawName(platformId);
        if (!string.IsNullOrWhiteSpace(platformReleaseIdName)
            && identity.PlatformReleaseIds?.TryGetValue(platformReleaseIdName, out var platformReleaseId) == true
            && !string.IsNullOrWhiteSpace(platformReleaseId))
        {
            track.ReleaseId = platformReleaseId.Trim();
            SetOtherValue(track, platformReleaseIdName, platformReleaseId);
        }
    }

    private static void SetOtherValue(AutoTagTrack track, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        track.Other[key.Trim()] = new List<string> { value.Trim() };
    }

    private static string ResolveAlbumFolderKey(AutoTagFileRunContext context, AutoTagTrack track)
    {
        var prospective = TryResolveProspectiveAlbumDirectory(context, track);
        if (!string.IsNullOrWhiteSpace(prospective))
        {
            return Path.GetFullPath(prospective).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(context.File))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static string? TryResolveProspectiveAlbumDirectory(AutoTagFileRunContext context, AutoTagTrack track)
    {
        if (context.Plan.Config.MaterializeToTemplatePath != true)
        {
            return null;
        }

        try
        {
            var separator = ResolveArtistSeparator(context.Plan.Config, context.File);
            var coreTrack = BuildCoreTrack(
                track,
                separator,
                context.Plan.TagSettings.SingleAlbumArtist,
                context.Plan.Settings);
            var pathInfo = BuildTemplatePathInfo(coreTrack, context.Plan.Settings);
            return string.IsNullOrWhiteSpace(pathInfo.FilePath) ? null : pathInfo.FilePath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static AlbumIdentity? TryReadAlbumIdentityFromSiblings(string filePath, string? destinationDirectory)
    {
        var identity = AlbumIdentity.Empty;
        foreach (var directory in EnumerateAlbumIdentitySeedDirectories(filePath, destinationDirectory))
        {
            identity = identity.CoalesceWith(ReadAlbumIdentityFromDirectory(directory, filePath));
            if (!identity.IsEmpty)
            {
                break;
            }
        }

        return identity.IsEmpty ? null : identity;
    }

    private static IEnumerable<string> EnumerateAlbumIdentitySeedDirectories(string filePath, string? destinationDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { destinationDirectory, Path.GetDirectoryName(filePath) })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            if (seen.Add(Path.GetFullPath(candidate)))
            {
                yield return candidate;
            }
        }
    }

    private static AlbumIdentity ReadAlbumIdentityFromDirectory(string directory, string filePath)
    {
        var identities = new List<AlbumIdentity>();
        IEnumerable<string> siblings;
        try
        {
            siblings = Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AlbumIdentity.Empty;
        }

        foreach (var sibling in siblings)
        {
            if (PathsReferToSameFile(sibling, filePath))
            {
                continue;
            }

            var extension = Path.GetExtension(sibling);
            if (!AlbumIdentitySeedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var file = TagLib.File.Create(sibling);
                var identity = new AlbumIdentity(
                    ReadRawTagValuesAny(file, extension, AlbumIdentityDateRawNames).FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, AlbumIdentityAlbumIdRawNames).FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, AlbumIdentityAlbumArtistIdRawNames).FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, AlbumIdentityReleaseGroupIdRawNames).FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, ReleaseStatusRawTag).FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, ReleaseCountryRawTag).FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, BarcodeRawTag, BarcodeTag, "upc").FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, ReleaseTypeRawTag).FirstOrDefault(),
                    ReadPlatformReleaseIds(file, extension));
                if (!identity.IsEmpty)
                {
                    identities.Add(identity);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }
        }

        return BuildMajorityAlbumIdentity(identities);
    }

    private static AlbumIdentity BuildMajorityAlbumIdentity(IReadOnlyList<AlbumIdentity> identities)
    {
        if (identities.Count == 0)
        {
            return AlbumIdentity.Empty;
        }

        return new AlbumIdentity(
            SelectMajority(identities.Select(identity => identity.ReleaseDate)),
            SelectMajority(identities.Select(identity => identity.AlbumId)),
            SelectMajority(identities.Select(identity => identity.AlbumArtistId)),
            SelectMajority(identities.Select(identity => identity.ReleaseGroupId)),
            SelectMajority(identities.Select(identity => identity.ReleaseStatus)),
            SelectMajority(identities.Select(identity => identity.ReleaseCountry)),
            SelectMajority(identities.Select(identity => identity.Barcode)),
            SelectMajority(identities.Select(identity => identity.ReleaseType)),
            BuildMajorityPlatformReleaseIds(identities));
    }

    private static IReadOnlyDictionary<string, string>? BuildMajorityPlatformReleaseIds(IReadOnlyList<AlbumIdentity> identities)
    {
        var keys = identities
            .SelectMany(identity => identity.PlatformReleaseIds?.Keys ?? Array.Empty<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
        {
            return null;
        }

        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var value = SelectMajority(identities.Select(identity =>
                identity.PlatformReleaseIds?.TryGetValue(key, out var platformValue) == true ? platformValue : null));
            if (!string.IsNullOrWhiteSpace(value))
            {
                selected[key] = value;
            }
        }

        return selected.Count == 0 ? null : selected;
    }

    private static string? SelectMajority(IEnumerable<string?> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    private static IReadOnlyDictionary<string, string>? ReadPlatformReleaseIds(TagLib.File file, string extension)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawName in PlatformReleaseIdRawNames)
        {
            AddPlatformReleaseId(ids, rawName, ReadRawTagValuesAny(file, extension, rawName).FirstOrDefault());
        }

        return ids.Count == 0 ? null : ids;
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
}
