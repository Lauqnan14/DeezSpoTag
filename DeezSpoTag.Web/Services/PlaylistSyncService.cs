using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Library;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistSyncService
{
    private const int MaxUploadedMergeArtworkBytes = 8 * 1024 * 1024;
    private const string PlaylistNotAvailableMessage = "Playlist not available.";
    private sealed record PlexConnection(string Url, string Token, string MachineIdentifier);
    private sealed record JellyfinConnection(string Url, string ApiKey, string UserId);
    private sealed record NavidromeConnection(string Url, string Username, string Password);

    private sealed record SyncTrackSummary(
        string SourceTrackId,
        string? Isrc,
        string Name,
        string Artists,
        string Album,
        string? ReleaseDate,
        bool? Explicit,
        IReadOnlyList<string> Genres,
        int? DurationMs,
        string? IdentitySource = null,
        string? IdentityTrackId = null);

    private sealed record SyncMatchSummary(
        List<string> TargetIds,
        List<PlaylistWatchTargetMembership> Memberships,
        int SourceTracks,
        int LocalMatches,
        int TargetMatches,
        int MissingTracks,
        int MetadataMatches,
        int SearchMatches);

    private const string SpotifySource = "spotify";
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string NavidromeService = "navidrome";
    private const string SyncModeMirror = "mirror";
    private const string SyncModeAppend = "append";
    private const int DurationToleranceMs = 2000;
    private const string NoTargetServerSelectedMessage = "No target server selected.";
    private const string UnsupportedPlaylistSyncTargetMessage = "Unsupported playlist sync target.";
    private const string PlexNotConfiguredMessage = "Plex is not configured.";
    private const string JellyfinNotConfiguredMessage = "Jellyfin is not configured.";
    private const string NavidromeNotConfiguredMessage = "Navidrome is not configured.";
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly PlatformAuthService _authService;
    private readonly PlaylistVisualService _playlistVisualService;
    private readonly MediaServerLibraryRefreshService _mediaServerRefreshService;
    private readonly CrossDeviceSyncService? _crossDeviceSyncService;
    private readonly ILogger<PlaylistSyncService> _logger;

    public PlaylistSyncService(PlaylistSyncDependencies dependencies)
    {
        _libraryRepository = dependencies.LibraryRepository;
        _spotifyMetadataService = dependencies.SpotifyMetadataService;
        _plexApiClient = dependencies.PlexApiClient;
        _jellyfinApiClient = dependencies.JellyfinApiClient;
        _navidromeApiClient = dependencies.NavidromeApiClient;
        _authService = dependencies.AuthService;
        _playlistVisualService = dependencies.PlaylistVisualService;
        _mediaServerRefreshService = dependencies.MediaServerRefreshService;
        _crossDeviceSyncService = dependencies.CrossDeviceSyncService;
        _logger = dependencies.Logger;
    }

    public sealed class PlaylistSyncDependencies
    {
        public required LibraryRepository LibraryRepository { get; init; }
        public required SpotifyMetadataService SpotifyMetadataService { get; init; }
        public required PlexApiClient PlexApiClient { get; init; }
        public required JellyfinApiClient JellyfinApiClient { get; init; }
        public required NavidromeApiClient NavidromeApiClient { get; init; }
        public required PlatformAuthService AuthService { get; init; }
        public required PlaylistVisualService PlaylistVisualService { get; init; }
        public required MediaServerLibraryRefreshService MediaServerRefreshService { get; init; }
        public CrossDeviceSyncService? CrossDeviceSyncService { get; init; }
        public required ILogger<PlaylistSyncService> Logger { get; init; }
    }

    public Task<PlaylistSyncResult> SyncSpotifyPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        bool force,
        CancellationToken cancellationToken)
    {
        return SyncPlaylistAsync(playlist, preference, trackCandidates: null, force, cancellationToken);
    }

    public sealed record PlaylistMergeSourceInput(
        PlaylistWatchlistDto Playlist,
        PlaylistWatchPreferenceDto? Preference,
        IReadOnlyList<PlaylistTrackCandidate> TrackCandidates);

    public sealed record PlaylistMergeSyncRequest(
        string? PlaylistName,
        string? Description,
        string? ArtworkDataUrl,
        string? ArtworkSource,
        string? ArtworkSourceId,
        string? SourceUsername,
        string? SyncMode,
        bool SyncToPlex,
        bool SyncToJellyfin,
        bool SyncToNavidrome,
        string? ExistingPlexPlaylistId = null,
        string? ExistingJellyfinPlaylistId = null,
        string? ExistingNavidromePlaylistId = null);

    public sealed record PlaylistMergeTargetResult(
        string Target,
        bool Success,
        string Message,
        string? PlaylistId,
        int SyncedTracks);

    public sealed record PlaylistMergeSyncResult(
        bool Success,
        string Message,
        int SourcePlaylists,
        int CandidateTracks,
        int MergedTracks,
        IReadOnlyList<PlaylistMergeTargetResult> Targets);

    public sealed record PlaylistTrackSyncReadiness(
        bool Ready,
        bool Terminal,
        string Message,
        string? Service = null,
        long? LocalTrackId = null,
        string? TargetId = null);

    public sealed record PlaylistTrackAvailability(
        string SourceTrackId,
        bool Eligible,
        bool InLocalLibrary,
        bool InTargetServer,
        long? LocalTrackId = null,
        string? TargetId = null);

    public sealed record PlaylistAvailabilitySummary(
        string? Service,
        int SourceTrackCount,
        int EligibleTrackCount,
        int LocalTrackCount,
        int TargetVisibleTrackCount,
        IReadOnlyList<PlaylistTrackAvailability> Tracks,
        string? ErrorMessage = null);

    public sealed record TargetPlaylistOption(
        string Id,
        string Name,
        int? TrackCount = null);

    public async Task<PlaylistMergeSyncResult> MergeAndSyncPlaylistsAsync(
        IReadOnlyList<PlaylistMergeSourceInput> mergeSources,
        PlaylistMergeSyncRequest request,
        CancellationToken cancellationToken)
    {
        var selectedSources = BuildValidMergeSourceList(mergeSources);
        var validationFailure = ValidateMergeRequest(request, selectedSources.Count);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var (candidateTrackCount, mergedTracks) = await BuildMergedTracksAsync(selectedSources, cancellationToken);

        if (mergedTracks.Count == 0)
        {
            return new PlaylistMergeSyncResult(
                false,
                "No eligible tracks remained after blocked/ignored filtering.",
                selectedSources.Count,
                candidateTrackCount,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        var now = DateTimeOffset.UtcNow;
        var mergedSourceId = Guid.NewGuid().ToString("N");
        var selectedArtworkUrl = await ResolveMergedPlaylistArtworkUrlAsync(
            mergedSourceId,
            request,
            selectedSources,
            cancellationToken);
        var mergedPlaylist = new PlaylistWatchlistDto(
            Id: 0,
            Source: "merged",
            SourceId: mergedSourceId,
            Name: ResolveMergedPlaylistName(request.PlaylistName),
            ImageUrl: selectedArtworkUrl,
            Description: BuildMergedPlaylistDescription(
                request.Description,
                selectedSources.Select(source => source.Playlist),
                request.SourceUsername),
            TrackCount: mergedTracks.Count,
            CreatedAt: now);

        var syncMode = NormalizeSyncMode(request.SyncMode);
        var targets = await SyncMergedPlaylistTargetsAsync(
            request,
            mergedPlaylist,
            mergedTracks,
            syncMode,
            now,
            cancellationToken);

        var anySucceeded = targets.Any(static target => target.Success);
        var allSucceeded = targets.Count > 0 && targets.All(static target => target.Success);
        string message;
        if (allSucceeded)
        {
            message = "Merged playlist synced successfully.";
        }
        else if (anySucceeded)
        {
            message = "Merged playlist synced to some targets. Review target results.";
        }
        else
        {
            message = "Merged playlist sync failed on all selected targets.";
        }
        return new PlaylistMergeSyncResult(
            anySucceeded,
            message,
            selectedSources.Count,
            candidateTrackCount,
            mergedTracks.Count,
            targets);
    }

    private async Task<string?> ResolveMergedPlaylistArtworkUrlAsync(
        string mergedSourceId,
        PlaylistMergeSyncRequest request,
        IReadOnlyList<PlaylistMergeSourceInput> selectedSources,
        CancellationToken cancellationToken)
    {
        var uploadedArtwork = TryParseUploadedArtwork(request.ArtworkDataUrl);
        if (uploadedArtwork is not null)
        {
            return await _playlistVisualService.StoreUploadedVisualAsync(
                "merged",
                mergedSourceId,
                uploadedArtwork.Value.Bytes,
                uploadedArtwork.Value.ContentType,
                cancellationToken);
        }

        var selectedStoredArtwork = ResolveSelectedSourceArtwork(request, selectedSources);
        if (selectedStoredArtwork is not null)
        {
            var bytes = await File.ReadAllBytesAsync(selectedStoredArtwork.FilePath, cancellationToken);
            return await _playlistVisualService.StoreUploadedVisualAsync(
                "merged",
                mergedSourceId,
                bytes,
                selectedStoredArtwork.ContentType,
                cancellationToken);
        }

        return null;
    }

    private PlaylistVisualService.StoredPlaylistVisual? ResolveSelectedSourceArtwork(
        PlaylistMergeSyncRequest request,
        IReadOnlyList<PlaylistMergeSourceInput> selectedSources)
    {
        if (string.IsNullOrWhiteSpace(request.ArtworkSource) || string.IsNullOrWhiteSpace(request.ArtworkSourceId))
        {
            return null;
        }

        var source = request.ArtworkSource.Trim();
        var sourceId = request.ArtworkSourceId.Trim();
        var isSelectedPlaylist = selectedSources.Any(candidate =>
            string.Equals(candidate.Playlist.Source, source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Playlist.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (!isSelectedPlaylist)
        {
            return null;
        }

        var visual = _playlistVisualService.GetStoredVisual(source, sourceId);
        return visual is not null && File.Exists(visual.FilePath) ? visual : null;
    }

    private static UploadedMergeArtwork? TryParseUploadedArtwork(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var trimmed = dataUrl.Trim();
        const string prefix = "data:";
        var commaIndex = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || commaIndex <= prefix.Length)
        {
            return null;
        }

        var metadata = trimmed[prefix.Length..commaIndex];
        var metadataParts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var contentType = metadataParts.FirstOrDefault(static part => part.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        if (!IsAllowedMergeArtworkContentType(contentType)
            || !metadataParts.Any(static part => part.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(trimmed[(commaIndex + 1)..]);
            return bytes.Length is > 0 and <= MaxUploadedMergeArtworkBytes
                ? new UploadedMergeArtwork(bytes, contentType!)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsAllowedMergeArtworkContentType(string? contentType)
    {
        return contentType is not null
            && (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct UploadedMergeArtwork(byte[] Bytes, string ContentType);

    public async Task<IReadOnlyList<TargetPlaylistOption>> GetTargetPlaylistsAsync(
        string target,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target, PlexService, StringComparison.OrdinalIgnoreCase))
        {
            var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
            if (configurationError is not null || plex is null)
            {
                return Array.Empty<TargetPlaylistOption>();
            }

            var playlists = await _plexApiClient.GetPlaylistsAsync(plex.Url, plex.Token, cancellationToken);
            return playlists
                .Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Id)
                    && !string.IsNullOrWhiteSpace(playlist.Title))
                .Select(static playlist => new TargetPlaylistOption(
                    playlist.Id!,
                    playlist.Title!,
                    playlist.TrackCount))
                .OrderBy(static playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (string.Equals(target, JellyfinService, StringComparison.OrdinalIgnoreCase))
        {
            var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
            if (configurationError is not null || jellyfin is null)
            {
                return Array.Empty<TargetPlaylistOption>();
            }

            var playlists = await _jellyfinApiClient.GetPlaylistsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                cancellationToken);
            return playlists
                .Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Id)
                    && !string.IsNullOrWhiteSpace(playlist.Name))
                .Select(static playlist => new TargetPlaylistOption(
                    playlist.Id!,
                    playlist.Name!,
                    null))
                .OrderBy(static playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (string.Equals(target, NavidromeService, StringComparison.OrdinalIgnoreCase))
        {
            var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
            if (configurationError is not null || navidrome is null)
            {
                return Array.Empty<TargetPlaylistOption>();
            }

            var playlists = await _navidromeApiClient.GetPlaylistsAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                cancellationToken);
            return playlists
                .Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Id)
                    && !string.IsNullOrWhiteSpace(playlist.Name))
                .Select(static playlist => new TargetPlaylistOption(
                    playlist.Id,
                    playlist.Name,
                    playlist.TrackCount))
                .OrderBy(static playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return Array.Empty<TargetPlaylistOption>();
    }

    private static List<PlaylistMergeSourceInput> BuildValidMergeSourceList(IReadOnlyList<PlaylistMergeSourceInput> mergeSources)
    {
        return (mergeSources ?? Array.Empty<PlaylistMergeSourceInput>())
            .Where(source => source?.Playlist is not null
                && !string.IsNullOrWhiteSpace(source.Playlist.Source)
                && !string.IsNullOrWhiteSpace(source.Playlist.SourceId))
            .ToList();
    }

    private static PlaylistMergeSyncResult? ValidateMergeRequest(
        PlaylistMergeSyncRequest? request,
        int selectedSourceCount)
    {
        if (request == null)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Merge request is required.",
                0,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        if (selectedSourceCount < 2)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Select at least two monitored playlists to merge.",
                selectedSourceCount,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        if (!request.SyncToPlex && !request.SyncToJellyfin && !request.SyncToNavidrome)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Select at least one destination server (Plex, Jellyfin, or Navidrome).",
                selectedSourceCount,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        return null;
    }

    private async Task<(int CandidateTrackCount, List<SyncTrackSummary> MergedTracks)> BuildMergedTracksAsync(
        IReadOnlyList<PlaylistMergeSourceInput> selectedSources,
        CancellationToken cancellationToken)
    {
        var candidateTrackCount = 0;
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedTracks = new List<SyncTrackSummary>();
        foreach (var source in selectedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = (source.TrackCandidates ?? Array.Empty<PlaylistTrackCandidate>())
                .Select(ToSyncTrackSummary)
                .ToList();
            candidateTrackCount += candidates.Count;

            var filteredTracks = await FilterTracksForSyncAsync(
                source.Playlist,
                source.Preference,
                candidates,
                cancellationToken);

            foreach (var track in filteredTracks)
            {
                var dedupeKey = BuildMergeTrackDedupKey(track);
                if (dedupe.Add(dedupeKey))
                {
                    mergedTracks.Add(track);
                }
            }
        }

        return (candidateTrackCount, mergedTracks);
    }

    private async Task<List<PlaylistMergeTargetResult>> SyncMergedPlaylistTargetsAsync(
        PlaylistMergeSyncRequest request,
        PlaylistWatchlistDto mergedPlaylist,
        IReadOnlyList<SyncTrackSummary> mergedTracks,
        string syncMode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var targets = new List<PlaylistMergeTargetResult>();
        if (request.SyncToPlex)
        {
            var result = await SyncToPlexAsync(
                mergedPlaylist,
                CreateMergedPlaylistPreference(mergedPlaylist, PlexService, syncMode, now),
                mergedTracks,
                request.ExistingPlexPlaylistId,
                cancellationToken);
            targets.Add(new PlaylistMergeTargetResult(
                PlexService,
                result.Success,
                result.Message,
                result.PlaylistId,
                result.SyncedTracks));
        }

        if (request.SyncToJellyfin)
        {
            var result = await SyncToJellyfinAsync(
                mergedPlaylist,
                CreateMergedPlaylistPreference(mergedPlaylist, JellyfinService, syncMode, now),
                mergedTracks,
                request.ExistingJellyfinPlaylistId,
                cancellationToken);
            targets.Add(new PlaylistMergeTargetResult(
                JellyfinService,
                result.Success,
                result.Message,
                result.PlaylistId,
                result.SyncedTracks));
        }

        if (request.SyncToNavidrome)
        {
            var result = await SyncToNavidromeAsync(
                mergedPlaylist,
                CreateMergedPlaylistPreference(mergedPlaylist, NavidromeService, syncMode, now),
                mergedTracks,
                request.ExistingNavidromePlaylistId,
                cancellationToken);
            targets.Add(new PlaylistMergeTargetResult(
                NavidromeService,
                result.Success,
                result.Message,
                result.PlaylistId,
                result.SyncedTracks));
        }

        return targets;
    }

    private static PlaylistWatchPreferenceDto CreateMergedPlaylistPreference(
        PlaylistWatchlistDto mergedPlaylist,
        string service,
        string syncMode,
        DateTimeOffset now)
    {
        return new PlaylistWatchPreferenceDto(
            Source: "merged",
            SourceId: mergedPlaylist.SourceId,
            DestinationFolderId: null,
            Service: service,
            SyncTargets: [service],
            PreferredEngine: null,
            DownloadEngineOrder: null,
            DownloadVariantMode: null,
            SyncMode: syncMode,
            UpdateArtwork: true,
            ReuseSavedArtwork: false,
            CreatedAt: now,
            UpdatedAt: now);
    }

    public async Task<PlaylistSyncResult> SyncPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        bool force,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return PlaylistSyncResult.Failed(PlaylistNotAvailableMessage);
        }

        var services = await ResolveTargetServicesAsync(preference, cancellationToken);
        if (services.Count == 0)
        {
            return PlaylistSyncResult.Failed(NoTargetServerSelectedMessage);
        }

        if (force)
        {
            foreach (var service in services)
            {
                await _mediaServerRefreshService.RefreshAsync(service, cancellationToken);
            }
        }

        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return PlaylistSyncResult.Failed(loadResult.ErrorMessage);
        }

        var tracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            loadResult.Tracks,
            cancellationToken);
        if (tracks.Count == 0)
        {
            return PlaylistSyncResult.Failed("No eligible tracks after blocked/ignored filtering.");
        }

        var result = await SyncPlaylistToTargetsAsync(
            services,
            playlist,
            preference,
            tracks,
            cancellationToken);

        await PublishWatchlistSyncUpdatedAsync(playlist, result, cancellationToken);
        return result;
    }

    public async Task<PlaylistSyncResult> SyncAvailablePlaylistTracksAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        bool force,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return PlaylistSyncResult.Failed(PlaylistNotAvailableMessage);
        }

        var services = await ResolveTargetServicesAsync(preference, cancellationToken);
        if (services.Count == 0)
        {
            return PlaylistSyncResult.Failed(NoTargetServerSelectedMessage);
        }

        if (force)
        {
            foreach (var service in services)
            {
                await _mediaServerRefreshService.RefreshAsync(service, cancellationToken);
            }
        }

        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return PlaylistSyncResult.Failed(loadResult.ErrorMessage);
        }

        var eligibleTracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            loadResult.Tracks,
            cancellationToken);
        if (eligibleTracks.Count == 0)
        {
            return PlaylistSyncResult.Failed("No eligible tracks after blocked/ignored filtering.");
        }

        var availableTrackRows = new List<(SyncTrackSummary Track, long LocalTrackId)>(eligibleTracks.Count);
        foreach (var track in eligibleTracks)
        {
            var (identitySource, identityTrackId) = ResolveTrackIdentity(playlist.Source, track);
            var identity = await _libraryRepository.ResolveLocalTrackIdentityAsync(
                new LibraryRepository.LibraryExistenceInput(
                    track.Isrc,
                    track.Name,
                    track.Artists,
                    track.DurationMs,
                    identitySource,
                    identityTrackId,
                    track.Album),
                cancellationToken: cancellationToken);
            var identityStatus = identity.IsAmbiguous
                ? "review"
                : identity.LocalTrackId.HasValue ? "identity_verified" : "missing";
            await _libraryRepository.UpdatePlaylistWatchTrackVerificationAsync(
                playlist.Source,
                playlist.SourceId,
                new PlaylistWatchTrackVerification(
                    track.SourceTrackId,
                    identity.LocalTrackId,
                    identityStatus,
                    identity.Reason),
                cancellationToken);
            if (identity.LocalTrackId.HasValue && !identity.IsAmbiguous)
            {
                availableTrackRows.Add((track, identity.LocalTrackId.Value));
            }
        }

        if (availableTrackRows.Count == 0)
        {
            return new PlaylistSyncResult(
                false,
                "No eligible playlist tracks are visible in the DeezSpoTag library yet.",
                SourceTracks: eligibleTracks.Count,
                MissingTracks: eligibleTracks.Count);
        }

        var availableTracks = availableTrackRows.Select(static row => row.Track).ToList();
        if (availableTracks.Count == 0)
        {
            return new PlaylistSyncResult(
                false,
                "No eligible playlist tracks are visible in the target server yet.",
                SourceTracks: eligibleTracks.Count,
                LocalMatches: availableTrackRows.Count,
                MissingTracks: eligibleTracks.Count);
        }

        var result = await SyncPlaylistToTargetsAsync(
            services,
            playlist,
            preference,
            availableTracks,
            cancellationToken);

        if (!result.Success)
        {
            return result;
        }

        var unavailableCount = Math.Max(0, eligibleTracks.Count - availableTracks.Count);
        if (unavailableCount == 0)
        {
            await PublishWatchlistSyncUpdatedAsync(playlist, result, cancellationToken);
            return result;
        }

        var partialResult = result with
        {
            Message = string.Concat(
                result.Message,
                " ",
                unavailableCount.ToString(CultureInfo.InvariantCulture),
                " eligible track(s) are still missing and were left for download/retry."),
            SourceTracks = eligibleTracks.Count,
            MissingTracks = unavailableCount + result.MissingTracks
        };
        await PublishWatchlistSyncUpdatedAsync(playlist, partialResult, cancellationToken);
        return partialResult;
    }

    private async Task<PlaylistSyncResult> SyncPlaylistToTargetsAsync(
        IReadOnlyList<string> services,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var results = new List<(string Service, PlaylistSyncResult Result)>(services.Count);
        foreach (var service in services)
        {
            var result = await SyncPlaylistToTargetAsync(service, playlist, preference, tracks, cancellationToken);
            results.Add((service, result));
        }

        return CombinePlaylistSyncTargetResults(results);
    }

    private async Task<PlaylistSyncResult> SyncPlaylistToTargetAsync(
        string service,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        try
        {
            return service switch
            {
                PlexService => await SyncToPlexAsync(playlist, preference, tracks, ResolveExistingTargetPlaylistId(preference, PlexService), cancellationToken),
                JellyfinService => await SyncToJellyfinAsync(playlist, preference, tracks, ResolveExistingTargetPlaylistId(preference, JellyfinService), cancellationToken),
                NavidromeService => await SyncToNavidromeAsync(playlist, preference, tracks, ResolveExistingTargetPlaylistId(preference, NavidromeService), cancellationToken),
                _ => PlaylistSyncResult.Failed(UnsupportedPlaylistSyncTargetMessage)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Playlist sync target {Target} failed for {Source}:{SourceId}; continuing with remaining enabled targets.",
                FormatTargetServiceLabel(service),
                SafeLog(playlist.Source),
                SafeLog(playlist.SourceId));
            return PlaylistSyncResult.Failed($"{FormatTargetServiceLabel(service)} sync failed: {ex.Message}");
        }
    }

    internal static PlaylistSyncResult CombinePlaylistSyncTargetResults(
        IReadOnlyList<(string Service, PlaylistSyncResult Result)> results)
    {
        if (results.Count == 0)
        {
            return PlaylistSyncResult.Failed(NoTargetServerSelectedMessage);
        }

        if (results.Count == 1)
        {
            return results[0].Result;
        }

        var successfulResults = results.Where(item => item.Result.Success).ToList();
        var message = string.Join(
            " ",
            results.Select(item => string.Concat(
                FormatTargetServiceLabel(item.Service),
                ": ",
                item.Result.Message)));

        if (successfulResults.Count != results.Count)
        {
            var first = results.First(item => !item.Result.Success).Result;
            return first with
            {
                Success = false,
                Message = message,
                SyncedTracks = successfulResults.Sum(item => item.Result.SyncedTracks),
                TargetMatches = successfulResults.Sum(item => item.Result.TargetMatches),
                MetadataMatches = successfulResults.Sum(item => item.Result.MetadataMatches),
                SearchMatches = successfulResults.Sum(item => item.Result.SearchMatches)
            };
        }

        var aggregate = successfulResults[0].Result;
        return aggregate with
        {
            Success = true,
            Message = message,
            SyncedTracks = successfulResults.Sum(item => item.Result.SyncedTracks),
            TargetMatches = successfulResults.Sum(item => item.Result.TargetMatches),
            MetadataMatches = successfulResults.Sum(item => item.Result.MetadataMatches),
            SearchMatches = successfulResults.Sum(item => item.Result.SearchMatches)
        };
    }

    private static string FormatTargetServiceLabel(string service)
        => NormalizeService(service) switch
        {
            PlexService => "Plex",
            JellyfinService => "Jellyfin",
            NavidromeService => "Navidrome",
            _ => service
        };

    private async Task PublishWatchlistSyncUpdatedAsync(
        PlaylistWatchlistDto playlist,
        PlaylistSyncResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Success || _crossDeviceSyncService is null)
        {
            return;
        }

        await _crossDeviceSyncService.PublishWatchlistUpdatedAsync(
            playlist.Source,
            playlist.SourceId,
            "playlist_sync_completed",
            cancellationToken);
    }

    public async Task<PlaylistAvailabilitySummary> GetPlaylistAvailabilityAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return new PlaylistAvailabilitySummary(null, 0, 0, 0, 0, Array.Empty<PlaylistTrackAvailability>(), PlaylistNotAvailableMessage);
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return new PlaylistAvailabilitySummary(service, 0, 0, 0, 0, Array.Empty<PlaylistTrackAvailability>(), loadResult.ErrorMessage);
        }

        var allTracks = loadResult.Tracks
            .Where(static track => !string.IsNullOrWhiteSpace(track.SourceTrackId))
            .ToList();
        var eligibleTracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            allTracks,
            cancellationToken);
        var eligibleIds = eligibleTracks
            .Select(static track => track.SourceTrackId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableTrackRows = await ResolveAvailableTrackRowsAsync(
            playlist.Source,
            eligibleTracks,
            cancellationToken);
        var localTrackIdBySourceId = availableTrackRows
            .Where(static row => !string.IsNullOrWhiteSpace(row.Track.SourceTrackId))
            .GroupBy(static row => row.Track.SourceTrackId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().LocalTrackId, StringComparer.OrdinalIgnoreCase);
        var targetIdBySourceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(service, PlexService, StringComparison.OrdinalIgnoreCase) && availableTrackRows.Count > 0)
        {
            var ratingKeys = await _libraryRepository.GetPlexRatingKeysByTrackIdsAsync(
                availableTrackRows
                    .Select(static row => row.LocalTrackId)
                    .Where(static id => id > 0)
                    .Distinct()
                    .ToList(),
                cancellationToken);
            foreach (var row in availableTrackRows)
            {
                if (!string.IsNullOrWhiteSpace(row.Track.SourceTrackId)
                    && ratingKeys.TryGetValue(row.LocalTrackId, out var ratingKey)
                    && !string.IsNullOrWhiteSpace(ratingKey))
                {
                    targetIdBySourceId[row.Track.SourceTrackId] = ratingKey;
                }
            }
        }

        var availability = allTracks
            .Select(track =>
            {
                var localTrackId = localTrackIdBySourceId.TryGetValue(track.SourceTrackId, out var resolvedLocalTrackId)
                    ? resolvedLocalTrackId
                    : (long?)null;
                var targetId = targetIdBySourceId.TryGetValue(track.SourceTrackId, out var resolvedTargetId)
                    ? resolvedTargetId
                    : null;
                return new PlaylistTrackAvailability(
                    track.SourceTrackId,
                    eligibleIds.Contains(track.SourceTrackId),
                    localTrackId.HasValue,
                    !string.IsNullOrWhiteSpace(targetId),
                    localTrackId,
                    targetId);
            })
            .ToList();

        return new PlaylistAvailabilitySummary(
            service,
            allTracks.Count,
            eligibleTracks.Count,
            localTrackIdBySourceId.Count,
            targetIdBySourceId.Count,
            availability);
    }

    private async Task<List<(SyncTrackSummary Track, long LocalTrackId)>> ResolveAvailableTrackRowsAsync(
        string source,
        IReadOnlyList<SyncTrackSummary> eligibleTracks,
        CancellationToken cancellationToken)
    {
        var availableTrackRows = new List<(SyncTrackSummary Track, long LocalTrackId)>(eligibleTracks.Count);
        var localTrackIds = await ResolveLocalTrackIdsAsync(source, eligibleTracks, cancellationToken);
        for (var index = 0; index < eligibleTracks.Count; index++)
        {
            if (localTrackIds[index] > 0)
            {
                availableTrackRows.Add((eligibleTracks[index], localTrackIds[index]));
            }
        }

        return availableTrackRows;
    }

    public async Task<PlaylistSyncResult> SyncPlaylistArtworkOnlyAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return PlaylistSyncResult.Failed(PlaylistNotAvailableMessage);
        }

        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return new PlaylistSyncResult(true, "Playlist artwork sync disabled.");
        }

        var services = await ResolveTargetServicesAsync(preference, cancellationToken);
        if (services.Count == 0)
        {
            return PlaylistSyncResult.Failed(NoTargetServerSelectedMessage);
        }

        var results = new List<(string Service, PlaylistSyncResult Result)>(services.Count);
        foreach (var service in services)
        {
            var result = service switch
            {
                PlexService => await SyncPlexPlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
                JellyfinService => await SyncJellyfinPlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
                NavidromeService => await SyncNavidromePlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
                _ => PlaylistSyncResult.Failed(UnsupportedPlaylistSyncTargetMessage)
            };
            results.Add((service, result));
        }

        return CombinePlaylistSyncTargetResults(results);
    }

    public async Task<PlaylistTrackSyncReadiness> CheckTrackReadyForAutomaticSyncAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        PlaylistTrackCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (playlist == null || candidate == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist or track candidate is unavailable.");
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        if (string.IsNullOrWhiteSpace(service))
        {
            return new PlaylistTrackSyncReadiness(false, true, NoTargetServerSelectedMessage);
        }

        if (string.Equals(service, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist sync target is disabled.", service);
        }

        var track = ToSyncTrackSummary(candidate);
        var localTrackId = await ResolveLocalTrackIdAsync(playlist.Source, track, cancellationToken);
        if (!localTrackId.HasValue)
        {
            return new PlaylistTrackSyncReadiness(
                false,
                false,
                "Track is not visible in the DeezSpoTag library yet.",
                service);
        }

        return service switch
        {
            PlexService => await CheckPlexTrackReadyAsync(localTrackId.Value, track, cancellationToken),
            JellyfinService => await CheckJellyfinTrackReadyAsync(localTrackId.Value, track, cancellationToken),
            NavidromeService => await CheckNavidromeTrackReadyAsync(localTrackId.Value, track, cancellationToken),
            _ => new PlaylistTrackSyncReadiness(false, true, UnsupportedPlaylistSyncTargetMessage, service, localTrackId)
        };
    }

    private async Task<PlaylistTrackSyncReadiness> CheckTargetTrackReadyAsync(
        PlaylistWatchPreferenceDto? preference,
        SyncTrackSummary track,
        long localTrackId,
        CancellationToken cancellationToken)
    {
        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        if (string.IsNullOrWhiteSpace(service))
        {
            return new PlaylistTrackSyncReadiness(false, true, NoTargetServerSelectedMessage);
        }

        if (string.Equals(service, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist sync target is disabled.", service, localTrackId);
        }

        return service switch
        {
            PlexService => await CheckPlexTrackReadyAsync(localTrackId, track, cancellationToken),
            JellyfinService => await CheckJellyfinTrackReadyAsync(localTrackId, track, cancellationToken),
            NavidromeService => await CheckNavidromeTrackReadyAsync(localTrackId, track, cancellationToken),
            _ => new PlaylistTrackSyncReadiness(false, true, UnsupportedPlaylistSyncTargetMessage, service, localTrackId)
        };
    }

    private async Task<(IReadOnlyList<SyncTrackSummary> Tracks, string? ErrorMessage)> LoadTracksForSyncAsync(
        PlaylistWatchlistDto playlist,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        CancellationToken cancellationToken)
    {
        var source = NormalizeSource(playlist.Source);
        if (trackCandidates is { Count: > 0 })
        {
            var candidates = PlaylistCandidateContract.ResolvableCandidates(source, trackCandidates);
            return (candidates.Select(ToSyncTrackSummary).ToList(), null);
        }

        if (string.Equals(source, SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await _spotifyMetadataService.FetchPlaylistSnapshotAsync(playlist.SourceId, cancellationToken);
            if (snapshot != null && snapshot.Tracks.Count > 0)
            {
                return (snapshot.Tracks.Select(ToSyncTrackSummary).ToList(), null);
            }

            return (Array.Empty<SyncTrackSummary>(), "Spotify playlist could not be loaded.");
        }

        return (Array.Empty<SyncTrackSummary>(), "Track candidates are unavailable for this source. Open playlist settings once and retry sync.");
    }

    private async Task<PlaylistSyncResult> SyncToPlexAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        string? existingPlaylistId,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (plex == null)
        {
            return PlaylistSyncResult.Failed(PlexNotConfiguredMessage);
        }

        var playlistName = ResolvePlaylistName(playlist);
        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var matchSummary = await ResolvePlexRatingKeysAsync(plex, tracks, orderedTrackIds, cancellationToken);
        if (matchSummary.TargetIds.Count == 0)
        {
            _logger.LogWarning(
                "No Plex matches found for playlist {Source}:{SourceId}. sourceTracks={SourceTracks}, localMatches={LocalMatches}, missingTracks={MissingTracks}",
                playlist.Source,
                playlist.SourceId,
                matchSummary.SourceTracks,
                matchSummary.LocalMatches,
                matchSummary.MissingTracks);
            return BuildFailedResult(
                BuildSyncMessage("No Plex matches found for this playlist.", matchSummary),
                matchSummary);
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingPlaylistId))
        {
            await PersistPlexMembershipAsync(
                playlist,
                plex,
                existingPlaylistId,
                matchSummary.Memberships,
                cancellationToken);
        }

        var playlistId = await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            plex.MachineIdentifier,
            playlistName,
            matchSummary.TargetIds,
            options: new PlexApiClient.PlaylistUpsertOptions(
                AppendMissingOnly: appendMissingOnly,
                ExistingPlaylistId: string.IsNullOrWhiteSpace(existingPlaylistId)
                    ? null
                    : existingPlaylistId.Trim()),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BuildFailedResult(
                BuildSyncMessage("Failed to create or update Plex playlist.", matchSummary),
                matchSummary);
        }

        await _plexApiClient.UpdatePlaylistMetadataAsync(
            plex.Url,
            plex.Token,
            playlistId,
            playlistName,
            playlist.Description,
            cancellationToken);

        await SyncPlexPlaylistArtworkAsync(plex, playlist, preference, playlistId, cancellationToken);
        await PersistTargetPlaylistBindingAsync(playlist, preference, PlexService, playlistId, cancellationToken);
        var verifiedMemberships = await PersistPlexMembershipAsync(
            playlist,
            plex,
            playlistId,
            matchSummary.Memberships,
            cancellationToken);

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        var verifiedSummary = matchSummary with
        {
            TargetMatches = verifiedMemberships.Count,
            MissingTracks = Math.Max(0, matchSummary.SourceTracks - verifiedMemberships.Count)
        };
        return BuildSuccessResult(
            BuildSyncMessage($"Playlist synced ({modeLabel}).", verifiedSummary),
            playlistId,
            verifiedSummary,
            verifiedMemberships.Count);
    }

    private async Task<PlaylistSyncResult> SyncToJellyfinAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        string? existingPlaylistId,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (jellyfin == null)
        {
            return PlaylistSyncResult.Failed(JellyfinNotConfiguredMessage);
        }

        var playlistName = ResolvePlaylistName(playlist);
        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var jellyfinMatches = await ResolveJellyfinItemIdsAsync(jellyfin, tracks, orderedTrackIds, cancellationToken);
        var itemIds = jellyfinMatches.Select(static item => item.TargetItemId).ToList();
        var matchSummary = new SyncMatchSummary(
            itemIds,
            jellyfinMatches,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: itemIds.Count,
            MissingTracks: Math.Max(0, tracks.Count - itemIds.Count),
            MetadataMatches: 0,
            SearchMatches: itemIds.Count);
        if (itemIds.Count == 0)
        {
            _logger.LogWarning("No Jellyfin matches found for playlist {Source}:{SourceId}.", playlist.Source, playlist.SourceId);
            return BuildFailedResult(
                BuildSyncMessage("No Jellyfin matches found for this playlist.", matchSummary),
                matchSummary);
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingPlaylistId))
        {
            await PersistJellyfinMembershipAsync(
                playlist,
                jellyfin,
                existingPlaylistId,
                jellyfinMatches,
                cancellationToken);
        }

        var playlistId = string.IsNullOrWhiteSpace(existingPlaylistId)
            ? await _jellyfinApiClient.FindPlaylistIdByNameAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistName,
                cancellationToken)
            : existingPlaylistId.Trim();

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            var createdPlaylistId = await _jellyfinApiClient.CreatePlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistName,
                itemIds,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(createdPlaylistId))
            {
                return BuildFailedResult(
                    BuildSyncMessage("Failed to create Jellyfin playlist.", matchSummary),
                    matchSummary);
            }

            playlistId = createdPlaylistId;
        }
        else
        {
            var syncItemsResult = await SyncExistingJellyfinPlaylistItemsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                itemIds,
                appendMissingOnly,
                cancellationToken);
            if (!syncItemsResult.Success)
            {
                return BuildFailedResult(
                    BuildSyncMessage(syncItemsResult.ErrorMessage ?? "Failed to sync Jellyfin playlist.", matchSummary),
                    matchSummary);
            }
        }

        var metadataSynced = await SyncJellyfinPlaylistMetadataAsync(
            jellyfin,
            playlist,
            playlistId,
            cancellationToken);
        var artworkSynced = await SyncJellyfinPlaylistArtworkAsync(jellyfin, playlist, preference, playlistId, cancellationToken);
        await PersistTargetPlaylistBindingAsync(playlist, preference, JellyfinService, playlistId, cancellationToken);
        var verifiedMemberships = await PersistJellyfinMembershipAsync(
            playlist,
            jellyfin,
            playlistId,
            jellyfinMatches,
            cancellationToken);

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        var verifiedSummary = matchSummary with
        {
            TargetIds = itemIds,
            TargetMatches = verifiedMemberships.Count,
            MissingTracks = Math.Max(0, matchSummary.SourceTracks - verifiedMemberships.Count)
        };
        var fullSyncIssues = BuildJellyfinFullSyncIssues(metadataSynced, artworkSynced, preference);
        if (fullSyncIssues.Count > 0)
        {
            return BuildPartialResult(
                BuildSyncMessage(
                    $"Jellyfin playlist tracks synced ({modeLabel}), but {string.Join(" ", fullSyncIssues)}",
                    verifiedSummary),
                playlistId,
                verifiedSummary,
                verifiedMemberships.Count);
        }

        return BuildSuccessResult(
            BuildSyncMessage($"Playlist synced ({modeLabel}).", verifiedSummary),
            playlistId,
            verifiedSummary,
            verifiedMemberships.Count);
    }

    private async Task<PlaylistSyncResult> SyncToNavidromeAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        string? existingPlaylistId,
        CancellationToken cancellationToken)
    {
        var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
        if (configurationError is not null)
        {
            return configurationError;
        }

        if (navidrome is null)
        {
            return PlaylistSyncResult.Failed(NavidromeNotConfiguredMessage);
        }

        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var navidromeMatches = await ResolveNavidromeItemIdsAsync(navidrome, tracks, orderedTrackIds, cancellationToken);
        var itemIds = navidromeMatches.Select(static item => item.TargetItemId).ToList();
        var matchSummary = new SyncMatchSummary(
            itemIds,
            navidromeMatches,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: itemIds.Count,
            MissingTracks: Math.Max(0, tracks.Count - itemIds.Count),
            MetadataMatches: 0,
            SearchMatches: itemIds.Count);
        if (itemIds.Count == 0)
        {
            return BuildFailedResult(
                BuildSyncMessage("No Navidrome matches found for this playlist.", matchSummary),
                matchSummary);
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        var playlistId = await _navidromeApiClient.CreateOrUpdatePlaylistAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            ResolvePlaylistName(playlist),
            itemIds,
            existingPlaylistId,
            appendMissingOnly,
            cancellationToken,
            playlist.Description);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BuildFailedResult(
                BuildSyncMessage("Failed to create or update the Navidrome playlist.", matchSummary),
                matchSummary);
        }

        var metadataSynced = await SyncNavidromePlaylistMetadataAsync(
            navidrome,
            playlist,
            playlistId,
            cancellationToken);
        var artworkSynced = await SyncNavidromePlaylistArtworkAsync(navidrome, playlist, preference, playlistId, cancellationToken);
        await PersistTargetPlaylistBindingAsync(playlist, preference, NavidromeService, playlistId, cancellationToken);
        var verifiedMemberships = await PersistNavidromeMembershipAsync(
            playlist,
            navidrome,
            playlistId,
            navidromeMatches,
            cancellationToken);
        var verifiedSummary = matchSummary with
        {
            TargetMatches = verifiedMemberships.Count,
            MissingTracks = Math.Max(0, matchSummary.SourceTracks - verifiedMemberships.Count)
        };
        var modeLabel = appendMissingOnly ? "append" : "mirror";
        var fullSyncIssues = BuildNavidromeFullSyncIssues(metadataSynced, artworkSynced, preference);
        if (fullSyncIssues.Count > 0)
        {
            return BuildPartialResult(
                BuildSyncMessage(
                    $"Navidrome playlist tracks synced ({modeLabel}), but {string.Join(" ", fullSyncIssues)}",
                    verifiedSummary),
                playlistId,
                verifiedSummary,
                verifiedMemberships.Count);
        }

        return BuildSuccessResult(
            BuildSyncMessage($"Playlist synced to Navidrome ({modeLabel}).", verifiedSummary),
            playlistId,
            verifiedSummary,
            verifiedMemberships.Count);
    }

    private async Task<List<PlaylistWatchTargetMembership>> PersistPlexMembershipAsync(
        PlaylistWatchlistDto playlist,
        PlexConnection plex,
        string playlistId,
        IReadOnlyCollection<PlaylistWatchTargetMembership> expectedMemberships,
        CancellationToken cancellationToken)
    {
        var actualTargetIds = (await _plexApiClient.GetPlaylistItemsAsync(
                plex.Url,
                plex.Token,
                playlistId,
                cancellationToken))
            .Select(static item => item.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verified = expectedMemberships
            .Where(item => actualTargetIds.Contains(item.TargetItemId))
            .ToList();
        await _libraryRepository.ReplacePlaylistWatchTargetMembershipAsync(
            playlist.Source,
            playlist.SourceId,
            PlexService,
            playlistId,
            verified,
            cancellationToken);
        return verified;
    }

    private async Task<List<PlaylistWatchTargetMembership>> PersistJellyfinMembershipAsync(
        PlaylistWatchlistDto playlist,
        JellyfinConnection jellyfin,
        string playlistId,
        IReadOnlyCollection<PlaylistWatchTargetMembership> expectedMemberships,
        CancellationToken cancellationToken)
    {
        var actualTargetIds = (await _jellyfinApiClient.GetPlaylistEntriesAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                cancellationToken))
            .Select(static item => item.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verified = expectedMemberships
            .Where(item => actualTargetIds.Contains(item.TargetItemId))
            .ToList();
        await _libraryRepository.ReplacePlaylistWatchTargetMembershipAsync(
            playlist.Source,
            playlist.SourceId,
            JellyfinService,
            playlistId,
            verified,
            cancellationToken);
        return verified;
    }

    private async Task<List<PlaylistWatchTargetMembership>> PersistNavidromeMembershipAsync(
        PlaylistWatchlistDto playlist,
        NavidromeConnection navidrome,
        string playlistId,
        IReadOnlyCollection<PlaylistWatchTargetMembership> expectedMemberships,
        CancellationToken cancellationToken)
    {
        var actualTargetIds = (await _navidromeApiClient.GetPlaylistEntriesAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                playlistId,
                cancellationToken))
            .Select(static item => item.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verified = expectedMemberships
            .Where(item => actualTargetIds.Contains(item.TargetItemId))
            .ToList();
        await _libraryRepository.ReplacePlaylistWatchTargetMembershipAsync(
            playlist.Source,
            playlist.SourceId,
            NavidromeService,
            playlistId,
            verified,
            cancellationToken);
        return verified;
    }

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> SyncExistingJellyfinPlaylistItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        bool appendMissingOnly,
        CancellationToken cancellationToken)
    {
        var entries = await _jellyfinApiClient.GetPlaylistEntriesAsync(
            url,
            apiKey,
            userId,
            playlistId,
            cancellationToken);
        if (appendMissingOnly)
        {
            return await AppendMissingJellyfinItemsAsync(url, apiKey, userId, playlistId, itemIds, entries, cancellationToken);
        }

        return await ReplaceJellyfinPlaylistItemsAsync(url, apiKey, userId, playlistId, itemIds, entries, cancellationToken);
    }

    private static string? ResolveExistingTargetPlaylistId(PlaylistWatchPreferenceDto? preference, string service)
    {
        if (preference is null || string.IsNullOrWhiteSpace(service))
        {
            return null;
        }

        return service.Trim().ToLowerInvariant() switch
        {
            PlexService => NormalizeExistingTargetPlaylistId(preference.PlexPlaylistId),
            JellyfinService => NormalizeExistingTargetPlaylistId(preference.JellyfinPlaylistId),
            NavidromeService => NormalizeExistingTargetPlaylistId(preference.NavidromePlaylistId),
            _ => null
        };
    }

    private static string? NormalizeExistingTargetPlaylistId(string? playlistId)
    {
        var normalized = (playlistId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private async Task PersistTargetPlaylistBindingAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string service,
        string? playlistId,
        CancellationToken cancellationToken)
    {
        if (preference is null
            || string.IsNullOrWhiteSpace(playlistId)
            || !string.Equals(playlist.Source, preference.Source, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(playlist.SourceId, preference.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _libraryRepository.UpdatePlaylistWatchTargetPlaylistIdAsync(
            playlist.Source,
            playlist.SourceId,
            service,
            playlistId,
            cancellationToken);
    }

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> AppendMissingJellyfinItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        IReadOnlyList<JellyfinPlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        var existingItemIds = entries
            .Select(static entry => entry.ItemId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = itemIds
            .Where(trackId => !existingItemIds.Contains(trackId))
            .ToList();
        if (pending.Count == 0)
        {
            return (true, null, 0);
        }

        var appended = await _jellyfinApiClient.AddPlaylistItemsAsync(
            url,
            apiKey,
            userId,
            playlistId,
            pending,
            cancellationToken);
        if (!appended)
        {
            return (false, "Failed to append tracks to Jellyfin playlist.", 0);
        }

        return (true, null, pending.Count);
    }

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> ReplaceJellyfinPlaylistItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        List<JellyfinPlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count > 0)
        {
            var cleared = await _jellyfinApiClient.RemovePlaylistEntriesAsync(
                url,
                apiKey,
                userId,
                playlistId,
                entries.Select(static entry => entry.PlaylistEntryId).ToList(),
                cancellationToken);
            if (!cleared)
            {
                return (false, "Failed to clear existing Jellyfin playlist items.", 0);
            }
        }

        var added = await _jellyfinApiClient.AddPlaylistItemsAsync(
            url,
            apiKey,
            userId,
            playlistId,
            itemIds,
            cancellationToken);
        if (!added)
        {
            return (false, "Failed to add tracks to Jellyfin playlist.", 0);
        }

        return (true, null, itemIds.Count);
    }

    private async Task<PlaylistSyncResult> SyncPlexPlaylistArtworkOnlyAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (plex == null)
        {
            return PlaylistSyncResult.Failed(PlexNotConfiguredMessage);
        }

        var playlistName = ResolvePlaylistName(playlist);
        var playlists = await _plexApiClient.GetPlaylistsAsync(plex.Url, plex.Token, cancellationToken);
        var targetPlaylist = playlists.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.Id)
            && string.Equals(item.Title, playlistName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(targetPlaylist?.Id))
        {
            return PlaylistSyncResult.Failed("Target Plex playlist was not found.");
        }

        await SyncPlexPlaylistArtworkAsync(
            plex,
            playlist,
            preference,
            targetPlaylist.Id,
            cancellationToken);

        return new PlaylistSyncResult(true, "Playlist artwork synced.", targetPlaylist.Id);
    }

    private async Task<PlaylistSyncResult> SyncJellyfinPlaylistArtworkOnlyAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (jellyfin == null)
        {
            return PlaylistSyncResult.Failed(JellyfinNotConfiguredMessage);
        }

        var playlistId = ResolveExistingTargetPlaylistId(preference, JellyfinService);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = await _jellyfinApiClient.FindPlaylistIdByNameAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                ResolvePlaylistName(playlist),
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return PlaylistSyncResult.Failed("Target Jellyfin playlist was not found.");
        }

        var updated = await SyncJellyfinPlaylistArtworkAsync(
            jellyfin,
            playlist,
            preference,
            playlistId,
            cancellationToken);

        return updated
            ? new PlaylistSyncResult(true, "Playlist artwork synced.", playlistId)
            : new PlaylistSyncResult(false, "Failed to sync Jellyfin playlist artwork.", playlistId);
    }

    private async Task<PlaylistSyncResult> SyncNavidromePlaylistArtworkOnlyAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (navidrome == null)
        {
            return PlaylistSyncResult.Failed(NavidromeNotConfiguredMessage);
        }

        var playlistId = ResolveExistingTargetPlaylistId(preference, NavidromeService);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = await _navidromeApiClient.FindPlaylistIdByNameAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                ResolvePlaylistName(playlist),
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return PlaylistSyncResult.Failed("Target Navidrome playlist was not found.");
        }

        var updated = await SyncNavidromePlaylistArtworkAsync(
            navidrome,
            playlist,
            preference,
            playlistId,
            cancellationToken);

        return updated
            ? new PlaylistSyncResult(true, "Playlist artwork synced.", playlistId)
            : new PlaylistSyncResult(false, "Failed to sync Navidrome playlist artwork.", playlistId);
    }

    private PlaylistVisualService.StoredPlaylistVisual? ResolveStoredVisualForArtworkSync(
        PlaylistWatchlistDto playlist,
        string? managedImageUrl,
        bool reuseSavedArtwork)
    {
        var visual = _playlistVisualService.GetStoredVisual(playlist.Source, playlist.SourceId);
        if (visual == null || !File.Exists(visual.FilePath))
        {
            return null;
        }

        if (reuseSavedArtwork || PlaylistVisualService.IsManagedVisualUrl(managedImageUrl))
        {
            return visual;
        }

        return null;
    }

    private PlaylistVisualService.StoredPlaylistVisual? ResolveCachedVisualForArtworkSync(PlaylistWatchlistDto playlist)
    {
        var visual = _playlistVisualService.GetStoredVisual(playlist.Source, playlist.SourceId);
        return visual is not null && File.Exists(visual.FilePath) ? visual : null;
    }

    private PlaylistVisualService.StoredPlaylistVisual? ResolveJellyfinVisualFromPlaylistImage(PlaylistWatchlistDto playlist)
    {
        var urlVisual = _playlistVisualService.GetStoredVisualFromManagedUrl(playlist.Source, playlist.SourceId, playlist.ImageUrl);
        if (urlVisual != null && File.Exists(urlVisual.FilePath))
        {
            return urlVisual;
        }

        return ResolveCachedVisualForArtworkSync(playlist);
    }

    private PlaylistVisualService.StoredPlaylistVisual? ResolveJellyfinVisualFromManagedImage(
        PlaylistWatchlistDto playlist,
        string? managedImageUrl,
        bool reuseSavedArtwork)
    {
        var urlVisual = _playlistVisualService.GetStoredVisualFromManagedUrl(playlist.Source, playlist.SourceId, managedImageUrl);
        if (urlVisual != null && File.Exists(urlVisual.FilePath))
        {
            return urlVisual;
        }

        return ResolveStoredVisualForArtworkSync(playlist, managedImageUrl, reuseSavedArtwork);
    }

    private static bool ShouldSyncPlaylistArtwork(PlaylistWatchPreferenceDto? preference)
    {
        return preference == null || preference.UpdateArtwork || preference.ReuseSavedArtwork;
    }

    private async Task SyncPlexPlaylistArtworkAsync(
        PlexConnection plex,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return;
        }

        var cachedVisual = ResolveCachedVisualForArtworkSync(playlist);
        if (cachedVisual != null)
        {
            await _plexApiClient.UpdatePlaylistPosterFromFileAsync(
                plex.Url,
                plex.Token,
                playlistId,
                cachedVisual.FilePath,
                cachedVisual.ContentType,
                cancellationToken);
            return;
        }

        var managedImageUrl = await _playlistVisualService.ResolveManagedVisualUrlAsync(
            playlist.Source,
            playlist.SourceId,
            playlist.Name,
            playlist.ImageUrl,
            preference?.ReuseSavedArtwork == true,
            cancellationToken);
        var visual = ResolveStoredVisualForArtworkSync(
            playlist,
            managedImageUrl,
            preference?.ReuseSavedArtwork == true);
        if (visual != null && File.Exists(visual.FilePath))
        {
            await _plexApiClient.UpdatePlaylistPosterFromFileAsync(
                plex.Url,
                plex.Token,
                playlistId,
                visual.FilePath,
                visual.ContentType,
                cancellationToken);
            return;
        }

        if (IsAbsoluteHttpUrl(managedImageUrl))
        {
            await _plexApiClient.UpdatePlaylistPosterFromUrlAsync(
                plex.Url,
                plex.Token,
                playlistId,
                managedImageUrl!,
                cancellationToken);
            return;
        }

        LogSkippedRelativeArtworkUrl("Plex", playlist);
    }

    private async Task<bool> SyncJellyfinPlaylistArtworkAsync(
        JellyfinConnection jellyfin,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return true;
        }

        var cachedVisual = ResolveJellyfinVisualFromPlaylistImage(playlist);
        if (cachedVisual != null)
        {
            var cachedUpdated = await _jellyfinApiClient.UpdateItemPrimaryImageFromFileAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                cachedVisual.FilePath,
                cachedVisual.ContentType,
                cancellationToken);
            if (!cachedUpdated)
            {
                _logger.LogWarning(
                    "Failed to update Jellyfin playlist artwork for {Source}:{SourceId} from cached local file {ImagePath}.",
                    SafeLog(playlist.Source),
                    SafeLog(playlist.SourceId),
                    SafeLog(cachedVisual.FilePath));
            }

            return cachedUpdated;
        }

        var managedImageUrl = await _playlistVisualService.ResolveManagedVisualUrlAsync(
            playlist.Source,
            playlist.SourceId,
            playlist.Name,
            playlist.ImageUrl,
            preference?.ReuseSavedArtwork == true,
            cancellationToken);
        var visual = ResolveJellyfinVisualFromManagedImage(
            playlist,
            managedImageUrl,
            preference?.ReuseSavedArtwork == true);
        if (visual != null && File.Exists(visual.FilePath))
        {
            var updated = await _jellyfinApiClient.UpdateItemPrimaryImageFromFileAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                visual.FilePath,
                visual.ContentType,
                cancellationToken);
            if (!updated)
            {
                _logger.LogWarning(
                    "Failed to update Jellyfin playlist artwork for {Source}:{SourceId} from local file {ImagePath}.",
                    SafeLog(playlist.Source),
                    SafeLog(playlist.SourceId),
                    SafeLog(visual.FilePath));
            }

            return updated;
        }

        if (IsAbsoluteHttpUrl(managedImageUrl))
        {
            var updated = await _jellyfinApiClient.UpdateItemPrimaryImageFromUrlAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                managedImageUrl!,
                cancellationToken);
            if (!updated)
            {
                _logger.LogWarning(
                    "Failed to update Jellyfin playlist artwork for {Source}:{SourceId} from URL {ImageUrl}.",
                    SafeLog(playlist.Source),
                    SafeLog(playlist.SourceId),
                    SafeLog(managedImageUrl));
            }

            return updated;
        }

        LogSkippedRelativeArtworkUrl("Jellyfin", playlist);
        return false;
    }

    private async Task<bool> SyncJellyfinPlaylistMetadataAsync(
        JellyfinConnection jellyfin,
        PlaylistWatchlistDto playlist,
        string playlistId,
        CancellationToken cancellationToken)
    {
        var playlistName = ResolvePlaylistName(playlist);
        var description = string.IsNullOrWhiteSpace(playlist.Description) ? null : playlist.Description.Trim();
        var updated = await _jellyfinApiClient.UpdateItemMetadataAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistId,
            playlistName,
            description,
            cancellationToken);
        if (!updated)
        {
            _logger.LogWarning(
                "Failed to update Jellyfin playlist metadata for {Source}:{SourceId}.",
                SafeLog(playlist.Source),
                SafeLog(playlist.SourceId));
            return false;
        }

        var actual = await _jellyfinApiClient.GetItemAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistId,
            cancellationToken);
        var nameMatches = string.Equals(actual?.Name?.Trim(), playlistName, StringComparison.Ordinal);
        var descriptionMatches = string.IsNullOrWhiteSpace(description)
            || string.Equals((actual?.Overview ?? string.Empty).Trim(), description, StringComparison.Ordinal);
        if (nameMatches && descriptionMatches)
        {
            return true;
        }

        _logger.LogWarning(
            "Jellyfin playlist metadata verification failed for {Source}:{SourceId}. Expected name='{Name}', description length={DescriptionLength}; actual name='{ActualName}', description length={ActualDescriptionLength}.",
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId),
            SafeLog(playlistName),
            description?.Length ?? 0,
            SafeLog(actual?.Name),
            actual?.Overview?.Length ?? 0);
        return false;
    }

    private static List<string> BuildJellyfinFullSyncIssues(
        bool metadataSynced,
        bool artworkSynced,
        PlaylistWatchPreferenceDto? preference)
    {
        var issues = new List<string>();
        if (!metadataSynced)
        {
            issues.Add("playlist metadata did not verify.");
        }

        if (ShouldSyncPlaylistArtwork(preference) && !artworkSynced)
        {
            issues.Add("playlist artwork did not update.");
        }

        return issues;
    }

    private async Task<bool> SyncNavidromePlaylistArtworkAsync(
        NavidromeConnection navidrome,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return true;
        }

        var cachedVisual = ResolveCachedVisualForArtworkSync(playlist);
        if (cachedVisual != null)
        {
            var cachedUpdated = await _navidromeApiClient.UpdatePlaylistImageFromFileAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                playlistId,
                cachedVisual.FilePath,
                cachedVisual.ContentType,
                cancellationToken);
            if (!cachedUpdated)
            {
                _logger.LogWarning(
                    "Failed to update Navidrome playlist artwork for {Source}:{SourceId} from cached local file {ImagePath}.",
                    SafeLog(playlist.Source),
                    SafeLog(playlist.SourceId),
                    SafeLog(cachedVisual.FilePath));
            }

            return cachedUpdated;
        }

        var managedImageUrl = await _playlistVisualService.ResolveManagedVisualUrlAsync(
            playlist.Source,
            playlist.SourceId,
            playlist.Name,
            playlist.ImageUrl,
            preference?.ReuseSavedArtwork == true,
            cancellationToken);
        var visual = ResolveStoredVisualForArtworkSync(
            playlist,
            managedImageUrl,
            preference?.ReuseSavedArtwork == true);
        if (visual != null && File.Exists(visual.FilePath))
        {
            var updated = await _navidromeApiClient.UpdatePlaylistImageFromFileAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                playlistId,
                visual.FilePath,
                visual.ContentType,
                cancellationToken);
            if (!updated)
            {
                _logger.LogWarning(
                    "Failed to update Navidrome playlist artwork for {Source}:{SourceId} from local file {ImagePath}.",
                    SafeLog(playlist.Source),
                    SafeLog(playlist.SourceId),
                    SafeLog(visual.FilePath));
            }

            return updated;
        }

        LogSkippedRelativeArtworkUrl("Navidrome", playlist);
        return false;
    }

    private async Task<bool> SyncNavidromePlaylistMetadataAsync(
        NavidromeConnection navidrome,
        PlaylistWatchlistDto playlist,
        string playlistId,
        CancellationToken cancellationToken)
    {
        var playlistName = ResolvePlaylistName(playlist);
        var description = string.IsNullOrWhiteSpace(playlist.Description) ? null : playlist.Description.Trim();
        var updated = await _navidromeApiClient.UpdatePlaylistMetadataAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            playlistId,
            playlistName,
            description,
            cancellationToken);
        if (!updated)
        {
            _logger.LogWarning(
                "Failed to update Navidrome playlist metadata for {Source}:{SourceId}.",
                SafeLog(playlist.Source),
                SafeLog(playlist.SourceId));
            return false;
        }

        var actual = await _navidromeApiClient.GetPlaylistAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            playlistId,
            cancellationToken);
        var nameMatches = string.Equals(actual?.Name?.Trim(), playlistName, StringComparison.Ordinal);
        var descriptionMatches = string.IsNullOrWhiteSpace(description)
            || string.Equals((actual?.Comment ?? string.Empty).Trim(), description, StringComparison.Ordinal);
        if (nameMatches && descriptionMatches)
        {
            return true;
        }

        _logger.LogWarning(
            "Navidrome playlist metadata verification failed for {Source}:{SourceId}. Expected name='{Name}', description length={DescriptionLength}; actual name='{ActualName}', description length={ActualDescriptionLength}.",
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId),
            SafeLog(playlistName),
            description?.Length ?? 0,
            SafeLog(actual?.Name),
            actual?.Comment?.Length ?? 0);
        return false;
    }

    private static List<string> BuildNavidromeFullSyncIssues(
        bool metadataSynced,
        bool artworkSynced,
        PlaylistWatchPreferenceDto? preference)
    {
        var issues = new List<string>();
        if (!metadataSynced)
        {
            issues.Add("playlist metadata did not verify.");
        }

        if (ShouldSyncPlaylistArtwork(preference) && !artworkSynced)
        {
            issues.Add("playlist artwork did not update.");
        }

        return issues;
    }

    private void LogSkippedRelativeArtworkUrl(string target, PlaylistWatchlistDto playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist.ImageUrl))
        {
            return;
        }

        _logger.LogWarning(
            "Skipped {Target} playlist artwork sync for {Source}:{SourceId} because image URL is relative and no stored visual file was found: {ImageUrl}",
            SafeLog(target),
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId),
            SafeLog(playlist.ImageUrl));
    }

    private static string SafeLog(string? value)
    {
        return DeezSpoTag.Core.Security.LogSanitizer.OneLine(value);
    }

    private static bool IsAbsoluteHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<SyncTrackSummary>> FilterTracksForSyncAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return new List<SyncTrackSummary>();
        }

        var normalizedSource = NormalizeSource(playlist.Source);
        var ignoredTrackIds = await _libraryRepository.GetPlaylistWatchIgnoredTrackIdsAsync(
            normalizedSource,
            playlist.SourceId,
            cancellationToken);
        var globalRules = await GetGlobalPlaylistBlockRulesAsync(cancellationToken);
        var effectiveBlockRules = PlaylistTrackBlockRuleHelper.MergeRules(preference?.IgnoreRules, globalRules);
        if (ignoredTrackIds.Count == 0 && (effectiveBlockRules == null || effectiveBlockRules.Count == 0))
        {
            return tracks.ToList();
        }

        return tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.SourceTrackId))
            .Where(track => !ignoredTrackIds.Contains(track.SourceTrackId))
            .Where(track => !ShouldBlockTrack(track, effectiveBlockRules))
            .ToList();
    }

    private async Task<IReadOnlyList<PlaylistTrackBlockRule>> GetGlobalPlaylistBlockRulesAsync(CancellationToken cancellationToken)
    {
        var preferences = await _libraryRepository.GetPlaylistWatchPreferencesAsync(cancellationToken);
        return PlaylistTrackBlockRuleHelper.BuildGlobalRules(preferences);
    }

    private static bool ShouldBlockTrack(SyncTrackSummary track, List<PlaylistTrackBlockRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        return rules.Any(rule =>
            PlaylistTrackBlockRuleMatcher.RuleMatches(
                new PlaylistTrackBlockRuleMatcher.TrackRuleMatchInput(
                    track.Name,
                    track.Artists,
                    track.Album,
                    track.Genres,
                    track.Explicit,
                    track.ReleaseDate),
                rule.ConditionField,
                rule.ConditionOperator,
                rule.ConditionValue));
    }

    private static bool TryParseReleaseYear(string? releaseDate, out int year)
    {
        year = 0;
        var value = (releaseDate ?? string.Empty).Trim();
        if (value.Length < 4)
        {
            return false;
        }

        return int.TryParse(value[..4], out year);
    }

    private static string NormalizeSyncMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == SyncModeAppend ? SyncModeAppend : SyncModeMirror;
    }

    private static string NormalizeService(string? service)
    {
        return (service ?? string.Empty).Trim().ToLowerInvariant();
    }

    private async Task<string> ResolveTargetServiceAsync(
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        if (preference?.SyncTargets is { Count: > 0 })
        {
            var configuredTargets = NormalizeTargetServices(preference.SyncTargets);
            if (configuredTargets.Count > 0)
            {
                return configuredTargets[0];
            }
        }

        var configuredService = NormalizeService(preference?.Service);
        if (string.Equals(configuredService, "none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(configuredService))
        {
            return configuredService;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var state = await _authService.LoadAsync();
        if (state.Plex is not null
            && !string.IsNullOrWhiteSpace(state.Plex.Url)
            && !string.IsNullOrWhiteSpace(state.Plex.Token)
            && !string.IsNullOrWhiteSpace(state.Plex.MachineIdentifier))
        {
            return PlexService;
        }

        if (state.Jellyfin is not null
            && !string.IsNullOrWhiteSpace(state.Jellyfin.Url)
            && !string.IsNullOrWhiteSpace(state.Jellyfin.ApiKey)
            && !string.IsNullOrWhiteSpace(state.Jellyfin.UserId))
        {
            return JellyfinService;
        }

        if (state.Navidrome is not null
            && !string.IsNullOrWhiteSpace(state.Navidrome.Url)
            && !string.IsNullOrWhiteSpace(state.Navidrome.Username)
            && !string.IsNullOrWhiteSpace(state.Navidrome.Password))
        {
            return NavidromeService;
        }

        return string.Empty;
    }

    private async Task<IReadOnlyList<string>> ResolveTargetServicesAsync(
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        if (preference?.SyncTargets is not null)
        {
            return NormalizeTargetServices(preference.SyncTargets);
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        return string.IsNullOrWhiteSpace(service)
            ? []
            : [service];
    }

    private static IReadOnlyList<string> NormalizeTargetServices(IEnumerable<string>? services)
    {
        if (services is null)
        {
            return [];
        }

        var normalized = new List<string>();
        foreach (var serviceValue in services)
        {
            var service = NormalizeService(serviceValue);
            if (string.IsNullOrWhiteSpace(service)
                || string.Equals(service, "none", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(service, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(service, PlexService, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, JellyfinService, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, NavidromeService, StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(service);
            }
        }

        return normalized;
    }

    private static string NormalizeSource(string? source)
    {
        return (source ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string ResolvePlaylistName(PlaylistWatchlistDto playlist)
    {
        var name = (playlist.Name ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(name) ? "Playlist" : name;
    }

    private async Task<(PlexConnection? Plex, PlaylistSyncResult? Error)> TryLoadConfiguredPlexAsync()
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return (null, new PlaylistSyncResult(false, PlexNotConfiguredMessage));
        }

        if (string.IsNullOrWhiteSpace(plex.MachineIdentifier))
        {
            return (null, new PlaylistSyncResult(false, "Plex machine identifier missing."));
        }

        return (new PlexConnection(plex.Url, plex.Token, plex.MachineIdentifier), null);
    }

    private async Task<(JellyfinConnection? Jellyfin, PlaylistSyncResult? Error)> TryLoadConfiguredJellyfinAsync()
    {
        var state = await _authService.LoadAsync();
        var jellyfin = state.Jellyfin;
        if (jellyfin is null || string.IsNullOrWhiteSpace(jellyfin.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            return (null, new PlaylistSyncResult(false, JellyfinNotConfiguredMessage));
        }

        if (string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            return (null, new PlaylistSyncResult(false, "Jellyfin user id is missing."));
        }

        return (new JellyfinConnection(jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId), null);
    }

    private async Task<(NavidromeConnection? Navidrome, PlaylistSyncResult? Error)> TryLoadConfiguredNavidromeAsync()
    {
        var state = await _authService.LoadAsync();
        var navidrome = state.Navidrome;
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            return (null, new PlaylistSyncResult(false, NavidromeNotConfiguredMessage));
        }

        return (new NavidromeConnection(navidrome.Url, navidrome.Username, navidrome.Password), null);
    }

    private async Task<List<long>> ResolveOrderedTrackIdsAsync(
        string playlistSource,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        return await ResolveLocalTrackIdsAsync(playlistSource, tracks, cancellationToken);
    }

    private async Task<List<long>> ResolveLocalTrackIdsAsync(
        string playlistSource,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var resolved = new List<long>(tracks.Count);
        foreach (var track in tracks)
        {
            var (identitySource, identityTrackId) = ResolveTrackIdentity(playlistSource, track);
            var decision = await _libraryRepository.ResolveLocalTrackIdentityAsync(
                new LibraryRepository.LibraryExistenceInput(
                    track.Isrc,
                    track.Name,
                    track.Artists,
                    track.DurationMs,
                    identitySource,
                    identityTrackId,
                    track.Album),
                cancellationToken: cancellationToken);
            resolved.Add(decision.IsAmbiguous ? 0L : decision.LocalTrackId ?? 0L);
        }

        return resolved;
    }

    private async Task<long?> ResolveLocalTrackIdAsync(
        string playlistSource,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (identitySource, identityTrackId) = ResolveTrackIdentity(playlistSource, track);
        var decision = await _libraryRepository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(
                track.Isrc,
                track.Name,
                track.Artists,
                track.DurationMs,
                identitySource,
                identityTrackId,
                track.Album),
            cancellationToken: cancellationToken);
        return decision.IsAmbiguous ? null : decision.LocalTrackId;
    }

    private async Task<PlaylistTrackSyncReadiness> CheckPlexTrackReadyAsync(
        long localTrackId,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError != null)
        {
            return new PlaylistTrackSyncReadiness(false, true, configurationError.Message, PlexService, localTrackId);
        }

        if (plex == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, PlexNotConfiguredMessage, PlexService, localTrackId);
        }

        var mapped = await _libraryRepository.GetPlexRatingKeysByTrackIdsAsync(
            new[] { localTrackId },
            cancellationToken);
        if (mapped.TryGetValue(localTrackId, out var mappedRatingKey)
            && !string.IsNullOrWhiteSpace(mappedRatingKey))
        {
            return new PlaylistTrackSyncReadiness(
                true,
                false,
                "Track is visible in Plex metadata mapping.",
                PlexService,
                localTrackId,
                mappedRatingKey);
        }

        var ratingKey = await ResolvePlexRatingKeyAsync(
            plex,
            track,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(ratingKey))
        {
            return new PlaylistTrackSyncReadiness(
                false,
                false,
                "Track is not visible in Plex yet.",
                PlexService,
                localTrackId);
        }

        await _libraryRepository.UpsertPlexTrackMetadataAsync(
            new[]
            {
                new PlexTrackMetadataUpsertDto(localTrackId, ratingKey, DateTimeOffset.UtcNow)
            },
            cancellationToken);
        return new PlaylistTrackSyncReadiness(
            true,
            false,
            "Track is visible in Plex search.",
            PlexService,
            localTrackId,
            ratingKey);
    }

    private async Task<PlaylistTrackSyncReadiness> CheckJellyfinTrackReadyAsync(
        long localTrackId,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError != null)
        {
            return new PlaylistTrackSyncReadiness(false, true, configurationError.Message, JellyfinService, localTrackId);
        }

        if (jellyfin == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, JellyfinNotConfiguredMessage, JellyfinService, localTrackId);
        }

        var itemId = await ResolveJellyfinItemIdAsync(
            jellyfin,
            track,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        return string.IsNullOrWhiteSpace(itemId)
            ? new PlaylistTrackSyncReadiness(false, false, "Track is not visible in Jellyfin yet.", JellyfinService, localTrackId)
            : new PlaylistTrackSyncReadiness(true, false, "Track is visible in Jellyfin search.", JellyfinService, localTrackId, itemId);
    }

    private async Task<PlaylistTrackSyncReadiness> CheckNavidromeTrackReadyAsync(
        long localTrackId,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
        if (configurationError is not null)
        {
            return new PlaylistTrackSyncReadiness(false, true, configurationError.Message, NavidromeService, localTrackId);
        }

        var itemId = await ResolveNavidromeItemIdAsync(
            navidrome!,
            track,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        return string.IsNullOrWhiteSpace(itemId)
            ? new PlaylistTrackSyncReadiness(false, false, "Track is not visible in Navidrome yet.", NavidromeService, localTrackId)
            : new PlaylistTrackSyncReadiness(true, false, "Track is visible in Navidrome search.", NavidromeService, localTrackId, itemId);
    }

    private async Task<SyncMatchSummary> ResolvePlexRatingKeysAsync(
        PlexConnection plex,
        IReadOnlyList<SyncTrackSummary> tracks,
        List<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var ratingKeyByTrackId = await _libraryRepository.GetPlexRatingKeysByTrackIdsAsync(
            orderedTrackIds.Where(id => id > 0).Distinct().ToList(),
            cancellationToken);

        var ratingKeysByIndex = new string?[tracks.Count];
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var metadataMatches = 0;
        var searchMatches = 0;
        var unresolvedSearchIndexes = new List<int>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var trackId = orderedTrackIds[i];
            if (trackId > 0 && ratingKeyByTrackId.TryGetValue(trackId, out var ratingKey))
            {
                ratingKeysByIndex[i] = ratingKey;
                metadataMatches++;
                continue;
            }

            unresolvedSearchIndexes.Add(i);
        }

        var metadataUpdates = new List<PlexTrackMetadataUpsertDto>();
        foreach (var index in unresolvedSearchIndexes)
        {
            var track = tracks[index];
            var resolved = await ResolvePlexRatingKeyAsync(plex, track, searchCache, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                ratingKeysByIndex[index] = resolved;
                searchMatches++;
                var trackId = orderedTrackIds[index];
                if (trackId > 0)
                {
                    metadataUpdates.Add(new PlexTrackMetadataUpsertDto(
                        trackId,
                        resolved,
                        DateTimeOffset.UtcNow));
                }
            }
        }

        if (metadataUpdates.Count > 0)
        {
            await _libraryRepository.UpsertPlexTrackMetadataAsync(
                metadataUpdates,
                cancellationToken);
        }

        var ratingKeys = ratingKeysByIndex
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToList();
        var memberships = ratingKeysByIndex
            .Select((targetId, index) => new { targetId, index })
            .Where(item => !string.IsNullOrWhiteSpace(item.targetId)
                           && orderedTrackIds[item.index] > 0
                           && !string.IsNullOrWhiteSpace(tracks[item.index].SourceTrackId))
            .Select(item => new PlaylistWatchTargetMembership(
                tracks[item.index].SourceTrackId,
                orderedTrackIds[item.index],
                item.targetId!))
            .ToList();
        return new SyncMatchSummary(
            ratingKeys,
            memberships,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: ratingKeys.Count,
            MissingTracks: Math.Max(0, tracks.Count - ratingKeys.Count),
            MetadataMatches: metadataMatches,
            SearchMatches: searchMatches);
    }

    private async Task<string?> ResolvePlexRatingKeyAsync(
        PlexConnection plex,
        SyncTrackSummary track,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var query = $"{track.Name} {track.Artists}".Trim();
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }

        var results = await _plexApiClient.SearchTracksAsync(
            plex.Url,
            plex.Token,
            query,
            cancellationToken);

        var match = results.FirstOrDefault(result =>
            IsTitleArtistMatch(track, result)
            && IsDurationMatch(track.DurationMs, result.DurationMs));

        var ratingKey = match?.RatingKey;
        cache[query] = ratingKey;
        return ratingKey;
    }

    private async Task<List<PlaylistWatchTargetMembership>> ResolveJellyfinItemIdsAsync(
        JellyfinConnection jellyfin,
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var mapped = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
            JellyfinService,
            orderedTrackIds.Where(static id => id > 0).Distinct().ToList(),
            cancellationToken);
        var itemIds = new List<PlaylistWatchTargetMembership>(tracks.Count);
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var metadataUpdates = new List<MediaServerTrackMetadataUpsertDto>();
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var localTrackId = orderedTrackIds[index];
            var resolved = localTrackId > 0 && mapped.TryGetValue(localTrackId, out var mappedItemId)
                ? mappedItemId
                : await ResolveJellyfinItemIdAsync(jellyfin, track, searchCache, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved) && orderedTrackIds[index] > 0)
            {
                itemIds.Add(new PlaylistWatchTargetMembership(
                    track.SourceTrackId,
                    orderedTrackIds[index],
                    resolved));
                if (!mapped.ContainsKey(localTrackId))
                {
                    metadataUpdates.Add(new MediaServerTrackMetadataUpsertDto(
                        localTrackId,
                        JellyfinService,
                        resolved,
                        null,
                        DateTimeOffset.UtcNow));
                }
            }
        }

        await _libraryRepository.UpsertMediaServerTrackMetadataAsync(metadataUpdates, cancellationToken);
        return itemIds;
    }

    private async Task<string?> ResolveJellyfinItemIdAsync(
        JellyfinConnection jellyfin,
        SyncTrackSummary track,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var query = $"{track.Name} {track.Artists}".Trim();
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }

        var results = await SearchJellyfinTrackCandidatesAsync(jellyfin, track, query, cache, cancellationToken);

        var match = results.FirstOrDefault(result =>
            IsTitleArtistMatch(track, result)
            && IsDurationMatch(track.DurationMs, result.DurationMs));

        var itemId = match?.Id;
        cache[query] = itemId;
        return itemId;
    }

    private async Task<List<JellyfinAudioTrack>> SearchJellyfinTrackCandidatesAsync(
        JellyfinConnection jellyfin,
        SyncTrackSummary track,
        string primaryQuery,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        var candidates = new List<JellyfinAudioTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in BuildServerSearchQueries(track, primaryQuery))
        {
            if (cache.TryGetValue(query, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                candidates.Add(new JellyfinAudioTrack(cached, track.Name, track.Artists, track.DurationMs));
                continue;
            }

            var results = await _jellyfinApiClient.SearchTracksAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                query,
                cancellationToken);
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.Id) && seen.Add(result.Id))
                {
                    candidates.Add(result);
                }
            }

            if (results.Count == 0)
            {
                cache[query] = null;
            }
        }

        return candidates;
    }

    private async Task<List<PlaylistWatchTargetMembership>> ResolveNavidromeItemIdsAsync(
        NavidromeConnection navidrome,
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var mapped = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
            NavidromeService,
            orderedTrackIds.Where(static id => id > 0).Distinct().ToList(),
            cancellationToken);
        var itemIds = new List<PlaylistWatchTargetMembership>(tracks.Count);
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var metadataUpdates = new List<MediaServerTrackMetadataUpsertDto>();
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var localTrackId = orderedTrackIds[index];
            var resolved = localTrackId > 0 && mapped.TryGetValue(localTrackId, out var mappedItemId)
                ? mappedItemId
                : await ResolveNavidromeItemIdAsync(navidrome, track, searchCache, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved) && orderedTrackIds[index] > 0)
            {
                itemIds.Add(new PlaylistWatchTargetMembership(
                    track.SourceTrackId,
                    orderedTrackIds[index],
                    resolved));
                if (!mapped.ContainsKey(localTrackId))
                {
                    metadataUpdates.Add(new MediaServerTrackMetadataUpsertDto(
                        localTrackId,
                        NavidromeService,
                        resolved,
                        null,
                        DateTimeOffset.UtcNow));
                }
            }
        }

        await _libraryRepository.UpsertMediaServerTrackMetadataAsync(metadataUpdates, cancellationToken);
        return itemIds;
    }

    private async Task<string?> ResolveNavidromeItemIdAsync(
        NavidromeConnection navidrome,
        SyncTrackSummary track,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var query = $"{track.Name} {track.Artists}".Trim();
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }

        var results = await SearchNavidromeTrackCandidatesAsync(navidrome, track, query, cache, cancellationToken);
        var match = results.FirstOrDefault(result =>
            IsTitleArtistMatch(track, result)
            && IsDurationMatch(track.DurationMs, result.DurationMs));
        var itemId = match?.Id;
        cache[query] = itemId;
        return itemId;
    }

    private async Task<List<NavidromeAudioTrack>> SearchNavidromeTrackCandidatesAsync(
        NavidromeConnection navidrome,
        SyncTrackSummary track,
        string primaryQuery,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        var candidates = new List<NavidromeAudioTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in BuildServerSearchQueries(track, primaryQuery))
        {
            if (cache.TryGetValue(query, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                candidates.Add(new NavidromeAudioTrack(cached, track.Name, track.Artists, track.DurationMs));
                continue;
            }

            var results = await _navidromeApiClient.SearchTracksAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                query,
                cancellationToken);
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.Id) && seen.Add(result.Id))
                {
                    candidates.Add(result);
                }
            }

            if (results.Count == 0)
            {
                cache[query] = null;
            }
        }

        return candidates;
    }

    private static IEnumerable<string> BuildServerSearchQueries(SyncTrackSummary track, string primaryQuery)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in new[]
                 {
                     primaryQuery,
                     track.Name,
                     $"{track.Artists} {track.Name}".Trim()
                 })
        {
            var normalized = (query ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static PlaylistSyncResult BuildFailedResult(string message, SyncMatchSummary matchSummary)
        => new(
            false,
            message,
            PlaylistId: null,
            SyncedTracks: 0,
            SourceTracks: matchSummary.SourceTracks,
            LocalMatches: matchSummary.LocalMatches,
            TargetMatches: matchSummary.TargetMatches,
            MissingTracks: matchSummary.MissingTracks,
            MetadataMatches: matchSummary.MetadataMatches,
            SearchMatches: matchSummary.SearchMatches);

    private static PlaylistSyncResult BuildSuccessResult(
        string message,
        string? playlistId,
        SyncMatchSummary matchSummary,
        int syncedTracks)
        => new(
            true,
            message,
            playlistId,
            syncedTracks,
            matchSummary.SourceTracks,
            matchSummary.LocalMatches,
            matchSummary.TargetMatches,
            matchSummary.MissingTracks,
            matchSummary.MetadataMatches,
            matchSummary.SearchMatches);

    private static PlaylistSyncResult BuildPartialResult(
        string message,
        string? playlistId,
        SyncMatchSummary matchSummary,
        int syncedTracks)
        => new(
            false,
            message,
            playlistId,
            syncedTracks,
            matchSummary.SourceTracks,
            matchSummary.LocalMatches,
            matchSummary.TargetMatches,
            matchSummary.MissingTracks,
            matchSummary.MetadataMatches,
            matchSummary.SearchMatches);

    private static string BuildSyncMessage(string baseMessage, SyncMatchSummary matchSummary)
    {
        return string.Concat(
            baseMessage,
            " Source tracks: ",
            matchSummary.SourceTracks.ToString(CultureInfo.InvariantCulture),
            ". Local matches: ",
            matchSummary.LocalMatches.ToString(CultureInfo.InvariantCulture),
            ". Target matches: ",
            matchSummary.TargetMatches.ToString(CultureInfo.InvariantCulture),
            ". Missing tracks: ",
            matchSummary.MissingTracks.ToString(CultureInfo.InvariantCulture),
            ".");
    }

    private static bool IsTitleArtistMatch(SyncTrackSummary track, PlexTrack result)
        => TrackTitleMatcher.TitlesMatch(track.Name, result.Title)
           && TrackTitleMatcher.ArtistsMatch(track.Artists, result.Artist);

    private static bool IsTitleArtistMatch(SyncTrackSummary track, JellyfinAudioTrack result)
        => TrackTitleMatcher.TitlesMatch(track.Name, result.Name)
           && TrackTitleMatcher.ArtistsMatch(track.Artists, result.Artist);

    private static bool IsTitleArtistMatch(SyncTrackSummary track, NavidromeAudioTrack result)
        => TrackTitleMatcher.TitlesMatch(track.Name, result.Title)
           && TrackTitleMatcher.ArtistsMatch(track.Artists, result.Artist);

    private static bool IsDurationMatch(int? durationMs, long durationCandidate)
    {
        if (!durationMs.HasValue || durationCandidate <= 0)
        {
            return true;
        }

        var delta = Math.Abs(durationMs.Value - durationCandidate);
        return delta <= DurationToleranceMs;
    }

    private static bool IsDurationMatch(int? durationMs, int? durationCandidateMs)
    {
        if (!durationMs.HasValue || !durationCandidateMs.HasValue || durationCandidateMs <= 0)
        {
            return true;
        }

        var delta = Math.Abs(durationMs.Value - durationCandidateMs.Value);
        return delta <= DurationToleranceMs;
    }

    private static string BuildMergeTrackDedupKey(SyncTrackSummary track)
    {
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            return $"isrc:{Normalize(track.Isrc)}";
        }

        var year = TryParseReleaseYear(track.ReleaseDate, out var parsedYear)
            ? parsedYear.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var durationBucket = track.DurationMs.HasValue && track.DurationMs.Value > 0
            ? (track.DurationMs.Value / DurationToleranceMs).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var sourceTrackKey = string.IsNullOrWhiteSpace(track.SourceTrackId)
            ? string.Empty
            : Normalize(track.SourceTrackId);
        return string.Join(
            "\u001F",
            Normalize(track.Name),
            Normalize(track.Artists),
            Normalize(track.Album),
            year,
            durationBucket,
            sourceTrackKey);
    }

    private static string ResolveMergedPlaylistName(string? requestedName)
    {
        var trimmed = (requestedName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "Merged Monitored Playlist" : trimmed;
    }

    private static string? BuildMergedPlaylistDescription(
        string? userDescription,
        IEnumerable<PlaylistWatchlistDto> selectedPlaylists,
        string? sourceUsername)
    {
        var values = new List<string>();
        var trimmedDescription = (userDescription ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmedDescription))
        {
            values.Add(trimmedDescription);
        }

        var sourceSummary = BuildMergeSourceSummary(selectedPlaylists);
        if (!string.IsNullOrWhiteSpace(sourceSummary))
        {
            values.Add(sourceSummary);
        }

        var trimmedUser = (sourceUsername ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmedUser))
        {
            values.Add($"Source user: {trimmedUser}");
        }

        return values.Count == 0 ? null : string.Join(" | ", values);
    }

    private static string? BuildMergeSourceSummary(IEnumerable<PlaylistWatchlistDto> selectedPlaylists)
    {
        var sources = selectedPlaylists
            .Select(static playlist => NormalizeMergeSourceLabel(playlist.Source))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return sources.Count == 0 ? null : $"Sources: {string.Join(", ", sources)}";
    }

    private static string NormalizeMergeSourceLabel(string? source)
    {
        var normalized = NormalizeSource(source);
        return normalized switch
        {
            "spotify" => "Spotify",
            "deezer" => "Deezer",
            "apple" => "Apple Music",
            "boomplay" => "Boomplay",
            "recommendations" => "Recommendations",
            "smarttracklist" => "Smart Tracklist",
            _ => string.IsNullOrWhiteSpace(normalized)
                ? "Unknown"
                : char.ToUpperInvariant(normalized[0]) + normalized[1..]
        };
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static SyncTrackSummary ToSyncTrackSummary(SpotifyTrackSummary track)
    {
        return new SyncTrackSummary(
            (track.Id ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
            track.Name?.Trim() ?? string.Empty,
            track.Artists?.Trim() ?? string.Empty,
            track.Album?.Trim() ?? string.Empty,
            track.ReleaseDate,
            track.Explicit,
            NormalizeGenres(track.Genres),
            track.DurationMs);
    }

    private static SyncTrackSummary ToSyncTrackSummary(PlaylistTrackCandidate track)
    {
        return new SyncTrackSummary(
            (track.TrackSourceId ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
            track.Title?.Trim() ?? string.Empty,
            track.Artist?.Trim() ?? string.Empty,
            track.Album?.Trim() ?? string.Empty,
            track.ReleaseYear?.ToString(CultureInfo.InvariantCulture),
            track.Explicit,
            NormalizeGenres(track.Genres),
            track.DurationMs,
            string.IsNullOrWhiteSpace(track.DeezerId) ? null : "deezer",
            string.IsNullOrWhiteSpace(track.DeezerId) ? null : track.DeezerId.Trim());
    }

    private static (string Source, string TrackId) ResolveTrackIdentity(
        string playlistSource,
        SyncTrackSummary track)
    {
        var source = string.IsNullOrWhiteSpace(track.IdentitySource)
            ? NormalizeSource(playlistSource)
            : NormalizeSource(track.IdentitySource);
        var trackId = string.IsNullOrWhiteSpace(track.IdentityTrackId)
            ? track.SourceTrackId
            : track.IdentityTrackId.Trim();
        return (source, trackId);
    }

    private static IReadOnlyList<string> NormalizeGenres(IReadOnlyList<string>? genres)
    {
        if (genres is null || genres.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return genres
            .Select(genre => (genre ?? string.Empty).Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(seen.Add)
            .ToList();
    }
}

public sealed record PlaylistSyncResult(
    bool Success,
    string Message,
    string? PlaylistId = null,
    int SyncedTracks = 0,
    int SourceTracks = 0,
    int LocalMatches = 0,
    int TargetMatches = 0,
    int MissingTracks = 0,
    int MetadataMatches = 0,
    int SearchMatches = 0)
{
    public static PlaylistSyncResult Failed(string message)
        => new(false, message);
}
