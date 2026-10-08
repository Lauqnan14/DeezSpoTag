using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Library;

using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

public sealed record QueueSoulseekBatchDownloadRequest(
    string Username,
    string RemoteDirectory,
    long? DestinationFolderId,
    string? DisplayCoverUrl,
    IReadOnlyList<QueueSoulseekBatchFileRequest>? Files,
    IReadOnlyList<string>? Sidecars = null);

public sealed record QueueSoulseekBatchFileRequest(
    string RemotePath,
    long? RemoteSizeBytes,
    string? Title,
    string? Artist,
    string? Album,
    int? TrackNumber,
    int? DurationMs,
    string? Quality);

public sealed record SoulseekBatchQueuePlanItem(string RemotePath, DownloadIntent Intent);

public sealed record SoulseekBatchQueueFileResult(
    string RemotePath,
    string Status,
    IReadOnlyList<string> QueueUuids,
    IReadOnlyList<string> ReasonCodes,
    string Message);

public sealed record SoulseekBatchQueuePlan(
    string? RequestErrorCode,
    string? RequestErrorMessage,
    IReadOnlyList<SoulseekBatchQueuePlanItem> Items,
    IReadOnlyList<SoulseekBatchQueueFileResult> Rejections)
{
    public bool IsValid => RequestErrorCode is null;
}

/// <summary>Validates an exact-peer batch and creates pinned intents without database or queue access.</summary>
public static class SoulseekBatchQueuePlanner
{
    public const int MaximumBatchSize = 100;

    /// <summary>Validates an exact-peer batch request against the fresh listing and builds the plan, turning every survivor into a pinned intent.</summary>
    /// <param name="validateSidecars">
    ///     Whether the requested sidecars may be checked against the peer's listing. The endpoint makes a
    ///     pre-flight pass with no listing at all, purely to reject a malformed request before spending a
    ///     peer round trip; that pass must not judge sidecars, or every one of them would be reported as gone.
    /// </param>
    public static SoulseekBatchQueuePlan CreatePlan(
        QueueSoulseekBatchDownloadRequest? request,
        IReadOnlyCollection<SoulseekBrowseFile> freshFiles,
        FolderDto? destination,
        bool validateSidecars = true)
    {
        ArgumentNullException.ThrowIfNull(freshFiles);

        if (request is null)
        {
            return Invalid("invalid_request", "A batch request is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Invalid("username_required", "A Soulseek peer is required.");
        }

        if (string.IsNullOrWhiteSpace(request.RemoteDirectory))
        {
            return Invalid("remote_directory_required", "A remote directory is required.");
        }

        if (request.DestinationFolderId is not > 0)
        {
            return Invalid("destination_required", "A destination folder is required.");
        }

        if (request.Files is null || request.Files.Count == 0)
        {
            return Invalid("empty_batch", "Select at least one file.");
        }

        if (request.Files.Count > MaximumBatchSize)
        {
            return Invalid("batch_too_large", $"A batch cannot contain more than {MaximumBatchSize} files.");
        }

        var normalizedDirectory = SoulseekRemotePath.Normalize(request.RemoteDirectory);
        var requestedPaths = request.Files
            .Select(file => SoulseekRemotePath.Normalize(file.RemotePath))
            .ToArray();

        if (requestedPaths.Any(path => !SoulseekRemotePath.IsDirectChildOf(path, normalizedDirectory)))
        {
            return Invalid("remote_path_outside_directory", "Every file must be a direct child of the selected directory.");
        }

        if (requestedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requestedPaths.Length)
        {
            return Invalid("duplicate_remote_path", "The batch contains a duplicate remote path.");
        }

        var destinationError = ValidateDestination(request.DestinationFolderId.Value, destination);
        if (destinationError is not null)
        {
            return destinationError;
        }

        var requestedPathSet = requestedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (freshFiles.Any(file => requestedPathSet.Contains(SoulseekRemotePath.Normalize(file.Filename))
            && !string.Equals(file.Username, request.Username.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Invalid("mixed_peer_identity", "Every selected file must come from the declared peer.");
        }

        var freshByPath = freshFiles
            .Where(file => string.Equals(file.Username, request.Username.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => SoulseekRemotePath.Normalize(file.Filename), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Sidecars are held to exactly the same rule as the audio: the same peer, and a direct child of the
        // same directory that was browsed. A request naming anything else is refused rather than quietly
        // trimmed, because silently dropping part of what was asked for is how a reader ends up with a
        // track and no artwork and no reason why.
        var sidecars = validateSidecars
            ? ResolveSidecars(request, normalizedDirectory, freshByPath, out var sidecarError)
            : ResolveSidecarPaths(request, out sidecarError);
        if (sidecarError is not null)
        {
            return sidecarError;
        }

        // The folder's release category, from the one piece of evidence that is reliable: how many audio
        // files the browsed folder actually holds. Album text is not evidence - a folder named after a song
        // is routinely a whole album, and a one-track release is routinely named after its album. A single
        // audio file proves nothing either way, so it stays unknown and the destination profile decides.
        var folderAudioCount = freshFiles.Count(file => file.Eligible);
        var releaseCategory = folderAudioCount > 1
            ? AutoTagReleaseCategory.Album
            : null;

        var items = new List<SoulseekBatchQueuePlanItem>(request.Files.Count);
        var rejections = new List<SoulseekBatchQueueFileResult>();

        for (var index = 0; index < request.Files.Count; index++)
        {
            var requested = request.Files[index];
            var normalizedPath = requestedPaths[index];
            if (!freshByPath.TryGetValue(normalizedPath, out var canonical)
                || !SoulseekRemotePath.IsDirectChildOf(canonical.Filename, normalizedDirectory))
            {
                rejections.Add(Reject(requested.RemotePath, "file_no_longer_available", "The peer no longer offers this file."));
                continue;
            }

            if (!canonical.Eligible)
            {
                rejections.Add(Reject(
                    requested.RemotePath,
                    canonical.RejectedBecause ?? "file_ineligible",
                    "This file is no longer eligible under the current Soulseek settings."));
                continue;
            }

            if (requested.RemoteSizeBytes is > 0
                && canonical.Size > 0
                && requested.RemoteSizeBytes.Value != canonical.Size)
            {
                rejections.Add(Reject(requested.RemotePath, "remote_size_changed", "The peer now reports a different file size."));
                continue;
            }

            var intent = BuildPinnedIntent(
                request.Username,
                canonical.Filename,
                canonical.Size,
                canonical.Title,
                canonical.Artist,
                canonical.Album,
                canonical.DurationSeconds is > 0 ? canonical.DurationSeconds.Value * 1000 : null,
                canonical.TrackNumber,
                canonical.QualityCode,
                request.DestinationFolderId,
                request.DisplayCoverUrl,
                sidecars: sidecars,
                releaseCategory: releaseCategory);
            items.Add(new SoulseekBatchQueuePlanItem(requested.RemotePath, intent));
        }

        return new SoulseekBatchQueuePlan(null, null, items, rejections);
    }

    /// <summary>
    ///     The shape check for the pre-flight pass, which has no peer listing to check against.
    /// </summary>
    /// <remarks>
    ///     Only the things that can be judged from the request alone are judged here. Whether a sidecar is
    ///     still on the peer is decided by the pass that has actually asked the peer.
    /// </remarks>
    private static List<string> ResolveSidecarPaths(QueueSoulseekBatchDownloadRequest request, out SoulseekBatchQueuePlan? error)
    {
        error = null;
        if (request.Sidecars is null || request.Sidecars.Count == 0)
        {
            return [];
        }

        if (request.Sidecars.Count > MaximumBatchSize)
        {
            error = Invalid("sidecar_batch_too_large", $"A batch cannot contain more than {MaximumBatchSize} sidecar files.");
            return [];
        }

        if (request.Sidecars.Any(path => SoulseekRemotePath.Normalize(path).Length == 0))
        {
            error = Invalid("sidecar_path_required", "A sidecar path is required.");
            return [];
        }

        return [];
    }

    /// <summary>
    ///     Validates the non-audio files the reader chose to take with the album.
    /// </summary>
    /// <remarks>
    ///     They are resolved against the peer's own current listing rather than trusted from the request, so a
    ///     sidecar that has since changed or disappeared cannot be pinned onto an intent. The direct-child rule
    ///     is the same one the audio files already satisfy, which is what keeps this scoped to exactly the
    ///     folder that was browsed.
    /// </remarks>
    private static List<string> ResolveSidecars(
        QueueSoulseekBatchDownloadRequest request,
        string normalizedDirectory,
        IReadOnlyDictionary<string, SoulseekBrowseFile> freshByPath,
        out SoulseekBatchQueuePlan? error)
    {
        error = null;
        var resolved = new List<string>();
        if (request.Sidecars is null || request.Sidecars.Count == 0)
        {
            return resolved;
        }

        if (request.Sidecars.Count > MaximumBatchSize)
        {
            error = Invalid("sidecar_batch_too_large", $"A batch cannot contain more than {MaximumBatchSize} sidecar files.");
            return resolved;
        }

        foreach (var normalized in request.Sidecars.Select(requested => SoulseekRemotePath.Normalize(requested)))
        {
            if (normalized.Length == 0)
            {
                error = Invalid("sidecar_path_required", "A sidecar path is required.");
                return [];
            }

            if (!freshByPath.TryGetValue(normalized, out var canonical))
            {
                error = Invalid("sidecar_no_longer_available", $"The peer no longer offers {SoulseekRemotePath.GetLeaf(normalized)}.");
                return [];
            }

            if (!SoulseekRemotePath.IsDirectChildOf(canonical.Filename, normalizedDirectory))
            {
                error = Invalid("sidecar_outside_directory", "Every sidecar must be a direct child of the selected directory.");
                return [];
            }

            if (!SoulseekSidecarPolicy.IsFetchable(canonical.SidecarRole))
            {
                error = Invalid(
                    "sidecar_not_takeable",
                    $"{canonical.DisplayFilename} is not artwork or lyrics and is not downloaded.");
                return [];
            }

            if (!resolved.Contains(canonical.Filename, StringComparer.OrdinalIgnoreCase))
            {
                resolved.Add(canonical.Filename);
            }
        }

        return resolved;
    }

    public static DownloadIntent BuildPinnedIntent(
        string? username,
        string? remotePath,
        long remoteSizeBytes,
        string? title,
        string? artist,
        string? album,
        int? durationMs,
        int? trackNumber,
        string? quality,
        long? destinationFolderId,
        string? displayCoverUrl,
        string? isrc = null,
        IReadOnlyList<string>? sidecars = null,
        string? releaseCategory = null)
    {
        var normalizedAlbum = album?.Trim() ?? string.Empty;
        var normalizedArtist = artist?.Trim() ?? string.Empty;
        if (normalizedArtist.Length == 0)
        {
            normalizedArtist = normalizedAlbum.Length > 0 ? normalizedAlbum : "Unknown Artist";
        }

        return new DownloadIntent
        {
            SourceService = SoulseekQueueItem.EngineId,
            PreferredEngine = SoulseekQueueItem.EngineId,
            Artist = normalizedArtist,
            Title = title?.Trim() ?? string.Empty,
            Album = normalizedAlbum,
            Isrc = isrc?.Trim() ?? string.Empty,
            DurationMs = durationMs ?? 0,
            TrackNumber = trackNumber ?? 0,
            Quality = quality?.Trim() ?? string.Empty,
            ContentType = "stereo",
            DestinationFolderId = destinationFolderId,
            DisplayCoverUrl = displayCoverUrl?.Trim() ?? string.Empty,
            SoulseekUsername = username?.Trim() ?? string.Empty,
            SoulseekRemotePath = remotePath?.Trim() ?? string.Empty,
            SoulseekRemoteSizeBytes = remoteSizeBytes > 0 ? remoteSizeBytes : 0,
            SoulseekSidecarRemotePaths = sidecars is null ? [] : new List<string>(sidecars),
            SoulseekReleaseCategory = releaseCategory
        };
    }

    private static SoulseekBatchQueuePlan? ValidateDestination(long destinationFolderId, FolderDto? destination)
    {
        if (destination is null || destination.Id != destinationFolderId)
        {
            return Invalid("destination_not_found", "The selected destination no longer exists.");
        }

        if (!destination.Enabled)
        {
            return Invalid("destination_disabled", "The selected destination is disabled.");
        }

        if (!FolderContentTypeResolver.IsRole(destination, FolderContentRole.Stereo))
        {
            return Invalid("destination_not_stereo", "Soulseek album files require a stereo destination.");
        }

        if (!destination.AutoTagEnabled)
        {
            return Invalid("destination_autotag_disabled", "AutoTag must be enabled for the selected destination.");
        }

        if (string.IsNullOrWhiteSpace(destination.AutoTagProfileId))
        {
            return Invalid("destination_autotag_profile_required", "The selected destination requires an AutoTag profile.");
        }

        return null;
    }

    private static SoulseekBatchQueuePlan Invalid(string code, string message)
        => new(code, message, [], []);

    private static SoulseekBatchQueueFileResult Reject(string remotePath, string code, string message)
        => new(remotePath, "failed", [], [code], message);
}
