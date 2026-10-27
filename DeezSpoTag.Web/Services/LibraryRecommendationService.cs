using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Matching;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryRecommendationService
{
    public const string WeeklyMissingType = "weekly-missing-favourites";
    public const string WeeklySimilarType = "weekly-similar-artists";
    public const string WeeklyStationPrefix = "weekly-rotation";
    private const string WeeklyCacheSource = "recommendations-weekly-pool";
    private readonly SpotifyPathfinderMetadataClient? _pathfinder;
    private readonly SpotifyArtistService? _spotifyArtists;
    private readonly SpotifyMetadataService? _spotifyMetadata;
    private readonly ArtistAliasService? _artistAliases;

    private readonly SemaphoreSlim _weeklyMappingGate = new(1, 1);

    private static async Task<RecommendationTrackDto> ResolveWeeklyTrackMappingAsync(
        RecommendationTrackDto track, Func<Task<string?>> resolve)
    {
        if (!string.IsNullOrWhiteSpace(track.DeezerId)) return track;
        var deezerId = await resolve();
        return track with { DeezerId = deezerId, MappingStatus = string.IsNullOrWhiteSpace(deezerId) ? "unmatched" : "matched" };
    }

    private static async Task<RecommendationDetailDto> HydrateWeeklyTrackIsrcsAsync(
        RecommendationDetailDto detail, Func<List<SpotifyTrackSummary>, Task<List<SpotifyTrackSummary>>> hydrate)
    {
        var pending = detail.Tracks.Where(t => string.IsNullOrWhiteSpace(t.DeezerId) && string.IsNullOrWhiteSpace(t.Isrc))
            .Select(t => new SpotifyTrackSummary(t.Id, t.Title, t.Artist.Name, t.Album.Title,
                t.Duration * 1000, t.SourceUrl ?? $"https://open.spotify.com/track/{t.Id}", t.Album.CoverMedium, t.Isrc)).ToList();
        if (pending.Count == 0) return detail;
        var hydrated = (await hydrate(pending)).Where(t => !string.IsNullOrWhiteSpace(t.Isrc))
            .GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Isrc!, StringComparer.Ordinal);
        return detail with { Tracks = detail.Tracks.Select(t =>
            string.IsNullOrWhiteSpace(t.DeezerId) && string.IsNullOrWhiteSpace(t.Isrc) && hydrated.TryGetValue(t.Id, out var isrc)
                ? t with { Isrc = isrc } : t).ToList() };
    }

    private async Task<RecommendationDetailDto> MapWeeklyTracksAsync(RecommendationDetailDto detail, CancellationToken cancellationToken)
    {
        var selectedCount = detail.Tracks.Count;
        var reserves = detail.ReserveTracks;
        detail = detail with { Tracks = detail.Tracks.Concat(reserves ?? []).ToList() };
        if (_spotifyMetadata is not null)
        {
            try
            {
                detail = await HydrateWeeklyTrackIsrcsAsync(detail, tracks => _spotifyMetadata.HydrateTrackIsrcsAsync(tracks, cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                && (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex) || ex is OperationCanceledException))
            {
                _logger.LogWarning(ex, "Weekly ISRC hydration unavailable; retaining Spotify identities for later mapping.");
            }
        }
        var mapped = new List<RecommendationTrackDto>();
        foreach (var track in detail.Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.DeezerId) && string.IsNullOrWhiteSpace(track.Isrc))
            {
                mapped.Add(track with { MappingStatus = "awaiting_isrc" });
                continue;
            }
            try
            {
                var result = await ResolveWeeklyTrackMappingAsync(track, async () =>
                {
                    var resolution = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(_deezerClient,
                        new SpotifyTrackSummary(track.Id, track.Title, track.Artist.Name, track.Album.Title,
                            track.Duration * 1000, track.SourceUrl ?? $"https://open.spotify.com/track/{track.Id}", track.Album.CoverMedium, track.Isrc),
                        new SpotifyTrackResolveOptions(AllowFallbackSearch: false, PreferIsrcOnly: true,
                            StrictMode: true, BypassNegativeCanonicalCache: false, Logger: _logger, CancellationToken: cancellationToken));
                    if (resolution.Outcome == SpotifyTracklistResolveOutcome.TransientFailure)
                        throw new HttpRequestException("Deezer mapping temporarily unavailable.");
                    return resolution.DeezerId;
                });
                mapped.Add(result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Weekly recommendation Deezer mapping unavailable for Spotify track {TrackId}.", track.Id);
                mapped.Add(track with { MappingStatus = "unavailable" });
            }
        }
        return DescribeWeeklyDeferredMappings(detail with { Tracks = mapped.Take(selectedCount).ToList(), ReserveTracks = mapped.Skip(selectedCount).ToList() });
    }

    private async Task<RecommendationDetailDto> EnsureWeeklyMappingsAsync(
        RecommendationDetailDto detail, string stationId, DateOnly week, CancellationToken cancellationToken)
    {
        await _weeklyMappingGate.WaitAsync(cancellationToken);
        try
        {
            var saved = await _repository.GetPlaylistTrackCandidateCacheAsync(WeeklyCacheSource, stationId, cancellationToken);
            if (saved?.SnapshotId != $"v1:{week:yyyyMMdd}") return detail;
            var scoped = ReadFolderScopedWeeklyPool(saved, stationId);
            if (scoped is null) return detail;
            detail = scoped;
            if (detail.Tracks.Concat(detail.ReserveTracks ?? []).All(t => !string.IsNullOrWhiteSpace(t.DeezerId)))
                return DescribeWeeklyDeferredMappings(detail);
            return await MapWeeklyTracksAsync(detail, cancellationToken);
        }
        finally { _weeklyMappingGate.Release(); }
    }

    private static RecommendationDetailDto DescribeWeeklyDeferredMappings(RecommendationDetailDto detail)
    {
        static bool Pending(RecommendationTrackDto track) => string.IsNullOrWhiteSpace(track.DeezerId)
            && track.MappingStatus is "unavailable" or "awaiting_isrc" or "pending";
        var selected = detail.Tracks.Count(Pending);
        var reserve = (detail.ReserveTracks ?? []).Count(Pending);
        var reasons = (detail.ReasonCodes ?? []).Where(r => r != "weekly_mapping_incomplete").ToList();
        var notices = new List<string>();
        if (reasons.Contains("weekly_artist_lookup_incomplete"))
            notices.Add("Some Spotify artist lookups failed; showing the confirmed recommendations.");
        if (selected + reserve > 0)
        {
            reasons.Add("weekly_mapping_incomplete");
            notices.Add($"Spotify recommendations are available; metadata mapping is pending for {selected} selected and {reserve} reserve songs.");
        }
        var message = notices.Count > 0 ? string.Join(" ", notices)
            : detail.ReasonCodes?.Contains("weekly_mapping_incomplete") == true ? null : detail.Message;
        return detail with { ReasonCodes = reasons, Message = message,
            Station = detail.Station with { ReasonCodes = reasons, Message = message } };
    }

    private static DateOnly GetRecommendationWeekStart(DateOnly localDay)
        => localDay.AddDays(-(((int)localDay.DayOfWeek + 6) % 7));

    /// <summary>
    /// Parses the weekly station id. The id mirrors the daily station shape
    /// (<c>daily-rotation:l{libraryId}:f{folderId}</c>): the folder is part of the identity, so
    /// every weekly lookup resolves to exactly one folder. The weekly section type is appended
    /// because a folder carries both weekly sections.
    /// </summary>
    public static bool TryParseWeeklyStation(
        string? stationId, out long libraryId, out long folderId, out string sectionType)
    {
        libraryId = 0;
        folderId = 0;
        sectionType = string.Empty;
        var parts = stationId?.Split(':');
        if (parts is not { Length: 4 } || parts[0] != WeeklyStationPrefix
            || !parts[1].StartsWith("l", StringComparison.Ordinal)
            || !long.TryParse(parts[1].AsSpan(1), out libraryId) || libraryId <= 0
            || !parts[2].StartsWith("f", StringComparison.Ordinal)
            || !long.TryParse(parts[2].AsSpan(1), out folderId) || folderId <= 0
            || (parts[3] != WeeklyMissingType && parts[3] != WeeklySimilarType))
        {
            libraryId = 0;
            folderId = 0;
            return false;
        }

        sectionType = parts[3];
        return true;
    }

    public static string BuildWeeklyStationId(long libraryId, long folderId, string sectionType)
        => $"{WeeklyStationPrefix}:l{libraryId}:f{folderId}:{sectionType}";

    private static RecommendationScope BuildWeeklyScope(long libraryId, FolderDto folder, string sectionType)
    {
        var stationId = BuildWeeklyStationId(libraryId, folder.Id, sectionType);
        return new RecommendationScope(libraryId, folder.Id, folder.DisplayName, stationId, stationId);
    }

    /// <summary>
    /// Resolves the folder a weekly station belongs to. A weekly station only exists while its
    /// folder is still eligible, which is what keeps a weekly selection from outliving the folder
    /// it was generated for.
    /// </summary>
    private async Task<FolderDto?> FindWeeklyFolderAsync(
        long libraryId, long folderId, CancellationToken cancellationToken)
    {
        var folders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        return folders.FirstOrDefault(folder => folder.LibraryId == libraryId && folder.Id == folderId);
    }

    /// <summary>
    /// Turns an incoming weekly station id (plus an optional folder filter from the request) into
    /// the folder-scoped, daily-shaped <see cref="RecommendationScope"/> the rest of the service
    /// works with, or <c>null</c> when the id, the library and the folder do not agree.
    /// </summary>
    private async Task<WeeklyStationContext?> ResolveWeeklyScopeAsync(
        long libraryId, string? stationId, long? folderId, CancellationToken cancellationToken)
    {
        if (!TryParseWeeklyStation(stationId, out var stationLibrary, out var stationFolder, out var sectionType))
        {
            return null;
        }

        if (stationLibrary != libraryId || (folderId.HasValue && folderId.Value != stationFolder))
        {
            return null;
        }

        var folder = await FindWeeklyFolderAsync(libraryId, stationFolder, cancellationToken);
        return folder is null ? null : new WeeklyStationContext(BuildWeeklyScope(libraryId, folder, sectionType), sectionType);
    }

    private static string WeeklyTrackKey(RecommendationTrackDto track) => $"spotify:track:{track.Id}";

    private readonly SemaphoreSlim _weeklyGenerationGate = new(1, 1);
    private static readonly TimeSpan WeeklyFailureRetryDelay = TimeSpan.FromMinutes(15);

    private static bool ShouldQueueWeeklyGeneration(RecommendationGenerationStateDto? state, DateTimeOffset now)
        => state is null || state.Status == "pending"
           || (state.Status == "failed" && now - state.UpdatedAtUtc >= WeeklyFailureRetryDelay)
           || (state.Status == "running" && now - (state.StartedAtUtc ?? state.UpdatedAtUtc) >= RecommendationGenerationLease);

    private static async Task<List<T>> FetchWeeklyArtistMetadataAsync<T>(
        Func<Task<List<T>>> fetch, Action<Exception> reportFailure, CancellationToken cancellationToken)
    {
        try { return await fetch(); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is InvalidOperationException or HttpRequestException or TimeoutException or OperationCanceledException)
        {
            reportFailure(ex);
            return [];
        }
    }

    private static async IAsyncEnumerable<SpotifyRelatedArtist> EnumerateWeeklyArtistsAsync(
        IAsyncEnumerable<SpotifyRelatedArtist> seeds,
        Func<SpotifyRelatedArtist, CancellationToken, Task<List<SpotifyRelatedArtist>>> expand,
        Func<SpotifyRelatedArtist, bool> include,
        Action<Exception> reportFailure,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var seed in seeds.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artists = await FetchWeeklyArtistMetadataAsync(() => expand(seed, cancellationToken), reportFailure, cancellationToken);
            foreach (var artist in artists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(artist.Id) && include(artist) && seen.Add(artist.Id)) yield return artist;
            }
        }
    }

    private static IReadOnlyList<RecommendationTrackDto> SelectWeeklyArtistTracks(
        IReadOnlyList<RecommendationTrackDto> candidates, int limit, DateOnly weekStart, string sectionKey)
    {
        var cap = Math.Clamp(limit, 1, 100);
        var lanes = candidates.GroupBy(t => t.SeedArtistId ?? t.Artist.Id)
            .OrderBy(g => ComputeStableHash($"{weekStart:yyyyMMdd}:{sectionKey}:{g.Key}"))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new RecommendationLane(g.Key, new Queue<RecommendationTrackDto>(g
                .OrderBy(t => ComputeStableHash($"{weekStart:yyyyMMdd}:{sectionKey}:{t.Id}"))
                .ThenBy(t => t.Id, StringComparer.Ordinal)))).ToList();
        var laneByKey = lanes.ToDictionary(l => l.Key, StringComparer.Ordinal);
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        string IdentityRoot(string key)
        {
            if (!identities.TryGetValue(key, out var parent)) return identities[key] = key;
            return parent == key ? key : identities[key] = IdentityRoot(parent);
        }
        foreach (var track in candidates)
        {
            var root = IdentityRoot($"track:{track.Id}");
            if (!string.IsNullOrWhiteSpace(track.Isrc))
            {
                var isrcRoot = IdentityRoot($"isrc:{track.Isrc.Trim().ToUpperInvariant()}");
                if (root != isrcRoot) identities[root] = isrcRoot;
            }
        }
        string TrackIdentity(RecommendationTrackDto track) => IdentityRoot($"track:{track.Id}");
        var firstTracks = new Dictionary<string, RecommendationTrackDto>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        // Assign shared recordings through alternative tracks before allowing artist repeats.
        bool AssignFirstTrack(RecommendationLane lane, HashSet<string> visited)
        {
            foreach (var track in lane.Tracks)
            {
                var identity = TrackIdentity(track);
                if (!visited.Add(identity)) continue;
                if (!owners.TryGetValue(identity, out var owner) || AssignFirstTrack(laneByKey[owner], visited))
                {
                    owners[identity] = lane.Key;
                    firstTracks[lane.Key] = track;
                    return true;
                }
            }
            return false;
        }
        foreach (var lane in lanes)
        {
            AssignFirstTrack(lane, new HashSet<string>(StringComparer.Ordinal));
            if (firstTracks.Count >= cap) break;
        }
        var result = lanes.Where(l => firstTracks.ContainsKey(l.Key)).Select(l => firstTracks[l.Key]).ToList();
        var seen = result.Select(TrackIdentity).ToHashSet(StringComparer.Ordinal);
        while (result.Count < cap && lanes.Any(l => l.Tracks.Count > 0))
        {
            foreach (var lane in lanes)
            {
                while (lane.Tracks.TryDequeue(out var track))
                {
                    if (!seen.Add(TrackIdentity(track))) continue;
                    result.Add(track);
                    break;
                }
                if (result.Count >= cap) break;
            }
        }
        return result.Select((track, index) => track with { TrackPosition = index + 1 }).ToList();
    }

    /// <summary>
    /// Builds the weekly station exactly the way daily stations are built: the name, description
    /// and value all come from the folder, so a weekly card can never show a hard-coded section
    /// label instead of the configured folder name - including while generation has not run yet.
    /// </summary>
    private RecommendationStationDto CreateWeeklyStation(RecommendationScope scope, string type)
    {
        var day = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        return new RecommendationStationDto(
            scope.StationId,
            $"Recommendations - {scope.FolderName}",
            BuildDailyRecommendationDescription(scope.FolderName, day.DayOfWeek),
            type,
            scope.FolderName,
            0,
            Status: "generating",
            Cadence: "weekly",
            LibraryId: scope.LibraryId);
    }

    private async Task<(HashSet<string> Ids, HashSet<string> Names, Dictionary<string, string> DisplayNames, int FailedLookups)> GetWeeklyLibraryArtistMembershipAsync(
        long libraryId, long folderId, bool discover, CancellationToken cancellationToken)
    {
        var ids=new HashSet<string>(StringComparer.Ordinal);
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var displayNames=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var artist in await _repository.GetRecommendationArtistSeedsForLibraryAsync(libraryId,folderId,cancellationToken))
        {
            names.Add(ArtistAliasService.NormalizeName(artist.Name));
            foreach (var id in artist.SpotifyIds ?? []) { ids.Add(id);displayNames.TryAdd(id,artist.Name); }
        }
        if (_artistAliases is not null)
            foreach (var group in await _artistAliases.GetAllGroupsAsync(cancellationToken))
            {
                var groupNames=group.Aliases.Prepend(group.PreferredName).Select(ArtistAliasService.NormalizeName).ToList();
                if (groupNames.Any(names.Contains)) names.UnionWith(groupNames);
            }
        if (discover) _logger.LogDebug("Weekly artist discovery will resolve missing identities lazily after stored Spotify identities.");
        return (ids,names,displayNames,0);
    }

    internal static async IAsyncEnumerable<SpotifyRelatedArtist> EnumerateWeeklySeedsAsync(
        IReadOnlyList<LibraryRecommendationArtistSeedDto> artists,
        Func<LibraryRecommendationArtistSeedDto,CancellationToken,Task<string?>> resolve,
        Action<Exception> failed,[EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var emitted=new HashSet<string>(StringComparer.Ordinal);
        foreach (var artist in artists)
            foreach (var id in artist.SpotifyIds ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (emitted.Add(id)) yield return new(id,artist.Name,[],null);
            }
        foreach (var artist in artists.Where(a=>a.SpotifyIds is null || a.SpotifyIds.Count==0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids=await FetchWeeklyArtistMetadataAsync(async ()=>
            {
                var id=await resolve(artist,cancellationToken);
                return string.IsNullOrWhiteSpace(id) ? new List<string>() : new List<string>{id};
            },failed,cancellationToken);
            foreach(var id in ids)
                if(emitted.Add(id)) yield return new(id,artist.Name,[],null);
        }
    }

    private async Task<bool> IsWeeklyTrackMissingAsync(
        long libraryId, long folderId, RecommendationTrackDto track, CancellationToken cancellationToken)
    {
        var match = await _repository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(track.Isrc, track.Title, track.Artist.Name,
                track.Duration > 0 ? track.Duration * 1000 : null, SpotifySource, track.Id, track.Album.Title),
            libraryId: libraryId, folderId: folderId, cancellationToken: cancellationToken);
        if (match.LocalTrackId is not null || match.CandidateTrackIds.Count > 0) return false;
        if (string.IsNullOrWhiteSpace(track.DeezerId)) return true;
        var deezerMatch = await _repository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(track.Isrc, track.Title, track.Artist.Name,
                track.Duration > 0 ? track.Duration * 1000 : null, DeezerSource, track.DeezerId, track.Album.Title),
            libraryId: libraryId, folderId: folderId, cancellationToken: cancellationToken);
        return deezerMatch.LocalTrackId is null && deezerMatch.CandidateTrackIds.Count == 0;
    }

    private async Task<RecommendationDetailDto> BuildWeeklyRecommendationsAsync(
        RecommendationScope scope, string sectionType, DateOnly weekStart, CancellationToken cancellationToken, int? savedTrackCount = null)
    {
        var weekly = _settings?.LoadSettings().WeeklyRecommendations ?? new();
        var targetCount = savedTrackCount ?? (sectionType == WeeklyMissingType ? weekly.MissingTopSongs.TrackCount : weekly.SimilarArtists.TrackCount);
        targetCount = Math.Clamp(targetCount, 20, 100);
        var libraryId = scope.LibraryId;
        var folderId = scope.FolderId;
        var stationId = scope.StationId;
        if (_pathfinder is null || !await _pathfinder.HasPathfinderAuthContextAsync(cancellationToken))
            throw new InvalidOperationException("Spotify Pathfinder authentication is unavailable.");
        var membership = await GetWeeklyLibraryArtistMembershipAsync(libraryId, folderId, true, cancellationToken);
        var failedArtists = membership.FailedLookups;
        Exception? lastArtistFailure = null;
        void ReportArtistFailure(Exception ex)
        {
            failedArtists++;
            lastArtistFailure = ex;
            _logger.LogWarning(ex, "Skipping unavailable Pathfinder artist metadata for weekly section {SectionType} in library {LibraryId}.", sectionType, libraryId);
        }
        var libraryArtists=(await _repository.GetRecommendationArtistSeedsForLibraryAsync(libraryId,folderId,cancellationToken))
            .OrderBy(artist=>ComputeStableHash($"{weekStart}:{stationId}:{(artist.ArtistId > 0 ? artist.ArtistId.ToString() : artist.Name)}")).ToArray();
        async IAsyncEnumerable<SpotifyRelatedArtist> Seeds([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var artist in EnumerateWeeklySeedsAsync(libraryArtists,
                (local,ct)=>_spotifyArtists is null ? Task.FromResult<string?>(null)
                    : _spotifyArtists.EnsurePathfinderRecommendationArtistIdAsync(local.ArtistId,local.Name,ct),
                ReportArtistFailure,token))
            {
                membership.Ids.Add(artist.Id);
                membership.DisplayNames.TryAdd(artist.Id,artist.Name);
                yield return artist;
            }
        }
        var similar = sectionType == WeeklySimilarType;
        var artists = EnumerateWeeklyArtistsAsync(Seeds(cancellationToken),
            (seed, token) => similar ? _pathfinder.FetchArtistRelatedArtistsAsync(seed.Id, token, throwOnUnavailable: true)
                : Task.FromResult(new List<SpotifyRelatedArtist> { seed }),
            artist => !similar || (!membership.Ids.Contains(artist.Id)
                && !membership.Names.Contains(ArtistAliasService.NormalizeName(artist.Name))),
            ReportArtistFailure, cancellationToken);
        var rejected = await _repository.GetRecommendationRejectedTrackIdsAsync(libraryId, folderId, stationId, cancellationToken);
        var candidates = new List<RecommendationTrackDto>();
        await foreach (var artist in artists.WithCancellation(cancellationToken))
        {
            var top = await FetchWeeklyArtistMetadataAsync(
                () => _pathfinder.FetchArtistTopTracksAsync(artist.Id, cancellationToken, throwOnUnavailable: true), ReportArtistFailure, cancellationToken);
            foreach (var dto in top.Select(track => new RecommendationTrackDto(track.Id, track.Name, (track.DurationMs ?? 0) / 1000,
                    track.Isrc ?? string.Empty, 0, new(artist.Id, string.IsNullOrWhiteSpace(artist.Name) ? track.Artists ?? string.Empty : artist.Name),
                    new(track.AlbumId ?? string.Empty, track.Album ?? string.Empty, track.ImageUrl ?? string.Empty),
                    SpotifySource, track.SourceUrl, artist.Id)))
            {
                var isRejected = rejected.Contains(WeeklyTrackKey(dto));
                if (!isRejected && await IsWeeklyTrackMissingAsync(libraryId, folderId, dto, cancellationToken)) candidates.Add(dto);
            }
            // Stop only after enough different artists have usable candidates, not after a fixed seed count.
            if (SelectWeeklyArtistTracks(candidates, targetCount, weekStart, stationId).Select(t => t.SeedArtistId).Distinct().Count() >= targetCount) break;
        }
        return CreateWeeklyRecommendationResult(CreateWeeklyStation(scope, sectionType), candidates, targetCount,
            weekStart, failedArtists, _timeProvider.GetUtcNow(), lastArtistFailure);
    }

    private static RecommendationDetailDto CreateWeeklyRecommendationResult(
        RecommendationStationDto station, List<RecommendationTrackDto> candidates, int targetCount,
        DateOnly weekStart, int failedArtists, DateTimeOffset generated, Exception? lastArtistFailure)
    {
        var selected = SelectWeeklyArtistTracks(candidates, targetCount, weekStart, station.Id);
        if (selected.Count == 0 && failedArtists > 0)
            throw new InvalidOperationException("Spotify artist requests failed before any eligible weekly songs could be collected.", lastArtistFailure);
        var reasons = failedArtists > 0 ? new[] { "weekly_artist_lookup_incomplete" } : Array.Empty<string>();
        var message = failedArtists > 0 ? "Some Spotify artist lookups failed; showing the confirmed recommendations."
            : selected.Count == 0 ? "No eligible Pathfinder top tracks are missing from this library." : null;
        var status = selected.Count == 0 ? "empty" : "ready";
        station = station with
        {
            TrackCount = selected.Count, DistinctArtistCount = selected.Select(t => t.SeedArtistId).Distinct().Count(),
            GeneratedAtUtc = generated, Status = status, ImageUrl = selected.FirstOrDefault()?.Album.CoverMedium,
            ReasonCodes = reasons, Message = message
        };
        var selectedIds = selected.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var selectedIsrcs = selected.Where(t => !string.IsNullOrWhiteSpace(t.Isrc)).Select(t => t.Isrc).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reserves = SelectWeeklyArtistTracks(candidates.Where(t => !selectedIds.Contains(t.Id)
            && (string.IsNullOrWhiteSpace(t.Isrc) || !selectedIsrcs.Contains(t.Isrc))).ToList(), targetCount, weekStart, $"{station.Id}:reserve");
        return new(station, selected, generated, status, reasons, message, reserves, WeeklyTrackCount: targetCount, SeedScopeKey: station.Id);
    }

    public Task RefreshWeeklyRecommendationsAsync(string reason, CancellationToken cancellationToken)
        => RefreshWeeklyRecommendationsForWeekAsync(GetRecommendationWeekStart(DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime)), reason, cancellationToken);

    public async Task RefreshWeeklyRecommendationsForWeekAsync(DateOnly weekStart, string reason, CancellationToken cancellationToken)
    {
        if (!await AreDailyRecommendationsCompleteAsync(weekStart, cancellationToken)) return;
        // The folder is the unit, exactly as it is for daily generation: every eligible folder
        // generates its own weekly section instead of one section being generated per library.
        foreach (var folder in await GetRecommendationEligibleFoldersAsync(cancellationToken))
        {
            var libraryId = folder.LibraryId!.Value;
            foreach (var type in new[] { WeeklyMissingType, WeeklySimilarType })
                await GenerateWeeklyRecommendationsAsync(libraryId, folder.Id, type, weekStart, reason, cancellationToken);
        }
    }

    private async Task GenerateWeeklyRecommendationsAsync(
        long libraryId, long folderId, string type, DateOnly week, string reason, CancellationToken cancellationToken)
    {
        await _weeklyGenerationGate.WaitAsync(cancellationToken);
        try
        {
            var folder = await FindWeeklyFolderAsync(libraryId, folderId, cancellationToken);
            if (folder is null) return;
            var scope = BuildWeeklyScope(libraryId, folder, type);
            var stationId = scope.StationId;
            var key = new RecommendationGenerationStateKey(libraryId, folderId, stationId, week);
            var saved = await _repository.GetPlaylistTrackCandidateCacheAsync(WeeklyCacheSource, stationId, cancellationToken);
            var validSaved = IsValidWeeklySnapshot(saved, stationId, week);
            var prior = await _repository.GetRecommendationGenerationStateAsync(libraryId, folderId, stationId, week, cancellationToken);
            if (validSaved && prior?.Status == "completed") return;
            if (!validSaved && prior?.Status == "completed")
            {
                await _repository.RequestRecommendationGenerationAsync(key, reason, forceReset: true, cancellationToken);
                prior = await _repository.GetRecommendationGenerationStateAsync(libraryId, folderId, stationId, week, cancellationToken);
            }
            if (!ShouldQueueWeeklyGeneration(prior, _timeProvider.GetUtcNow())) return;
            if (!await _repository.TryStartRecommendationGenerationAsync(key, reason,
                _timeProvider.GetUtcNow() - RecommendationGenerationLease, cancellationToken)) return;
            var stage = "spotify_candidates";
            try
            {
                if (validSaved)
                {
                    stage = "publication";
                    if (!await _repository.PublishRecommendationSnapshotAsync(key, WeeklyCacheSource, stationId, saved!.SnapshotId!, saved.CandidatesJson, cancellationToken))
                        throw new InvalidOperationException("Weekly publication claim was lost.");
                    return;
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(20));
                var detail = await BuildWeeklyRecommendationsAsync(scope, type, week, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                stage = "publication";
                await PublishWeeklyRecommendationResultAsync(key, detail, deadline.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Weekly Pathfinder recommendations failed for {StationId}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(stationId));
                await _repository.FailRecommendationGenerationAsync(key,
                    ex.Message == "Spotify Pathfinder authentication is unavailable."
                        ? "pathfinder_authentication_unavailable"
                        : $"weekly_{stage}_{(ex is OperationCanceledException or TimeoutException ? "timeout" : "failed")}",
                    $"Weekly stage={stage}; failure={ex.GetType().Name}; cause={ex.InnerException?.GetType().Name ?? "none"}; retry scheduled.", CancellationToken.None);
            }
        }
        finally { _weeklyGenerationGate.Release(); }
    }

    private async Task PublishWeeklyRecommendationResultAsync(
        RecommendationGenerationStateKey key, RecommendationDetailDto detail, CancellationToken cancellationToken)
    {
        if (detail.Station.Id != key.StationId || detail.SeedScopeKey != key.StationId)
            throw new InvalidOperationException("Weekly recommendation seed scope does not match the publication scope.");
        // A weekly track already has its native Spotify identity. Optional ISRC/Deezer enrichment belongs
        // to the existing maintenance path and must not hold an otherwise valid weekly selection hostage.
        static RecommendationTrackDto Pending(RecommendationTrackDto track)
            => string.IsNullOrWhiteSpace(track.DeezerId) && string.IsNullOrWhiteSpace(track.MappingStatus)
                ? track with { MappingStatus = "pending" } : track;
        detail = DescribeWeeklyDeferredMappings(detail with
        {
            Tracks = detail.Tracks.Select(Pending).ToList(),
            ReserveTracks = detail.ReserveTracks?.Select(Pending).ToList()
        });
        if (!await _repository.PublishRecommendationSnapshotAsync(key, WeeklyCacheSource, key.StationId,
            $"v1:{key.TargetDay:yyyyMMdd}", JsonSerializer.Serialize(detail), cancellationToken))
            throw new InvalidOperationException("Weekly publication claim was lost.");
    }

    /// <summary>
    /// Re-derives the folder-sourced parts of a weekly station. A cached selection may have been
    /// written under an older folder name, and the waiting/failed path builds its station from
    /// scratch, so the identity is re-applied on every read - a weekly card can therefore never
    /// change heading while the page is open.
    /// </summary>
    private RecommendationStationDto ApplyWeeklyFolderIdentity(
        RecommendationStationDto station, RecommendationScope scope, string sectionType)
    {
        var day = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        return station with
        {
            Id = scope.StationId,
            Name = $"Recommendations - {scope.FolderName}",
            Description = BuildDailyRecommendationDescription(scope.FolderName, day.DayOfWeek),
            Type = sectionType,
            Value = scope.FolderName,
            Cadence = "weekly",
            LibraryId = scope.LibraryId,
            LibraryName = scope.FolderName
        };
    }

    private static RecommendationDetailDto? ReadFolderScopedWeeklyPool(
        PlaylistTrackCandidateCacheDto? saved, string stationId)
    {
        if (saved is null) return null;
        var detail = JsonSerializer.Deserialize<RecommendationDetailDto>(saved.CandidatesJson);
        return detail?.Station.Id == stationId && detail.SeedScopeKey == stationId ? detail : null;
    }

    private static bool IsValidWeeklySnapshot(PlaylistTrackCandidateCacheDto? saved, string stationId, DateOnly week)
    {
        if (saved?.SnapshotId != $"v1:{week:yyyyMMdd}" || !saved.IsComplete) return false;
        try { return ReadFolderScopedWeeklyPool(saved, stationId) is not null; }
        catch (JsonException) { return false; }
    }

    private async Task<RecommendationDetailDto?> PrepareWeeklyRecommendationsAsync(long libraryId, string stationId, int limit, CancellationToken cancellationToken, bool resolveMappings = true, bool allowRefill = true, DateOnly? targetWeek = null, long? folderId = null)
    {
        var resolved = await ResolveWeeklyScopeAsync(libraryId, stationId, folderId, cancellationToken);
        if (resolved is null) return null;
        var scope = resolved.Scope;
        var type = resolved.SectionType;
        var week = targetWeek ?? GetRecommendationWeekStart(DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime));
        var cached = await _repository.GetPlaylistTrackCandidateCacheAsync(WeeklyCacheSource, scope.StationId, cancellationToken);
        var detail = cached?.SnapshotId == $"v1:{week:yyyyMMdd}" ? ReadFolderScopedWeeklyPool(cached, scope.StationId) : null;
        if (detail is null) return null;
        if (resolveMappings) detail = await EnsureWeeklyMappingsAsync(detail, scope.StationId, week, cancellationToken);
        var membership = type == WeeklySimilarType
            ? await GetWeeklyLibraryArtistMembershipAsync(scope.LibraryId, scope.FolderId, false, cancellationToken)
            : default;
        var rejected = await _repository.GetRecommendationRejectedTrackIdsAsync(scope.LibraryId, scope.FolderId, scope.StationId, cancellationToken);
        var ignored = await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(RecommendationSource, scope.StationId, cancellationToken);
        async Task<List<RecommendationTrackDto>> SelectMissingAsync(RecommendationDetailDto preparedDetail)
        {
            var visible = new List<RecommendationTrackDto>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenIsrcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in preparedDetail.Tracks.Concat(preparedDetail.ReserveTracks ?? [])
                    .Where(track => !(rejected.Contains(WeeklyTrackKey(track)) || ignored.Contains(WeeklyTrackKey(track)) || ignored.Contains(track.Id))))
            {
                if (type == WeeklySimilarType && (membership.Ids.Contains(track.SeedArtistId ?? track.Artist.Id)
                    || membership.Names.Contains(ArtistAliasService.NormalizeName(track.Artist.Name)))) continue;
                if (seenIds.Contains(track.Id) || (!string.IsNullOrWhiteSpace(track.Isrc) && seenIsrcs.Contains(track.Isrc))) continue;
                if (await IsWeeklyTrackMissingAsync(scope.LibraryId, scope.FolderId, track, cancellationToken))
                {
                    seenIds.Add(track.Id);
                    if (!string.IsNullOrWhiteSpace(track.Isrc)) seenIsrcs.Add(track.Isrc);
                    visible.Add(track with { TrackPosition = visible.Count + 1 });
                }
            }
            return visible;
        }
        var visible = await SelectMissingAsync(detail);
        var savedCount = detail.WeeklyTrackCount ?? 50;
        var cappedLimit = limit <= 0 ? savedCount : Math.Min(Math.Clamp(limit, 1, 100), savedCount);
        if (allowRefill && visible.Count < cappedLimit && detail.Tracks.Count > 0
            && _pathfinder is not null)
        {
            var refill = await BuildWeeklyRecommendationsAsync(scope, type, week, cancellationToken, detail.WeeklyTrackCount ?? 50);
            var existingIds = detail.Tracks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var reserveTracks = (detail.ReserveTracks ?? []).Concat(refill.Tracks).Concat(refill.ReserveTracks ?? [])
                .Where(t => !existingIds.Contains(t.Id)).GroupBy(t => t.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            detail = await MapWeeklyTracksAsync(detail with { ReserveTracks = reserveTracks }, cancellationToken);
            visible = await SelectMissingAsync(detail);
        }
        var statusVisible = visible.Count > 0 ? "ready" : "empty";
        var prepared = detail with { Tracks = visible.Take(cappedLimit).ToList(), ReserveTracks = visible.Skip(cappedLimit).ToList(), Status = statusVisible,
            Station = ApplyWeeklyFolderIdentity(detail.Station with { TrackCount = Math.Min(cappedLimit, visible.Count), Status = statusVisible,
                DistinctArtistCount = visible.Select(t => t.SeedArtistId).Distinct().Count() }, scope, type) };
        await _repository.UpsertPlaylistTrackCandidateCacheAsync(WeeklyCacheSource, scope.StationId, cached!.SnapshotId,
            JsonSerializer.Serialize(prepared), 0, null, null, true, cancellationToken);
        return prepared;
    }

    private async Task<RecommendationDetailDto?> GetWeeklyRecommendationsAsync(
        long libraryId, string stationId, int limit, CancellationToken cancellationToken, long? folderId = null)
    {
        var resolved = await ResolveWeeklyScopeAsync(libraryId, stationId, folderId, cancellationToken);
        if (resolved is null) return null;
        var scope = resolved.Scope;
        var type = resolved.SectionType;
        var week = GetRecommendationWeekStart(DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime));
        var saved = await _repository.GetPlaylistTrackCandidateCacheAsync(WeeklyCacheSource, scope.StationId, cancellationToken);
        var detail = ReadFolderScopedWeeklyPool(saved, scope.StationId);
        var state = await _repository.GetRecommendationGenerationStateAsync(
            scope.LibraryId, scope.FolderId, scope.StationId, week, cancellationToken);
        if (detail is null)
        {
            var failureReason = state?.ReasonCode;
            var failed = state?.Status == "failed";
            var status = failed ? "failed" : "waiting";
            var message = failed ? DescribeWeeklyGenerationFailure(failureReason)
                : "Waiting for Monday's daily charts and weekly generation.";
            var reasons = failed && !string.IsNullOrWhiteSpace(failureReason) ? new[] { failureReason } : Array.Empty<string>();
            return new(ApplyWeeklyFolderIdentity(CreateWeeklyStation(scope, type), scope, type) with { Status = status, Message = message, ReasonCodes = reasons },
                [], _timeProvider.GetUtcNow(), status, reasons, message);
        }
        var rejected = await _repository.GetRecommendationRejectedTrackIdsAsync(scope.LibraryId, scope.FolderId, scope.StationId, cancellationToken);
        var ignored = await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(RecommendationSource, scope.StationId, cancellationToken);
        var tracks = detail.Tracks.Concat(detail.ReserveTracks ?? [])
            .Where(t => !rejected.Contains(WeeklyTrackKey(t)) && !ignored.Contains(WeeklyTrackKey(t)) && !ignored.Contains(t.Id))
            .DistinctBy(t => t.Id).Take(limit <= 0 ? detail.WeeklyTrackCount ?? 50 : Math.Min(Math.Clamp(limit, 1, 100), detail.WeeklyTrackCount ?? 50)).Select((t, i) => t with { TrackPosition = i + 1 }).ToList();
        var previous = saved!.SnapshotId != $"v1:{week:yyyyMMdd}";
        var statusSaved = previous ? (state?.Status == "failed" ? "refresh_failed" : "refreshing") : tracks.Count > 0 ? "ready" : "empty";
        var messageSaved = previous ? state?.Status == "failed"
            ? "Weekly refresh failed. " + DescribeWeeklyGenerationFailure(state?.ReasonCode) + " Showing the previous saved selection."
            : "Showing the previous saved selection while the new weekly selection is prepared." : detail.Message;
        var savedReasons = previous && state?.Status == "failed" && !string.IsNullOrWhiteSpace(state.ReasonCode)
            ? new[] { state.ReasonCode } : detail.ReasonCodes;
        return detail with { Tracks = tracks, Status = statusSaved, Message = messageSaved, ReasonCodes = savedReasons,
            Station = ApplyWeeklyFolderIdentity(detail.Station with { TrackCount = tracks.Count, Status = statusSaved, Message = messageSaved, ReasonCodes = savedReasons,
                GeneratedAtUtc = detail.GeneratedAtUtc, DistinctArtistCount = tracks.Select(t => t.SeedArtistId ?? t.Artist.Id).Distinct().Count() }, scope, type) };
    }

    private static string DescribeWeeklyGenerationFailure(string? reason)
        => reason switch
        {
            "pathfinder_authentication_unavailable" => "Spotify Pathfinder authentication is unavailable; retry scheduled.",
            "weekly_spotify_candidates_timeout" => "Spotify artist discovery or top-song lookup timed out; retry scheduled.",
            "weekly_spotify_candidates_failed" => "Spotify artist discovery or top-song lookup failed; retry scheduled.",
            "weekly_track_mapping_timeout" => "Weekly track metadata mapping timed out; retry scheduled.",
            "weekly_track_mapping_failed" => "Weekly track metadata mapping failed; retry scheduled.",
            "weekly_publication_failed" or "weekly_publication_timeout" => "Weekly recommendations could not be saved; retry scheduled.",
            _ => "Weekly generation failed and will retry."
        };

    private static RecommendationDetailDto CreateDisabledDailyResponse(RecommendationScope scope, DateTimeOffset now)
    {
        const string message = "Daily recommendations are disabled. Enable a source to resume.";
        var station = new RecommendationStationDto(scope.StationId, $"Recommendations - {scope.FolderName}",
            message, RecommendationSourceId, scope.FolderName, 0, Status: "disabled", Message: message);
        return new(station, [], now, "disabled", Message: message);
    }

    internal static DateTimeOffset DailyBoundaryUtc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
        => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(time, DateTimeKind.Unspecified), zone));

    internal static bool AutomaticDailyAllowed(DateTimeOffset nowLocal)
        => nowLocal.TimeOfDay < TimeSpan.FromHours(22);

    private RecommendationDetailDto StampDailyResponse(RecommendationDetailDto detail, DateOnly day)
    {
        var now = _timeProvider.GetUtcNow();
        var expiry = DailyBoundaryUtc(day.AddDays(1), TimeOnly.MinValue, _timeProvider.LocalTimeZone);
        return detail with { DailyDateLocal = day, ExpiresAtUtc = expiry, ServerNowUtc = now,
            Station = detail.Station with { DailyDateLocal = day, ExpiresAtUtc = expiry, ServerNowUtc = now } };
    }

    private async Task<RecommendationDetailDto> GetSavedDailyResponseAsync(
        RecommendationScope scope, DateOnly targetDay, string? artwork, int limit, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetDay = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
            var response = await GetCurrentDailyResponseAsync(scope, targetDay, artwork, limit, cancellationToken);
            if (DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime) == targetDay) return response;
            artwork = null; // A read crossing midnight must also discard the old day's cover.
        }
    }

    private async Task<RecommendationDetailDto> GetCurrentDailyResponseAsync(
        RecommendationScope scope, DateOnly targetDay, string? artwork, int limit, CancellationToken cancellationToken)
    {
        var configuration = _settings?.LoadSettings().DailyRecommendations ?? new();
        if (configuration.IsDisabled)
            return StampDailyResponse(CreateDisabledDailyResponse(scope, _timeProvider.GetUtcNow()), targetDay);
        var pool = await GetDailyPoolAsync(BuildDailyCacheKey(scope.ScopeKey, targetDay), scope, targetDay, artwork, cancellationToken);
        var state = await _repository.GetRecommendationGenerationStateAsync(scope.LibraryId, scope.FolderId, scope.StationId, targetDay, cancellationToken);
        var late = !AutomaticDailyAllowed(_timeProvider.GetLocalNow());
        if (pool is null)
        {
            var reasons = state?.Status == "failed" ? ResolveGenerationStateReasonCodes(state) : new[] { GenerationQueuedReason };
            var unavailable = CreateUnavailableRecommendationDetail(scope, artwork, targetDay, reasons);
            var unavailableStatus = late ? "waiting" : state?.Status == "failed" ? "failed" : "generating";
            var unavailableMessage = late ? "Today's selection is empty. Automatic recommendations resume at midnight (22:00 cutoff)."
                : state?.Status == "failed" ? "Today's selection could not be generated. " + BuildRecommendationUnavailableMessage(reasons)
                : "Today's selection is being prepared. No expired daily playlist is shown.";
            return StampDailyResponse(unavailable with { Status = unavailableStatus, Message = unavailableMessage,
                DailyTrackCount = configuration.TrackCount, DailyAllocations = configuration.Allocations(),
                Station = unavailable.Station with { Status = unavailableStatus, Message = unavailableMessage, TrackCount = 0 } }, targetDay);
        }
        var excluded = BuildNormalizedRecommendationIdSet(await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(RecommendationSource, scope.StationId, cancellationToken));
        excluded.UnionWith(await _repository.GetRecommendationRejectedTrackIdsAsync(scope.LibraryId, scope.FolderId, scope.StationId, cancellationToken));
        // Generation and background maintenance check library ownership before saving the pool.
        // Display requests only project that saved result: rerunning the identity lookup here
        // starts a database transaction and can block every recommendation section behind a writer.
        // Current AI preferences use the evidence already saved with each track, without fetching.
        var aiMode = _settings is null ? pool.DailyAiFilterMode ?? configuration.AiFilterMode : configuration.AiFilterMode;
        var eligibleTracks = pool.Tracks.Where(track => EvaluateDailyAiPolicy(track.AiEvidence, aiMode) is null).ToList();
        var savedCount = pool.DailyTrackCount ?? MaxDailyRecommendations;
        var viewLimit = limit <= 0 ? savedCount : Math.Min(limit, savedCount);
        var selected = pool.DailyAllocations is null
            ? BuildVisibleDailySelection(eligibleTracks, excluded, savedCount, targetDay)
            : BuildSavedDailySelection(eligibleTracks, pool.DailyAllocations, savedCount, targetDay, excluded, pool.Diagnostics?.PublishedTrackIds);
        var complete = pool.DailyAllocations is null || HasCompleteDailyAllocation(selected, pool.DailyAllocations);
        var published = pool.Diagnostics?.InitialSelectionPublished ?? pool.Status == "ready";
        var finalized = published || pool.Status == "ready";
        var visible = !finalized || !complete ? new List<RecommendationTrackDto>() : selected.Take(viewLimit).ToList();
        var shortages = pool.DailyAllocations?.ToDictionary(p => p.Key,
            p => Math.Max(0, p.Value - selected.Count(t => t.RecommendationOrigin == p.Key)));
        var exhausted = pool.Diagnostics?.ExhaustedSources?.Where(p => p.Value && (shortages?.GetValueOrDefault(p.Key) ?? 0) > 0).Select(p => p.Key).ToList() ?? [];
        var refreshFailed = state?.Status == "failed" && finalized;
        var status = complete && finalized ? (refreshFailed ? "refresh_failed" : "ready") : late ? "waiting" : published ? "refilling"
            : state?.Status == "failed" ? "failed" : exhausted.Count > 0 ? "unavailable" : "generating";
        var progress = pool.DailyAllocations is null ? "" : string.Join(", ", pool.DailyAllocations.Where(p => p.Value > 0)
            .Select(p => $"{p.Key}: {p.Value - shortages![p.Key]}/{p.Value}"));
        var reserves = Math.Max(0, eligibleTracks.Count(t => !excluded.Contains(t.Id)) - selected.Count);
        var message = !finalized && complete ? "Today's full selection has been collected and is being finalized. " + progress
            : !complete
            ? (published ? "Replacement selection is incomplete. " : "Today's initial selection is not ready; no partial playlist is published. ")
                + BuildDailyShortfallMessage(shortages ?? new Dictionary<string,int>()) + " " + progress + ". "
                + (late ? "Automatic collection resumes at midnight (22:00 cutoff). " : "")
                + (exhausted.Count > 0 ? "Eligible starting songs exhausted for " + string.Join(", ", exhausted) + ". " : "")
                + $"{reserves} replacement suggestions available."
            : $"{selected.Count}/{savedCount} tracks. {progress}. {reserves} replacement suggestions available.";
        if (refreshFailed) message += " The latest refresh failed; the current-day saved selection is retained.";
        if (pool.Diagnostics?.ProviderFailures is { Count: > 0 } failures && !complete)
            message += " Provider retries required: " + string.Join(", ", failures.Where(p => shortages?.GetValueOrDefault(p.Key) > 0).Select(p => p.Key + " (" + p.Value + ")")) + ".";
        if (pool.ReasonCodes?.Contains("no_seed_tracks") == true)
            message += " No usable starting songs are available in this folder.";
        var diagnostics = pool.Diagnostics is null ? null : pool.Diagnostics with
        { MissingSourceCounts = shortages ?? pool.Diagnostics.MissingSourceCounts, DestinationCount = visible.Count, OverflowCount = reserves };
        return StampDailyResponse(pool with { Tracks = visible, Status = status, Message = message, Diagnostics = diagnostics, DailyAiFilterMode = aiMode,
            ReasonCodes = state?.Status == "failed" && !complete ? ResolveGenerationStateReasonCodes(state) : pool.ReasonCodes,
            Station = pool.Station with { Id = scope.StationId, Name = $"Recommendations - {scope.FolderName}", Value = scope.FolderName,
                Description = BuildDailyRecommendationDescription(scope.FolderName, targetDay.DayOfWeek),
                ImageUrl = artwork ?? pool.Station.ImageUrl, TrackCount = visible.Count, Status = status, Message = message,
                GeneratedAtUtc = pool.GeneratedAtUtc } }, targetDay);
    }

    private const string FolderContentMusic = "music";
    private const string FolderContentAtmos = "atmos";
    private const string FolderContentVideo = "video";
    private const string FolderContentPodcast = "podcast";
    public sealed class LibraryRecommendationCollaborators
    {
        public SpotifyPathfinderMetadataClient? Pathfinder { get; init; }
        public SpotifyArtistService? SpotifyArtists { get; init; }
        public SpotifyMetadataService? SpotifyMetadata { get; init; }
        public ArtistAliasService? ArtistAliases { get; init; }
        public LibraryRepository Repository { get; init; } = null!;
        public ShazamRecognitionService ShazamRecognitionService { get; init; } = null!;
        public ShazamDiscoveryService ShazamDiscoveryService { get; init; } = null!;
        public DeezerClient DeezerClient { get; init; } = null!;
        public DeezerGatewayService DeezerGatewayService { get; init; } = null!;
        public ITrackIdentityResolver TrackIdentityResolver { get; init; } = null!;
        public DownloadDedupeService DedupeService { get; init; } = null!;
        public DeezSpoTagSettingsService? Settings { get; init; }
        public SpotifyRecommendationService? SpotifyRecommendations { get; init; }
        public TidalCatalogClient? TidalCatalog { get; init; }
        public PlatformTrackIdentityResolver? PlatformIdentities { get; init; }
    }

    public const string RecommendationSource = "recommendations";
    public const string RecommendationSourceId = "daily-rotation";
    /// <summary>
    ///     Deezer's source id, aliased to the one canonical definition.
    /// </summary>
    /// <remarks>
    ///     This was a private byte-identical copy of the canonical value. Every use either reads a
    ///     stored artist source id or labels a recommendation's platform, so a copy that drifted
    ///     would read back nothing and produce a recommendation with no matching track. The local
    ///     name is kept because this file already uses it; the value is defined once.
    /// </remarks>
    private const string DeezerSource = DownloadTagSourceHelper.DeezerSource;

    private const string SpotifySource = DownloadTagSourceHelper.SpotifySource;

    private const string AppleSource = DownloadTagSourceHelper.AppleSource;
    private const string StatusMatched = "matched";
    private const string StatusMatchedNoRelated = "matched_no_related";
    private const string StatusMatchedNoDeezerResolution = "matched_no_deezer_resolution";
    private const string StatusError = "error";
    private const string StatusNoMatch = "no_match";
    private const string UnknownTitle = "Unknown";
    private const string UnknownArtist = "Unknown Artist";
    private const string UnknownAlbum = "Unknown Album";
    private const string DailyPoolCacheSource = "recommendations-daily-pool";
    private const string ExposureHistoryCacheSource = "recommendations-exposure-history";
    private const string DailyPoolSnapshotVersion = "v1";
    private const string ExposureHistorySnapshotVersion = "v1";
    private const string EmptyPoolReason = "empty_pool";
    private const string GenerationQueuedReason = "generation_queued";
    private const string BackgroundGenerationFailedReason = "background_generation_failed";
    private const string PersistFailedReason = "persist_failed";
    private const string PersistTimedOutReason = "persist_timed_out";
    private const string GenerationReasonManualRebuild = "manual-rebuild";
    private const string GenerationReasonManualReplacement = "manual-replacement";
    private const int PersistedFailureReasonMaxLength = 240;
    private const int DeezerMetadataCacheLimit = 2048;

    private const int MaxDailyRecommendations = 50;
    private const int RecommendationPoolMultiplier = 3;
    private const int RecommendationPoolLimit = MaximumConfiguredDailyTracks * RecommendationPoolMultiplier;
    private const int DailySeedProbeLimit = 32;
    private const int DailyRecommendationsPerSeed = 3;
    private const int DeezerSearchLimit = 12;
    private const int MaxArtistOccurrences = 2;
    private const int MaxAlbumOccurrences = 2;
    private const int ShazamRelatedPerSeed = 10;
    private const int ShazamSimilarLookupLimit = 20;
    private const double ShazamDeezerMinTitleSimilarity = 0.62d;
    private const double ShazamDeezerMinArtistSimilarity = 0.52d;
    private const int ShazamSelectedSeedLimit = 12;
    private const int ShazamBackgroundBatchSize = 120;
    private const int RecommendationExposureRetentionDays = 14;
    private const int MaximumConfiguredDailyTracks = 100;
    private static readonly TimeSpan RecommendationGenerationLease = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TrackMixRequestTimeout = TimeSpan.FromSeconds(8);

    private readonly LibraryRepository _repository;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly ShazamDiscoveryService _shazamDiscoveryService;
    private readonly DeezerClient _deezerClient;
    private readonly DeezerGatewayService _deezerGatewayService;
    private readonly ITrackIdentityResolver _trackIdentityResolver;
    private readonly DownloadDedupeService _dedupeService;
    private readonly string _recommendationArtworkRootPath;
    private readonly ILogger<LibraryRecommendationService> _logger;
    private readonly CancellationToken _backgroundCancellationToken;
    private readonly ConcurrentDictionary<string, RecommendationDetailDto> _dailyPoolCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _backgroundScans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RecommendationTrackDto> _deezerRecommendationMetadataCache = new(StringComparer.Ordinal);

    private readonly TimeProvider _timeProvider;
    private readonly DeezSpoTagSettingsService? _settings;
    private readonly SpotifyRecommendationService? _spotifyRecommendations;
    private readonly TidalCatalogClient? _tidalCatalog;
    private readonly PlatformTrackIdentityResolver? _platformIdentities;

    public LibraryRecommendationService(
        LibraryRecommendationCollaborators collaborators,
        IWebHostEnvironment webHostEnvironment,
        ILogger<LibraryRecommendationService> logger,
        IHostApplicationLifetime? hostApplicationLifetime = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _settings = collaborators.Settings;
        _spotifyRecommendations = collaborators.SpotifyRecommendations;
        _tidalCatalog = collaborators.TidalCatalog;
        _platformIdentities = collaborators.PlatformIdentities;
        _repository = collaborators.Repository;
        _pathfinder = collaborators.Pathfinder;
        _spotifyArtists = collaborators.SpotifyArtists;
        _spotifyMetadata = collaborators.SpotifyMetadata;
        _artistAliases = collaborators.ArtistAliases;
        _shazamRecognitionService = collaborators.ShazamRecognitionService;
        _shazamDiscoveryService = collaborators.ShazamDiscoveryService;
        _deezerClient = collaborators.DeezerClient;
        _deezerGatewayService = collaborators.DeezerGatewayService;
        _trackIdentityResolver = collaborators.TrackIdentityResolver;
        _dedupeService = collaborators.DedupeService;
        _recommendationArtworkRootPath = string.IsNullOrWhiteSpace(webHostEnvironment.WebRootPath)
            ? string.Empty
            : Path.Join(webHostEnvironment.WebRootPath, "images", "recommendations");
        _logger = logger;
        _backgroundCancellationToken = hostApplicationLifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    private sealed record RecommendationScope(
        long LibraryId,
        long FolderId,
        string FolderName,
        string StationId,
        string ScopeKey);
    private sealed record WeeklyStationContext(RecommendationScope Scope, string SectionType);
    private sealed record RecommendationArtworkCandidate(string Url, string DayKey);
    private sealed record PersistedDailyPoolDto(
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<RecommendationTrackDto> Tracks,
        string? StationImageUrl = null,
        int? TrackCount = null,
        IReadOnlyDictionary<string, int>? Allocations = null,
        DailyRecommendationAiFilterMode? AiFilterMode = null,
        DailyRecommendationDiagnosticsDto? Diagnostics = null,
        string Status = "ready",
        IReadOnlyList<string>? ReasonCodes = null,
        string? SeedScopeKey = null);
    private sealed record RecommendationExposureHistoryDto(
        IReadOnlyList<RecommendationExposureEntryDto> Entries);
    private sealed record RecommendationExposureEntryDto(string TrackId, string Day);
    private sealed record RecommendationBuildResult(RecommendationDetailDto? Detail, IReadOnlyList<string> ReasonCodes);
    private sealed record ShazamRecommendationBuildResult(List<RecommendationTrackDto> Tracks, string EmptyReasonCode)
    {
        public static ShazamRecommendationBuildResult Empty(string reasonCode)
            => new(new List<RecommendationTrackDto>(), reasonCode);
    }

    private sealed record ResolvedRecommendationSeed(LibraryRecommendationSeedTrackDto LocalTrack, string DeezerTrackId);
    private sealed class RecommendationAccumulator
    {
        public RecommendationAccumulator(int limit)
        {
            Limit = limit;
            DestinationTracks = new List<RecommendationTrackDto>(limit);
            OverflowTracks = new List<RecommendationTrackDto>(limit);
            SeenRecommendationIds = new HashSet<string>(StringComparer.Ordinal);
            ArtistCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            AlbumCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public int Limit { get; }
        public List<RecommendationTrackDto> DestinationTracks { get; }
        public List<RecommendationTrackDto> OverflowTracks { get; }
        public HashSet<string> SeenRecommendationIds { get; }
        public Dictionary<string, int> ArtistCounts { get; }
        public Dictionary<string, int> AlbumCounts { get; }
        public int FailedSeedLoads { get; set; }
    }
    private sealed record PersistDailyPoolResult(bool Success, string? ReasonCode = null)
    {
        public static PersistDailyPoolResult Ok { get; } = new(true);
        public static PersistDailyPoolResult Failed(string reasonCode) => new(false, reasonCode);
    }

    private async Task<IReadOnlyList<FolderDto>> GetRecommendationEligibleFoldersAsync(CancellationToken cancellationToken)
    {
        var folders = await _repository.GetFoldersAsync(cancellationToken);
        return folders
            .Where(folder => folder.Enabled
                             && folder.LibraryId.HasValue
                             && folder.LibraryId.Value > 0
                             && !IsExcludedFromRecommendations(folder))
            .ToList();
    }

    private static List<FolderDto> FilterScopedFolders(
        IReadOnlyList<FolderDto> folders,
        long libraryId,
        long? folderId)
    {
        var filtered = folders
            .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value == libraryId)
            .ToList();

        if (folderId.HasValue)
        {
            filtered = filtered
                .Where(folder => folder.Id == folderId.Value)
                .ToList();
        }

        return filtered;
    }

    private async Task<IReadOnlyList<FolderDto>> GetScopedFoldersAsync(
        long libraryId,
        long? folderId,
        CancellationToken cancellationToken)
    {
        var folders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        return FilterScopedFolders(folders, libraryId, folderId);
    }

    private static bool IsExcludedFromRecommendations(FolderDto folder)
    {
        var mode = ResolveFolderContentType(folder);
        return mode == FolderContentAtmos
            || mode == FolderContentVideo
            || mode == FolderContentPodcast;
    }

    private static string ResolveFolderContentType(FolderDto folder)
    {
        var normalized = (folder.DesiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return FolderContentMusic;
        }

        if (normalized.Contains("atmos", StringComparison.Ordinal))
        {
            return FolderContentAtmos;
        }

        if (normalized.Contains("video", StringComparison.Ordinal))
        {
            return FolderContentVideo;
        }

        if (normalized.Contains("podcast", StringComparison.Ordinal))
        {
            return FolderContentPodcast;
        }

        // Legacy numeric quality rank for Atmos.
        if (normalized == "5")
        {
            return FolderContentAtmos;
        }

        // Legacy destination mode "0" historically mapped to mixed content.
        // Default it to music unless the folder naming strongly indicates video/podcast.
        if (normalized == "0")
        {
            var folderDescriptor = $"{folder.DisplayName} {folder.RootPath}".ToLowerInvariant();
            if (folderDescriptor.Contains("video", StringComparison.Ordinal))
            {
                return FolderContentVideo;
            }

            if (folderDescriptor.Contains("podcast", StringComparison.Ordinal))
            {
                return FolderContentPodcast;
            }

            return FolderContentMusic;
        }

        return FolderContentMusic;
    }

    private static string? ResolveRecommendationArtworkUrl(
        string stationId,
        IReadOnlyDictionary<string, string> artworkAssignments)
    {
        if (string.IsNullOrWhiteSpace(stationId) || artworkAssignments.Count == 0)
        {
            return null;
        }

        return artworkAssignments.TryGetValue(stationId, out var imageUrl)
            ? imageUrl
            : null;
    }

    private Dictionary<string, string> BuildRecommendationArtworkAssignments(
        IReadOnlyList<FolderDto> folders,
        DateTimeOffset nowLocal)
    {
        if (folders.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var windowCandidates = GetRecommendationArtworkCandidatesForWindow(nowLocal);
        if (windowCandidates.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var orderedCandidates = windowCandidates
            .OrderBy(candidate => ComputeStableHash($"{nowLocal:yyyyMMdd}|{candidate.Url}"))
            .ThenBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orderedCandidates.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var stationIds = folders
            .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value > 0)
            .Select(folder => BuildStationId(folder.LibraryId!.Value, folder.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var assignments = new Dictionary<string, string>(stationIds.Count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < stationIds.Count; index++)
        {
            assignments[stationIds[index]] = orderedCandidates[index % orderedCandidates.Count].Url;
        }

        return assignments;
    }

    private IReadOnlyList<RecommendationArtworkCandidate> GetRecommendationArtworkCandidatesForWindow(DateTimeOffset nowLocal)
    {
        var candidates = GetRecommendationArtworkCandidates();
        if (candidates.Count == 0)
        {
            return Array.Empty<RecommendationArtworkCandidate>();
        }

        var dayKey = nowLocal.DayOfWeek.ToString().ToLowerInvariant();
        var dayCandidates = candidates
            .Where(candidate => string.Equals(candidate.DayKey, dayKey, StringComparison.Ordinal))
            .ToList();

        return dayCandidates.Count > 0
            ? dayCandidates
            : candidates.ToList();
    }

    private IReadOnlyList<RecommendationArtworkCandidate> GetRecommendationArtworkCandidates()
    {
        if (string.IsNullOrWhiteSpace(_recommendationArtworkRootPath)
            || !Directory.Exists(_recommendationArtworkRootPath))
        {
            return Array.Empty<RecommendationArtworkCandidate>();
        }

        var results = new List<RecommendationArtworkCandidate>();
        foreach (var filePath in Directory.EnumerateFiles(_recommendationArtworkRootPath, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(filePath);
            if (!IsSupportedRecommendationArtworkExtension(extension))
            {
                continue;
            }

            var dayKey = NormalizeRecommendationArtworkDay(Path.GetFileNameWithoutExtension(filePath));
            if (string.IsNullOrWhiteSpace(dayKey))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(_recommendationArtworkRootPath, filePath);
            var urlPath = relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            results.Add(new RecommendationArtworkCandidate($"/images/recommendations/{urlPath}", dayKey));
        }

        return results;
    }

    private static bool IsSupportedRecommendationArtworkExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeRecommendationArtworkDay(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "monday" => "monday",
            "tuesday" => "tuesday",
            "tueday" => "tuesday",
            "wednesday" => "wednesday",
            "thursday" => "thursday",
            "friday" => "friday",
            "saturday" => "saturday",
            "sunday" => "sunday",
            _ => null
        };
    }

    private static RecommendationScope? ResolveScope(
        long libraryId,
        IReadOnlyList<FolderDto> folders,
        string? stationId,
        long? folderId)
    {
        if (folders.Count == 0)
        {
            return null;
        }

        if (TryParseStationId(stationId, out var stationLibraryId, out var stationFolderId))
        {
            if (stationLibraryId != libraryId || (folderId.HasValue && stationFolderId != folderId.Value))
            {
                return null;
            }

            var stationFolder = folders.FirstOrDefault(folder => folder.Id == stationFolderId);
            if (stationFolder is null)
            {
                return null;
            }

            return BuildScope(libraryId, stationFolder);
        }

        if (!string.IsNullOrWhiteSpace(stationId)
            && !string.Equals(stationId, RecommendationSourceId, StringComparison.OrdinalIgnoreCase)) return null;

        if (folderId.HasValue)
        {
            var selectedFolder = folders.FirstOrDefault(folder => folder.Id == folderId.Value);
            if (selectedFolder is null)
            {
                return null;
            }

            return BuildScope(libraryId, selectedFolder);
        }

        var firstFolder = folders
            .OrderBy(folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First();
        return BuildScope(libraryId, firstFolder);
    }

    private static RecommendationScope BuildScope(long libraryId, FolderDto folder)
    {
        var stationId = BuildStationId(libraryId, folder.Id);
        return new RecommendationScope(
            libraryId,
            folder.Id,
            folder.DisplayName,
            stationId,
            stationId);
    }

    private static string BuildStationId(long libraryId, long folderId)
        => $"daily-rotation:l{libraryId}:f{folderId}";

    private static bool TryParseStationId(string? stationId, out long libraryId, out long folderId)
    {
        libraryId = 0;
        folderId = 0;

        if (string.IsNullOrWhiteSpace(stationId))
        {
            return false;
        }

        var value = stationId.Trim();
        if (!value.StartsWith("daily-rotation:l", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        var libPart = parts[1];
        var folderPart = parts[2];
        if (!libPart.StartsWith("l", StringComparison.OrdinalIgnoreCase)
            || !folderPart.StartsWith("f", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(libPart[1..], out libraryId) && libraryId > 0
            && long.TryParse(folderPart[1..], out folderId) && folderId > 0;
    }

    public async Task<IReadOnlyList<RecommendationStationDto>> GetStationsAsync(
        long libraryId,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured)
        {
            return Array.Empty<RecommendationStationDto>();
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        var folders = FilterScopedFolders(allRecommendationFolders, libraryId, folderId);
        if (folders.Count == 0)
        {
            return Array.Empty<RecommendationStationDto>();
        }

        var nowLocal = _timeProvider.GetLocalNow();
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);
        var stations = new List<RecommendationStationDto>(folders.Count * 3);
        foreach (var folder in folders.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var scope = BuildScope(libraryId, folder);

            var stationId = scope.StationId;

            var imageUrl = ResolveRecommendationArtworkUrl(stationId, artworkAssignments);
            var dayLocal = DateOnly.FromDateTime(nowLocal.DateTime);
            var detail = await GetSavedDailyResponseAsync(scope, dayLocal, imageUrl, 0, cancellationToken);
            stations.Add(detail.Station);

            // Weekly follows the same folder iteration as daily: one folder, one pair of weekly
            // sections, both scoped to - and named after - that folder.
            foreach (var type in new[] { WeeklyMissingType, WeeklySimilarType })
            {
                try
                {
                    var weekly = await GetWeeklyRecommendationsAsync(
                        libraryId, BuildWeeklyStationId(libraryId, folder.Id, type), 0, cancellationToken, folder.Id);
                    if (weekly is not null) stations.Add(weekly.Station);
                }
                catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex)
                    && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Weekly recommendation cache unavailable for folder {FolderId}.", folder.Id);
                    stations.Add(CreateWeeklyStation(BuildWeeklyScope(libraryId, folder, type), type) with
                    {
                        Status = "failed", Message = "Weekly selection unavailable; daily charts remain available."
                    });
                }
            }
        }
        return stations;
    }

    public async Task<RecommendationDetailDto?> GetRecommendationsAsync(
        long libraryId,
        string? stationId = null,
        long? folderId = null,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured)
        {
            return null;
        }

        if (stationId is not null && TryParseWeeklyStation(stationId, out _, out _, out _))
            return await GetWeeklyRecommendationsAsync(libraryId, stationId, limit, cancellationToken, folderId);

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        var folders = FilterScopedFolders(allRecommendationFolders, libraryId, folderId);
        var scope = ResolveScope(libraryId, folders, stationId, folderId);
        if (scope is null)
        {
            return null;
        }

        var nowLocal = _timeProvider.GetLocalNow();
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);
        var stationImageUrl = ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);

        return await GetSavedDailyResponseAsync(scope, DateOnly.FromDateTime(nowLocal.DateTime), stationImageUrl, limit, cancellationToken);
    }

    private async Task<RecommendationDetailDto?> GetDailyPoolAsync(
        string cacheKey,
        RecommendationScope scope,
        DateOnly dayLocal,
        string? stationImageUrl,
        CancellationToken cancellationToken)
    {
        if (_dailyPoolCache.TryGetValue(cacheKey, out var cachedPool) && cachedPool.SeedScopeKey == scope.ScopeKey)
        {
            return cachedPool;
        }

        var basePool = await TryLoadPersistedDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken);
        if (basePool != null)
        {
            _dailyPoolCache[cacheKey] = basePool;
        }

        return basePool;
    }

    private static RecommendationDetailDto CreateUnavailableRecommendationDetail(
        RecommendationScope scope,
        string? stationImageUrl,
        DateOnly dayLocal,
        string[] reasonCodes)
    {
        var reasons = reasonCodes.Length > 0
            ? reasonCodes
            : [EmptyPoolReason];
        var message = BuildRecommendationUnavailableMessage(reasons);
        var isGenerating = reasonCodes.Contains(GenerationQueuedReason, StringComparer.Ordinal);
        var station = new RecommendationStationDto(
            scope.StationId,
            $"Recommendations - {scope.FolderName}",
            BuildDailyRecommendationDescription(scope.FolderName, dayLocal.DayOfWeek),
            RecommendationSourceId,
            scope.FolderName,
            0,
            stationImageUrl,
            isGenerating ? "generating" : "unavailable",
            reasons,
            message);

        return new RecommendationDetailDto(
            station,
            Array.Empty<RecommendationTrackDto>(),
            DateTimeOffset.UtcNow,
            isGenerating ? "generating" : "unavailable",
            reasons,
            message);
    }

    private static string BuildRecommendationUnavailableMessage(IReadOnlyList<string> reasonCodes)
    {
        var sourceShortages = reasonCodes.Where(r => r.EndsWith("_allocation_incomplete", StringComparison.Ordinal))
            .Select(r => r[..^"_allocation_incomplete".Length]).Select(r => r == "shazam" ? "Shazam" : r == "deezer" ? "Deezer" : r == "spotify" ? "Spotify" : r == "tidal" ? "Tidal" : r).ToList();
        if (sourceShortages.Count > 0) return string.Join(", ", sourceShortages) + " could not supply the configured daily allocation. Generation failed to meet the allocation and will be retried.";
        if (reasonCodes.Contains(GenerationQueuedReason, StringComparer.Ordinal))
        {
            return "Recommendation generation is running in the background. Refresh this tracklist shortly.";
        }

        if (reasonCodes.Contains(PersistFailedReason, StringComparer.Ordinal)
            || reasonCodes.Contains(PersistTimedOutReason, StringComparer.Ordinal))
        {
            return "Recommendation generation completed but failed to save. Try regenerating the station.";
        }

        if (reasonCodes.Contains(BackgroundGenerationFailedReason, StringComparer.Ordinal))
        {
            return "Recommendation generation failed in the background. Try regenerating the station.";
        }

        if (reasonCodes.Contains("no_seed_tracks", StringComparer.Ordinal)
            || reasonCodes.Contains("no_library_tracks", StringComparer.Ordinal))
        {
            return "No library tracks are available to seed recommendations.";
        }

        if (reasonCodes.Contains("deezer_seed_resolution_failed", StringComparer.Ordinal))
        {
            return "No local tracks could be identified on Deezer for recommendation seeding.";
        }

        if (reasonCodes.Contains("dedupe_removed_all", StringComparer.Ordinal))
        {
            return "All generated recommendation tracks already exist in the library or queue.";
        }

        if (reasonCodes.Contains("all_candidates_rejected_or_ignored", StringComparer.Ordinal))
        {
            return "No recommendation tracks are available after user rejections and ignores.";
        }

        return "No recommendations are available for this station today.";
    }

    private static RecommendationGenerationStateKey BuildGenerationStateKey(
        RecommendationScope scope,
        DateOnly dayLocal)
        => new(scope.LibraryId, scope.FolderId, scope.StationId, dayLocal);

    private static string[] ResolveGenerationStateReasonCodes(RecommendationGenerationStateDto? state)
    {
        if (state is null)
        {
            return Array.Empty<string>();
        }

        if (string.Equals(state.Status, "pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return [GenerationQueuedReason];
        }

        if (string.Equals(state.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return [string.IsNullOrWhiteSpace(state.ReasonCode) ? BackgroundGenerationFailedReason : state.ReasonCode];
        }

        return Array.Empty<string>();
    }

    private static async Task RunRecommendationSequenceAsync(
        Func<CancellationToken, Task> daily, Func<CancellationToken, Task<bool>> completed,
        Func<CancellationToken, Task> weekly, CancellationToken cancellationToken)
    {
        await daily(cancellationToken);
        if (await completed(cancellationToken)) await weekly(cancellationToken);
    }

    public async Task<bool> AreDailyRecommendationsCompleteAsync(DateOnly targetDay, CancellationToken cancellationToken)
    {
        if (_settings?.LoadSettings().DailyRecommendations.IsDisabled == true) return true;
        foreach (var folder in await GetRecommendationEligibleFoldersAsync(cancellationToken))
        {
            var scope = BuildScope(folder.LibraryId!.Value, folder);
            var state = await _repository.GetRecommendationGenerationStateAsync(
                scope.LibraryId, scope.FolderId, scope.StationId, targetDay, cancellationToken);
            if (state?.Status != "completed") return false;
            if (await TryLoadPersistedDailyPoolAsync(scope, targetDay, null, cancellationToken) is null) return false;
        }
        return true;
    }

    public async Task ReconcileRecommendationGenerationAsync(DateTimeOffset nowLocal, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured) return;
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var monday = GetRecommendationWeekStart(today);
        // Current charts and first-use weekly lists must not wait for historical catch-up.
        if (today != monday)
        {
            await RefreshDailyRecommendationsForDateAsync(today, "scheduled", cancellationToken);
            await InitializeMissingWeeklyRecommendationsAsync(today, monday, cancellationToken);
        }
        await RunRecommendationSequenceAsync(
            token => RefreshDailyRecommendationsForDateAsync(monday, "weekly-prerequisite", token),
            token => AreDailyRecommendationsCompleteAsync(monday, token),
            token => RefreshWeeklyRecommendationsForWeekAsync(monday, "scheduled", token), cancellationToken);
        if (today == monday) await InitializeMissingWeeklyRecommendationsAsync(today, monday, cancellationToken);
        await MaintainSavedRecommendationsAsync(today, monday, cancellationToken);
    }

    private async Task InitializeMissingWeeklyRecommendationsAsync(
        DateOnly today, DateOnly week, CancellationToken cancellationToken)
    {
        foreach (var folder in await GetRecommendationEligibleFoldersAsync(cancellationToken))
        {
            var scope = BuildScope(folder.LibraryId!.Value, folder);
            var daily = await _repository.GetRecommendationGenerationStateAsync(
                scope.LibraryId, scope.FolderId, scope.StationId, today, cancellationToken);
            if (daily?.Status != "completed"
                || await TryLoadPersistedDailyPoolAsync(scope, today, null, cancellationToken) is null) continue;
            foreach (var type in new[] { WeeklyMissingType, WeeklySimilarType })
            {
                var stationId = BuildWeeklyStationId(scope.LibraryId, scope.FolderId, type);
                var saved = await _repository.GetPlaylistTrackCandidateCacheAsync(WeeklyCacheSource, stationId, cancellationToken);
                // Preserve existing selections for the regular Monday refresh. Only initialize
                // absent or unproven sections; generation retains its persisted lease/retry rules.
                if (IsValidWeeklySnapshot(saved, stationId, week)) continue;
                var hasPreviousSelection = false;
                if (saved?.IsComplete == true)
                {
                    try { hasPreviousSelection = ReadFolderScopedWeeklyPool(saved, stationId) is not null; }
                    catch (JsonException) { /* A corrupt cache requires initialization. */ }
                }
                if (hasPreviousSelection) continue;
                await GenerateWeeklyRecommendationsAsync(scope.LibraryId, scope.FolderId, type, week,
                    "initialization", cancellationToken);
            }
        }
    }

    private async Task MaintainSavedRecommendationsAsync(DateOnly today, DateOnly week, CancellationToken cancellationToken)
    {
        var folders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        foreach (var scope in folders.Select(folder => BuildScope(folder.LibraryId!.Value, folder)))
        {
            if (!AutomaticDailyAllowed(_timeProvider.GetLocalNow())) continue;
            var state = await _repository.GetRecommendationGenerationStateAsync(
                scope.LibraryId, scope.FolderId, scope.StationId, today, cancellationToken);
            if (state?.Status != "completed") continue;
            try
            {
            await RunRecommendationMaintenanceAsync($"recommendation-maintenance:{scope.StationId}:{today:yyyyMMdd}", async () =>
            {
                if (_settings?.LoadSettings().DailyRecommendations.IsDisabled == true || !AutomaticDailyAllowed(_timeProvider.GetLocalNow())) return;
                var remaining=DailyBoundaryUtc(today,new TimeOnly(22,0),_timeProvider.LocalTimeZone)-_timeProvider.GetUtcNow();
                using var cutoff = new CancellationTokenSource(remaining>TimeSpan.Zero ? remaining : TimeSpan.Zero,_timeProvider);
                using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,cutoff.Token);
                var maintenanceToken = work.Token;
                var pool = await TryLoadPersistedDailyPoolAsync(scope, today, null, maintenanceToken);
                if (pool is null) return;
                var excluded = BuildNormalizedRecommendationIdSet(await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(RecommendationSource, scope.StationId, maintenanceToken));
                excluded.UnionWith(await _repository.GetRecommendationRejectedTrackIdsAsync(scope.LibraryId, scope.FolderId, scope.StationId, maintenanceToken));
                var eligible = await FilterRecommendationCandidatesThroughDedupeAsync(scope, pool.Tracks.ToList(), maintenanceToken);
                eligible = await FilterDailyRecommendationEligibilityAsync(eligible,
                    _settings?.LoadSettings().DailyRecommendations.AiFilterMode ?? pool.DailyAiFilterMode ?? DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated,
                    maintenanceToken);
                if (pool.DailyAllocations is not null && !HasCompleteDailyAllocation(
                    BuildSavedDailySelection(eligible,pool.DailyAllocations.ToDictionary(p=>p.Key,p=>p.Value*RecommendationPoolMultiplier),
                        (pool.DailyTrackCount ?? MaxDailyRecommendations)*RecommendationPoolMultiplier,today,excluded,null,
                        DailyRecommendationsPerSeed * RecommendationPoolMultiplier),
                    pool.DailyAllocations.ToDictionary(p=>p.Key,p=>p.Value*RecommendationPoolMultiplier)))
                {
                    var refill = await BuildDailyPoolAsync(scope, today, pool.Station.ImageUrl, maintenanceToken,
                        ConfigurationFromSavedDailyPool(pool));
                    if (refill.Detail is not null) { pool = refill.Detail; eligible = refill.Detail.Tracks.Where(t=>!excluded.Contains(t.Id)).ToList(); }
                }
                maintenanceToken.ThrowIfCancellationRequested();
                if (DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime) != today) return;
                var enriched = await EnrichRecommendationMetadataAsync(eligible, maintenanceToken);
                maintenanceToken.ThrowIfCancellationRequested();
                if (DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime) != today) return;
                var selection = pool.DailyAllocations is null ? enriched : BuildSavedDailySelection(enriched, pool.DailyAllocations, pool.DailyTrackCount ?? MaxDailyRecommendations, today, excluded, pool.Diagnostics?.PublishedTrackIds);
                var complete = pool.DailyAllocations is null || HasCompleteDailyAllocation(selection, pool.DailyAllocations);
                var missing = pool.DailyAllocations?.ToDictionary(p => p.Key, p => Math.Max(0, p.Value - selection.Count(t => t.RecommendationOrigin == p.Key)));
                var diagnostics = pool.Diagnostics is null ? null : pool.Diagnostics with { MissingSourceCounts = missing ?? new Dictionary<string, int>(), DestinationCount = selection.Count, OverflowCount = Math.Max(0, enriched.Count - selection.Count), PublishedTrackIds = selection.Select(t=>t.Id).ToArray() };
                var payload = JsonSerializer.Serialize(new PersistedDailyPoolDto(pool.GeneratedAtUtc, enriched, pool.Station.ImageUrl,
                    pool.DailyTrackCount, pool.DailyAllocations, pool.DailyAiFilterMode, diagnostics, complete ? "ready" : "partial", pool.ReasonCodes, SeedScopeKey: scope.ScopeKey));
                await _repository.UpsertPlaylistTrackCandidateCacheAsync(DailyPoolCacheSource, scope.ScopeKey + $":day:{today:yyyyMMdd}",
                    BuildDailyPoolSnapshotId(today), payload, 0, null, null, true, maintenanceToken);
                await _repository.UpsertPlaylistTrackCandidateCacheAsync(DailyPoolCacheSource, scope.ScopeKey,
                    BuildDailyPoolSnapshotId(today), payload, 0, null, null, true, maintenanceToken);
                _dailyPoolCache.TryRemove(BuildDailyCacheKey(scope.ScopeKey, today), out _);
            }, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !AutomaticDailyAllowed(_timeProvider.GetLocalNow()))
            { /* Daily cutoff must leave weekly maintenance eligible. */ }
        }
        if (!await AreDailyRecommendationsCompleteAsync(week, cancellationToken)) return;
        foreach (var folder in folders)
        {
            var libraryId = folder.LibraryId!.Value;
            foreach (var type in new[] { WeeklyMissingType, WeeklySimilarType })
            {
                var id = BuildWeeklyStationId(libraryId, folder.Id, type);
                var state = await _repository.GetRecommendationGenerationStateAsync(libraryId, folder.Id, id, week, cancellationToken);
                if (state?.Status != "completed") continue;
                await RunRecommendationMaintenanceAsync($"recommendation-maintenance:{id}:{week:yyyyMMdd}",
                    async () => { await PrepareWeeklyRecommendationsAsync(libraryId, id, 0, cancellationToken, folderId: folder.Id); }, cancellationToken);
            }
        }
    }

    private async Task RunRecommendationMaintenanceAsync(string jobKey, Func<Task> prepare, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!await _repository.TryClaimBackgroundJobAsync(jobKey, TimeSpan.FromMinutes(5), now, cancellationToken)) return;
        try
        {
            await prepare();
            await _repository.CompleteBackgroundJobAsync(jobKey, TimeSpan.FromMinutes(5), _timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Recommendation maintenance failed for {JobKey}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobKey));
            await _repository.FailBackgroundJobAsync(jobKey, WeeklyFailureRetryDelay, _timeProvider.GetUtcNow(), CancellationToken.None);
        }
    }

    public Task RefreshDailyRecommendationsAsync(string reasonCode = "scheduled", CancellationToken cancellationToken = default)
        => RefreshDailyRecommendationsForDateAsync(DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime), reasonCode, cancellationToken);

    public async Task RefreshDailyRecommendationsForDateAsync(DateOnly targetDay, string reasonCode, CancellationToken cancellationToken)
    {
        if (_settings?.LoadSettings().DailyRecommendations.IsDisabled == true) return;
        if (targetDay == DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime)
            && reasonCode != GenerationReasonManualRebuild && !AutomaticDailyAllowed(_timeProvider.GetLocalNow())) return;
        if (!_repository.IsConfigured)
        {
            return;
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        if (allRecommendationFolders.Count == 0)
        {
            return;
        }

        var nowLocal = new DateTimeOffset(targetDay.ToDateTime(TimeOnly.MinValue), _timeProvider.GetLocalNow().Offset);
        var dayLocal = targetDay;
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);

        PruneOldCache(dayLocal);

        foreach (var folder in OrderRefreshRecommendationFolders(allRecommendationFolders))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshDailyRecommendationFolderAsync(folder, dayLocal, artworkAssignments, reasonCode, cancellationToken);
        }
    }

    private static IOrderedEnumerable<FolderDto> OrderRefreshRecommendationFolders(IReadOnlyList<FolderDto> folders)
        => folders
            .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value > 0)
            .OrderBy(folder => folder.LibraryId!.Value)
            .ThenBy(folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase);

    private async Task RefreshDailyRecommendationFolderAsync(
        FolderDto folder,
        DateOnly dayLocal,
        IReadOnlyDictionary<string, string> artworkAssignments,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var scope = BuildScope(folder.LibraryId!.Value, folder);
        try
        {
            await RefreshDailyRecommendationScopeAsync(scope, dayLocal, artworkAssignments, reasonCode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Daily recommendation refresh timed out for library {LibraryId}, folder {FolderId}.",
                scope.LibraryId,
                scope.FolderId);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to refresh daily recommendations for library {LibraryId}, folder {FolderId}.",
                scope.LibraryId,
                scope.FolderId);
        }
    }

    private async Task RefreshDailyRecommendationScopeAsync(
        RecommendationScope scope,
        DateOnly dayLocal,
        IReadOnlyDictionary<string, string> artworkAssignments,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var stationImageUrl = ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);
        var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayLocal);
        var existingPool = await TryLoadPersistedDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken);
        if (existingPool?.Status == "ready")
        {
            _dailyPoolCache[cacheKey] = existingPool;
            var key = BuildGenerationStateKey(scope, dayLocal);
            var state = await _repository.GetRecommendationGenerationStateAsync(scope.LibraryId, scope.FolderId, scope.StationId, dayLocal, cancellationToken);
            if (state?.Status != "completed" && ShouldQueueWeeklyGeneration(state, _timeProvider.GetUtcNow())
                && await _repository.TryStartRecommendationGenerationAsync(key, reasonCode, _timeProvider.GetUtcNow() - RecommendationGenerationLease, cancellationToken))
                await _repository.PublishRecommendationSnapshotAsync(key, DailyPoolCacheSource, scope.ScopeKey, BuildDailyPoolSnapshotId(dayLocal),
                    JsonSerializer.Serialize(new PersistedDailyPoolDto(existingPool.GeneratedAtUtc, existingPool.Tracks, existingPool.Station.ImageUrl, existingPool.DailyTrackCount, existingPool.DailyAllocations, existingPool.DailyAiFilterMode, existingPool.Diagnostics, existingPool.Status, existingPool.ReasonCodes, SeedScopeKey: scope.ScopeKey)), cancellationToken);
            else if (state?.Status == "completed")
                await _repository.UpsertPlaylistTrackCandidateCacheAsync(DailyPoolCacheSource, scope.ScopeKey + $":day:{dayLocal:yyyyMMdd}", BuildDailyPoolSnapshotId(dayLocal),
                    JsonSerializer.Serialize(new PersistedDailyPoolDto(existingPool.GeneratedAtUtc, existingPool.Tracks, existingPool.Station.ImageUrl, existingPool.DailyTrackCount, existingPool.DailyAllocations, existingPool.DailyAiFilterMode, existingPool.Diagnostics, existingPool.Status, existingPool.ReasonCodes, SeedScopeKey: scope.ScopeKey)), 0, null, null, true, cancellationToken);
            return;
        }

        await RunDailyRecommendationGenerationAsync(
            scope,
            dayLocal,
            stationImageUrl,
            reasonCode,
            forceReset: false,
            cancellationToken);
    }

    private async Task<bool> RunDailyRecommendationGenerationAsync(
        RecommendationScope scope,
        DateOnly dayLocal,
        string? stationImageUrl,
        string reasonCode,
        bool forceReset,
        CancellationToken cancellationToken,
        bool preserveProgress = false)
    {
        if (_settings?.LoadSettings().DailyRecommendations.IsDisabled == true) return false;
        var operationDay = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var historical = reasonCode == "weekly-prerequisite" && dayLocal < operationDay;
        var automatic = reasonCode != GenerationReasonManualRebuild && reasonCode != GenerationReasonManualReplacement;
        if (!historical && (dayLocal != operationDay || (automatic && !AutomaticDailyAllowed(_timeProvider.GetLocalNow())))) return false;
        var callerToken = cancellationToken;
        var deadline = historical ? (DateTimeOffset?)null : DailyBoundaryUtc(automatic ? dayLocal : dayLocal.AddDays(1),
            automatic ? new TimeOnly(22,0) : TimeOnly.MinValue, _timeProvider.LocalTimeZone);
        using var dayEnded = deadline.HasValue ? new CancellationTokenSource(
            deadline.Value > _timeProvider.GetUtcNow() ? deadline.Value - _timeProvider.GetUtcNow() : TimeSpan.Zero, _timeProvider) : null;
        using var dailyWork = dayEnded is null ? null : CancellationTokenSource.CreateLinkedTokenSource(callerToken,dayEnded.Token);
        var stateKey = BuildGenerationStateKey(scope, dayLocal);
        if (forceReset)
        {
            await _repository.RequestRecommendationGenerationAsync(
                stateKey,
                reasonCode,
                forceReset: true,
                cancellationToken);
        }

        var prior = await _repository.GetRecommendationGenerationStateAsync(scope.LibraryId, scope.FolderId, scope.StationId, dayLocal, cancellationToken);
        if (!forceReset && prior?.Status == "completed"
            && (await TryLoadPersistedDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken))?.Status != "ready")
        {
            await _repository.RequestRecommendationGenerationAsync(stateKey, reasonCode, forceReset: true, cancellationToken);
            prior = await _repository.GetRecommendationGenerationStateAsync(scope.LibraryId, scope.FolderId, scope.StationId, dayLocal, cancellationToken);
        }
        if (!forceReset && !ShouldQueueWeeklyGeneration(prior, _timeProvider.GetUtcNow())) return false;
        var runningExpiresBeforeUtc = _timeProvider.GetUtcNow() - RecommendationGenerationLease;
        if (!await _repository.TryStartRecommendationGenerationAsync(
                stateKey,
                reasonCode,
                runningExpiresBeforeUtc,
                cancellationToken))
        {
            return false;
        }

        var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayLocal);
        var existingPool = await TryLoadPersistedDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken);
        if (existingPool?.Status == "ready" && !forceReset)
        {
            _dailyPoolCache[cacheKey] = existingPool;
            await _repository.CompleteRecommendationGenerationAsync(stateKey, cancellationToken);
            return true;
        }

        try
        {
            if (forceReset)
            {
                _dailyPoolCache.TryRemove(cacheKey, out _);
            }

            cancellationToken = dailyWork?.Token ?? callerToken;
            var dailyPool = await BuildDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken, resetProgress: forceReset && !preserveProgress);
            cancellationToken.ThrowIfCancellationRequested();
            if (!historical && DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime) != dayLocal)
                return false;
            if (dailyPool.Detail is null)
            {
                await FailRecommendationGenerationAsync(stateKey, dailyPool.ReasonCodes, cancellationToken);
                return false;
            }

            if (dailyPool.Detail.Status != "ready")
            {
                _dailyPoolCache[cacheKey] = dailyPool.Detail;
                await FailRecommendationGenerationAsync(stateKey, dailyPool.ReasonCodes, callerToken);
                return false;
            }
            var persistResult = await PersistDailyPoolAsync(scope, dayLocal, dailyPool.Detail, cancellationToken, historical);
            if (!persistResult.Success)
            {
                await FailRecommendationGenerationAsync(
                    stateKey,
                    [persistResult.ReasonCode ?? PersistFailedReason],
                    cancellationToken);
                return false;
            }

            _dailyPoolCache[cacheKey] = dailyPool.Detail;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recommendation pool generated for scope {ScopeKey} ({DayLocal}).",
                    scope.ScopeKey,
                    dayLocal);
            }

            return true;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (dayEnded?.IsCancellationRequested == true)
        {
            await _repository.FailRecommendationGenerationAsync(stateKey,"daily_cutoff_reached",
                "Automatic daily work paused at its calendar deadline; saved progress will resume when eligible.",CancellationToken.None);
            return false;
        }
        catch (OperationCanceledException ex)
        {
            await _repository.FailRecommendationGenerationAsync(
                stateKey,
                BackgroundGenerationFailedReason,
                ex.Message,
                CancellationToken.None);
            _logger.LogWarning(
                ex,
                "Recommendation generation timed out for scope {ScopeKey}.",
                scope.ScopeKey);
            return false;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            await _repository.FailRecommendationGenerationAsync(
                stateKey,
                BackgroundGenerationFailedReason,
                ex.Message,
                CancellationToken.None);
            _logger.LogWarning(
                ex,
                "Recommendation generation failed for scope {ScopeKey}.",
                scope.ScopeKey);
            return false;
        }
    }

    private async Task FailRecommendationGenerationAsync(
        RecommendationGenerationStateKey stateKey,
        IReadOnlyList<string> reasonCodes,
        CancellationToken cancellationToken)
    {
        var reasons = reasonCodes.Count > 0
            ? reasonCodes
            : [EmptyPoolReason];
        await _repository.FailRecommendationGenerationAsync(
            stateKey,
            reasons[0],
            string.Join(", ", reasons),
            cancellationToken);
    }

    public async Task<bool> TriggerFullLibraryShazamScanAsync(
        long libraryId,
        long? folderId,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured || !_shazamRecognitionService.IsAvailable)
        {
            return false;
        }

        var folders = await GetScopedFoldersAsync(libraryId, folderId, cancellationToken);
        if (folders.Count == 0)
        {
            return false;
        }

        var scope = ResolveScope(libraryId, folders, null, folderId);
        if (scope is null)
        {
            return false;
        }

        if (force)
        {
            var allTrackIds = await _repository.GetTrackIdsForLibraryScopeAsync(libraryId, scope.FolderId, cancellationToken);
            if (allTrackIds.Count == 0)
            {
                return false;
            }

            return StartBackgroundShazamRefresh(scope, allTrackIds);
        }

        return StartBackgroundShazamRefresh(scope, null);
    }

    public async Task<LibraryShazamScanStatusDto?> GetShazamScanStatusAsync(
        long libraryId,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured)
        {
            return null;
        }

        var folders = await GetScopedFoldersAsync(libraryId, folderId, cancellationToken);
        var scope = ResolveScope(libraryId, folders, null, folderId);
        if (scope is null)
        {
            return null;
        }

        var trackIds = await _repository.GetTrackIdsForLibraryScopeAsync(libraryId, scope.FolderId, cancellationToken);
        if (trackIds.Count == 0)
        {
            return new LibraryShazamScanStatusDto(
                libraryId,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                _backgroundScans.ContainsKey(scope.ScopeKey));
        }

        var cacheByTrackId = await _repository.GetShazamTrackCacheByTrackIdForLibraryAsync(
            libraryId,
            scope.FolderId,
            cancellationToken);
        var cachedTracks = 0;
        var matchedTracks = 0;
        var noMatchTracks = 0;
        var errorTracks = 0;
        DateTimeOffset? lastScannedAtUtc = null;

        foreach (var trackId in trackIds)
        {
            if (!cacheByTrackId.TryGetValue(trackId, out var cache))
            {
                continue;
            }

            cachedTracks++;
            if (cache.ScannedAtUtc.HasValue
                && (!lastScannedAtUtc.HasValue || cache.ScannedAtUtc.Value > lastScannedAtUtc.Value))
            {
                lastScannedAtUtc = cache.ScannedAtUtc.Value;
            }

            if (string.Equals(cache.Status, StatusMatched, StringComparison.OrdinalIgnoreCase))
            {
                matchedTracks++;
            }
            else if (string.Equals(cache.Status, "no_match", StringComparison.OrdinalIgnoreCase))
            {
                noMatchTracks++;
            }
            else if (string.Equals(cache.Status, StatusError, StringComparison.OrdinalIgnoreCase))
            {
                errorTracks++;
            }
        }

        var pendingTracks = Math.Max(0, trackIds.Count - cachedTracks);
        return new LibraryShazamScanStatusDto(
            libraryId,
            trackIds.Count,
            cachedTracks,
            matchedTracks,
            noMatchTracks,
            errorTracks,
            pendingTracks,
            lastScannedAtUtc,
            _backgroundScans.ContainsKey(scope.ScopeKey));
    }

    public async Task<RecommendationDetailDto?> RebuildRecommendationsAsync(
        long libraryId,
        string? stationId = null,
        long? folderId = null,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        if (stationId is not null && TryParseWeeklyStation(stationId, out _, out _, out _))
            return await GetWeeklyRecommendationsAsync(libraryId, stationId, limit, cancellationToken, folderId);
        if (libraryId <= 0 || !_repository.IsConfigured)
        {
            return null;
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        var folders = FilterScopedFolders(allRecommendationFolders, libraryId, folderId);
        var scope = ResolveScope(libraryId, folders, stationId, folderId);
        if (scope is null)
        {
            return null;
        }

        var nowLocal = _timeProvider.GetLocalNow();
        var dayLocal = DateOnly.FromDateTime(nowLocal.DateTime);
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);
        var stationImageUrl = ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);

        var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayLocal);
        _dailyPoolCache.TryRemove(cacheKey, out _);
        await RunDailyRecommendationGenerationAsync(
            scope,
            dayLocal,
            stationImageUrl,
            GenerationReasonManualRebuild,
            forceReset: true,
            cancellationToken);
        return await GetRecommendationsAsync(libraryId, scope.StationId, scope.FolderId, limit, cancellationToken);
    }

    private async Task<RecommendationDetailDto?> RejectWeeklyRecommendationAsync(
        RecommendationRejectionUpsertInput input, int limit, CancellationToken cancellationToken)
    {
        var resolved = await ResolveWeeklyScopeAsync(input.LibraryId, input.StationId, input.FolderId, cancellationToken);
        if (resolved is null) return null;
        if (!input.TrackSourceId.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
        var scope = resolved.Scope;
        var current = await GetWeeklyRecommendationsAsync(input.LibraryId, scope.StationId, 0, cancellationToken, scope.FolderId);
        if (current is null || !current.Tracks.Any(t => WeeklyTrackKey(t) == input.TrackSourceId)) return null;
        // Rejections are recorded against the real folder, exactly as daily rejections are.
        await _repository.AddRecommendationRejectionAsync(
            input with { FolderId = scope.FolderId, StationId = scope.StationId }, cancellationToken);
        return await GetWeeklyRecommendationsAsync(input.LibraryId, scope.StationId, limit, cancellationToken, scope.FolderId);
    }

    public async Task<RecommendationDetailDto?> RejectRecommendationTrackAsync(
        RecommendationRejectionUpsertInput input,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        if (TryParseWeeklyStation(input.StationId, out _, out _, out _))
            return await RejectWeeklyRecommendationAsync(input, limit, cancellationToken);
        var normalizedTrackSourceId = NormalizeId(input.TrackSourceId);
        if (input.LibraryId <= 0
            || string.IsNullOrWhiteSpace(input.StationId)
            || string.IsNullOrWhiteSpace(normalizedTrackSourceId)
            || !TryParseStationId(input.StationId, out _, out _))
        {
            return null;
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        var folders = FilterScopedFolders(allRecommendationFolders, input.LibraryId, input.FolderId);
        var scope = ResolveScope(input.LibraryId, folders, input.StationId, input.FolderId);
        if (scope is null)
        {
            return null;
        }

        await _repository.AddRecommendationRejectionAsync(
            new RecommendationRejectionUpsertInput(
                scope.LibraryId,
                scope.FolderId,
                scope.StationId,
                normalizedTrackSourceId,
                input.Isrc,
                input.Title,
                input.Artist),
            cancellationToken);
        var day = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var pool = await TryLoadPersistedDailyPoolAsync(scope, day, null, cancellationToken);
        if (pool?.DailyAllocations is not null && (pool.Diagnostics?.InitialSelectionPublished ?? pool.Status == "ready"))
        {
            var excluded = await _repository.GetRecommendationRejectedTrackIdsAsync(scope.LibraryId, scope.FolderId, scope.StationId, cancellationToken);
            excluded.UnionWith(await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(RecommendationSource, scope.StationId, cancellationToken));
            var replacement = BuildSavedDailySelection(pool.Tracks, pool.DailyAllocations, pool.DailyTrackCount ?? MaxDailyRecommendations, day, excluded, pool.Diagnostics?.PublishedTrackIds);
            // A saved reserve is immediate. Only a real shortfall needs an explicit provider refill.
            if (!HasCompleteDailyAllocation(replacement, pool.DailyAllocations)
                && (await GetOrderedRecommendationSeedsAsync(scope, day, cancellationToken)).Count > 0)
                await RunDailyRecommendationGenerationAsync(scope, day, pool.Station.ImageUrl,
                    GenerationReasonManualReplacement, forceReset: true, cancellationToken, preserveProgress: true);
        }
        return await GetRecommendationsAsync(scope.LibraryId, scope.StationId, scope.FolderId,
            limit <= 0 ? 0 : Math.Clamp(limit, 1, MaximumConfiguredDailyTracks), cancellationToken);
    }

    private async Task<RecommendationBuildResult> BuildDailyPoolAsync(
        RecommendationScope scope, DateOnly dayUtc, string? stationImageUrl,
        CancellationToken cancellationToken, DailyRecommendationSettings? configuration = null, bool resetProgress = false)
    {
        var saved = resetProgress ? null : await TryLoadPersistedDailyPoolAsync(scope, dayUtc, stationImageUrl, cancellationToken);
        configuration ??= saved?.DailyAllocations is not null ? ConfigurationFromSavedDailyPool(saved)
            : _settings?.LoadSettings().DailyRecommendations ?? new();
        if (_settings is not null) configuration.AiFilterMode = _settings.LoadSettings().DailyRecommendations.AiFilterMode;
        if (!configuration.TryValidate(out _)) return new(null, ["daily_settings_invalid"]);
        var allocations = configuration.Allocations();
        if (configuration.IsDisabled) return new(CreateDisabledDailyResponse(scope, _timeProvider.GetUtcNow()), []);
        var seeds = await GetOrderedRecommendationSeedsAsync(scope, dayUtc, cancellationToken);
        var excluded = await _repository.GetRecommendationRejectedTrackIdsAsync(scope.LibraryId, scope.FolderId, scope.StationId, cancellationToken);
        excluded.UnionWith(await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(RecommendationSource, scope.StationId, cancellationToken));
        var previousDiagnostics = saved?.Diagnostics;
        var published = previousDiagnostics?.InitialSelectionPublished ?? saved?.Status == "ready";
        var rejectionCounts = new Dictionary<string,int>(previousDiagnostics?.Rejections ?? new Dictionary<string,int>());
        var providerFailures = new Dictionary<string,string>();
        var resolvedSeeds = new Dictionary<string,int>(previousDiagnostics?.ResolvedSeeds ?? new Dictionary<string,int>());
        var rawCounts = new Dictionary<string,int>(previousDiagnostics?.RawCandidates ?? new Dictionary<string,int>());
        var attempts = new Dictionary<string,int>(previousDiagnostics?.AttemptedSeeds ?? new Dictionary<string,int>());
        var processed = previousDiagnostics?.ProcessedSeedIds?.ToDictionary(p => p.Key, p => p.Value.ToHashSet())
            ?? new Dictionary<string,HashSet<long>>();
        var mappings = new Dictionary<string,string?>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        void Reject(string category) => rejectionCounts[category] = rejectionCounts.GetValueOrDefault(category) + 1;
        void Removed(string category,int before,int after) { for (var i=after;i<before;i++) Reject(category); }
        async Task<List<RecommendationTrackDto>> Filter(IReadOnlyList<RecommendationTrackDto> fetched,string source,CancellationToken token)
        {
            var allowed = fetched.Where(t => { var failure=EvaluateDailyAiPolicy(t.AiEvidence,configuration.AiFilterMode);
                if (failure is null) return true; Reject(failure); return false; }).ToList();
            var rows = allowed.Select(t => NormalizeRecommendationTrack(t) with { RecommendationOrigin = source })
                .Where(t => !excluded.Contains(t.Id)).ToList();
            Removed("USER_IGNORED",allowed.Count,rows.Count);
            var before=rows.Count;
            rows=await FilterRecommendationCandidatesThroughDedupeAsync(scope,rows,token);
            Removed("ALREADY_IN_LIBRARY",before,rows.Count);before=rows.Count;
            rows=FilterDerivativeRecommendationCandidates(rows);
            Removed("INVALID_METADATA",before,rows.Count);before=rows.Count;
            rows=await FilterRecommendationCandidatesThroughDeezerValidationAsync(rows,token);
            Removed("NO_DEEZER_MATCH",before,rows.Count);
            rows = await FilterDailyRecommendationEligibilityAsync(rows, configuration.AiFilterMode, token);
            return await PreferFreshRecommendationCandidatesAsync(scope,rows,dayUtc,token);
        }
        var initial = new List<RecommendationTrackDto>();
        if (saved is not null)
            foreach (var source in allocations.Where(p => p.Value>0))
                initial.AddRange(await Filter(saved.Tracks.Where(t => t.RecommendationOrigin==source.Key).ToList(),source.Key,cancellationToken));
        RecommendationDetailDto MakeDetail(IReadOnlyList<RecommendationTrackDto> pool,bool final)
        {
            var selected=BuildSavedDailySelection(pool,allocations,configuration.TrackCount,dayUtc,excluded,previousDiagnostics?.PublishedTrackIds);
            var complete=HasCompleteDailyAllocation(selected,allocations);
            var missing=allocations.ToDictionary(p=>p.Key,p=>Math.Max(0,p.Value-selected.Count(t=>t.RecommendationOrigin==p.Key)));
            var failures=providerFailures.Where(p=>missing.GetValueOrDefault(p.Key)>0).ToDictionary(p=>p.Key,p=>p.Value);
            var reasons=missing.Where(p=>p.Value>0).Select(p=>p.Key+"_allocation_incomplete")
                .Concat(failures.Select(p=>p.Key+"_"+p.Value)).ToList();
            if (seeds.Count==0) reasons.Add("no_seed_tracks");
            var exhausted=allocations.Where(p=>p.Value>0).ToDictionary(p=>p.Key,
                p=>seeds.All(seed=>processed.GetValueOrDefault(p.Key)?.Contains(seed.TrackId)==true));
            if (!complete && exhausted.Any(p=>p.Value && missing.GetValueOrDefault(p.Key)>0)) reasons.Add("daily_seeds_exhausted");
            var diagnostics=new DailyRecommendationDiagnosticsDto(seeds.Count,resolvedSeeds,rawCounts,rejectionCounts,failures,missing,
                selected.Count,Math.Max(0,pool.Count-selected.Count),
                processed.ToDictionary(p=>p.Key,p=>(IReadOnlyList<long>)p.Value.Order().ToArray()), attempts,exhausted,
                published || (final && complete),
                published || (final && complete) ? selected.Select(t=>t.Id).ToArray() : null);
            return new(new(scope.StationId,$"Recommendations - {scope.FolderName}",
                    BuildDailyRecommendationDescription(scope.FolderName,dayUtc.DayOfWeek),RecommendationSourceId,
                    scope.FolderName,complete ? selected.Count : 0,stationImageUrl),pool,
                saved?.GeneratedAtUtc ?? _timeProvider.GetUtcNow(),
                Status: final && complete ? "ready" : published ? "partial" : "draft",ReasonCodes:reasons,
                Message:complete ? null : BuildDailyShortfallMessage(missing),DailyTrackCount:configuration.TrackCount,
                DailyAllocations:allocations,DailyAiFilterMode:configuration.AiFilterMode,Diagnostics:diagnostics,SeedScopeKey:scope.ScopeKey);
        }
        async Task Checkpoint(IReadOnlyList<RecommendationTrackDto> pool)
        {
            var detail=MakeDetail(pool,false);
            await SaveDailyDraftAsync(scope,dayUtc,detail,CancellationToken.None);
            _dailyPoolCache[BuildDailyCacheKey(scope.ScopeKey,dayUtc)]=detail;
        }
        var candidates=await CollectDailySeedBatchesAsync(seeds,allocations,dayUtc,initial,processed,blocked,
            async (source,batch,desired,token)=>
            {
                attempts[source]=attempts.GetValueOrDefault(source)+batch.Count;
                try
                {
                    IReadOnlyList<RecommendationTrackDto> fetched;
                    var rawBefore=rawCounts.GetValueOrDefault(source);
                    if (source=="deezer")
                    {
                        var result = await BuildDeezerRecommendationsAsync(batch,desired,token);
                        fetched=result.Tracks;
                        if (result.FailedSeedLoads > 0) providerFailures[source]="seed_request_failed";
                        rawCounts[source]=rawBefore+fetched.Count;
                        resolvedSeeds[source]=resolvedSeeds.GetValueOrDefault(source)+batch.Count;
                    }
                    else fetched=await BuildStreamingDailyRecommendationsAsync(source,batch,desired,configuration.AiFilterMode,
                        mappings,Reject,resolvedSeeds,rawCounts,providerFailures,token,scope);
                    if (providerFailures.TryGetValue(source,out var failure)
                        && (failure is "provider_unavailable" or "seed_request_failed" or "provider_request_failed"
                            || (rawCounts.GetValueOrDefault(source)==rawBefore && failure.Contains("unavailable",StringComparison.Ordinal))))
                        blocked.Add(source);
                    return await Filter(fetched,source,token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
                {
                    providerFailures[source]="provider_request_failed";blocked.Add(source);Reject("PROVIDER_FAILURE");
                    _logger.LogWarning(ex,"Daily source {Source} deferred for folder {FolderId}; progress retained.",source,scope.FolderId);
                    return Array.Empty<RecommendationTrackDto>();
                }
            },Checkpoint,cancellationToken);
        var result=MakeDetail(candidates,true);
        if (result.Status=="ready")
            await PersistRecommendationExposureHistoryAsync(scope,dayUtc,
                BuildAllocatedDailySelection(candidates,allocations,configuration.TrackCount,dayUtc,excluded),cancellationToken);
        await SaveDailyDraftAsync(scope,dayUtc,result with { Status=published ? result.Status : "draft",
            Diagnostics=result.Diagnostics! with {InitialSelectionPublished=published}},CancellationToken.None);
        return new(result,result.ReasonCodes ?? []);
    }

    private async Task SaveDailyDraftAsync(RecommendationScope scope,DateOnly day,RecommendationDetailDto detail,CancellationToken token)
    {
        if (detail.Station.Id != scope.StationId || detail.SeedScopeKey != scope.ScopeKey)
            throw new InvalidOperationException("Daily recommendation seed scope does not match the progress scope.");
        var payload=new PersistedDailyPoolDto(detail.GeneratedAtUtc,detail.Tracks,detail.Station.ImageUrl,
            detail.DailyTrackCount,detail.DailyAllocations,detail.DailyAiFilterMode,detail.Diagnostics,detail.Status,detail.ReasonCodes, SeedScopeKey: scope.ScopeKey);
        await _repository.UpsertPlaylistTrackCandidateCacheAsync(DailyPoolCacheSource,scope.ScopeKey+$":day:{day:yyyyMMdd}",
            BuildDailyPoolSnapshotId(day),JsonSerializer.Serialize(payload),0,null,null,true,token);
    }

    internal static async Task<List<RecommendationTrackDto>> CollectDailySeedBatchesAsync(
        IReadOnlyList<LibraryRecommendationSeedTrackDto> seeds, IReadOnlyDictionary<string,int> allocations,
        DateOnly day, IReadOnlyList<RecommendationTrackDto> initial, Dictionary<string,HashSet<long>> processed,
        HashSet<string> blocked,
        Func<string,IReadOnlyList<LibraryRecommendationSeedTrackDto>,int,CancellationToken,Task<IReadOnlyList<RecommendationTrackDto>>> fetch,
        Func<IReadOnlyList<RecommendationTrackDto>,Task> checkpoint, CancellationToken cancellationToken)
    {
        var reserveQuotas = allocations.Where(p => p.Value > 0 && p.Key != "shazam")
            .ToDictionary(p => p.Key, p => p.Value * RecommendationPoolMultiplier);
        var target = reserveQuotas.Values.Sum();
        var pool = BuildSavedDailySelection(MergeDailyRecommendationProvenance(initial), reserveQuotas, Math.Max(1,target), day, null, null, DailyRecommendationsPerSeed * RecommendationPoolMultiplier);
        var advanced = true;
        while (advanced && !HasCompleteDailyAllocation(pool, reserveQuotas))
        {
            advanced = false;
            foreach (var pair in reserveQuotas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (blocked.Contains(pair.Key) || pool.Count(t => t.RecommendationOrigin == pair.Key) >= pair.Value) continue;
                if (!processed.TryGetValue(pair.Key, out var seen)) processed[pair.Key] = seen = [];
                var batch = seeds.Where(seed => !seen.Contains(seed.TrackId)).Take(DailySeedProbeLimit).ToArray();
                if (batch.Length == 0) continue;
                var fetched = await fetch(pair.Key, batch, pair.Value, cancellationToken);
                pool = BuildSavedDailySelection(MergeDailyRecommendationProvenance(pool.Concat(fetched)), reserveQuotas, target, day, null, null, DailyRecommendationsPerSeed * RecommendationPoolMultiplier);
                if (!blocked.Contains(pair.Key)) { seen.UnionWith(batch.Select(seed => seed.TrackId)); advanced = true; }
                await checkpoint(pool);
            }
        }
        return pool;
    }

    internal static async Task<RecommendationTrackDto?> ResolveDailyDeezerCandidateCoreAsync(
        DailyRecommendationCandidateDto candidate, DailyRecommendationAiFilterMode mode,
        Func<DailyRecommendationCandidateDto, CancellationToken, Task<string?>> resolve,
        Action<string> reject, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(candidate.ProviderTrackId) || string.IsNullOrWhiteSpace(candidate.Title)
            || string.IsNullOrWhiteSpace(candidate.Artists) || candidate.DurationMs is null or <= 0)
        { reject("INVALID_METADATA"); return null; }
        var aiRejection = EvaluateDailyAiPolicy(candidate.AiEvidence, mode);
        if (aiRejection is not null) { reject(aiRejection); return null; }
        var id = await resolve(candidate, cancellationToken);
        if (string.IsNullOrWhiteSpace(id) || !IsNumericIdentifier(id)) { reject("NO_DEEZER_MATCH"); return null; }
        var url = candidate.Provider == "spotify" ? "https://open.spotify.com/track/" + candidate.ProviderTrackId : "https://tidal.com/track/" + candidate.ProviderTrackId;
        return new(id, candidate.Title, candidate.DurationMs.Value / 1000, candidate.Isrc ?? string.Empty, 1,
            new(string.Empty, candidate.Artists), new(string.Empty, candidate.Album ?? UnknownAlbum, string.Empty),
            SourceUrl: url, RecommendationOrigin: candidate.Provider,
            Provenance: new[] { new RecommendationProvenanceDto(candidate.Provider, candidate.ProviderTrackId,
                candidate.LocalSeedTrackId, candidate.SeedProviderTrackId, candidate.ProviderRank) }, AiEvidence: candidate.AiEvidence);
    }

    private Task<RecommendationTrackDto?> ResolveDailyDeezerCandidateAsync(
        DailyRecommendationCandidateDto candidate, DailyRecommendationAiFilterMode mode, Dictionary<string, string?> mappings,
        Action<string> reject, CancellationToken cancellationToken)
        => ResolveDailyDeezerCandidateCoreAsync(candidate, mode, async (track, token) =>
        {
            var key = string.Join("|", track.Isrc?.Trim().ToUpperInvariant(), track.Title.Trim().ToUpperInvariant(),
                track.Artists?.Trim().ToUpperInvariant(), track.DurationMs);
            if (mappings.TryGetValue(key, out var cached)) return cached;
            var url = track.Provider == "spotify" ? "https://open.spotify.com/track/" + track.ProviderTrackId : "https://tidal.com/track/" + track.ProviderTrackId;
            var match = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(_deezerClient,
                new SpotifyTrackSummary(track.ProviderTrackId, track.Title, track.Artists, track.Album, track.DurationMs,
                    url, null, track.Isrc), new SpotifyTrackResolveOptions(AllowFallbackSearch: true, PreferIsrcOnly: false,
                        StrictMode: true, BypassNegativeCanonicalCache: false, Logger: _logger, CancellationToken: token));
            if (match.Outcome == SpotifyTracklistResolveOutcome.TransientFailure) throw new HttpRequestException("Daily Deezer mapping is temporarily unavailable.");
            if (string.IsNullOrWhiteSpace(match.DeezerId)) return mappings[key] = null;
            var identity = await _trackIdentityResolver.ResolveAsync(new TrackIdentityResolutionRequest(track.Provider, url,
                track.Title, track.Artists, track.Album, track.Isrc, track.DurationMs,
                SpotifyId: track.Provider == "spotify" ? track.ProviderTrackId : null,
                DeezerId: match.DeezerId, TidalId: track.Provider == "tidal" ? track.ProviderTrackId : null,
                TargetPlatforms: new[] { DeezerSource }), token);
            return mappings[key] = identity.DeezerId;
        }, reject, cancellationToken);

    private async Task<IReadOnlyList<RecommendationTrackDto>> BuildStreamingDailyRecommendationsAsync(
        string source, IReadOnlyList<LibraryRecommendationSeedTrackDto> seeds, int desired,
        DailyRecommendationAiFilterMode mode, Dictionary<string, string?> mappings, Action<string> reject,
        Dictionary<string, int> resolvedSeeds, Dictionary<string, int> raw, Dictionary<string, string> failures,
        CancellationToken cancellationToken, RecommendationScope? scope = null)
    {
        if (_platformIdentities is null || (source == "spotify" ? _spotifyRecommendations is null : _tidalCatalog is null))
        { failures[source] = "provider_unavailable"; return []; }
        if (source == "spotify" && (_pathfinder is null || !await _pathfinder.HasPathfinderAuthContextAsync(cancellationToken)))
        { failures[source] = "provider_unavailable"; return []; }
        var selectedSeeds = seeds.ToList();
        var ids = await _platformIdentities.ResolveAsync(source, selectedSeeds.Select(t =>
            new PlatformIdentityTrack(t.TrackId, t.Isrc, t.Title, t.Artist, t.Album, t.DurationMs)).ToList(), cancellationToken);
        resolvedSeeds[source] = resolvedSeeds.GetValueOrDefault(source) + ids.Count;
        var rows = new List<RecommendationTrackDto>();
        var acceptedRecordingIds = new HashSet<string>(StringComparer.Ordinal);
        var acceptedIsrcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var admitted = new Dictionary<string, RecommendationTrackDto?>(StringComparer.Ordinal);
        foreach (var seed in selectedSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ids.TryGetValue(seed.TrackId, out var seedId)) continue;
            var seedKey = source + ":seed:" + seedId;
            if (mappings.ContainsKey(seedKey)) continue;
            try
            {
                var candidates = new List<DailyRecommendationCandidateDto>();
                if (source == "spotify")
                {
                    var tracks = await _spotifyRecommendations!.FetchSeedTrackRecommendationsAsync("https://open.spotify.com/track/" + seedId, desired, cancellationToken);
                    if (tracks.Count == 0) failures[source] = "spotify_track_radio_unavailable";
                    candidates.AddRange(tracks.Select((t, i) => new DailyRecommendationCandidateDto(source, t.Id, seed.TrackId,
                        seedId, i + 1, t.Name, t.Artists, t.Album, t.DurationMs, t.Isrc)));
                }
                else
                {
                    var radio = await _tidalCatalog!.GetTrackRadioAsync(seedId, desired, cancellationToken);
                    if (radio.Outcome != TidalTrackRadioOutcome.Ready)
                    { failures[source] = radio.Reason ?? "tidal_track_radio_unavailable"; reject("PROVIDER_FAILURE"); continue; }
                    candidates.AddRange(radio.Tracks.Select((t, i) => new DailyRecommendationCandidateDto(source, t.Id, seed.TrackId,
                        seedId, i + 1, t.Title, t.Artist, t.Album, t.DurationMs, t.Isrc,
                        t.Ai == true ? new[] { new RecommendationAiEvidenceDto(RecommendationAiStatus.FullyAiTrack,
                            "tidal", "track.ai", "verified_provider_disclosure", _timeProvider.GetUtcNow()) } : null)));
                }
                mappings[seedKey] = seedId;
                raw[source] = raw.GetValueOrDefault(source) + candidates.Count;
                var acceptedForSeed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidate in candidates)
                {
                    if (acceptedForSeed.Count >= DailyRecommendationsPerSeed) break;
                    if (!admitted.TryGetValue(candidate.ProviderTrackId, out var track))
                    {
                        track = await ResolveDailyDeezerCandidateAsync(candidate, mode, mappings, reject, cancellationToken);
                        if (track is not null)
                        {
                            if (scope is null) throw new InvalidOperationException("Daily candidate admission requires its folder scope.");
                            var eligible = await FilterRecommendationCandidatesThroughDedupeAsync(scope, [NormalizeRecommendationTrack(track)], cancellationToken);
                            if (eligible.Count == 0) reject("ALREADY_IN_LIBRARY");
                            eligible = FilterDerivativeRecommendationCandidates(eligible);
                            eligible = await FilterRecommendationCandidatesThroughDeezerValidationAsync(eligible, cancellationToken);
                            eligible = await FilterDailyRecommendationEligibilityAsync(eligible, mode, cancellationToken);
                            track = eligible.FirstOrDefault();
                        }
                        admitted[candidate.ProviderTrackId] = track;
                    }
                    if (track is null || acceptedRecordingIds.Contains(track.Id)
                        || (!string.IsNullOrWhiteSpace(track.Isrc) && acceptedIsrcs.Contains(track.Isrc.Trim()))) continue;
                    acceptedRecordingIds.Add(track.Id);
                    if (!string.IsNullOrWhiteSpace(track.Isrc)) acceptedIsrcs.Add(track.Isrc.Trim());
                    acceptedForSeed.Add(track.Id);
                    rows.Add(track with { Provenance = [new(source, candidate.ProviderTrackId, seed.TrackId, seedId, candidate.ProviderRank)] });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                failures[source] = "seed_request_failed"; reject("PROVIDER_FAILURE");
                _logger.LogWarning("Daily {Source} seed {LocalTrackId} failed ({FailureType}).", source, seed.TrackId, ex.GetType().Name);
                break;
            }
        }
        return rows;
    }

    private static DailyRecommendationSettings ConfigurationFromSavedDailyPool(RecommendationDetailDto pool)
    {
        int Count(string source) => pool.DailyAllocations?.GetValueOrDefault(source) ?? 0;
        return new()
        {
            TrackCount = pool.DailyTrackCount ?? MaxDailyRecommendations,
            ShazamEnabled = Count("shazam") > 0, ShazamTrackCount = Count("shazam"),
            DeezerEnabled = Count("deezer") > 0, DeezerTrackCount = Count("deezer"),
            SpotifyEnabled = Count("spotify") > 0, SpotifyTrackCount = Count("spotify"),
            TidalEnabled = Count("tidal") > 0, TidalTrackCount = Count("tidal"),
            AiFilterMode = pool.DailyAiFilterMode ?? DailyRecommendationAiFilterMode.BlockAiArtistsAndFullyGenerated
        };
    }

    internal static DateTimeOffset GetShazamDailyFreshnessBoundary(DateTimeOffset nowLocal)
        => new DateTimeOffset(nowLocal.Date, nowLocal.Offset).ToUniversalTime();

    internal static List<RecommendationTrackDto> MergeDailyRecommendationProvenance(IEnumerable<RecommendationTrackDto> rows)
        => rows.GroupBy(t => NormalizeId(t.Id), StringComparer.Ordinal).Select(group => group.First() with
        {
            Provenance = group.SelectMany(t => t.Provenance ?? []).Distinct().ToList(),
            AiEvidence = group.SelectMany(t => t.AiEvidence ?? []).Distinct().ToList()
        }).ToList();

    private static string BuildDailyShortfallMessage(IReadOnlyDictionary<string, int> missing)
        => "Partial daily selection: " + string.Join(", ", missing.Where(p => p.Value > 0).Select(p => p.Key + " is short by " + p.Value + " tracks")) + ".";

    internal static string? EvaluateDailyAiPolicy(IReadOnlyList<RecommendationAiEvidenceDto>? evidence, DailyRecommendationAiFilterMode mode)
    {
        if (mode == DailyRecommendationAiFilterMode.Allow || evidence is null) return null;
        foreach (var item in evidence.Where(e => e.Authority == "verified_provider_disclosure"))
        {
            if (item.Status == RecommendationAiStatus.AiArtist) return "AI_ARTIST";
            if (item.Status == RecommendationAiStatus.FullyAiTrack) return "FULLY_AI_TRACK";
            if (mode == DailyRecommendationAiFilterMode.Strict && item.Status == RecommendationAiStatus.PartialAiInvolvement)
                return "AI_DISCLOSURE_STRICT_POLICY";
        }
        return null;
    }

    internal static bool HasCompleteDailyAllocation(IReadOnlyList<RecommendationTrackDto> tracks, IReadOnlyDictionary<string, int> quotas)
        => tracks.Count == quotas.Values.Sum() && tracks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == tracks.Count
            && quotas.All(p => tracks.Count(t => t.RecommendationOrigin == p.Key) == p.Value);

    internal static List<RecommendationTrackDto> BuildAllocatedDailySelection(
        IReadOnlyList<RecommendationTrackDto> tracks, IReadOnlyDictionary<string, int> quotas, int limit, DateOnly day,
        HashSet<string>? excluded = null)
        => BuildSavedDailySelection(tracks, quotas, limit, day, excluded, null);

    internal static List<RecommendationTrackDto> BuildSavedDailySelection(
        IReadOnlyList<RecommendationTrackDto> tracks, IReadOnlyDictionary<string, int> quotas, int limit, DateOnly day,
        HashSet<string>? excluded, IReadOnlyList<string>? preferredIds, int maxTracksPerSeed = DailyRecommendationsPerSeed)
    {
        var preferred = preferredIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        var merged = MergeDailyRecommendationProvenance(tracks).ToDictionary(t => NormalizeId(t.Id), StringComparer.Ordinal);
        var eligible = tracks.Where(t => !string.IsNullOrWhiteSpace(t.Id) && !(excluded?.Contains(NormalizeId(t.Id)) ?? false))
            .Select(t => t with { Provenance = merged[NormalizeId(t.Id)].Provenance, AiEvidence = merged[NormalizeId(t.Id)].AiEvidence }).ToList();
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        string Root(string key)
        {
            if (!identities.TryGetValue(key, out var parent)) return identities[key] = key;
            return parent == key ? key : identities[key] = Root(parent);
        }
        foreach (var track in eligible)
        {
            var root = Root("track:" + track.Id);
            if (!string.IsNullOrWhiteSpace(track.Isrc))
            {
                var recording = Root("isrc:" + track.Isrc.Trim().ToUpperInvariant());
                if (root != recording) identities[root] = recording;
            }
        }
        // Different releases can have different catalog IDs and ISRCs for the same recording.
        // Reuse the strict recording-title matcher; live/remix versions remain distinct.
        for (var i = 0; i < eligible.Count; i++)
            for (var j = 0; j < i; j++)
                if (eligible[i].Duration > 0 && eligible[j].Duration > 0
                    && Math.Abs(eligible[i].Duration - eligible[j].Duration) <= 2
                    && (string.IsNullOrWhiteSpace(eligible[i].Artist.Id) || string.IsNullOrWhiteSpace(eligible[j].Artist.Id)
                        || eligible[i].Artist.Id == eligible[j].Artist.Id)
                    && DeezSpoTag.Core.Utils.TrackTitleMatcher.ArtistsMatch(eligible[i].Artist.Name, eligible[j].Artist.Name)
                    && DeezSpoTag.Core.Utils.TrackTitleMatcher.RecordingTitlesMatch(eligible[i].Title, eligible[j].Title))
                    identities[Root("track:" + eligible[i].Id)] = Root("track:" + eligible[j].Id);
        var rejectedBySeedLimit = new HashSet<string>(StringComparer.Ordinal);
        static IEnumerable<long> Seeds(RecommendationTrackDto track)
            => (track.Provenance ?? []).Select(p => p.LocalSeedTrackId).Where(id => id > 0).Distinct();
        var candidates = quotas.Where(p => p.Value > 0).ToDictionary(p => p.Key,
            p => BuildDiversifiedTrackSelection(OrderDeterministically(eligible.Where(t => t.RecommendationOrigin == p.Key || (t.Provenance?.Any(x => x.Provider == p.Key) ?? false)).ToList(), day, p.Key),
                MaximumConfiguredDailyTracks * RecommendationPoolMultiplier, day).OrderByDescending(t => preferred.Contains(t.Id)).ToList(), StringComparer.Ordinal);
        // A provider with many songs from one seed has less usable supply than one
        // with the same number of songs spread across several seeds.
        var sourceCapacity = candidates.ToDictionary(pair => pair.Key, pair =>
            pair.Value.SelectMany(Seeds).Distinct().Count() * maxTracksPerSeed
                + pair.Value.Where(t => !Seeds(t).Any()).Select(t => Root("track:" + t.Id)).Distinct().Count(), StringComparer.Ordinal);
        var slots = quotas.Where(p => p.Value > 0).SelectMany(p => Enumerable.Repeat(p.Key, p.Value)).ToList();
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        var assigned = new Dictionary<int, RecommendationTrackDto>();
        bool Assign(int slot, HashSet<string> visited)
        {
            foreach (var track in candidates[slots[slot]])
            {
                var key = Root("track:" + track.Id);
                if (rejectedBySeedLimit.Contains(key) || !visited.Add(key)) continue;
                if (!owners.TryGetValue(key, out var prior) || Assign(prior, visited))
                {
                    owners[key] = slot;
                    assigned[slot] = track with { RecommendationOrigin = slots[slot] };
                    return true;
                }
            }
            return false;
        }
        while (true)
        {
            owners.Clear();
            assigned.Clear();
            for (var slot = 0; slot < slots.Count; slot++) Assign(slot, new(StringComparer.Ordinal));
            var overloaded = assigned.Values.SelectMany(t => Seeds(t).Select(seed => (Seed: seed, Track: t)))
                .GroupBy(p => p.Seed).Where(group => group.Count() > maxTracksPerSeed).ToList();
            if (overloaded.Count == 0) break;
            // Remove excess recordings, then rerun the existing source-quota assignment so
            // other seeds can fill the vacated places. Each pass removes at least one identity.
            foreach (var group in overloaded)
                foreach (var item in group.OrderBy(p => (double)sourceCapacity[p.Track.RecommendationOrigin!] / quotas[p.Track.RecommendationOrigin!])
                    .ThenByDescending(p => preferred.Contains(p.Track.Id))
                    .ThenBy(p => p.Track.Provenance?.Min(x => x.ProviderRank) ?? int.MaxValue)
                    .ThenBy(p => p.Track.Id, StringComparer.Ordinal).Skip(maxTracksPerSeed))
                    rejectedBySeedLimit.Add(Root("track:" + item.Track.Id));
        }
        // The assignment can move shared songs between sources to preserve all quotas.
        var selected = assigned.OrderBy(p => p.Key).Select(p => p.Value).ToList();
        var currentSources = MergeRotating(selected.Where(t => t.RecommendationOrigin == "deezer").ToList(), selected.Where(t => t.RecommendationOrigin == "shazam").ToList(), MaximumConfiguredDailyTracks * RecommendationPoolMultiplier, day);
        var addedSources = MergeRotating(selected.Where(t => t.RecommendationOrigin == "spotify").ToList(), selected.Where(t => t.RecommendationOrigin == "tidal").ToList(), MaximumConfiguredDailyTracks * RecommendationPoolMultiplier, day);
        return MergeRotating(currentSources, addedSources, Math.Clamp(limit, 1, MaximumConfiguredDailyTracks * RecommendationPoolMultiplier), day);
    }

    private async Task<List<LibraryRecommendationSeedTrackDto>> GetOrderedRecommendationSeedsAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        CancellationToken cancellationToken)
    {
        var localTracks = await _repository.GetRecommendationSeedTracksForLibraryScopeAsync(
            scope.LibraryId,
            scope.FolderId,
            cancellationToken);
        return localTracks
            .Where(HasUsableRecommendationSeedMetadata)
            .OrderBy(track => ComputeDailyScore(track.TrackId.ToString(), dayUtc))
            .ThenBy(track => track.TrackId)
            .ToList();
    }

    private static bool HasUsableRecommendationSeedMetadata(LibraryRecommendationSeedTrackDto track)
    {
        return !string.IsNullOrWhiteSpace(track.DeezerTrackId)
            || (!string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(track.Artist))
            || !string.IsNullOrWhiteSpace(track.Isrc);
    }

    private async Task<(List<RecommendationTrackDto> Tracks, int ResolvedSeedCount, int FailedSeedLoads)> BuildDeezerRecommendationsAsync(
        IReadOnlyList<LibraryRecommendationSeedTrackDto> orderedSeeds,
        int cappedLimit,
        CancellationToken cancellationToken)
    {
        var resolvedSeeds = await ResolveDeezerRecommendationSeedsAsync(orderedSeeds, cancellationToken);
        if (resolvedSeeds.Count == 0)
        {
            return (new List<RecommendationTrackDto>(), 0, 0);
        }

        var localDeezerIds = resolvedSeeds
            .Select(seed => NormalizeId(seed.DeezerTrackId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var accumulator = new RecommendationAccumulator(cappedLimit);
        foreach (var seed in resolvedSeeds.Take(DailySeedProbeLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var mixTracks = (await LoadDeezerTrackMixAsync(seed.DeezerTrackId, cancellationToken))
                    .Select((t, rank) => t with { Provenance = new[] { new RecommendationProvenanceDto("deezer", t.Id, seed.LocalTrack.TrackId, seed.DeezerTrackId, rank + 1) } }).ToList();
                AddUniqueRecommendationTracks(mixTracks, localDeezerIds, accumulator);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                accumulator.FailedSeedLoads++;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        ex,
                        "Deezer recommendation seed load failed for local track {TrackId} / Deezer {DeezerTrackId}.",
                        seed.LocalTrack.TrackId,
                        seed.DeezerTrackId);
                }
            }
        }

        // The seed batch bounds raw work; quotas apply only after all validation filters.
        AppendOverflowRecommendationTracks(accumulator, int.MaxValue);
        return (accumulator.DestinationTracks, resolvedSeeds.Count, accumulator.FailedSeedLoads);
    }

    private async Task<List<ResolvedRecommendationSeed>> ResolveDeezerRecommendationSeedsAsync(
        IReadOnlyList<LibraryRecommendationSeedTrackDto> orderedSeeds,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedRecommendationSeed>(Math.Min(orderedSeeds.Count, DailySeedProbeLimit));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in orderedSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deezerId = await ResolveDeezerSeedTrackIdAsync(seed, cancellationToken);
            deezerId = NormalizeId(deezerId);
            if (string.IsNullOrWhiteSpace(deezerId) || !seen.Add(deezerId))
            {
                continue;
            }

            resolved.Add(new ResolvedRecommendationSeed(seed, deezerId));
            if (resolved.Count >= DailySeedProbeLimit)
            {
                break;
            }
        }

        return resolved;
    }

    private static bool IsMissingDeezerSeedRecording(Exception exception)
        => exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
            || (exception is InvalidOperationException && exception.Message.StartsWith("API Error: 800 -", StringComparison.Ordinal));

    internal static async Task<string> ValidateStoredDeezerSeedAsync(
        LibraryRecommendationSeedTrackDto seed,
        Func<string, CancellationToken, Task<ApiTrack>> fetch,
        CancellationToken cancellationToken)
    {
        var id = NormalizeId(seed.DeezerTrackId);
        if (!IsNumericIdentifier(id)) return string.Empty;
        try
        {
            var candidate = await fetch(id, cancellationToken);
            return NormalizeId(candidate?.Id) == id && IsValidDeezerSeedCandidate(seed, candidate) ? id : string.Empty;
        }
        catch (Exception ex) when (IsMissingDeezerSeedRecording(ex))
        {
            return string.Empty;
        }
    }

    private async Task<string> ResolveDeezerSeedTrackIdAsync(
        LibraryRecommendationSeedTrackDto seed,
        CancellationToken cancellationToken)
    {
        var existingId = NormalizeId(seed.DeezerTrackId);
        if (IsNumericIdentifier(existingId))
        {
            var verifiedId = await ValidateStoredDeezerSeedAsync(seed,
                (id, token) => _deezerClient.GetTrack(id).WaitAsync(token), cancellationToken);
            if (!string.IsNullOrWhiteSpace(verifiedId)) return verifiedId;
        }

        var isrcId = await TryResolveDeezerSeedByIsrcAsync(seed, cancellationToken);
        if (!string.IsNullOrWhiteSpace(isrcId))
        {
            await PersistResolvedDeezerSeedAsync(seed.TrackId, isrcId, cancellationToken);
            return isrcId;
        }

        var metadataId = await TryResolveDeezerSeedByMetadataAsync(seed, cancellationToken);
        if (!string.IsNullOrWhiteSpace(metadataId))
        {
            await PersistResolvedDeezerSeedAsync(seed.TrackId, metadataId, cancellationToken);
            return metadataId;
        }

        return string.Empty;
    }

    private async Task<string> TryResolveDeezerSeedByIsrcAsync(
        LibraryRecommendationSeedTrackDto seed,
        CancellationToken cancellationToken)
    {
        var isrc = NormalizeText(seed.Isrc, string.Empty);
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return string.Empty;
        }

        try
        {
            var track = await _deezerClient.GetTrackByIsrcAsync(isrc).WaitAsync(cancellationToken);
            return IsValidDeezerSeedCandidate(seed, track)
                ? NormalizeId(track.Id?.ToString())
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer recommendation ISRC seed resolve failed for local track {TrackId}.", seed.TrackId);
            }
            if (IsMissingDeezerSeedRecording(ex)) return string.Empty;
            throw;
        }
    }

    private async Task<string> TryResolveDeezerSeedByMetadataAsync(
        LibraryRecommendationSeedTrackDto seed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seed.Title) || string.IsNullOrWhiteSpace(seed.Artist))
        {
            return string.Empty;
        }

        try
        {
            var query = $"{seed.Artist} {seed.Title}".Trim();
            var result = await _deezerClient.SearchTrackAsync(
                    query,
                    new ApiOptions { Limit = DeezerSearchLimit, Strict = true })
                .WaitAsync(cancellationToken);
            return SelectBestDeezerSeedCandidateId(result, seed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer recommendation metadata seed resolve failed for local track {TrackId}.", seed.TrackId);
            }
            if (IsMissingDeezerSeedRecording(ex)) return string.Empty;
            throw;
        }
    }

    private static string SelectBestDeezerSeedCandidateId(
        DeezerSearchResult? result,
        LibraryRecommendationSeedTrackDto seed)
    {
        if (result?.Data == null || result.Data.Length == 0)
        {
            return string.Empty;
        }

        foreach (var item in result.Data)
        {
            if (!TryParseDeezerSearchCandidate(item, out var deezerId, out var candidate))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(deezerId) && IsValidDeezerSeedCandidate(seed, candidate))
            {
                return deezerId;
            }
        }

        return string.Empty;
    }

    private static bool IsValidDeezerSeedCandidate(LibraryRecommendationSeedTrackDto seed, ApiTrack? candidate)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Id?.ToString()))
        {
            return false;
        }

        if (HasIsrcMismatch(seed.Isrc, candidate.Isrc))
        {
            return false;
        }

        var sourceTitle = NormalizeMatchText(seed.Title);
        var candidateTitle = NormalizeMatchText(BuildCandidateTitle(candidate));
        if (!string.IsNullOrWhiteSpace(sourceTitle)
            && ComputeTokenSimilarity(sourceTitle, candidateTitle) < ShazamDeezerMinTitleSimilarity)
        {
            return false;
        }

        var sourceArtist = NormalizeMatchText(seed.Artist);
        var candidateArtist = NormalizeMatchText(candidate.Artist?.Name);
        if (!string.IsNullOrWhiteSpace(sourceArtist)
            && ComputeTokenSimilarity(sourceArtist, candidateArtist) < ShazamDeezerMinArtistSimilarity)
        {
            return false;
        }

        return !HasSeedDurationMismatch(seed.DurationMs, candidate.Duration);
    }

    private static bool HasSeedDurationMismatch(int? seedDurationMs, int candidateDurationSeconds)
    {
        if (seedDurationMs is not > 0 || candidateDurationSeconds <= 0)
        {
            return false;
        }

        var seedSeconds = (int)Math.Round(seedDurationMs.Value / 1000d);
        return Math.Abs(seedSeconds - candidateDurationSeconds) > 5;
    }

    private async Task PersistResolvedDeezerSeedAsync(
        long trackId,
        string deezerId,
        CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeId(deezerId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return;
        }

        await _repository.UpsertTrackSourceLinkAsync(
            trackId,
            DeezerSource,
            normalizedId,
            $"https://www.deezer.com/track/{normalizedId}",
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<RecommendationTrackDto>> LoadDeezerTrackMixAsync(
        string sourceTrackId,
        CancellationToken cancellationToken)
    {
        var response = await _deezerGatewayService
            .GetContextualTrackMixAsync(new[] { sourceTrackId })
            .WaitAsync(TrackMixRequestTimeout, cancellationToken);
        if (response is null)
        {
            return Array.Empty<RecommendationTrackDto>();
        }

        var results = response["results"] as JObject ?? response;
        var data = results["data"] as JArray ?? results["DATA"] as JArray;
        if (data is null || data.Count == 0)
        {
            return Array.Empty<RecommendationTrackDto>();
        }

        var tracks = new List<RecommendationTrackDto>(data.Count);
        for (var i = 0; i < data.Count; i++)
        {
            var mapped = MapDeezerMixTrack(data[i] as JObject, i + 1);
            if (mapped is not null)
            {
                tracks.Add(mapped);
            }
        }

        return tracks;
    }

    private static RecommendationTrackDto? MapDeezerMixTrack(JObject? track, int defaultPosition)
    {
        if (track is null)
        {
            return null;
        }

        var id = GetJObjectString(track, "SNG_ID", "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = GetJObjectString(track, "SNG_TITLE", "title");
        var version = GetJObjectString(track, "VERSION", "title_version");
        if (!string.IsNullOrWhiteSpace(version)
            && !title.Contains(version, StringComparison.OrdinalIgnoreCase))
        {
            title = string.IsNullOrWhiteSpace(title) ? version : $"{title} {version}";
        }

        return NormalizeRecommendationTrack(new RecommendationTrackDto(
            id,
            title,
            GetJObjectInt(track, "DURATION", "duration") ?? 0,
            GetJObjectString(track, "ISRC", "isrc"),
            GetJObjectInt(track, "TRACK_NUMBER", "position", "track_position") ?? defaultPosition,
            new RecommendationArtistDto(
                GetJObjectString(track, "ART_ID", "artist_id"),
                FirstNonEmpty(GetJObjectString(track, "ART_NAME", "artist_name"), UnknownArtist) ?? UnknownArtist),
            new RecommendationAlbumDto(
                GetJObjectString(track, "ALB_ID", "album_id"),
                FirstNonEmpty(GetJObjectString(track, "ALB_TITLE", "album_title"), UnknownAlbum) ?? UnknownAlbum,
                FirstNonEmpty(
                    GetJObjectString(track, "ALB_PICTURE_MEDIUM", "album_cover_medium", "cover_medium"),
                    BuildCoverUrl(GetJObjectString(track, "ALB_PICTURE", "album_cover"))) ?? string.Empty)));
    }

    private static string GetJObjectString(JObject source, params string[] keys)
    {
        foreach (var value in keys
                     .Select(key => source[key])
                     .OfType<JToken>()
                     .Where(token => token.Type != JTokenType.Null)
                     .Select(token => token.Type == JTokenType.String ? token.Value<string>() : token.ToString())
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            return value!.Trim();
        }

        return string.Empty;
    }

    private static int? GetJObjectInt(JObject source, params string[] keys)
    {
        foreach (var token in keys
                     .Select(key => source[key])
                     .OfType<JToken>()
                     .Where(candidate => candidate.Type != JTokenType.Null))
        {
            if (token.Type == JTokenType.Integer && token.Value<int>() is var number)
            {
                return number;
            }

            if (int.TryParse(token.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static void AddUniqueRecommendationTracks(
        IReadOnlyList<RecommendationTrackDto> sourceTracks,
        HashSet<string> normalizedLibraryIds,
        RecommendationAccumulator accumulator)
    {
        foreach (var track in sourceTracks)
        {

            TryAddRecommendationTrack(track, normalizedLibraryIds, accumulator);
        }
    }

    private static void TryAddRecommendationTrack(
        RecommendationTrackDto track,
        HashSet<string> normalizedLibraryIds,
        RecommendationAccumulator accumulator)
    {
        var normalizedTrackId = NormalizeId(track.Id);
        if (string.IsNullOrWhiteSpace(normalizedTrackId) || normalizedLibraryIds.Contains(normalizedTrackId)) return;
        if (!accumulator.SeenRecommendationIds.Add(normalizedTrackId))
        {
            foreach (var list in new[] { accumulator.DestinationTracks, accumulator.OverflowTracks })
            {
                var index = list.FindIndex(t => NormalizeId(t.Id) == normalizedTrackId);
                if (index >= 0) list[index] = MergeDailyRecommendationProvenance(new[] { list[index], track })[0];
            }
            return;
        }

        if (CanAddWithDiversity(track, accumulator.ArtistCounts, accumulator.AlbumCounts))
        {
            accumulator.DestinationTracks.Add(track with { TrackPosition = accumulator.DestinationTracks.Count + 1 });
            IncrementDiversityCount(GetArtistDiversityKey(track), accumulator.ArtistCounts);
            IncrementDiversityCount(GetAlbumDiversityKey(track), accumulator.AlbumCounts);
            return;
        }

        accumulator.OverflowTracks.Add(track);
    }

    private static void AppendOverflowRecommendationTracks(
        RecommendationAccumulator accumulator,
        int cappedLimit)
    {
        if (accumulator.DestinationTracks.Count >= cappedLimit || accumulator.OverflowTracks.Count == 0)
        {
            return;
        }

        foreach (var overflowTrack in accumulator.OverflowTracks)
        {
            if (accumulator.DestinationTracks.Count >= cappedLimit)
            {
                return;
            }

            accumulator.DestinationTracks.Add(overflowTrack with { TrackPosition = accumulator.DestinationTracks.Count + 1 });
        }
    }

    private static bool CanAddWithDiversity(
        RecommendationTrackDto track,
        Dictionary<string, int> artistCounts,
        Dictionary<string, int> albumCounts)
    {
        var artistKey = GetArtistDiversityKey(track);
        var albumKey = GetAlbumDiversityKey(track);
        return GetDiversityCount(artistKey, artistCounts) < MaxArtistOccurrences
               && GetDiversityCount(albumKey, albumCounts) < MaxAlbumOccurrences;
    }

    private static string GetArtistDiversityKey(RecommendationTrackDto track)
    {
        var artistId = NormalizeId(track.Artist.Id);
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            return $"artist:{artistId}";
        }

        var artistName = NormalizeText(track.Artist.Name, UnknownArtist);
        return !string.IsNullOrWhiteSpace(artistName)
               && !artistName.Equals(UnknownArtist, StringComparison.OrdinalIgnoreCase)
            ? $"artist-name:{artistName.ToLowerInvariant()}"
            : string.Empty;
    }

    private static string GetAlbumDiversityKey(RecommendationTrackDto track)
    {
        var albumId = NormalizeId(track.Album.Id);
        if (!string.IsNullOrWhiteSpace(albumId))
        {
            return $"album:{albumId}";
        }

        var albumTitle = NormalizeText(track.Album.Title, UnknownAlbum);
        return !string.IsNullOrWhiteSpace(albumTitle)
               && !albumTitle.Equals(UnknownAlbum, StringComparison.OrdinalIgnoreCase)
            ? $"album-title:{albumTitle.ToLowerInvariant()}"
            : string.Empty;
    }

    private static int GetDiversityCount(string key, Dictionary<string, int> counts)
        => string.IsNullOrWhiteSpace(key) ? 0 : counts.GetValueOrDefault(key);

    private static void IncrementDiversityCount(string key, Dictionary<string, int> counts)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            counts[key] = GetDiversityCount(key, counts) + 1;
        }
    }

    private async Task<List<RecommendationTrackDto>> FilterDailyRecommendationEligibilityAsync(
        IReadOnlyList<RecommendationTrackDto> tracks, DailyRecommendationAiFilterMode aiMode, CancellationToken cancellationToken)
    {
        var eligible = MergeDailyRecommendationProvenance(tracks)
            .Where(track => EvaluateDailyAiPolicy(track.AiEvidence, aiMode) is null).ToList();
        if (eligible.Count == 0 || !_repository.IsConfigured) return eligible;
        // Retain native provider identities: a remapped Deezer release ID alone can miss
        // a file that was indexed under its Spotify/Tidal ID or another release's ISRC.
        var lookups = eligible.SelectMany((track, index) =>
            new[] { (Provider: DeezerSource, Id: track.Id) }.Concat(
                (track.Provenance ?? []).Select(p => (Provider: p.Provider, Id: p.ProviderTrackId)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Id)).Distinct()
            .Select(p => (Index: index, Input: new LibraryRepository.LibraryExistenceInput(
                track.Isrc, track.Title, track.Artist.Name, track.Duration > 0 ? track.Duration * 1000 : null,
                p.Provider, p.Id, track.Album.Title)))).ToList();
        var matches = await _repository.ResolveLocalTrackIdentitiesAsync(lookups.Select(p => p.Input).ToArray(), cancellationToken);
        var owned = matches.Select((match, index) => (match, index)).Where(p => p.match.Exists)
            .Select(p => lookups[p.index].Index).ToHashSet();
        return eligible.Where((_, index) => !owned.Contains(index)).ToList();
    }

    private async Task<List<RecommendationTrackDto>> FilterRecommendationCandidatesThroughDedupeAsync(
        RecommendationScope scope,
        List<RecommendationTrackDto> candidates,
        CancellationToken cancellationToken)
    {
        var accepted = new List<RecommendationTrackDto>(Math.Min(candidates.Count, MaximumConfiguredDailyTracks * RecommendationPoolMultiplier));
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await _dedupeService.CheckAsync(
                BuildRecommendationDedupeRequest(scope, candidate),
                cancellationToken);
            if (!decision.Allowed)
            {
                continue;
            }

            accepted.Add(candidate with { TrackPosition = accepted.Count + 1 });
        }

        return accepted;
    }

    private static DownloadDedupeRequest BuildRecommendationDedupeRequest(
        RecommendationScope scope,
        RecommendationTrackDto track)
    {
        return new DownloadDedupeRequest
        {
            Isrc = track.Isrc,
            DeezerTrackId = track.Id,
            TrackTitle = track.Title,
            TrackArtist = track.Artist.Name,
            Album = track.Album.Title,
            DurationMs = track.Duration > 0 ? track.Duration * 1000 : null,
            DestinationFolderId = scope.FolderId
        };
    }

    private static List<RecommendationTrackDto> FilterDerivativeRecommendationCandidates(
        List<RecommendationTrackDto> candidates)
    {
        var accepted = new List<RecommendationTrackDto>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (IsDerivativeRecommendationCandidate(candidate))
            {
                continue;
            }

            accepted.Add(candidate with { TrackPosition = accepted.Count + 1 });
        }

        return accepted;
    }

    private static bool IsDerivativeRecommendationCandidate(RecommendationTrackDto track)
    {
        var candidate = new ApiTrack
        {
            Id = NormalizeId(track.Id),
            Title = NormalizeText(track.Title, string.Empty),
            TitleShort = NormalizeText(track.Title, string.Empty),
            TitleVersion = string.Empty,
            Isrc = NormalizeText(track.Isrc, string.Empty),
            Duration = Math.Max(0, track.Duration),
            Artist = new ApiArtist { Name = NormalizeText(track.Artist?.Name, string.Empty) },
            Album = new ApiAlbum { Title = NormalizeText(track.Album?.Title, string.Empty) }
        };
        return DeezerCandidateHeuristics.IsDerivativeCandidate(candidate);
    }

    private async Task<List<RecommendationTrackDto>> FilterRecommendationCandidatesThroughDeezerValidationAsync(
        List<RecommendationTrackDto> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var hydrated = await HydrateRecommendationCandidatesAsync(candidates, cancellationToken);
        if (hydrated.Count == 0)
        {
            return new List<RecommendationTrackDto>();
        }

        var accepted = new List<RecommendationTrackDto>(Math.Min(candidates.Count, MaximumConfiguredDailyTracks * RecommendationPoolMultiplier));
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = NormalizeId(candidate.Id);
            if (string.IsNullOrWhiteSpace(id) || !hydrated.TryGetValue(id, out var metadata))
            {
                continue;
            }

            var validation = ValidateRecommendationCandidate(candidate, metadata);
            if (!validation.Accepted || IsDerivativeRecommendationCandidate(metadata))
            {
                continue;
            }

            accepted.Add(MergeRecommendationTrack(candidate, metadata) with { TrackPosition = accepted.Count + 1 });
            if (accepted.Count >= MaximumConfiguredDailyTracks * RecommendationPoolMultiplier)
            {
                break;
            }
        }

        return accepted;
    }

    private async Task<Dictionary<string, RecommendationTrackDto>> HydrateRecommendationCandidatesAsync(
        IReadOnlyList<RecommendationTrackDto> candidates,
        CancellationToken cancellationToken)
    {
        var hydrated = new Dictionary<string, RecommendationTrackDto>(StringComparer.Ordinal);
        var ids = candidates
            .Select(track => NormalizeId(track.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        const int batchSize = 100;
        for (var start = 0; start < ids.Count; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = ids.Skip(start).Take(batchSize).ToList();
            try
            {
                var tracks = await _deezerGatewayService.GetTracksAsync(batch);
                foreach (var metadata in tracks.Select(MapGatewayTrack))
                {
                    var id = NormalizeId(metadata.Id);
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        hydrated[id] = metadata;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Recommendation candidate Deezer validation failed for {Count} tracks.", batch.Count);
                }
                throw; // A validation outage is retryable, not proof that these songs are ineligible.
            }
        }

        return hydrated;
    }

    private static TrackCandidateValidationResult ValidateRecommendationCandidate(
        RecommendationTrackDto source,
        RecommendationTrackDto candidate)
    {
        return TrackCandidateValidator.Validate(
            new TrackMatchSource(
                source.Isrc,
                source.Title,
                source.Artist?.Name,
                source.Album?.Title,
                source.Duration > 0 ? source.Duration * 1000 : null),
            new TrackMatchCandidate(
                candidate.Id,
                candidate.Isrc,
                candidate.Title,
                candidate.Artist?.Name,
                candidate.Album?.Title,
                candidate.Duration > 0 ? candidate.Duration * 1000 : null),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                MaxIsrcDurationDifferenceMs: 20_000,
                MaxMetadataDurationDifferenceMs: 8_000));
    }

    private async Task<RecommendationDetailDto?> TryLoadPersistedDailyPoolAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        string? stationImageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await _repository.GetPlaylistTrackCandidateCacheAsync(
                DailyPoolCacheSource,
                scope.ScopeKey + $":day:{dayUtc:yyyyMMdd}",
                cancellationToken)
                ?? await _repository.GetPlaylistTrackCandidateCacheAsync(DailyPoolCacheSource, scope.ScopeKey, cancellationToken);
            if (persisted is null
                || !string.Equals(
                    NormalizeDailyPoolSnapshotId(persisted.SnapshotId),
                    BuildDailyPoolSnapshotId(dayUtc),
                    StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(persisted.CandidatesJson))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<PersistedDailyPoolDto>(persisted.CandidatesJson);
            if (payload?.SeedScopeKey != scope.ScopeKey)
            {
                // A completed public commit may outlive an older private progress entry.
                var published = await _repository.GetPlaylistTrackCandidateCacheAsync(DailyPoolCacheSource, scope.ScopeKey, cancellationToken);
                if (published is null || NormalizeDailyPoolSnapshotId(published.SnapshotId) != BuildDailyPoolSnapshotId(dayUtc)) return null;
                var committed = JsonSerializer.Deserialize<PersistedDailyPoolDto>(published.CandidatesJson);
                if (committed?.SeedScopeKey != scope.ScopeKey || committed.Status != "ready") return null;
                payload = committed;
            }
            // Publication commits the public snapshot and completed state atomically. Recover
            // that result if shutdown/storage failure interrupted the following progress-cache write.
            if (payload?.Status == "draft" && payload.Diagnostics?.InitialSelectionPublished != true)
            {
                var published = await _repository.GetPlaylistTrackCandidateCacheAsync(DailyPoolCacheSource, scope.ScopeKey, cancellationToken);
                if (published?.SnapshotId == BuildDailyPoolSnapshotId(dayUtc))
                {
                    var committed = JsonSerializer.Deserialize<PersistedDailyPoolDto>(published.CandidatesJson);
                    if (committed?.Status == "ready" && committed.SeedScopeKey == scope.ScopeKey && committed.GeneratedAtUtc >= payload.GeneratedAtUtc)
                        payload = committed;
                }
            }
            if (payload is null)
            {
                return null;
            }

            var normalizedTracks = (payload.Tracks ?? Array.Empty<RecommendationTrackDto>())
                .Select(NormalizeRecommendationTrack)
                .Where(track => !string.IsNullOrWhiteSpace(track.Id))
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();

            var station = new RecommendationStationDto(
                scope.StationId,
                $"Recommendations - {scope.FolderName}",
                BuildDailyRecommendationDescription(scope.FolderName, dayUtc.DayOfWeek),
                RecommendationSourceId,
                scope.FolderName,
                Math.Min(MaxDailyRecommendations, normalizedTracks.Count),
                string.IsNullOrWhiteSpace(payload.StationImageUrl) ? stationImageUrl : payload.StationImageUrl);

            return new RecommendationDetailDto(
                station,
                normalizedTracks,
                payload.GeneratedAtUtc, Status: payload.Status, ReasonCodes: payload.ReasonCodes, DailyTrackCount: payload.TrackCount, DailyAllocations: payload.Allocations,
                        DailyAiFilterMode: payload.AiFilterMode, Diagnostics: payload.Diagnostics, SeedScopeKey: payload.SeedScopeKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Daily recommendation build timed out for scope {ScopeKey}.",
                scope.ScopeKey);

            return null;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to load persisted recommendation daily pool for scope {ScopeKey}.",
                scope.ScopeKey);
            return null;
        }
    }

    private async Task<PersistDailyPoolResult> PersistDailyPoolAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        RecommendationDetailDto detail,
        CancellationToken cancellationToken,
        bool allowHistorical = false)
    {
        if (detail.Station.Id != scope.StationId || detail.SeedScopeKey != scope.ScopeKey)
            return PersistDailyPoolResult.Failed("recommendation_seed_scope_mismatch");
        if (!allowHistorical && DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime) != dayUtc)
            return PersistDailyPoolResult.Failed("daily_selection_expired");
        if (detail.Tracks.Count == 0)
        {
            return PersistDailyPoolResult.Failed(EmptyPoolReason);
        }

        try
        {
            var payload = new PersistedDailyPoolDto(
                detail.GeneratedAtUtc,
                detail.Tracks
                    .Select(NormalizeRecommendationTrack)
                    .Where(track => !string.IsNullOrWhiteSpace(track.Id))
                    .Select((track, index) => track with { TrackPosition = index + 1 })
                    .ToList(),
                detail.Station.ImageUrl, detail.DailyTrackCount, detail.DailyAllocations, detail.DailyAiFilterMode, detail.Diagnostics, detail.Status, detail.ReasonCodes, SeedScopeKey: scope.ScopeKey);

            var published = await _repository.PublishRecommendationSnapshotAsync(BuildGenerationStateKey(scope, dayUtc),
                DailyPoolCacheSource, scope.ScopeKey, BuildDailyPoolSnapshotId(dayUtc), JsonSerializer.Serialize(payload), cancellationToken);
            if (!published) return PersistDailyPoolResult.Failed(PersistFailedReason);
            try { await SaveDailyDraftAsync(scope,dayUtc,detail,CancellationToken.None); }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            { _logger.LogWarning(ex,"Daily publication succeeded but its progress cache could not be updated for {ScopeKey}.",scope.ScopeKey); }
            return PersistDailyPoolResult.Ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Persisting recommendation daily pool timed out for scope {ScopeKey}.",
                scope.ScopeKey);
            return PersistDailyPoolResult.Failed(PersistTimedOutReason);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to persist recommendation daily pool for scope {ScopeKey}.",
                scope.ScopeKey);
            return PersistDailyPoolResult.Failed(PersistFailedReason);
        }
    }

    private static string BuildDailyPoolSnapshotId(DateOnly dayUtc)
        => $"{DailyPoolSnapshotVersion}:{dayUtc:yyyyMMdd}";

    private static string NormalizeDailyPoolSnapshotId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private async Task<List<RecommendationTrackDto>> PreferFreshRecommendationCandidatesAsync(
        RecommendationScope scope,
        List<RecommendationTrackDto> candidates,
        DateOnly dayUtc,
        CancellationToken cancellationToken)
    {
        if (candidates.Count <= MaxDailyRecommendations)
        {
            return candidates
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var recentIds = await LoadRecentRecommendationExposureIdsAsync(scope, dayUtc, cancellationToken);
        if (recentIds.Count == 0)
        {
            return candidates
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var fresh = new List<RecommendationTrackDto>(candidates.Count);
        var recent = new List<RecommendationTrackDto>();
        foreach (var candidate in candidates)
        {
            var id = NormalizeId(candidate.Id);
            if (!string.IsNullOrWhiteSpace(id) && recentIds.Contains(id))
            {
                recent.Add(candidate);
            }
            else
            {
                fresh.Add(candidate);
            }
        }

        return fresh
            .Concat(recent)
            .Take(RecommendationPoolLimit)
            .Select((track, index) => track with { TrackPosition = index + 1 })
            .ToList();
    }

    private async Task<HashSet<string>> LoadRecentRecommendationExposureIdsAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        CancellationToken cancellationToken)
    {
        var history = await LoadRecommendationExposureHistoryAsync(scope, cancellationToken);
        var oldestAllowedDay = dayUtc.AddDays(-RecommendationExposureRetentionDays);
        return history.Entries
            .Select(entry => new
            {
                TrackId = NormalizeId(entry.TrackId),
                Day = TryParseExposureDay(entry.Day)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TrackId)
                            && entry.Day.HasValue
                            && entry.Day.Value < dayUtc
                            && entry.Day.Value >= oldestAllowedDay)
            .Select(entry => entry.TrackId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<RecommendationExposureHistoryDto> LoadRecommendationExposureHistoryAsync(
        RecommendationScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await _repository.GetPlaylistTrackCandidateCacheAsync(
                ExposureHistoryCacheSource,
                scope.ScopeKey,
                cancellationToken);
            if (persisted is null || string.IsNullOrWhiteSpace(persisted.CandidatesJson))
            {
                return new RecommendationExposureHistoryDto(Array.Empty<RecommendationExposureEntryDto>());
            }

            var payload = JsonSerializer.Deserialize<RecommendationExposureHistoryDto>(persisted.CandidatesJson);
            return payload ?? new RecommendationExposureHistoryDto(Array.Empty<RecommendationExposureEntryDto>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to load recommendation exposure history for scope {ScopeKey}.", scope.ScopeKey);
            }

            return new RecommendationExposureHistoryDto(Array.Empty<RecommendationExposureEntryDto>());
        }
    }

    private async Task PersistRecommendationExposureHistoryAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        IEnumerable<RecommendationTrackDto> visibleTracks,
        CancellationToken cancellationToken)
    {
        var history = await LoadRecommendationExposureHistoryAsync(scope, cancellationToken);
        var oldestRetainedDay = dayUtc.AddDays(-RecommendationExposureRetentionDays);
        var dayKey = FormatExposureDay(dayUtc);
        var entries = history.Entries
            .Where(entry =>
            {
                var parsedDay = TryParseExposureDay(entry.Day);
                return parsedDay.HasValue
                       && parsedDay.Value >= oldestRetainedDay
                       && parsedDay.Value <= dayUtc
                       && !string.Equals(entry.Day, dayKey, StringComparison.Ordinal);
            })
            .ToList();

        entries.AddRange(visibleTracks
            .Select(track => NormalizeId(track.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => new RecommendationExposureEntryDto(id, dayKey)));

        try
        {
            await _repository.UpsertPlaylistTrackCandidateCacheAsync(
                ExposureHistoryCacheSource,
                scope.ScopeKey,
                $"{ExposureHistorySnapshotVersion}:{dayKey}",
                JsonSerializer.Serialize(new RecommendationExposureHistoryDto(entries)),
                schemaVersion: 0,
                identityRevision: null,
                providerReadinessRevision: null,
                isComplete: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist recommendation exposure history for scope {ScopeKey}.", scope.ScopeKey);
            }
        }
    }

    private static DateOnly? TryParseExposureDay(string? value)
    {
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed)
            ? parsed
            : null;
    }

    private static string FormatExposureDay(DateOnly day)
        => day.ToString("yyyy-MM-dd");

    private async Task<ShazamRecommendationBuildResult> BuildShazamRecommendationsAsync(
        RecommendationScope scope,
        IReadOnlyList<LibraryRecommendationSeedTrackDto> orderedSeeds,
        CancellationToken cancellationToken, int candidateLimit = MaxDailyRecommendations * 2)
    {
        if (!IsShazamRecommendationAvailable())
        {
            return ShazamRecommendationBuildResult.Empty("shazam_unavailable");
        }

        var shazamSeedTrackIds = orderedSeeds
            .Select(seed => seed.TrackId)
            .Take(Math.Min(DailySeedProbeLimit, Math.Max(ShazamSelectedSeedLimit, (candidateLimit + ShazamRelatedPerSeed - 1) / ShazamRelatedPerSeed)))
            .ToList();
        if (shazamSeedTrackIds.Count == 0)
        {
            return ShazamRecommendationBuildResult.Empty("shazam_no_seed_tracks");
        }

        await RefreshStaleShazamSeedsAsync(scope, shazamSeedTrackIds, cancellationToken);

        var cacheByTrackId = await _repository.GetShazamTrackCacheByTrackIdForLibraryAsync(
            scope.LibraryId,
            scope.FolderId,
            cancellationToken);
        var tracks = BuildRecommendationsFromShazamCache(shazamSeedTrackIds, cacheByTrackId, cancellationToken, candidateLimit);
        return tracks.Count > 0
            ? new ShazamRecommendationBuildResult(tracks, "shazam_ok")
            : ShazamRecommendationBuildResult.Empty(ResolveEmptyShazamReasonCode(shazamSeedTrackIds, cacheByTrackId));
    }

    private bool IsShazamRecommendationAvailable()
    {
        if (_shazamRecognitionService is not null && _shazamRecognitionService.IsAvailable)
        {
            return true;
        }

        _logger.LogDebug("Skipping Shazam recommendation scan because recognizer is unavailable.");
        return false;
    }

    private async Task RefreshStaleShazamSeedsAsync(
        RecommendationScope scope,
        IReadOnlyList<long> selectedSeedTrackIds,
        CancellationToken cancellationToken)
    {
        var staleBeforeUtc = GetShazamDailyFreshnessBoundary(_timeProvider.GetLocalNow());
        var staleTrackIds = await _repository.GetTrackIdsNeedingShazamRefreshAsync(
            scope.LibraryId,
            staleBeforeUtc,
            scope.FolderId,
            cancellationToken: cancellationToken);
        if (staleTrackIds.Count == 0)
        {
            return;
        }

        var staleSet = new HashSet<long>(staleTrackIds);
        var inlineTargets = selectedSeedTrackIds
            .Where(staleSet.Contains)
            .ToList();

        if (inlineTargets.Count > 0)
        {
            await RefreshShazamCacheForTrackBatchAsync(inlineTargets, cancellationToken);
        }
    }

    private static List<RecommendationTrackDto> BuildRecommendationsFromShazamCache(
        IReadOnlyList<long> orderedSeeds,
        IReadOnlyDictionary<long, ShazamTrackCacheDto> cacheByTrackId,
        CancellationToken cancellationToken, int candidateLimit = MaxDailyRecommendations * 2)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<RecommendationTrackDto>();
        var maxResults = Math.Clamp(candidateLimit, 1, MaximumConfiguredDailyTracks * RecommendationPoolMultiplier);

        foreach (var trackId in orderedSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cacheByTrackId.TryGetValue(trackId, out var cache)
                || !ShouldIncludeShazamCacheEntry(cache))
            {
                continue;
            }

            for (var rank = 0; rank < cache.RelatedTracks.Count; rank++)
            {
                var relatedTrack = cache.RelatedTracks[rank];
                var nativeId = relatedTrack.Provenance?.FirstOrDefault(p => p.Provider == "shazam")?.ProviderTrackId ?? string.Empty;
                var row = relatedTrack with { Provenance = new[] { new RecommendationProvenanceDto("shazam", nativeId,
                    trackId, cache.ShazamTrackId ?? string.Empty, rank + 1) } };
                if (results.Count < maxResults || seen.Contains(NormalizeId(row.Id))) TryAddShazamRelatedRecommendation(row, seen, results);
            }
        }

        return results;
    }

    private static bool ShouldIncludeShazamCacheEntry(ShazamTrackCacheDto cache)
    {
        return string.Equals(cache.Status, StatusMatched, StringComparison.OrdinalIgnoreCase)
               && cache.RelatedTracks.Count > 0;
    }

    private static string ResolveEmptyShazamReasonCode(
        IReadOnlyList<long> orderedSeeds,
        IReadOnlyDictionary<long, ShazamTrackCacheDto> cacheByTrackId)
    {
        var hasCachedSeed = false;
        var hasNoDeezerResolution = false;
        var hasNoRelated = false;
        var hasNoMatch = false;
        var hasError = false;

        foreach (var trackId in orderedSeeds)
        {
            if (!cacheByTrackId.TryGetValue(trackId, out var cache))
            {
                continue;
            }

            hasCachedSeed = true;
            if (string.Equals(cache.Status, StatusMatchedNoDeezerResolution, StringComparison.OrdinalIgnoreCase))
            {
                hasNoDeezerResolution = true;
            }
            else if (string.Equals(cache.Status, StatusMatchedNoRelated, StringComparison.OrdinalIgnoreCase))
            {
                hasNoRelated = true;
            }
            else if (string.Equals(cache.Status, StatusNoMatch, StringComparison.OrdinalIgnoreCase))
            {
                hasNoMatch = true;
            }
            else if (string.Equals(cache.Status, StatusError, StringComparison.OrdinalIgnoreCase))
            {
                hasError = true;
            }
        }

        if (hasNoDeezerResolution)
        {
            return "shazam_no_deezer_resolution";
        }

        if (hasNoRelated)
        {
            return "shazam_no_related";
        }

        if (hasNoMatch)
        {
            return "shazam_no_match";
        }

        if (hasError)
        {
            return "shazam_failed";
        }

        return hasCachedSeed ? "shazam_empty" : "shazam_no_cache";
    }

    private static void TryAddShazamRelatedRecommendation(
        RecommendationTrackDto track,
        HashSet<string> seen,
        List<RecommendationTrackDto> results)
    {
        var normalized = NormalizeRecommendationTrack(track);
        var deezerId = NormalizeId(normalized.Id);
        if (string.IsNullOrWhiteSpace(deezerId)) return;
        if (!seen.Add(deezerId))
        {
            var index = results.FindIndex(t => t.Id == deezerId);
            if (index >= 0) results[index] = MergeDailyRecommendationProvenance(new[] { results[index], normalized })[0];
            return;
        }

        results.Add(normalized with { Id = deezerId, TrackPosition = results.Count + 1 });
    }

    private bool StartBackgroundShazamRefresh(RecommendationScope scope, IReadOnlyList<long>? explicitTrackIds)
    {
        if (!_backgroundScans.TryAdd(scope.ScopeKey, 0))
        {
            return false;
        }

        _ = StartBackgroundShazamRefreshCoreAsync(scope, explicitTrackIds);
        return true;
    }

    private async Task StartBackgroundShazamRefreshCoreAsync(
        RecommendationScope scope,
        IReadOnlyList<long>? explicitTrackIds)
    {
        try
        {
            if (explicitTrackIds is { Count: > 0 })
            {
                await RefreshShazamCacheForTrackBatchAsync(explicitTrackIds, CancellationToken.None);
            }
            else
            {
                var staleBeforeUtc = GetShazamDailyFreshnessBoundary(_timeProvider.GetLocalNow());
                while (true)
                {
                    var batch = await _repository.GetTrackIdsNeedingShazamRefreshAsync(
                        scope.LibraryId,
                        staleBeforeUtc,
                        scope.FolderId,
                        ShazamBackgroundBatchSize,
                        CancellationToken.None);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    await RefreshShazamCacheForTrackBatchAsync(batch, CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Background Shazam library scan failed for scope {ScopeKey}.", scope.ScopeKey);
        }
        finally
        {
            _backgroundScans.TryRemove(scope.ScopeKey, out _);
        }
    }

    private async Task RefreshShazamCacheForTrackBatchAsync(
        IReadOnlyList<long> trackIds,
        CancellationToken cancellationToken)
    {
        if (trackIds.Count == 0)
        {
            return;
        }

        var deezerResolveCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trackId in trackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshShazamCacheForTrackAsync(trackId, deezerResolveCache, cancellationToken);
        }
    }

    private async Task RefreshShazamCacheForTrackAsync(
        long trackId,
        IDictionary<string, string> deezerResolveCache,
        CancellationToken cancellationToken)
    {
        var scannedAtUtc = _timeProvider.GetUtcNow();
        var filePath = await _repository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
        if (IsMissingPrimaryAudioFile(filePath))
        {
            await PersistShazamCacheFileNotFoundAsync(trackId, scannedAtUtc, cancellationToken);
            return;
        }

        var attempt = await TryRecognizeTrackWithShazamAsync(trackId, filePath!, scannedAtUtc, cancellationToken);
        if (attempt is null)
        {
            return;
        }

        var recognizedTrack = await TryCreateRecognizedTrackAsync(trackId, attempt, scannedAtUtc, cancellationToken);
        if (recognizedTrack is null)
        {
            return;
        }

        await PersistRecognitionSourceLinksAsync(trackId, recognizedTrack.Recognition, cancellationToken);
        var similarCards = await TryFetchSimilarShazamTracksAsync(trackId, recognizedTrack, scannedAtUtc, cancellationToken);
        if (similarCards is null)
        {
            return;
        }

        var relatedRecommendations = await BuildRelatedShazamRecommendationsAsync(
            similarCards,
            trackId,
            deezerResolveCache,
            cancellationToken);
        var status = ResolveShazamSimilarStatus(relatedRecommendations.Count, similarCards.Count);
        var failureReason = status switch
        {
            StatusMatchedNoDeezerResolution => "Shazam similar tracks did not resolve to Deezer tracks.",
            StatusMatchedNoRelated => "Shazam returned no similar tracks.",
            _ => null
        };
        await PersistMatchedShazamCacheAsync(
            trackId,
            recognizedTrack,
            relatedRecommendations,
            scannedAtUtc,
            status,
            failureReason,
            cancellationToken);
    }

    private sealed record RecognizedShazamTrack(ShazamRecognitionInfo Recognition, string ShazamTrackId);

    private static bool IsMissingPrimaryAudioFile(string? filePath)
    {
        return string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath);
    }

    private async Task PersistShazamCacheFileNotFoundAsync(
        long trackId,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        await _repository.UpsertTrackShazamCacheAsync(
            new LibraryRepository.TrackShazamCacheUpsertInput(
                trackId,
                StatusError,
                null,
                null,
                null,
                null,
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                "Primary audio file not found."),
            cancellationToken);
    }

    private async Task<ShazamRecognitionAttempt?> TryRecognizeTrackWithShazamAsync(
        long trackId,
        string filePath,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return _shazamRecognitionService.RecognizeWithDetails(filePath, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Shazam recognition timed out for track {TrackId} ({FilePath}).",
                    trackId,
                    filePath);
            }

            await _repository.UpsertTrackShazamCacheAsync(
                new LibraryRepository.TrackShazamCacheUpsertInput(
                    trackId,
                    StatusError,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<RecommendationTrackDto>(),
                    scannedAtUtc,
                    "Shazam recognition timed out."),
                cancellationToken);
            return null;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            var reason = BuildPersistedFailureReason("Shazam recognition failed", ex);
            _logger.LogWarning(ex, "Shazam recognition failed for library track {TrackId}.", trackId);
            await _repository.UpsertTrackShazamCacheAsync(
                new LibraryRepository.TrackShazamCacheUpsertInput(
                    trackId,
                    StatusError,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<RecommendationTrackDto>(),
                    scannedAtUtc,
                    reason),
                cancellationToken);
            return null;
        }
    }

    private async Task<RecognizedShazamTrack?> TryCreateRecognizedTrackAsync(
        long trackId,
        ShazamRecognitionAttempt attempt,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!attempt.Matched || attempt.Recognition is null || !attempt.Recognition.HasCoreMetadata)
        {
            await PersistIncompleteShazamAttemptAsync(trackId, attempt, scannedAtUtc, cancellationToken);
            return null;
        }

        var recognition = attempt.Recognition;
        var shazamTrackId = NormalizeId(recognition.TrackId);
        if (string.IsNullOrWhiteSpace(shazamTrackId))
        {
            await _repository.UpsertTrackShazamCacheAsync(
                new LibraryRepository.TrackShazamCacheUpsertInput(
                    trackId,
                    StatusNoMatch,
                    null,
                    NormalizeText(recognition.Title, string.Empty),
                    GetRecognitionArtist(recognition),
                    NormalizeText(recognition.Isrc, string.Empty),
                    Array.Empty<RecommendationTrackDto>(),
                    scannedAtUtc,
                    "Shazam did not return a track id."),
                cancellationToken);
            return null;
        }

        return new RecognizedShazamTrack(recognition, shazamTrackId);
    }

    private async Task PersistIncompleteShazamAttemptAsync(
        long trackId,
        ShazamRecognitionAttempt attempt,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        var status = attempt.Outcome == ShazamRecognitionOutcome.NoMatch ? StatusNoMatch : StatusError;
        await _repository.UpsertTrackShazamCacheAsync(
            new LibraryRepository.TrackShazamCacheUpsertInput(
                trackId,
                status,
                NormalizeId(attempt.Recognition?.TrackId),
                NormalizeText(attempt.Recognition?.Title, string.Empty),
                GetRecognitionArtist(attempt.Recognition),
                NormalizeText(attempt.Recognition?.Isrc, string.Empty),
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                string.IsNullOrWhiteSpace(attempt.Error) ? null : attempt.Error.Trim()),
            cancellationToken);
    }

    private async Task<IReadOnlyList<ShazamTrackCard>?> TryFetchRelatedShazamTracksAsync(
        long trackId,
        RecognizedShazamTrack recognizedTrack,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _shazamDiscoveryService.GetRelatedTracksAsync(
                recognizedTrack.ShazamTrackId,
                limit: ShazamRelatedPerSeed,
                offset: 0,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Shazam related-track fetch timed out for track {TrackId} ({ShazamTrackId}).",
                    trackId,
                    recognizedTrack.ShazamTrackId);
            }

            await PersistMatchedShazamCacheAsync(
                trackId,
                recognizedTrack,
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                StatusMatchedNoRelated,
                "Shazam related-track fetch timed out.",
                cancellationToken);
            return null;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Shazam related-track fetch failed for track {TrackId} ({ShazamTrackId}). Persisting matched recognition without related tracks.",
                trackId,
                recognizedTrack.ShazamTrackId);
            await PersistMatchedShazamCacheAsync(
                trackId,
                recognizedTrack,
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                StatusMatchedNoRelated,
                BuildPersistedFailureReason("Shazam related-track fetch failed", ex),
                cancellationToken);
            return null;
        }
    }

    private async Task<IReadOnlyList<ShazamTrackCard>?> TryFetchSimilarShazamTracksAsync(
        long trackId,
        RecognizedShazamTrack recognizedTrack,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        // Related-only. Shazam's text-search endpoints are retired, so the search half of
        // the old related+search merge could only ever contribute an empty list while still
        // spending a discover.py process per library track. Related already answers from the
        // same recognition's track id and is the only half that ever returned anything.
        var relatedCards = await TryFetchRelatedShazamTracksAsync(trackId, recognizedTrack, scannedAtUtc, cancellationToken);
        if (relatedCards is null)
        {
            return null;
        }

        // Still de-duplicated: related can echo the matched track back, and it is not
        // itself a recommendation.
        return BuildShazamSimilarCards(relatedCards, recognizedTrack);
    }

    private static List<ShazamTrackCard> BuildShazamSimilarCards(
        IReadOnlyList<ShazamTrackCard> relatedCards,
        RecognizedShazamTrack recognizedTrack)
    {
        var output = new List<ShazamTrackCard>(ShazamSimilarLookupLimit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedIdentity = BuildShazamRecognitionIdentity(recognizedTrack);
        AddShazamSimilarCards(relatedCards, output, seen, matchedIdentity);
        return output;
    }

    private static string ResolveShazamSimilarStatus(int relatedRecommendationCount, int similarCardCount)
    {
        if (relatedRecommendationCount > 0)
        {
            return StatusMatched;
        }

        return similarCardCount > 0 ? StatusMatchedNoDeezerResolution : StatusMatchedNoRelated;
    }

    private static void AddShazamSimilarCards(
        IReadOnlyList<ShazamTrackCard> source,
        List<ShazamTrackCard> output,
        HashSet<string> seen,
        string? matchedIdentity)
    {
        foreach (var card in source)
        {
            if (output.Count >= ShazamSimilarLookupLimit)
            {
                return;
            }

            var identity = BuildShazamCardIdentity(card);
            if (string.IsNullOrWhiteSpace(identity)
                || string.Equals(identity, matchedIdentity, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(identity))
            {
                continue;
            }

            output.Add(card);
        }
    }

    private static string? BuildShazamCardIdentity(ShazamTrackCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.Id))
        {
            return $"id:{card.Id.Trim()}";
        }

        return BuildShazamTextIdentity(card.Title, card.Artist);
    }

    private static string? BuildShazamRecognitionIdentity(RecognizedShazamTrack recognizedTrack)
    {
        if (!string.IsNullOrWhiteSpace(recognizedTrack.ShazamTrackId))
        {
            return $"id:{recognizedTrack.ShazamTrackId.Trim()}";
        }

        return BuildShazamTextIdentity(
            recognizedTrack.Recognition.Title,
            recognizedTrack.Recognition.Artist);
    }

    private static string? BuildShazamTextIdentity(string? title, string? artist)
    {
        var normalizedTitle = NormalizeText(title, string.Empty).ToLowerInvariant();
        var normalizedArtist = NormalizeText(artist, string.Empty).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedArtist)
            ? null
            : $"ta:{normalizedTitle}|{normalizedArtist}";
    }

    private static string BuildPersistedFailureReason(string prefix, Exception exception)
    {
        var message = exception.Message?.Trim();
        var reason = string.IsNullOrWhiteSpace(message)
            ? prefix
            : $"{prefix}: {message}";
        return reason.Length <= PersistedFailureReasonMaxLength
            ? reason
            : reason[..PersistedFailureReasonMaxLength];
    }

    private async Task<List<RecommendationTrackDto>> BuildRelatedShazamRecommendationsAsync(
        IReadOnlyList<ShazamTrackCard> relatedCards,
        long sourceTrackId,
        IDictionary<string, string> deezerResolveCache,
        CancellationToken cancellationToken)
    {
        var relatedRecommendations = new List<RecommendationTrackDto>();
        var seenDeezerIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in relatedCards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deezerId = NormalizeId(await ResolveDeezerIdAsync(card, sourceTrackId, deezerResolveCache, cancellationToken));
            if (string.IsNullOrWhiteSpace(deezerId) || !seenDeezerIds.Add(deezerId))
            {
                continue;
            }

            relatedRecommendations.Add(CreateRecommendationFromShazamCard(card, deezerId, relatedRecommendations.Count + 1) with
            { Provenance = new[] { new RecommendationProvenanceDto("shazam", card.Id, sourceTrackId, string.Empty, relatedRecommendations.Count + 1) } });
            if (relatedRecommendations.Count >= ShazamRelatedPerSeed)
            {
                break;
            }
        }

        return relatedRecommendations;
    }

    private static RecommendationTrackDto CreateRecommendationFromShazamCard(
        ShazamTrackCard card,
        string deezerId,
        int trackPosition)
    {
        var duration = card.DurationMs.HasValue && card.DurationMs.Value > 0
            ? Math.Max(0, (int)Math.Round(card.DurationMs.Value / 1000d))
            : 0;
        return NormalizeRecommendationTrack(new RecommendationTrackDto(
            deezerId,
            NormalizeText(card.Title, UnknownTitle),
            duration,
            NormalizeText(card.Isrc, string.Empty),
            trackPosition,
            new RecommendationArtistDto(
                NormalizeId(card.ArtistIds.FirstOrDefault() ?? string.Empty),
                NormalizeText(card.Artist, UnknownArtist)),
            new RecommendationAlbumDto(
                NormalizeId(card.AlbumAdamId ?? string.Empty),
                NormalizeText(card.Album, UnknownAlbum),
                NormalizeCoverMedium(card.ArtworkUrl)), SourceUrl: card.Url, RecommendationOrigin: "shazam"));
    }

    private async Task PersistMatchedShazamCacheAsync(
        long trackId,
        RecognizedShazamTrack recognizedTrack,
        IReadOnlyList<RecommendationTrackDto> relatedRecommendations,
        DateTimeOffset scannedAtUtc,
        string status,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        await _repository.UpsertTrackShazamCacheAsync(
            new LibraryRepository.TrackShazamCacheUpsertInput(
                trackId,
                status,
                recognizedTrack.ShazamTrackId,
                NormalizeText(recognizedTrack.Recognition.Title, string.Empty),
                GetRecognitionArtist(recognizedTrack.Recognition),
                NormalizeText(recognizedTrack.Recognition.Isrc, string.Empty),
                relatedRecommendations,
                scannedAtUtc,
                failureReason),
            cancellationToken);
    }

    private static string GetRecognitionArtist(ShazamRecognitionInfo? recognition)
    {
        var artist = recognition?.Artists.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? recognition?.Artist;
        return NormalizeText(artist, string.Empty);
    }

    private async Task<string> ResolveDeezerIdAsync(
        ShazamTrackCard card,
        long sourceTrackId,
        IDictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildResolveCacheKey(card);
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolved = string.Empty;
        foreach (var deezerLink in EnumerateShazamDeezerLinks(card))
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved = NormalizeId(TryExtractDeezerTrackId(deezerLink));
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = await TryResolveShazamCardThroughCentralResolverAsync(card, sourceTrackId, cancellationToken);
        }

        cache[cacheKey] = resolved;
        return resolved;
    }

    private async Task<string> TryResolveShazamCardThroughCentralResolverAsync(
        ShazamTrackCard card,
        long sourceTrackId,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _trackIdentityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: "shazam",
                    SourceUrl: card.Url,
                    Title: NormalizeText(card.Title, string.Empty),
                    Artist: NormalizeText(card.Artist, string.Empty),
                    Album: NormalizeText(card.Album, string.Empty),
                    Isrc: NormalizeText(card.Isrc, string.Empty),
                    DurationMs: card.DurationMs,
                    TargetPlatforms: new[] { DeezerSource }),
                cancellationToken);
            return NormalizeId(resolution.DeezerId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Shazam recommendation central Deezer resolve failed for source track {TrackId}.",
                    sourceTrackId);
            }

            return string.Empty;
        }
    }

    private static bool TryParseDeezerSearchCandidate(
        object item,
        out string deezerId,
        out ApiTrack? candidate)
    {
        deezerId = string.Empty;
        candidate = null;

        if (item is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        deezerId = NormalizeId(GetJsonString(element, "id"));
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return false;
        }

        candidate = new ApiTrack
        {
            Id = deezerId,
            Title = NormalizeText(GetJsonString(element, "title"), string.Empty),
            TitleVersion = NormalizeText(GetJsonString(element, "title_version"), string.Empty),
            Isrc = NormalizeText(GetJsonString(element, "isrc"), string.Empty),
            Duration = GetJsonInt(element, "duration") ?? 0,
            Artist = new ApiArtist
            {
                Name = NormalizeText(
                    GetJsonNestedString(element, "artist", "name") ?? GetJsonString(element, "artist"),
                    string.Empty)
            },
            Album = new ApiAlbum
            {
                Title = NormalizeText(GetJsonNestedString(element, "album", "title"), string.Empty)
            }
        };

        return true;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static string? GetJsonNestedString(JsonElement element, string parentProperty, string childProperty)
    {
        if (!element.TryGetProperty(parentProperty, out var parent)
            || parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(childProperty, out var child))
        {
            return null;
        }

        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString()?.Trim(),
            JsonValueKind.Number => child.ToString(),
            _ => null
        };
    }

    private static int? GetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static bool HasIsrcMismatch(string? sourceIsrc, string? candidateIsrc)
    {
        var source = NormalizeText(sourceIsrc, string.Empty);
        var candidate = NormalizeText(candidateIsrc, string.Empty);
        return !string.IsNullOrWhiteSpace(source)
            && !string.IsNullOrWhiteSpace(candidate)
            && !string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCandidateTitle(ApiTrack candidate)
    {
        var title = NormalizeText(candidate.Title, string.Empty);
        var version = NormalizeText(candidate.TitleVersion, string.Empty);
        if (string.IsNullOrWhiteSpace(version))
        {
            return title;
        }

        return title.Contains(version, StringComparison.OrdinalIgnoreCase)
            ? title
            : $"{title} {version}".Trim();
    }

    private static string NormalizeMatchText(string? value)
    {
        var normalized = NormalizeText(value, string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        var compact = string.Join(
            " ",
            builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact;
    }

    private static double ComputeTokenSimilarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0d;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
        {
            return 0.92d;
        }

        var leftTokens = left
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var rightTokens = right
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0d;
        }

        var intersectionCount = leftTokens.Count(token => rightTokens.Contains(token));
        var unionCount = leftTokens.Count + rightTokens.Count - intersectionCount;
        return unionCount == 0 ? 0d : (double)intersectionCount / unionCount;
    }

    private static IEnumerable<string> EnumerateShazamDeezerLinks(ShazamTrackCard card)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (card.Tags.TryGetValue("SHAZAM_DEEZER_URL", out var deezerUrls))
        {
            foreach (var normalized in deezerUrls
                .Select(deezerUrl => NormalizeText(deezerUrl, string.Empty))
                .Where(normalized => !string.IsNullOrWhiteSpace(normalized)
                    && IsDeezerLinkCandidate(normalized)
                    && seen.Add(normalized)))
            {
                yield return normalized;
            }
        }

        if (IsDeezerLinkCandidate(card.Url))
        {
            var normalized = NormalizeText(card.Url, string.Empty);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static bool IsDeezerLinkCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(TryExtractDeezerTrackId(value));
    }

    private static string TryExtractDeezerTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (IsNumericIdentifier(trimmed))
        {
            return trimmed;
        }

        if (TryExtractTrackIdFromDeezerTrackPrefix(trimmed, out var prefixedId))
        {
            return prefixedId;
        }

        if (TryExtractTrackIdFromDeezerUri(trimmed, out var uriId))
        {
            return uriId;
        }

        if (TryExtractTrackIdFromTrackMarker(trimmed, out var markerId))
        {
            return markerId;
        }

        return string.Empty;
    }

    private static bool IsNumericIdentifier(string value)
    {
        return long.TryParse(value, out _);
    }

    private static bool TryExtractTrackIdFromDeezerTrackPrefix(string value, out string trackId)
    {
        trackId = string.Empty;
        const string deezerTrackPrefix = "deezer:track:";
        if (!value.StartsWith(deezerTrackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var raw = value[deezerTrackPrefix.Length..];
        var candidate = new string(raw.TakeWhile(char.IsDigit).ToArray());
        if (!IsNumericIdentifier(candidate))
        {
            return false;
        }

        trackId = candidate;
        return true;
    }

    private static bool TryExtractTrackIdFromDeezerUri(string value, out string trackId)
    {
        trackId = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, DeezerSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "track", StringComparison.OrdinalIgnoreCase))
        {
            var deezerPath = uri.AbsolutePath.Trim('/');
            if (IsNumericIdentifier(deezerPath))
            {
                trackId = deezerPath;
                return true;
            }
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = segments[i + 1];
            if (IsNumericIdentifier(candidate))
            {
                trackId = candidate;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryExtractTrackIdFromTrackMarker(string value, out string trackId)
    {
        trackId = string.Empty;
        const string deezerTrackMarker = "deezer.com/track/";
        var markerIndex = value.IndexOf(deezerTrackMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var raw = value[(markerIndex + deezerTrackMarker.Length)..];
        var candidate = new string(raw.TakeWhile(char.IsDigit).ToArray());
        if (!IsNumericIdentifier(candidate))
        {
            return false;
        }

        trackId = candidate;
        return true;
    }

    private async Task PersistRecognitionSourceLinksAsync(
        long trackId,
        ShazamRecognitionInfo recognition,
        CancellationToken cancellationToken)
    {
        var spotifyUrl = NormalizeOptionalText(recognition.SpotifyUrl);
        var appleUrl = NormalizeOptionalText(recognition.AppleMusicUrl);
        await TryPersistPlatformSourceLinkAsync(
            trackId,
            SpotifySource,
            spotifyUrl,
            TryExtractSpotifyTrackId,
            cancellationToken);
        await TryPersistPlatformSourceLinkAsync(
            trackId,
            AppleSource,
            appleUrl,
            TryExtractAppleTrackId,
            cancellationToken);

        var deezerLink = await TryResolveAndPersistPlatformSourcesAsync(
            trackId,
            spotifyUrl,
            appleUrl,
            cancellationToken);
        var deezerId = deezerLink.DeezerId;
        var deezerUrl = deezerLink.DeezerUrl;

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            deezerId = await TryResolveDeezerIdByIsrcAsync(trackId, recognition.Isrc);
        }

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            deezerId = await TryResolveDeezerIdByMetadataAsync(trackId, recognition);
        }

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return;
        }

        var resolvedDeezerUrl = string.IsNullOrWhiteSpace(deezerUrl)
            ? $"https://www.deezer.com/track/{deezerId}"
            : deezerUrl;
        await _repository.UpsertTrackSourceLinkAsync(
            trackId,
            DeezerSource,
            deezerId,
            resolvedDeezerUrl,
            cancellationToken: cancellationToken);
    }

    private async Task TryPersistPlatformSourceLinkAsync(
        long trackId,
        string source,
        string? platformUrl,
        Func<string?, string?> sourceIdExtractor,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeOptionalText(platformUrl);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            return;
        }

        var sourceId = sourceIdExtractor(normalizedUrl);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        await _repository.UpsertTrackSourceLinkAsync(
            trackId,
            source,
            sourceId,
            normalizedUrl,
            cancellationToken: cancellationToken);
    }

    private async Task<(string DeezerId, string DeezerUrl)> TryResolveAndPersistPlatformSourcesAsync(
        long trackId,
        string? spotifyUrl,
        string? appleUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyUrl) && string.IsNullOrWhiteSpace(appleUrl))
        {
            return (string.Empty, string.Empty);
        }

        var preferredUrl = !string.IsNullOrWhiteSpace(spotifyUrl) ? spotifyUrl : appleUrl!;
        try
        {
            var linked = await _trackIdentityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: !string.IsNullOrWhiteSpace(spotifyUrl) ? SpotifySource : AppleSource,
                    SourceUrl: preferredUrl,
                    Title: null,
                    Artist: null,
                    Album: null,
                    Isrc: null,
                    DurationMs: null,
                    TargetPlatforms: new[] { DeezerSource, SpotifySource, AppleSource }),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(appleUrl))
            {
                await TryPersistPlatformSourceLinkAsync(
                    trackId,
                    AppleSource,
                    linked.AppleUrl,
                    TryExtractAppleTrackId,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(spotifyUrl))
            {
                await TryPersistPlatformSourceLinkAsync(
                    trackId,
                    SpotifySource,
                    linked.SpotifyUrl,
                    TryExtractSpotifyTrackId,
                    cancellationToken);
            }

            return (
                NormalizeId(linked.DeezerId),
                NormalizeOptionalText(linked.DeezerUrl) ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "platform source-link persistence failed for library track {TrackId}.", trackId);
            }
            return (string.Empty, string.Empty);
        }
    }

    private async Task<string> TryResolveDeezerIdByIsrcAsync(
        long trackId,
        string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return string.Empty;
        }

        try
        {
            var deezerTrack = await _deezerClient.GetTrackByIsrcAsync(isrc.Trim());
            return NormalizeId(deezerTrack?.Id?.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer ISRC source-link persistence failed for library track {TrackId}.", trackId);
            }
            return string.Empty;
        }
    }

    private async Task<string> TryResolveDeezerIdByMetadataAsync(
        long trackId,
        ShazamRecognitionInfo recognition)
    {
        if (string.IsNullOrWhiteSpace(recognition.Artist) || string.IsNullOrWhiteSpace(recognition.Title))
        {
            return string.Empty;
        }

        try
        {
            return NormalizeId(await _deezerClient.GetTrackIdFromMetadataAsync(
                recognition.Artist.Trim(),
                recognition.Title.Trim(),
                recognition.Album?.Trim() ?? string.Empty,
                recognition.DurationMs.HasValue && recognition.DurationMs.Value > 0
                    ? (int?)recognition.DurationMs.Value
                    : null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer metadata source-link persistence failed for library track {TrackId}.", trackId);
            }
            return string.Empty;
        }
    }

    private static List<RecommendationTrackDto> MergeRotating(
        List<RecommendationTrackDto> deezerTracks,
        List<RecommendationTrackDto> shazamTracks,
        int limit,
        DateOnly dayUtc)
    {
        var cappedLimit = Math.Clamp(limit, 1, RecommendationPoolLimit);
        var merged = new List<RecommendationTrackDto>(cappedLimit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deezerIndex = 0;
        var shazamIndex = 0;
        var useShazam = dayUtc.DayNumber % 2 == 0;

        while (merged.Count < cappedLimit && (deezerIndex < deezerTracks.Count || shazamIndex < shazamTracks.Count))
        {
            var added = false;

            if (useShazam)
            {
                added = TryAddTrack(shazamTracks, ref shazamIndex, merged, seen);
                if (!added)
                {
                    added = TryAddTrack(deezerTracks, ref deezerIndex, merged, seen);
                }
            }
            else
            {
                added = TryAddTrack(deezerTracks, ref deezerIndex, merged, seen);
                if (!added)
                {
                    added = TryAddTrack(shazamTracks, ref shazamIndex, merged, seen);
                }
            }

            if (!added)
            {
                break;
            }

            useShazam = !useShazam;
        }

        DrainTracksUntilLimit(deezerTracks, ref deezerIndex, merged, seen, cappedLimit);
        DrainTracksUntilLimit(shazamTracks, ref shazamIndex, merged, seen, cappedLimit);

        for (var index = 0; index < merged.Count; index++)
        {
            merged[index] = merged[index] with { TrackPosition = index + 1 };
        }

        return merged;
    }

    private sealed record RecommendationLane(string Key, Queue<RecommendationTrackDto> Tracks);

    private static List<RecommendationTrackDto> TopUpRecommendationSelection(
        List<RecommendationTrackDto> primarySelection,
        IReadOnlyList<RecommendationTrackDto> topUpCandidates,
        int limit,
        DateOnly dayUtc)
    {
        var cappedLimit = Math.Clamp(limit, 1, MaxDailyRecommendations);
        if (primarySelection.Count >= cappedLimit)
        {
            return primarySelection
                .Take(cappedLimit)
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var output = new List<RecommendationTrackDto>(cappedLimit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddUniqueTracks(primarySelection, output, seen, cappedLimit);
        if (output.Count < cappedLimit)
        {
            AddTopUpTracks(topUpCandidates, output, seen, cappedLimit, dayUtc);
        }

        return output
            .Select((track, index) => track with { TrackPosition = index + 1 })
            .ToList();
    }

    private static void AddUniqueTracks(
        IEnumerable<RecommendationTrackDto> tracks,
        List<RecommendationTrackDto> output,
        HashSet<string> seen,
        int limit)
    {
        foreach (var track in tracks)
        {
            if (!TryNormalizeTrackId(track.Id, out var normalizedId)
                || !seen.Add(normalizedId))
            {
                continue;
            }

            output.Add(track with { Id = normalizedId });
            if (output.Count >= limit)
            {
                return;
            }
        }
    }

    private static void AddTopUpTracks(
        IReadOnlyList<RecommendationTrackDto> topUpCandidates,
        List<RecommendationTrackDto> output,
        HashSet<string> seen,
        int limit,
        DateOnly dayUtc)
    {
        var remaining = topUpCandidates
            .Where(track => TryNormalizeTrackId(track.Id, out var normalizedId) && !seen.Contains(normalizedId))
            .ToList();
        var topUpTracks = BuildDiversifiedTrackSelection(
            remaining,
            limit - output.Count,
            dayUtc);
        AddUniqueTracks(topUpTracks, output, seen, limit);
    }

    private static bool TryNormalizeTrackId(string? id, out string normalizedId)
    {
        normalizedId = NormalizeId(id);
        return !string.IsNullOrWhiteSpace(normalizedId);
    }

    private static List<RecommendationTrackDto> BuildDiversifiedTrackSelection(
        IReadOnlyList<RecommendationTrackDto> tracks,
        int limit,
        DateOnly dayUtc)
    {
        var cappedLimit = Math.Clamp(limit, 1, MaximumConfiguredDailyTracks * RecommendationPoolMultiplier);
        if (tracks.Count == 0)
        {
            return new List<RecommendationTrackDto>();
        }

        if (tracks.Count <= cappedLimit)
        {
            return tracks
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var lanes = tracks
            .GroupBy(BuildRecommendationLaneKey, StringComparer.Ordinal)
            .Select(group => new RecommendationLane(
                group.Key,
                new Queue<RecommendationTrackDto>(group)))
            .OrderBy(lane => ComputeStableHash($"{dayUtc:yyyyMMdd}:{lane.Key}"))
            .ThenBy(lane => lane.Key, StringComparer.Ordinal)
            .ToList();

        if (lanes.Count == 0)
        {
            return tracks
                .Take(cappedLimit)
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var selected = new List<RecommendationTrackDto>(cappedLimit);
        while (selected.Count < cappedLimit && lanes.Count > 0)
        {
            for (var index = 0; index < lanes.Count && selected.Count < cappedLimit; index++)
            {
                var lane = lanes[index];
                if (lane.Tracks.Count == 0)
                {
                    continue;
                }

                selected.Add(lane.Tracks.Dequeue());
            }

            lanes.RemoveAll(lane => lane.Tracks.Count == 0);
        }

        return selected
            .Select((track, index) => track with { TrackPosition = index + 1 })
            .ToList();
    }

    private static List<RecommendationTrackDto> BuildVisibleDailySelection(
        IReadOnlyList<RecommendationTrackDto> tracks,
        HashSet<string> excludedTrackIds,
        int limit,
        DateOnly dayUtc)
    {
        var eligibleTracks = excludedTrackIds.Count == 0
            ? tracks
            : tracks
                .Where(track => !excludedTrackIds.Contains(NormalizeId(track.Id)))
                .ToList();
        var dailySelection = BuildDiversifiedTrackSelection(eligibleTracks, limit, dayUtc);
        dailySelection = TopUpRecommendationSelection(dailySelection, eligibleTracks, limit, dayUtc);
        if (eligibleTracks.Any(t => t.Provenance is { Count: > 0 }))
        {
            var quotas = dailySelection.GroupBy(t => t.RecommendationOrigin ?? DeezerSource)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            return BuildSavedDailySelection(eligibleTracks.Select(t => t with { RecommendationOrigin = t.RecommendationOrigin ?? DeezerSource }).ToList(),
                quotas, limit, dayUtc, excludedTrackIds, null);
        }
        return dailySelection;
    }

    private static HashSet<string> BuildNormalizedRecommendationIdSet(IEnumerable<string> ids)
    {
        return ids
            .Select(NormalizeId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildRecommendationLaneKey(RecommendationTrackDto track)
    {
        var artistId = NormalizeReferenceId(track.Artist.Id);
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            return $"artist:{artistId}";
        }

        var artistName = NormalizeText(track.Artist.Name, UnknownArtist);
        if (!string.IsNullOrWhiteSpace(artistName)
            && !string.Equals(artistName, UnknownArtist, StringComparison.OrdinalIgnoreCase))
        {
            return $"artist-name:{artistName.ToLowerInvariant()}";
        }

        var albumId = NormalizeReferenceId(track.Album.Id);
        if (!string.IsNullOrWhiteSpace(albumId))
        {
            return $"album:{albumId}";
        }

        var normalizedTrackId = NormalizeTrackId(track.Id);
        if (!string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            return $"track:{normalizedTrackId}";
        }

        var textArtist = NormalizeText(track.Artist.Name, UnknownArtist);
        var textTitle = NormalizeText(track.Title, UnknownTitle);
        return $"text:{textArtist.ToLowerInvariant()}|{textTitle.ToLowerInvariant()}";
    }

    private static void DrainTracksUntilLimit(
        List<RecommendationTrackDto> tracks,
        ref int index,
        List<RecommendationTrackDto> output,
        HashSet<string> seen,
        int limit)
    {
        while (output.Count < limit)
        {
            if (!TryAddTrack(tracks, ref index, output, seen))
            {
                break;
            }
        }
    }

    private static bool TryAddTrack(
        List<RecommendationTrackDto> tracks,
        ref int index,
        List<RecommendationTrackDto> output,
        HashSet<string> seen)
    {
        while (index < tracks.Count)
        {
            var track = tracks[index++];
            var id = NormalizeId(track.Id);
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            output.Add(track with { Id = id });
            return true;
        }

        return false;
    }

    private static List<RecommendationTrackDto> OrderDeterministically(
        IEnumerable<RecommendationTrackDto> tracks,
        DateOnly dayUtc,
        string sourceTag)
    {
        return tracks
            .OrderBy(track => ComputeDailyScore($"{sourceTag}:{NormalizeId(track.Id)}", dayUtc))
            .ThenBy(track => NormalizeId(track.Id), StringComparer.Ordinal)
            .ToList();
    }

    private static RecommendationTrackDto NormalizeRecommendationTrack(RecommendationTrackDto track)
    {
        return track with
        {
            Id = NormalizeTrackId(track.Id), Title = NormalizeText(track.Title, UnknownTitle),
            Duration = Math.Max(0, track.Duration), Isrc = NormalizeText(track.Isrc, string.Empty),
            TrackPosition = track.TrackPosition > 0 ? track.TrackPosition : 1,
            Artist = new(NormalizeReferenceId(track.Artist?.Id), NormalizeText(track.Artist?.Name, UnknownArtist)),
            Album = new(NormalizeReferenceId(track.Album?.Id), NormalizeText(track.Album?.Title, UnknownAlbum), NormalizeCoverMedium(track.Album?.CoverMedium))
        };
    }

    private async Task<IReadOnlyList<RecommendationTrackDto>> EnrichRecommendationMetadataAsync(
        List<RecommendationTrackDto> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return tracks;
        }

        var normalized = tracks
            .Select(NormalizeRecommendationTrack)
            .ToArray();
        var pendingIndexes = GetRecommendationMetadataPendingIndexes(normalized);
        if (pendingIndexes.Count == 0)
        {
            return normalized;
        }

        var pendingByTrackId = BuildPendingTrackMap(normalized, pendingIndexes);
        if (pendingByTrackId.Count == 0)
        {
            return normalized;
        }

        var unresolvedSet = ApplyCachedRecommendationMetadata(normalized, pendingByTrackId);
        if (unresolvedSet.Count == 0)
        {
            return normalized;
        }

        await EnrichRecommendationsFromGatewayAsync(normalized, pendingByTrackId, unresolvedSet, cancellationToken);
        return normalized;
    }

    private static List<int> GetRecommendationMetadataPendingIndexes(RecommendationTrackDto[] normalized)
    {
        var pendingIndexes = new List<int>(normalized.Length);
        for (var index = 0; index < normalized.Length; index++)
        {
            if (NeedsRecommendationMetadataEnrichment(normalized[index]))
            {
                pendingIndexes.Add(index);
            }
        }

        return pendingIndexes;
    }

    private static Dictionary<string, List<int>> BuildPendingTrackMap(
        RecommendationTrackDto[] normalized,
        IReadOnlyList<int> pendingIndexes)
    {
        var pendingByTrackId = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var index in pendingIndexes)
        {
            var deezerId = NormalizeId(normalized[index].Id);
            if (string.IsNullOrWhiteSpace(deezerId))
            {
                continue;
            }

            if (!pendingByTrackId.TryGetValue(deezerId, out var indexes))
            {
                indexes = new List<int>();
                pendingByTrackId[deezerId] = indexes;
            }

            indexes.Add(index);
        }

        return pendingByTrackId;
    }

    private HashSet<string> ApplyCachedRecommendationMetadata(
        RecommendationTrackDto[] normalized,
        Dictionary<string, List<int>> pendingByTrackId)
    {
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deezerId in pendingByTrackId.Keys)
        {
            if (_deezerRecommendationMetadataCache.TryGetValue(deezerId, out var cached))
            {
                ApplyRecommendationMetadata(normalized, pendingByTrackId, deezerId, cached);
                continue;
            }

            unresolved.Add(deezerId);
        }

        return unresolved;
    }

    private async Task EnrichRecommendationsFromGatewayAsync(
        RecommendationTrackDto[] normalized,
        Dictionary<string, List<int>> pendingByTrackId,
        HashSet<string> unresolvedTrackIds,
        CancellationToken cancellationToken)
    {
        const int batchSize = 100;
        var unresolvedIds = unresolvedTrackIds.ToList();
        for (var start = 0; start < unresolvedIds.Count; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = unresolvedIds
                .Skip(start)
                .Take(batchSize)
                .ToList();
            try
            {
                var gatewayTracks = await _deezerGatewayService.GetTracksAsync(batch);
                foreach (var deezerMetadata in gatewayTracks.Select(MapGatewayTrack))
                {
                    var deezerId = NormalizeId(deezerMetadata.Id);
                    if (string.IsNullOrWhiteSpace(deezerId) || !unresolvedTrackIds.Remove(deezerId))
                    {
                        continue;
                    }

                    CacheDeezerRecommendationMetadata(deezerId, deezerMetadata);
                    ApplyRecommendationMetadata(normalized, pendingByTrackId, deezerId, deezerMetadata);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Recommendation metadata batch enrichment failed for {Count} tracks.", batch.Count);
                }
            }
        }
    }

    private static void ApplyRecommendationMetadata(
        RecommendationTrackDto[] normalized,
        Dictionary<string, List<int>> pendingByTrackId,
        string deezerId,
        RecommendationTrackDto metadata)
    {
        if (!pendingByTrackId.TryGetValue(deezerId, out var indexes))
        {
            return;
        }

        foreach (var index in indexes)
        {
            normalized[index] = MergeRecommendationTrack(normalized[index], metadata);
        }
    }

    private static RecommendationTrackDto MapGatewayTrack(GwTrack track)
    {
        var trackId = track?.SngId > 0 ? track.SngId.ToString() : string.Empty;
        var artistId = track?.ArtId > 0
            ? track.ArtId.ToString()
            : string.Empty;
        var albumId = NormalizeId(track?.AlbId);
        var title = track?.SngTitle;
        var version = NormalizeText(track?.Version, string.Empty);
        if (!string.IsNullOrWhiteSpace(version))
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                title = version;
            }
            else if (!title.Contains(version, StringComparison.OrdinalIgnoreCase))
            {
                title = $"{title} {version}";
            }
        }

        var albumTitle = track?.AlbTitle;
        var artistName = track?.ArtName;
        var duration = Math.Max(0, track?.Duration ?? 0);
        var position = 1;
        if (track?.TrackNumber > 0)
        {
            position = track.TrackNumber;
        }
        else if (track?.Position > 0)
        {
            position = track.Position;
        }
        var cover = FirstNonEmpty(
            BuildCoverUrl(track?.AlbPicture));

        return new RecommendationTrackDto(
            NormalizeTrackId(trackId),
            NormalizeText(title, UnknownTitle),
            duration,
            NormalizeText(track?.Isrc, string.Empty),
            position,
            new RecommendationArtistDto(
                NormalizeReferenceId(artistId),
                NormalizeText(artistName, UnknownArtist)),
            new RecommendationAlbumDto(
                NormalizeReferenceId(albumId),
                NormalizeText(albumTitle, UnknownAlbum),
                NormalizeText(cover, string.Empty)));
    }

    private static RecommendationTrackDto MergeRecommendationTrack(
        RecommendationTrackDto current,
        RecommendationTrackDto deezerMetadata)
    {
        var mergedArtistId = !string.IsNullOrWhiteSpace(current.Artist?.Id)
            ? NormalizeReferenceId(current.Artist.Id)
            : NormalizeReferenceId(deezerMetadata.Artist?.Id);
        var mergedArtistName = IsMissingOrUnknown(current.Artist!.Name, UnknownArtist)
            ? NormalizeText(deezerMetadata.Artist?.Name, UnknownArtist)
            : NormalizeText(current.Artist.Name, UnknownArtist);
        var mergedAlbumId = !string.IsNullOrWhiteSpace(current.Album?.Id)
            ? NormalizeReferenceId(current.Album.Id)
            : NormalizeReferenceId(deezerMetadata.Album?.Id);
        var mergedAlbumTitle = IsMissingOrUnknown(current.Album!.Title, UnknownAlbum)
            ? NormalizeText(deezerMetadata.Album?.Title, UnknownAlbum)
            : NormalizeText(current.Album.Title, UnknownAlbum);
        var currentCover = NormalizeCoverMedium(current.Album?.CoverMedium);
        var deezerCover = NormalizeCoverMedium(deezerMetadata.Album?.CoverMedium);
        var mergedCover = !string.IsNullOrWhiteSpace(currentCover)
            ? currentCover
            : deezerCover;

        return new RecommendationTrackDto(
            NormalizeTrackId(current.Id),
            NormalizeText(current.Title, UnknownTitle),
            current.Duration > 0 ? current.Duration : Math.Max(0, deezerMetadata.Duration),
            !string.IsNullOrWhiteSpace(current.Isrc)
                ? NormalizeText(current.Isrc, string.Empty)
                : NormalizeText(deezerMetadata.Isrc, string.Empty),
            current.TrackPosition > 0 ? current.TrackPosition : 1,
            new RecommendationArtistDto(mergedArtistId, mergedArtistName),
            new RecommendationAlbumDto(mergedAlbumId, mergedAlbumTitle, mergedCover), current.Source, current.SourceUrl, current.SeedArtistId, current.DeezerId, current.MappingStatus, current.RecommendationOrigin, current.Provenance, current.AiEvidence);
    }

    private static bool NeedsRecommendationMetadataEnrichment(RecommendationTrackDto track)
    {
        return track.Duration <= 0
            || IsMissingOrUnknown(track.Artist.Name, UnknownArtist)
            || IsMissingOrUnknown(track.Album.Title, UnknownAlbum)
            || string.IsNullOrWhiteSpace(track.Album.CoverMedium);
    }

    private static bool IsMissingOrUnknown(string? value, string unknownLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return string.Equals(value.Trim(), unknownLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCoverMedium(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{trimmed}";
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return string.Empty;
        }

        return LooksLikeDeezerCoverHash(trimmed)
            ? BuildCoverUrl(trimmed)
            : string.Empty;
    }

    private static bool LooksLikeDeezerCoverHash(string value)
    {
        return value.Length == 32 && value.All(Uri.IsHexDigit);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();
    }

    private static string BuildCoverUrl(string? md5OrUrl)
    {
        if (string.IsNullOrWhiteSpace(md5OrUrl))
        {
            return string.Empty;
        }

        var normalized = md5OrUrl.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return normalized;
        }

        return $"https://e-cdns-images.dzcdn.net/images/cover/{normalized}/500x500-000000-80-0-0.jpg";
    }

    private static string BuildResolveCacheKey(ShazamTrackCard card)
    {
        var isrc = NormalizeText(card.Isrc, string.Empty);
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            return $"isrc:{isrc}";
        }

        return $"meta:{NormalizeText(card.Artist, string.Empty)}|{NormalizeText(card.Title, string.Empty)}|{NormalizeText(card.Album, string.Empty)}|{card.DurationMs?.ToString() ?? string.Empty}";
    }

    private static ulong ComputeDailyScore(string value, DateOnly dayUtc)
    {
        var input = $"{dayUtc:yyyyMMdd}:{value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, sizeof(ulong)));
    }

    private static string BuildDailyCacheKey(string scopeKey, DateOnly dayUtc)
        => $"{scopeKey}:{dayUtc:yyyyMMdd}";

    private static ulong ComputeStableHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, sizeof(ulong)));
    }

    private void PruneOldCache(DateOnly currentDayUtc)
    {
        var marker = $":{currentDayUtc:yyyyMMdd}";
        var staleKeys = _dailyPoolCache.Keys
            .Where(key => !key.EndsWith(marker, StringComparison.Ordinal))
            .ToArray();
        foreach (var key in staleKeys)
        {
            _dailyPoolCache.TryRemove(key, out _);
        }
    }

    private void CacheDeezerRecommendationMetadata(string deezerId, RecommendationTrackDto metadata)
    {
        _deezerRecommendationMetadataCache[deezerId] = metadata;
        var excess = _deezerRecommendationMetadataCache.Count - DeezerMetadataCacheLimit;
        if (excess <= 0)
        {
            return;
        }

        foreach (var key in _deezerRecommendationMetadataCache.Keys.Take(excess))
        {
            _deezerRecommendationMetadataCache.TryRemove(key, out _);
        }
    }

    private static string NormalizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return long.TryParse(trimmed, out _) ? trimmed : string.Empty;
    }

    private static string NormalizeTrackId(string? value)
        => NormalizeId(value);

    private static string NormalizeReferenceId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeText(string? value, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = string.Join(
            " ",
            value
                .Trim()
                .Replace('\u2013', '-')
                .Replace('\u2014', '-')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrWhiteSpace(normalized) ? defaultValue : normalized;
    }

    private static string BuildDailyRecommendationDescription(string folderName, DayOfWeek dayOfWeek)
    {
        var normalizedFolderName = NormalizeText(folderName, "Library");
        var dayName = $"{dayOfWeek}'s";
        return $"{dayName} recommendation for {normalizedFolderName}.";
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = NormalizeText(value, string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? TryExtractSpotifyTrackId(string? spotifyUrl)
    {
        if (string.IsNullOrWhiteSpace(spotifyUrl))
        {
            return null;
        }

        return SpotifyMetadataService.TryParseSpotifyUrl(spotifyUrl, out var type, out var id)
               && string.Equals(type, "track", StringComparison.OrdinalIgnoreCase)
            ? NormalizeId(id)
            : null;
    }

    private static string? TryExtractAppleTrackId(string? appleUrl)
    {
        if (string.IsNullOrWhiteSpace(appleUrl)
            || !Uri.TryCreate(appleUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?');
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(parts => parts.Length == 2 && string.Equals(parts[0], "i", StringComparison.OrdinalIgnoreCase));
        if (parts is { Length: 2 })
        {
            return NormalizeId(Uri.UnescapeDataString(parts[1]));
        }

        var lastSegment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return NormalizeId(lastSegment);
    }
}
