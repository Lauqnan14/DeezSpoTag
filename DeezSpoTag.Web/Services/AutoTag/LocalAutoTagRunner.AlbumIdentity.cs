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

    private static string? ResolveAlbumRootDirectory(string filePath, string? prospectiveAlbumRoot)
    {
        string? candidate = !string.IsNullOrWhiteSpace(prospectiveAlbumRoot)
            ? prospectiveAlbumRoot
            : Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(fullPath);
            if (Regex.IsMatch(leaf, "^CD\\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                fullPath = Path.GetDirectoryName(fullPath)?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) ?? fullPath;
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryResolvePersistedAlbumRoot(string libraryRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot)
            || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(libraryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            var relative = Path.GetRelativePath(root, candidate);
            if (relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return null;
            }

            return candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? BuildAlbumRelativePath(string libraryRoot, string? albumRoot)
    {
        if (string.IsNullOrWhiteSpace(albumRoot))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(libraryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(albumRoot);
            var relative = Path.GetRelativePath(root, candidate);
            return relative == "."
                || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.IsPathRooted(relative)
                    ? null
                    : relative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static AutoTagAudioInfo ApplyConfirmedProviderReleaseIdHint(
        AutoTagAudioInfo source,
        AlbumIdentity identity,
        string platformId)
    {
        var clone = CloneAudioInfo(source);
        foreach (var rawName in AllProviderReleaseIdCleanupRawNames())
        {
            clone.Tags.Remove(rawName);
        }

        var canonicalName = PlatformReleaseIdRawName(platformId);
        if (string.IsNullOrWhiteSpace(canonicalName)
            || !identity.IsPlatformReleaseIdConfirmed(canonicalName)
            || identity.PlatformReleaseIds?.TryGetValue(canonicalName, out var value) != true
            || string.IsNullOrWhiteSpace(value))
        {
            return clone;
        }

        foreach (var rawName in ProviderReleaseIdWriteRawNames(platformId))
        {
            clone.Tags[rawName] = [value.Trim()];
        }

        if (string.Equals(platformId, "musicbrainz", StringComparison.OrdinalIgnoreCase))
        {
            clone.Tags[AlbumIdRawTag] = [value.Trim()];
            clone.Tags["MUSICBRAINZ_ALBUMID"] = [value.Trim()];
        }

        return clone;
    }

    private static void ApplyProviderReleaseIdState(
        TagWriteContext tagWriteContext,
        string platformId,
        string? releaseId,
        bool authoritativeAbsence)
    {
        var cleanupNames = ProviderReleaseIdCleanupRawNames(platformId);
        var writeNames = ProviderReleaseIdWriteRawNames(platformId);
        var overwrite = ShouldOverwriteTag(tagWriteContext.Config, SupportedTag.ReleaseId);
        if (authoritativeAbsence && !overwrite)
        {
            return;
        }

        if (!overwrite && cleanupNames.Any(name => HasRawTag(
                tagWriteContext.File,
                tagWriteContext.Extension,
                name)))
        {
            tagWriteContext.AttemptedTags.Add(SupportedTag.ReleaseId);
            return;
        }

        if (overwrite)
        {
            foreach (var rawName in cleanupNames)
            {
                RemoveRawTagValues(tagWriteContext, rawName);
            }
        }

        if (!authoritativeAbsence && !string.IsNullOrWhiteSpace(releaseId))
        {
            foreach (var rawName in writeNames)
            {
                SetRaw(tagWriteContext, rawName, SupportedTag.ReleaseId, [releaseId.Trim()], force: true);
            }
        }

        tagWriteContext.AttemptedTags.Add(SupportedTag.ReleaseId);
    }

    private Task ReconcileBatchAlbumIdentitiesAsync(
        AutoTagRunPlan plan,
        int batchStart,
        int batchEnd,
        CancellationToken token)
    {
        var enabled = BuildConfiguredTagSet(plan.Config.Tags);
        var releaseIdEnabled = enabled.Contains(ReleaseIdTag);
        for (var fileIndex = Math.Max(0, batchStart); fileIndex < Math.Min(batchEnd, plan.FileCount); fileIndex++)
        {
            token.ThrowIfCancellationRequested();
            if (!plan.AlbumReleaseContexts.TryGetValue(fileIndex, out var releaseContext)
                || !plan.AlbumIdentities.TryGet(releaseContext.ReleaseKey, out var identity)
                || identity.IsEmpty)
            {
                continue;
            }

            var filePath = plan.Files[fileIndex];
            using var file = TagLib.File.Create(filePath);
            var extension = Path.GetExtension(filePath);
            var writeContext = new TagWriteContext(
                file,
                extension,
                plan.Config,
                ResolveArtistSeparator(plan.Config, filePath),
                string.Empty,
                plan.TagSettings.UseNullSeparator,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<string>(),
                false,
                new HashSet<SupportedTag>());

            if (enabled.Contains(AlbumTag)
                && !string.IsNullOrWhiteSpace(identity.CanonicalAlbumTitle)
                && (ShouldOverwriteTag(plan.Config, SupportedTag.Album)
                    || string.IsNullOrWhiteSpace(file.Tag.Album)))
            {
                file.Tag.Album = identity.CanonicalAlbumTitle;
            }

            if (enabled.Contains(AlbumArtistTag)
                && !string.IsNullOrWhiteSpace(identity.CanonicalAlbumArtist)
                && (ShouldOverwriteTag(plan.Config, SupportedTag.AlbumArtist)
                    || file.Tag.AlbumArtists.Length == 0))
            {
                file.Tag.AlbumArtists = [identity.CanonicalAlbumArtist];
            }

            if (enabled.Contains(ReleaseDateTag)
                && AlbumIdentity.ParseReleaseDate(identity.ReleaseDate) is { } releaseDate)
            {
                WriteDate(
                    file,
                    extension,
                    ReleaseDateTag,
                    releaseDate,
                    SupportedTag.ReleaseDate,
                    plan.Config,
                    plan.TagSettings.UseNullSeparator);
            }

            WriteReconciledRawIdentity(writeContext, enabled, ReleaseStatusTag, ReleaseStatusRawTag, SupportedTag.ReleaseStatus, identity.ReleaseStatus);
            WriteReconciledRawIdentity(writeContext, enabled, ReleaseCountryTag, ReleaseCountryRawTag, SupportedTag.ReleaseCountry, identity.ReleaseCountry);
            WriteReconciledRawIdentity(writeContext, enabled, BarcodeTag, BarcodeRawTag, SupportedTag.Barcode, identity.Barcode);
            WriteReconciledRawIdentity(writeContext, enabled, ReleaseTypeTag, ReleaseTypeRawTag, SupportedTag.ReleaseType, identity.ReleaseType);

            if (releaseIdEnabled)
            {
                foreach (var platform in plan.EffectivePlatforms)
                {
                    var providerKey = PlatformReleaseIdRawName(platform);
                    if (string.IsNullOrWhiteSpace(providerKey)
                        || !identity.IsPlatformReleaseIdConfirmed(providerKey))
                    {
                        continue;
                    }

                    string? providerReleaseId = null;
                    identity.PlatformReleaseIds?.TryGetValue(providerKey, out providerReleaseId);
                    ApplyProviderReleaseIdState(
                        writeContext with { PlatformId = platform },
                        platform,
                        providerReleaseId,
                        authoritativeAbsence: string.IsNullOrWhiteSpace(providerReleaseId));
                }
            }

            file.Save();
            foreach (var platform in plan.EffectivePlatforms)
            {
                var providerKey = PlatformReleaseIdRawName(platform);
                if (!releaseIdEnabled
                    || string.IsNullOrWhiteSpace(providerKey)
                    || !identity.IsPlatformReleaseIdConfirmed(providerKey))
                {
                    continue;
                }

                string? expected = null;
                identity.PlatformReleaseIds?.TryGetValue(providerKey, out expected);
                if (!VerifyProviderReleaseIdState(
                        file,
                        extension,
                        platform,
                        expected,
                        ShouldOverwriteTag(plan.Config, SupportedTag.ReleaseId),
                        authoritativeAbsence: string.IsNullOrWhiteSpace(expected)))
                {
                    throw new IOException($"Album identity reconciliation failed for {platform} release ID.");
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void WriteReconciledRawIdentity(
        TagWriteContext context,
        HashSet<string> enabled,
        string configuredName,
        string rawName,
        SupportedTag supportedTag,
        string? value)
    {
        if (enabled.Contains(configuredName) && !string.IsNullOrWhiteSpace(value))
        {
            SetRaw(context, rawName, supportedTag, [value.Trim()]);
        }
    }

    private static string? ResolveSourceAlbumIdentityKey(
        string libraryRoot,
        AutoTagAudioInfo source)
    {
        var albumArtist = source.Tags.TryGetValue("ALBUMARTIST", out var values)
            ? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;
        albumArtist ??= source.Artists.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        albumArtist ??= source.Artist;
        return AlbumIdentity.BuildScopedEditionAwareKey(libraryRoot, albumArtist, source.Album);
    }

    private AutoTagAudioInfo PrepareProviderMatchInfo(
        AutoTagFileRunContext context,
        AutoTagAudioInfo source)
    {
        var key = ResolveSourceAlbumIdentityKey(context.Plan.TargetPath, source);
        if (key is null)
        {
            return CloneAudioInfo(source);
        }

        var prospectiveRoot = ResolveAlbumRootDirectory(context.File, null);
        if (!context.Plan.AlbumIdentities.TryGet(key, out var identity))
        {
            var seed = TryReadAlbumIdentityFromFolder(context.File, prospectiveRoot);
            identity = context.Plan.AlbumIdentities.Establish(key, AlbumIdentity.Empty, seed);
        }

        var albumRoot = TryResolvePersistedAlbumRoot(context.Plan.TargetPath, identity.AlbumRelativePath)
            ?? prospectiveRoot;
        var albumArtist = identity.CanonicalAlbumArtist
            ?? source.Tags.GetValueOrDefault("ALBUMARTIST")?.FirstOrDefault()
            ?? source.Artists.FirstOrDefault()
            ?? source.Artist;
        var albumTitle = identity.CanonicalAlbumTitle ?? source.Album ?? string.Empty;
        context.Plan.AlbumReleaseContexts[context.FileIndex] = new AlbumReleaseContext(
            key,
            albumRoot,
            albumTitle,
            albumArtist,
            identity);

        return ApplyConfirmedProviderReleaseIdHint(source, identity, context.Platform);
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
    private void ApplyAlbumIdentityConsensus(
        AutoTagFileRunContext context,
        AutoTagAudioInfo sourceInfo,
        AutoTagTrack track,
        string? matchedProviderReleaseId,
        bool hasAuthoritativeProviderResult)
    {
        if (!hasAuthoritativeProviderResult)
        {
            return;
        }

        track.HasAuthoritativeProviderReleaseIdResult = true;
        track.HasAuthoritativeProviderReleaseIdAbsence = string.IsNullOrWhiteSpace(matchedProviderReleaseId);

        var albumArtist = track.AlbumArtists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? track.Artists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        context.Plan.AlbumReleaseContexts.TryGetValue(context.FileIndex, out var releaseContext);
        var sameKnownEdition = releaseContext is not null
            && AlbumTitleNormalizer.IsSameEdition(releaseContext.AlbumTitle, track.Album);
        if (sameKnownEdition)
        {
            track.Album = releaseContext!.AlbumTitle;
            if (!string.IsNullOrWhiteSpace(releaseContext.AlbumArtist))
            {
                albumArtist = releaseContext.AlbumArtist;
                track.AlbumArtists = [releaseContext.AlbumArtist];
            }
        }

        var key = sameKnownEdition
            ? releaseContext!.ReleaseKey
            : AlbumIdentity.BuildScopedEditionAwareKey(context.Plan.TargetPath, albumArtist, track.Album);
        if (key is null)
        {
            return;
        }

        AlbumIdentity? seed = null;
        if (context.Plan.SeededAlbumIdentityKeys.Add(key))
        {
            seed = TryReadAlbumIdentityFromFolder(
                context.File,
                releaseContext?.AlbumRoot ?? TryResolveProspectiveAlbumDirectory(context, track));
        }

        var albumRoot = !string.IsNullOrWhiteSpace(releaseContext?.Identity.AlbumRelativePath)
            ? releaseContext.AlbumRoot
            : ResolveAlbumRootDirectory(context.File, TryResolveProspectiveAlbumDirectory(context, track))
                ?? releaseContext?.AlbumRoot;
        var candidate = BuildAlbumIdentityCandidate(track, context.Platform) with
        {
            CanonicalAlbumTitle = track.Album,
            CanonicalAlbumArtist = albumArtist,
            AlbumRelativePath = BuildAlbumRelativePath(context.Plan.TargetPath, albumRoot)
        };
        var established = context.Plan.AlbumIdentities.Establish(
            key,
            candidate,
            seed,
            PlatformReleaseIdRawName(context.Platform),
            matchedProviderReleaseId,
            hasAuthoritativeProviderResult,
            ShouldOverwriteTag(context.Plan.Config, SupportedTag.ReleaseId));
        if (established.IsEmpty)
        {
            return;
        }

        context.Plan.AlbumReleaseContexts[context.FileIndex] = new AlbumReleaseContext(
            key,
            albumRoot,
            established.CanonicalAlbumTitle ?? track.Album ?? string.Empty,
            established.CanonicalAlbumArtist ?? albumArtist,
            established);

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

        establishedFolder = establishedFolder with { Identity = establishedIdentity };
        context.Plan.AlbumFolderIdentities[folderKey] = establishedFolder;

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
        var isMusicBrainz = string.Equals(platformId, "musicbrainz", StringComparison.OrdinalIgnoreCase);
        return NormalizeSharedAlbumIdentity(new AlbumIdentity(
            AlbumIdentity.FormatReleaseDate(track.ReleaseDate),
            isMusicBrainz ? FirstMusicBrainzShapedId(
                ResolveOtherValues(track, "MUSICBRAINZ_ALBUMID")
                    .Concat(ResolveOtherValues(track, "MUSICBRAINZ_ALBUM_ID"))
                    .Append(track.AlbumId)) : null,
            isMusicBrainz ? FirstMusicBrainzShapedId(
                ResolveOtherValues(track, "MUSICBRAINZ_ALBUMARTISTID")
                    .Concat(ResolveOtherValues(track, "MUSICBRAINZ_ALBUM_ARTIST_ID"))
                    .Append(track.AlbumArtistId)) : null,
            isMusicBrainz ? FirstMusicBrainzShapedId(
                ResolveOtherValues(track, "MUSICBRAINZ_RELEASEGROUPID")
                    .Concat(ResolveOtherValues(track, "MUSICBRAINZ_RELEASE_GROUP_ID"))
                    .Append(track.ReleaseGroupId)) : null,
            track.ReleaseStatus,
            track.ReleaseCountry,
            track.Barcode,
            track.ReleaseType,
            BuildPlatformReleaseIds(track, platformId)));
    }

    private static AlbumIdentity NormalizeSharedAlbumIdentity(AlbumIdentity identity)
    {
        return identity with
        {
            AlbumId = ToMusicBrainzShapedId(identity.AlbumId),
            AlbumArtistId = ToMusicBrainzShapedId(identity.AlbumArtistId),
            ReleaseGroupId = ToMusicBrainzShapedId(identity.ReleaseGroupId)
        };
    }

    private static string? FirstMusicBrainzShapedId(IEnumerable<string?> values)
        => values.Select(ToMusicBrainzShapedId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyDictionary<string, string>? BuildPlatformReleaseIds(AutoTagTrack track, string platformId)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPlatformReleaseId(values, PlatformReleaseIdRawName(platformId), track.ReleaseId);
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
        => ProviderReleaseIdWriteRawNames(platformId).FirstOrDefault();

    private static IReadOnlyList<string> ProviderReleaseIdWriteRawNames(string? platformId)
        => string.IsNullOrWhiteSpace(platformId)
            ? Array.Empty<string>()
            : string.Equals(platformId.Trim(), ItunesPlatform, StringComparison.OrdinalIgnoreCase)
            ? ["ITUNES_RELEASE_ID", "APPLE_ALBUM_ID"]
            : string.Equals(platformId.Trim(), "musicbrainz", StringComparison.OrdinalIgnoreCase)
            ? ["MUSICBRAINZ_RELEASE_ID", "MUSICBRAINZ_ALBUMID", AlbumIdRawTag]
            : [$"{platformId.Trim().ToUpperInvariant()}_RELEASE_ID"];

    private static IReadOnlyList<string> ProviderReleaseIdCleanupRawNames(string? platformId)
        => string.Equals(platformId?.Trim(), ItunesPlatform, StringComparison.OrdinalIgnoreCase)
            ? AutoTagIdentityTags.AppleLegacyIdentityAliases
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : string.Equals(platformId?.Trim(), "musicbrainz", StringComparison.OrdinalIgnoreCase)
            ? ["MUSICBRAINZ_RELEASE_ID", "MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_ALBUM_ID", AlbumIdRawTag]
            : ProviderReleaseIdWriteRawNames(platformId);

    private static IReadOnlyList<string> AllProviderReleaseIdCleanupRawNames()
        => KnownProviderReleaseIdPlatforms
            .SelectMany(ProviderReleaseIdCleanupRawNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ApplyEstablishedAlbumIdentity(AutoTagTrack track, AlbumIdentity identity, string platformId)
    {
        var establishedDate = AlbumIdentity.ParseReleaseDate(identity.ReleaseDate);
        if (establishedDate.HasValue)
        {
            track.ReleaseDate = establishedDate;
            SetOtherValue(track, "RELEASEDATE", identity.ReleaseDate);
        }

        var musicBrainzConfirmed = identity.IsPlatformReleaseIdConfirmed("MUSICBRAINZ_RELEASE_ID");
        if (musicBrainzConfirmed && !string.IsNullOrWhiteSpace(identity.AlbumId))
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

        if (musicBrainzConfirmed && !string.IsNullOrWhiteSpace(identity.ReleaseGroupId))
        {
            track.ReleaseGroupId = identity.ReleaseGroupId;
            SetOtherValue(track, ReleaseGroupIdRawTag, identity.ReleaseGroupId);
            SetOtherValue(track, "MUSICBRAINZ_RELEASEGROUPID", identity.ReleaseGroupId);
        }

        if (musicBrainzConfirmed && !string.IsNullOrWhiteSpace(identity.AlbumArtistId))
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
            && identity.IsPlatformReleaseIdConfirmed(platformReleaseIdName)
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
        return ResolveAlbumRootDirectory(context.File, prospective) ?? string.Empty;
    }

    private static AlbumIdentity? TryReadAlbumIdentityFromFolder(string filePath, string? destinationDirectory)
    {
        var identity = AlbumIdentity.Empty;
        foreach (var directory in EnumerateAlbumIdentitySeedDirectories(filePath, destinationDirectory))
        {
            identity = identity.CoalesceWith(ReadAlbumIdentityFromDirectory(directory));
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

    private static AlbumIdentity ReadAlbumIdentityFromDirectory(string directory)
    {
        var identities = new List<AlbumIdentity>();
        IEnumerable<string> siblings;
        try
        {
            var seedDirectories = new List<string> { directory };
            seedDirectories.AddRange(Directory.EnumerateDirectories(directory)
                .Where(path => Regex.IsMatch(
                    Path.GetFileName(path),
                    "^CD\\d+$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
            siblings = seedDirectories
                .SelectMany(Directory.EnumerateFiles)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AlbumIdentity.Empty;
        }

        foreach (var sibling in siblings)
        {
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
                    FirstMusicBrainzShapedId(ReadRawTagValuesAny(file, extension, AlbumIdentityAlbumIdRawNames)),
                    FirstMusicBrainzShapedId(ReadRawTagValuesAny(file, extension, AlbumIdentityAlbumArtistIdRawNames)),
                    FirstMusicBrainzShapedId(ReadRawTagValuesAny(file, extension, AlbumIdentityReleaseGroupIdRawNames)),
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
        foreach (var rawName in AllProviderReleaseIdCleanupRawNames())
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
        if (!context.EnabledTags.Contains(ReleaseIdTag)
            || !context.SourceTrack.HasAuthoritativeProviderReleaseIdResult)
        {
            return;
        }

        var releaseId = context.SourceTrack.ReleaseId?.Trim();
        // Namespace guard: a value from another platform's id family (e.g. an Audiomack
        // numeric album id) must never be written into this platform's release-id tag.
        if (!string.IsNullOrWhiteSpace(releaseId)
            && !IsPlatformReleaseIdShapeValid(context.PlatformId, releaseId))
        {
            return;
        }

        ApplyProviderReleaseIdState(
            tagWriteContext,
            context.PlatformId,
            releaseId,
            context.SourceTrack.HasAuthoritativeProviderReleaseIdAbsence);
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
