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

    private static string GetAlbumSortKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static bool AlbumsReferToSameRelease(string? frozen, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(frozen) || string.IsNullOrWhiteSpace(candidate))
        {
            return true;
        }

        return string.Equals(
            AutoTagSimilarity.NormalizeText(frozen),
            AutoTagSimilarity.NormalizeText(candidate),
            StringComparison.Ordinal);
    }

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
}
