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

    private static (string Scope, string RootPath) ResolveDestinationLibraryScope(
        AutoTagFileRunContext context,
        string albumRoot)
    {
        var scopes = context.Plan.Config.DestinationFolderScopes ?? [];
        var matched = scopes
            .Select(scope => (Scope: scope, Root: TryGetFullPath(scope.RootPath)))
            .Where(candidate => candidate.Root is not null && IsPathWithin(candidate.Root, albumRoot))
            .OrderByDescending(candidate => candidate.Root!.Length)
            .FirstOrDefault();
        if (matched.Scope is not null && matched.Root is not null)
        {
            return ($"id:{matched.Scope.Id.ToString(CultureInfo.InvariantCulture)}", matched.Root);
        }

        var destinationFolderId = context.Plan.Config.DestinationFolderId
            ?? context.Plan.Config.ManualDestinationFolderId;
        return destinationFolderId is > 0
            ? ($"id:{destinationFolderId.Value.ToString(CultureInfo.InvariantCulture)}", context.Plan.TargetPath)
            : (context.Plan.TargetPath, context.Plan.TargetPath);
    }

    private static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsPathWithin(string root, string path)
    {
        var fullPath = TryGetFullPath(path);
        if (fullPath is null)
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, fullPath);
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static AutoTagAudioInfo ApplyConfirmedProviderReleaseIdHint(
        AutoTagAudioInfo source,
        AlbumIdentity identity,
        string platformId)
    {
        var clone = CloneAudioInfo(source);
        // Drop every known provider release alias the file carried: a value that was not
        // confirmed through a native payload must never reach a downstream consumer.
        foreach (var rawName in AllKnownProviderReleaseAliasNames())
        {
            clone.Tags.Remove(rawName);
        }

        var providerId = AlbumIdentity.NormalizeProviderId(platformId);
        var confirmed = identity.GetProviderIdentity(providerId);
        if (confirmed is null || confirmed.IsEmpty)
        {
            return clone;
        }

        foreach (var field in new[]
                 {
                     ProviderIdentityField.ReleaseId,
                     ProviderIdentityField.AlbumId,
                     ProviderIdentityField.AlbumArtistId
                 })
        {
            var value = field switch
            {
                ProviderIdentityField.ReleaseId => confirmed.ReleaseId,
                ProviderIdentityField.AlbumId => confirmed.AlbumId,
                _ => confirmed.AlbumArtistId
            };
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var family = AutoTagIdentityTags.ResolveFamily(providerId, field);
            foreach (var rawName in family.WriteNames)
            {
                clone.Tags[rawName] = [value.Trim()];
            }
        }

        return clone;
    }

    private static IReadOnlyList<string> AllKnownProviderReleaseAliasNames()
        => AutoTagIdentityTags.KnownProviders
            .SelectMany(provider => AutoTagIdentityTags.ResolveFamily(provider, ProviderIdentityField.ReleaseId).CleanupNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static bool ShouldSkipAlbumIdentityFile(ISet<string> reviewedFiles, string? filePath)
        => string.IsNullOrWhiteSpace(filePath)
           || reviewedFiles.Contains(filePath)
           || !IOFile.Exists(filePath);

    private async Task ReconcileBatchAlbumIdentitiesAsync(
        AutoTagRunPlan plan,
        int batchStart,
        int batchEnd,
        Action<string> logCallback,
        CancellationToken token)
    {
        var enabled = BuildConfiguredTagSet(plan.Config.Tags);
        var releaseIdEnabled = enabled.Contains(ReleaseIdTag);
        var albumIdEnabled = enabled.Contains(AlbumIdTag);
        var albumArtistIdEnabled = enabled.Contains(AlbumArtistIdTag);
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
            if (ShouldSkipAlbumIdentityFile(plan.ReviewedFiles, filePath))
            {
                logCallback($"{AutoTagProtocol.LogMarker} skipped album-identity reconcile for missing or reviewed file {filePath}");
                continue;
            }

            var extension = Path.GetExtension(filePath);
            try
            {
            using (var file = TagLib.File.Create(filePath))
            {
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
                && !string.IsNullOrWhiteSpace(identity.CanonicalAlbumTitle))
            {
                file.Tag.Album = identity.CanonicalAlbumTitle;
            }

            if (enabled.Contains(AlbumArtistTag)
                && !string.IsNullOrWhiteSpace(identity.CanonicalAlbumArtist))
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
                    plan.TagSettings.UseNullSeparator,
                    forceOverwrite: true);
            }

            WriteReconciledRawIdentity(writeContext, enabled, ReleaseStatusTag, ReleaseStatusRawTag, SupportedTag.ReleaseStatus, identity.ReleaseStatus);
            WriteReconciledRawIdentity(writeContext, enabled, ReleaseCountryTag, ReleaseCountryRawTag, SupportedTag.ReleaseCountry, identity.ReleaseCountry);
            WriteReconciledRawIdentity(writeContext, enabled, BarcodeTag, BarcodeRawTag, SupportedTag.Barcode, identity.Barcode);
            WriteReconciledRawIdentity(writeContext, enabled, ReleaseTypeTag, ReleaseTypeRawTag, SupportedTag.ReleaseType, identity.ReleaseType);
            WriteReconciledRawIdentity(writeContext, enabled, ReleaseGroupIdTag, ReleaseGroupIdRawTag, SupportedTag.ReleaseGroupId, identity.ReleaseGroupId);
            WriteReconciledRawIdentity(writeContext, enabled, ReleaseGroupIdTag, "MUSICBRAINZ_RELEASEGROUPID", SupportedTag.ReleaseGroupId, identity.ReleaseGroupId);

                file.Save();
            }

            // Every confirmed provider identity travels through the single central writer,
            // synthesized from album-scoped fields only: no track id, artist id or URL is
            // ever propagated to a sibling track.
            var identityProviders = (identity.ProviderIdentities?.Keys ?? Array.Empty<string>())
                .Concat(plan.EffectivePlatforms)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var platform in identityProviders)
            {
                var providerId = AlbumIdentity.NormalizeProviderId(platform);
                var providerIdentity = identity.GetProviderIdentity(providerId);
                if (providerIdentity is null || providerIdentity.IsEmpty)
                {
                    continue;
                }

                var fields = new HashSet<ProviderIdentityField>();
                if (albumIdEnabled)
                {
                    fields.Add(ProviderIdentityField.AlbumId);
                }

                if (releaseIdEnabled)
                {
                    fields.Add(ProviderIdentityField.ReleaseId);
                }

                if (albumArtistIdEnabled)
                {
                    fields.Add(ProviderIdentityField.AlbumArtistId);
                }

                if (fields.Count == 0)
                {
                    continue;
                }

                var payload = new ProviderIdentityPayload(
                    providerId,
                    TrackId: null,
                    providerIdentity.AlbumId,
                    providerIdentity.ReleaseId,
                    ArtistId: null,
                    providerIdentity.AlbumArtistId,
                    Url: null,
                    IsNativeProviderResult: true);
                var result = await WriteProviderIdentityAsync(
                    filePath,
                    payload with { ForceOverwrite = true },
                    plan.Config,
                    fields,
                    token);
                if (result.Failures.Count > 0)
                {
                    var failure = result.Failures[0];
                    throw new IOException(
                        $"Provider identity persistence failed: format={failure.Format}, provider={failure.Provider}, field={failure.Field}, reason={failure.Reason}.");
                }
            }
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       && (ex is FileNotFoundException
                                           || ex is DirectoryNotFoundException
                                           || !IOFile.Exists(filePath)))
            {
                logCallback($"{AutoTagProtocol.LogMarker} skipped album-identity reconcile for missing file {filePath}: {ex.Message}");
            }
        }
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
        var albumRoot = ResolveAlbumRootDirectory(context.File, null);
        var destination = albumRoot is null
            ? default
            : ResolveDestinationLibraryScope(context, albumRoot);
        var albumRelativePath = albumRoot is null
            ? null
            : BuildAlbumRelativePath(destination.RootPath, albumRoot);
        var key = AlbumIdentity.BuildFolderScopedKey(destination.Scope, albumRelativePath)
            ?? ResolveSourceAlbumIdentityKey(context.Plan.TargetPath, source);
        if (key is null)
        {
            return CloneAudioInfo(source);
        }

        if (!context.Plan.AlbumIdentities.TryGet(key, out var identity))
        {
            var seed = FindPersistedIdentityByRelativePath(albumRelativePath)
                ?? TryReadAlbumIdentityFromFolder(context.File, albumRoot);
            identity = context.Plan.AlbumIdentities.Establish(key, AlbumIdentity.Empty, seed);
        }

        albumRoot = TryResolvePersistedAlbumRoot(destination.RootPath, identity.AlbumRelativePath)
            ?? albumRoot;
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

    private AlbumIdentity? FindPersistedIdentityByRelativePath(string? albumRelativePath)
        => string.IsNullOrWhiteSpace(albumRelativePath) || _albumIdentityStore is null
            ? null
            : _albumIdentityStore.Entries
                .Where(entry => string.Equals(
                    entry.Identity.AlbumRelativePath?.Replace('\\', '/').Trim('/'),
                    albumRelativePath.Replace('\\', '/').Trim('/'),
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.UpdatedAt)
                .Select(entry => entry.Identity)
                .FirstOrDefault();

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
        ProviderIdentityPayload payload,
        bool hasAuthoritativeProviderResult)
    {
        // Only a native provider result may establish album-scoped provider identity;
        // a fallback value is never attributed to the stage provider.
        if (!hasAuthoritativeProviderResult || !payload.IsNativeProviderResult)
        {
            return;
        }

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

        var currentAlbumRoot = ResolveAlbumRootDirectory(context.File, null);
        var belongsToConfiguredDestination = (context.Plan.Config.DestinationFolderScopes ?? [])
            .Select(scope => TryGetFullPath(scope.RootPath))
            .Any(root => root is not null && IsPathWithin(root, context.File));
        var useProspectiveAlbumRoot = context.Plan.Config.ManualDestinationFolderId is > 0
            && !belongsToConfiguredDestination;
        var albumRoot = useProspectiveAlbumRoot
            ? ResolveAlbumRootDirectory(context.File, TryResolveProspectiveAlbumDirectory(context, track))
                ?? currentAlbumRoot
            : currentAlbumRoot;
        albumRoot ??= releaseContext?.AlbumRoot;
        var destination = albumRoot is null
            ? default
            : ResolveDestinationLibraryScope(context, albumRoot);
        var albumRelativePath = albumRoot is null
            ? null
            : BuildAlbumRelativePath(destination.RootPath, albumRoot);
        var folderKey = AlbumIdentity.BuildFolderScopedKey(destination.Scope, albumRelativePath);
        var key = folderKey ?? releaseContext?.ReleaseKey;
        if (key is null)
        {
            return;
        }

        AlbumIdentity? seed = null;
        if (context.Plan.SeededAlbumIdentityKeys.Add(key))
        {
            seed = TryReadAlbumIdentityFromFolder(
                context.File,
                albumRoot);
        }

        var candidate = BuildAlbumIdentityCandidate(track, payload) with
        {
            CanonicalAlbumTitle = releaseContext?.AlbumTitle ?? track.Album,
            CanonicalAlbumArtist = releaseContext?.AlbumArtist ?? albumArtist,
            AlbumRelativePath = albumRelativePath
        };
        var established = context.Plan.AlbumIdentities.Establish(
            key,
            candidate,
            seed,
            payload.ProviderId,
            candidate.GetProviderIdentity(payload.ProviderId),
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
            ApplyEstablishedFolderIdentity(track, establishedFolder, context.Platform);
            return;
        }

        establishedFolder = establishedFolder with { Identity = establishedIdentity };
        context.Plan.AlbumFolderIdentities[folderKey] = establishedFolder;

        ApplyEstablishedFolderIdentity(track, establishedFolder, context.Platform);
    }

    private static void ApplyEstablishedFolderIdentity(
        AutoTagTrack track,
        FolderAlbumIdentity establishedFolder,
        string platform)
    {
        if (!string.IsNullOrWhiteSpace(establishedFolder.AlbumTitle))
        {
            track.Album = establishedFolder.AlbumTitle;
        }

        if (!string.IsNullOrWhiteSpace(establishedFolder.AlbumArtist))
        {
            track.AlbumArtists = [establishedFolder.AlbumArtist];
        }

        ApplyEstablishedAlbumIdentity(track, establishedFolder.Identity, platform);
    }

    /// <summary>
    /// Builds the album-scoped candidate from the provider's immutable native payload.
    /// Only album id, release id and album-artist id are album-scoped; track id, artist
    /// id and URL are never part of an album identity.
    /// </summary>
    private static AlbumIdentity BuildAlbumIdentityCandidate(AutoTagTrack track, ProviderIdentityPayload payload)
    {
        var providerId = AlbumIdentity.NormalizeProviderId(payload.ProviderId);
        var isMusicBrainz = providerId == "musicbrainz";
        var albumId = isMusicBrainz
            ? FirstMusicBrainzShapedId(
                ResolveOtherValues(track, "MUSICBRAINZ_ALBUMID")
                    .Concat(ResolveOtherValues(track, "MUSICBRAINZ_ALBUM_ID"))
                    .Append(payload.AlbumId))
            : null;
        var albumArtistId = isMusicBrainz
            ? FirstMusicBrainzShapedId(
                ResolveOtherValues(track, "MUSICBRAINZ_ALBUMARTISTID")
                    .Concat(ResolveOtherValues(track, "MUSICBRAINZ_ALBUM_ARTIST_ID"))
                    .Append(payload.AlbumArtistId))
            : null;
        var releaseGroupId = isMusicBrainz
            ? FirstMusicBrainzShapedId(
                ResolveOtherValues(track, "MUSICBRAINZ_RELEASEGROUPID")
                    .Concat(ResolveOtherValues(track, "MUSICBRAINZ_RELEASE_GROUP_ID"))
                    .Append(track.ReleaseGroupId))
            : null;
        var releaseId = !string.IsNullOrWhiteSpace(payload.ReleaseId)
            && IsPlatformReleaseIdShapeValid(providerId, payload.ReleaseId!)
                ? payload.ReleaseId!.Trim()
                : null;

        var providerIdentity = new ProviderAlbumIdentity(albumId, releaseId, albumArtistId);
        var identity = NormalizeSharedAlbumIdentity(new AlbumIdentity(
            AlbumIdentity.FormatReleaseDate(track.ReleaseDate),
            albumId,
            albumArtistId,
            releaseGroupId,
            track.ReleaseStatus,
            track.ReleaseCountry,
            track.Barcode,
            track.ReleaseType,
            ProviderIdentities: providerIdentity.IsEmpty
                ? null
                : new Dictionary<string, ProviderAlbumIdentity>(StringComparer.OrdinalIgnoreCase)
                {
                    [providerId] = providerIdentity
                }));

        return identity;
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

    private static void ApplyEstablishedAlbumIdentity(AutoTagTrack track, AlbumIdentity identity, string platformId)
    {
        var establishedDate = AlbumIdentity.ParseReleaseDate(identity.ReleaseDate);
        if (establishedDate.HasValue)
        {
            track.ReleaseDate = establishedDate;
            SetOtherValue(track, "RELEASEDATE", identity.ReleaseDate);
        }

        // MusicBrainz is the only provider whose album-scoped ids are shared through
        // MUSICBRAINZ_* aliases; every other provider keeps its ids in its own family.
        var musicBrainz = identity.GetProviderIdentity("musicbrainz");
        if (musicBrainz?.AlbumId is { Length: > 0 } musicBrainzAlbumId)
        {
            track.AlbumId = musicBrainzAlbumId;
            SetOtherValue(track, "MUSICBRAINZ_ALBUMID", musicBrainzAlbumId);
        }

        if (musicBrainz?.ReleaseId is { Length: > 0 } musicBrainzReleaseId)
        {
            SetOtherValue(track, "MUSICBRAINZ_RELEASE_ID", musicBrainzReleaseId);
        }

        if (!string.IsNullOrWhiteSpace(identity.ReleaseGroupId))
        {
            track.ReleaseGroupId = identity.ReleaseGroupId;
            SetOtherValue(track, ReleaseGroupIdRawTag, identity.ReleaseGroupId);
            SetOtherValue(track, "MUSICBRAINZ_RELEASEGROUPID", identity.ReleaseGroupId);
        }

        if (musicBrainz?.AlbumArtistId is { Length: > 0 } musicBrainzAlbumArtistId)
        {
            track.AlbumArtistId = musicBrainzAlbumArtistId;
            SetOtherValue(track, "MUSICBRAINZ_ALBUMARTISTID", musicBrainzAlbumArtistId);
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

        // The pass platform's own release id is carried under that provider's family
        // alias, so the central writer (which resolves the same family) picks it up.
        var providerId = AlbumIdentity.NormalizeProviderId(platformId);
        var platformIdentity = identity.GetProviderIdentity(providerId);
        if (platformIdentity?.ReleaseId is { Length: > 0 } platformReleaseId)
        {
            track.ReleaseId = platformReleaseId;
            var family = AutoTagIdentityTags.ResolveFamily(providerId, ProviderIdentityField.ReleaseId);
            SetOtherValue(track, family.WriteNames[0], platformReleaseId);
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
                    ProviderIdentities: ReadProviderIdentities(file, extension));
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
            ProviderIdentities: BuildMajorityProviderIdentities(identities));
    }

    /// <summary>
    /// Majority vote per provider and album-scoped field. Providers never mix: a value
    /// confirmed for one provider only ever lands in that provider's entry.
    /// </summary>
    private static IReadOnlyDictionary<string, ProviderAlbumIdentity>? BuildMajorityProviderIdentities(
        IReadOnlyList<AlbumIdentity> identities)
    {
        var providers = identities
            .SelectMany(identity => identity.ProviderIdentities?.Keys ?? Array.Empty<string>())
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Select(AlbumIdentity.NormalizeProviderId)
            .Where(provider => provider.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(provider => provider, StringComparer.Ordinal)
            .ToList();
        if (providers.Count == 0)
        {
            return null;
        }

        var selected = new Dictionary<string, ProviderAlbumIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            string? Vote(Func<ProviderAlbumIdentity, string?> select)
                => SelectMajority(identities.Select(identity =>
                    identity.ProviderIdentities is not null
                    && identity.ProviderIdentities.TryGetValue(provider, out var value)
                        ? select(value)
                        : null));

            var identity = new ProviderAlbumIdentity(
                Vote(value => value.AlbumId),
                Vote(value => value.ReleaseId),
                Vote(value => value.AlbumArtistId));
            if (!identity.IsEmpty)
            {
                selected[provider] = identity;
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

    /// <summary>
    /// Reads the provider release ids already present on disk, per provider family. The
    /// numeric/opaque shape contract is applied on intake, so a foreign platform's id can
    /// never re-seed its own namespace.
    /// </summary>
    private static IReadOnlyDictionary<string, ProviderAlbumIdentity>? ReadProviderIdentities(
        TagLib.File file,
        string extension)
    {
        var values = new Dictionary<string, ProviderAlbumIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in AutoTagIdentityTags.KnownProviders)
        {
            var family = AutoTagIdentityTags.ResolveFamily(provider, ProviderIdentityField.ReleaseId);
            var releaseId = family.CleanupNames
                .Select(rawName => ReadRawTagValuesAny(file, extension, rawName).FirstOrDefault())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(releaseId) || !IsPlatformReleaseIdShapeValid(provider, releaseId!))
            {
                continue;
            }

            values[provider] = new ProviderAlbumIdentity(null, releaseId!.Trim(), null);
        }

        return values.Count == 0 ? null : values;
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
