using System.Collections.Concurrent;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed record TargetIdentityFetchResult(
    string Service,
    bool Success,
    int DeletedRows,
    TargetServerIdentityCoverageDto? Coverage,
    string? Error);

public sealed class MediaServerLibraryRefreshService
{
    private const int PlexTrackPageSize = 500;
    private const int RefreshAttemptCount = 5;
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string NavidromeService = "navidrome";
    private const string NoneService = "none";
    private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromSeconds(2);

    private readonly PlatformAuthService _authService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<MediaServerLibraryRefreshService> _logger;

    private sealed record TargetTrackIdentityCandidate(
        string TargetItemId,
        string FilePath,
        string? Title,
        string? Artist,
        string? Album,
        int? DurationMs,
        long? SizeBytes = null);

    public sealed record TargetIdentityRefreshProgressDto(
        string Service,
        long? FolderId,
        bool Running,
        int TotalTracks,
        int MappedTracks,
        int MissingTracks,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    private readonly ConcurrentDictionary<string, TargetIdentityRefreshProgressDto> _targetIdentityProgress =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class TargetIdentityLocalIndex
    {
        private const int DurationToleranceMs = 2000;

        private readonly Dictionary<string, List<long>> _pathMap;
        private readonly Dictionary<string, List<long>> _suffixMap;
        private readonly Dictionary<string, List<long>> _parentFileMap;
        private readonly Dictionary<string, List<long>> _albumTitlePathMap;
        private readonly Dictionary<long, TargetServerIdentityLocalTrackDto> _tracksById;
        private readonly IReadOnlyDictionary<string, string> _pathMappings;

        private TargetIdentityLocalIndex(
            IReadOnlyList<TargetServerIdentityLocalTrackDto> tracks,
            HashSet<long> missingTrackIds,
            Dictionary<string, List<long>> pathMap,
            Dictionary<string, List<long>> suffixMap,
            Dictionary<string, List<long>> parentFileMap,
            Dictionary<string, List<long>> albumTitlePathMap,
            IReadOnlyDictionary<string, string> pathMappings)
        {
            Tracks = tracks;
            MissingTrackIds = missingTrackIds;
            _pathMap = pathMap;
            _suffixMap = suffixMap;
            _parentFileMap = parentFileMap;
            _albumTitlePathMap = albumTitlePathMap;
            _pathMappings = pathMappings;
            _tracksById = tracks.ToDictionary(track => track.TrackId, track => track);
        }

        public IReadOnlyList<TargetServerIdentityLocalTrackDto> Tracks { get; }
        public HashSet<long> MissingTrackIds { get; }
        public IReadOnlyDictionary<string, string> PathMappings => _pathMappings;

        public static TargetIdentityLocalIndex Build(
            IReadOnlyList<TargetServerIdentityLocalTrackDto> tracks,
            IReadOnlyCollection<long>? requestedTrackIds = null,
            IReadOnlyDictionary<string, string>? pathMappings = null)
        {
            if (requestedTrackIds is { Count: > 0 })
            {
                var requested = requestedTrackIds.Where(static id => id > 0).ToHashSet();
                tracks = tracks.Where(track => requested.Contains(track.TrackId)).ToList();
            }
            var pathMap = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var suffixMap = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var parentFileMap = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var albumTitlePathMap = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var missingTrackIds = tracks
                .Where(static track => string.IsNullOrWhiteSpace(track.TargetItemId))
                .Select(static track => track.TrackId)
                .ToHashSet();

            foreach (var track in tracks)
            {
                AddPathKeys(track.AbsolutePath, track.TrackId, pathMap, suffixMap, parentFileMap, albumTitlePathMap);
                AddPathKeys(track.RelativePath, track.TrackId, pathMap, suffixMap, parentFileMap, albumTitlePathMap);
            }

            return new TargetIdentityLocalIndex(
                tracks,
                missingTrackIds,
                pathMap,
                suffixMap,
                parentFileMap,
                albumTitlePathMap,
                pathMappings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Layered resolution for one server item:
        ///   1. stored server-root -> local-root mapping turns the server path into a
        ///      deterministic local path (exact lookup);
        ///   2. exact normalized path;
        ///   3. path-suffix / parent-file / album-title tiers, collecting ALL matching
        ///      local candidates and narrowing them with size+duration fingerprints;
        ///   4. ties that survive fingerprinting are reported as ambiguous instead of
        ///      being guessed.
        /// </summary>
        public TargetIdentityPathResolution ResolveCandidate(TargetTrackIdentityCandidate candidate)
        {
            var normalized = NormalizePathForIdentity(candidate.FilePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return TargetIdentityPathResolution.Unmatched();
            }

            // 1. deterministic transform via a learned/overridden root mapping.
            foreach (var mapping in _pathMappings)
            {
                var serverRoot = NormalizePathForIdentity(mapping.Key);
                var localRoot = NormalizePathForIdentity(mapping.Value);
                if (serverRoot.Length == 0
                    || localRoot.Length == 0
                    || !normalized.StartsWith(serverRoot + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                var mappedPath = localRoot + "/" + normalized[(serverRoot.Length + 1)..];
                if (TrySingle(_pathMap, mappedPath, out var mappedTrackId))
                {
                    return TargetIdentityPathResolution.Mapped(mappedTrackId);
                }
            }

            // 2. exact normalized path.
            if (TrySingle(_pathMap, normalized, out var exactTrackId))
            {
                return TargetIdentityPathResolution.Mapped(exactTrackId);
            }

            // 3. tiered path evidence, fingerprint-narrowed.
            var tierCandidates = new List<long>();
            var matchedSuffixLength = 0;
            string? matchedSuffix = null;
            foreach (var suffix in BuildPathSuffixes(normalized))
            {
                if (_suffixMap.TryGetValue(suffix, out var tier))
                {
                    tierCandidates = tier.ToList();
                    matchedSuffixLength = suffix.Split('/').Length;
                    matchedSuffix = suffix;
                    break;
                }
            }

            if (tierCandidates.Count == 0)
            {
                var parentFileKey = BuildParentFileKey(normalized);
                if (!string.IsNullOrWhiteSpace(parentFileKey)
                    && _parentFileMap.TryGetValue(parentFileKey, out var parentTier))
                {
                    tierCandidates = parentTier.ToList();
                }
            }

            if (tierCandidates.Count == 0)
            {
                var albumTitleKey = BuildAlbumTitleKeyFromPath(normalized);
                if (!string.IsNullOrWhiteSpace(albumTitleKey)
                    && _albumTitlePathMap.TryGetValue(albumTitleKey, out var albumTier))
                {
                    tierCandidates = albumTier.ToList();
                }
            }

            tierCandidates = tierCandidates
                .Where(MissingTrackIds.Contains)
                .Distinct()
                .ToList();
            if (tierCandidates.Count == 0)
            {
                return TargetIdentityPathResolution.Unmatched();
            }

            var fingerprinted = tierCandidates
                .Where(trackId => FingerprintMatches(candidate, trackId))
                .ToList();
            if (fingerprinted.Count == 1)
            {
                var bound = fingerprinted[0];
                var derivation = DeriveMappingCandidate(
                    normalized, matchedSuffix, _tracksById[bound].AbsolutePath);
                return TargetIdentityPathResolution.Mapped(
                    bound,
                    derivation.ServerRoot,
                    derivation.LocalRoot);
            }

            if (fingerprinted.Count > 1)
            {
                return TargetIdentityPathResolution.CreateAmbiguous(fingerprinted);
            }

            // No fingerprint evidence on either side: single tier hit is acceptable,
            // multiple tier hits are ambiguous rather than guessed.
            if (tierCandidates.Count == 1 && !HasFingerprintEvidence(candidate))
            {
                var bound = tierCandidates[0];
                var derivation = DeriveMappingCandidate(
                    normalized, matchedSuffix, _tracksById[bound].AbsolutePath);
                return TargetIdentityPathResolution.Mapped(
                    bound,
                    derivation.ServerRoot,
                    derivation.LocalRoot);
            }

            return TargetIdentityPathResolution.CreateAmbiguous(tierCandidates);
        }

        private bool FingerprintMatches(TargetTrackIdentityCandidate candidate, long trackId)
        {
            if (!_tracksById.TryGetValue(trackId, out var local))
            {
                return false;
            }

            if (candidate.SizeBytes.HasValue && local.SizeBytes.HasValue)
            {
                return candidate.SizeBytes.Value == local.SizeBytes.Value;
            }

            if (candidate.DurationMs.HasValue && local.DurationMs.HasValue)
            {
                return Math.Abs(candidate.DurationMs.Value - local.DurationMs.Value) <= DurationToleranceMs;
            }

            return true;
        }

        private static bool HasFingerprintEvidence(TargetTrackIdentityCandidate candidate)
            => candidate.SizeBytes.HasValue || candidate.DurationMs.HasValue;

        private static (string? ServerRoot, string? LocalRoot) DeriveMappingCandidate(
            string candidatePath,
            string? matchedSuffix,
            string? localAbsolutePath)
        {
            if (string.IsNullOrWhiteSpace(matchedSuffix)
                || string.IsNullOrWhiteSpace(localAbsolutePath))
            {
                return (null, null);
            }

            var normalizedLocal = NormalizePathForIdentity(localAbsolutePath);
            var suffixIndex = normalizedLocal.EndsWith(matchedSuffix, StringComparison.Ordinal)
                ? normalizedLocal.Length - matchedSuffix.Length
                : -1;
            if (suffixIndex <= 0)
            {
                return (null, null);
            }

            var localRoot = normalizedLocal[..suffixIndex].TrimEnd('/');
            var serverRoot = candidatePath[..^matchedSuffix.Length].TrimEnd('/');
            if (localRoot.Length == 0 || serverRoot.Length == 0)
            {
                return (null, null);
            }

            return (serverRoot, localRoot);
        }

        private static bool TrySingle(Dictionary<string, List<long>> map, string key, out long trackId)
        {
            trackId = 0;
            if (map.TryGetValue(key, out var matches)
                && matches.Count == 1
                && matches[0] > 0)
            {
                trackId = matches[0];
                return true;
            }

            return false;
        }

        private static void AddPathKeys(
            string? path,
            long trackId,
            Dictionary<string, List<long>> pathMap,
            Dictionary<string, List<long>> suffixMap,
            Dictionary<string, List<long>> parentFileMap,
            Dictionary<string, List<long>> albumTitlePathMap)
        {
            var normalized = NormalizePathForIdentity(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            AddUnique(pathMap, normalized, trackId);
            foreach (var suffix in BuildPathSuffixes(normalized))
            {
                AddUnique(suffixMap, suffix, trackId);
            }

            AddUnique(parentFileMap, BuildParentFileKey(normalized), trackId);
            AddUnique(albumTitlePathMap, BuildAlbumTitleKeyFromPath(normalized), trackId);
        }

        private static void AddUnique(Dictionary<string, List<long>> map, string key, long trackId)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = [trackId];
                return;
            }

            if (!existing.Contains(trackId))
            {
                existing.Add(trackId);
            }
        }

        private static IEnumerable<string> BuildPathSuffixes(string normalizedPath)
        {
            var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var length = 2; length <= Math.Min(6, parts.Length); length++)
            {
                yield return string.Join('/', parts.Skip(parts.Length - length));
            }
        }

        private static string BuildParentFileKey(string normalizedPath)
        {
            var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length < 2
                ? string.Empty
                : $"{parts[^2]}/{parts[^1]}";
        }

        private static string BuildAlbumTitleKeyFromPath(string normalizedPath)
        {
            var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return string.Empty;
            }

            var album = NormalizeIdentityText(parts[^2]);
            var title = NormalizeIdentityText(Path.GetFileNameWithoutExtension(parts[^1]));
            return string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(title)
                ? string.Empty
                : $"{album}|{title}";
        }

    }

    /// <summary>
    /// Outcome of layered path resolution for one server item. Ambiguous means the
    /// path evidence tied between multiple local tracks even after fingerprinting —
    /// reported instead of guessed.
    /// </summary>
    internal sealed record TargetIdentityPathResolution(
        long? TrackId,
        bool Ambiguous,
        IReadOnlyList<long> TiedTrackIds,
        string? DerivedServerRoot,
        string? DerivedLocalRoot)
    {
        public static TargetIdentityPathResolution Mapped(
            long trackId,
            string? derivedServerRoot = null,
            string? derivedLocalRoot = null)
            => new(trackId, false, [trackId], derivedServerRoot, derivedLocalRoot);

        public static TargetIdentityPathResolution CreateAmbiguous(IReadOnlyList<long> tiedTrackIds)
            => new(null, true, tiedTrackIds, null, null);

        public static TargetIdentityPathResolution Unmatched()
            => new(null, false, [], null, null);
    }

    public MediaServerLibraryRefreshService(
        PlatformAuthService authService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        NavidromeApiClient navidromeApiClient,
        LibraryRepository libraryRepository,
        ILogger<MediaServerLibraryRefreshService> logger)
    {
        _authService = authService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _navidromeApiClient = navidromeApiClient;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    private static string NormalizePathForIdentity(string? path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(normalized, UriKind.Absolute, out var fileUri)
            && fileUri.IsFile)
        {
            normalized = fileUri.LocalPath;
        }

        normalized = TryUnescapePath(normalized);

        normalized = normalized.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimEnd('/').ToLowerInvariant();
    }

    private static string NormalizeIdentityText(string? value)
        => TrackTitleMatcher.NormalizeText(value);

    private static string TryUnescapePath(string path)
    {
        try
        {
            return Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return path;
        }
    }

    public TargetIdentityRefreshProgressDto? GetTargetIdentityRefreshProgress(string service, long? folderId)
        => _targetIdentityProgress.TryGetValue(BuildTargetIdentityProgressKey(service, folderId), out var progress)
            ? progress
            : null;

    public void StartTargetIdentityResetProgress(
        string service,
        long? folderId,
        TargetServerIdentityCoverageDto coverage)
    {
        var now = DateTimeOffset.UtcNow;
        _targetIdentityProgress[BuildTargetIdentityProgressKey(service, folderId)] = new TargetIdentityRefreshProgressDto(
            service,
            folderId,
            Running: true,
            coverage.TotalTracks,
            MappedTracks: 0,
            MissingTracks: coverage.TotalTracks,
            now,
            now);
    }

    private static string BuildTargetIdentityProgressKey(string service, long? folderId)
        => $"{(service ?? string.Empty).Trim().ToLowerInvariant()}:{folderId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"}";

    private void StartTargetIdentityProgress(string service, long? folderId, TargetIdentityLocalIndex localIndex)
    {
        var now = DateTimeOffset.UtcNow;
        var total = localIndex.Tracks.Count;
        var mapped = Math.Max(0, total - localIndex.MissingTrackIds.Count);
        _targetIdentityProgress[BuildTargetIdentityProgressKey(service, folderId)] = new TargetIdentityRefreshProgressDto(
            service,
            folderId,
            Running: true,
            total,
            mapped,
            Math.Max(0, total - mapped),
            now,
            now);
    }

    private void ReportTargetIdentityProgress(string service, long? folderId, TargetIdentityLocalIndex localIndex)
    {
        var key = BuildTargetIdentityProgressKey(service, folderId);
        var now = DateTimeOffset.UtcNow;
        var total = localIndex.Tracks.Count;
        var mapped = Math.Max(0, total - localIndex.MissingTrackIds.Count);
        _targetIdentityProgress.AddOrUpdate(
            key,
            _ => new TargetIdentityRefreshProgressDto(
                service,
                folderId,
                Running: true,
                total,
                mapped,
                Math.Max(0, total - mapped),
                now,
                now),
            (_, current) => current with
            {
                Running = true,
                TotalTracks = total,
                MappedTracks = mapped,
                MissingTracks = Math.Max(0, total - mapped),
                UpdatedAtUtc = now
            });
    }

    private void CompleteTargetIdentityProgress(string service, long? folderId, TargetIdentityLocalIndex localIndex)
    {
        var key = BuildTargetIdentityProgressKey(service, folderId);
        var now = DateTimeOffset.UtcNow;
        var total = localIndex.Tracks.Count;
        var mapped = Math.Max(0, total - localIndex.MissingTrackIds.Count);
        _targetIdentityProgress.AddOrUpdate(
            key,
            _ => new TargetIdentityRefreshProgressDto(
                service,
                folderId,
                Running: false,
                total,
                mapped,
                Math.Max(0, total - mapped),
                now,
                now),
            (_, current) => current with
            {
                Running = false,
                TotalTracks = total,
                MappedTracks = mapped,
                MissingTracks = Math.Max(0, total - mapped),
                UpdatedAtUtc = now
            });
    }

    public async Task RefreshAsync(string? service, CancellationToken cancellationToken)
    {
        var normalizedService = (service ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedService == NoneService)
        {
            return;
        }

        var state = await _authService.LoadAsync();
        if (normalizedService == JellyfinService)
        {
            await RefreshJellyfinAsync(state.Jellyfin, cancellationToken);
            return;
        }

        if (normalizedService == PlexService)
        {
            await RefreshPlexAsync(state.Plex, cancellationToken);
            return;
        }

        if (normalizedService == NavidromeService)
        {
            await RefreshNavidromeAsync(state.Navidrome, cancellationToken);
            return;
        }

        if (state.Plex is { } plex
            && !string.IsNullOrWhiteSpace(plex.Url)
            && !string.IsNullOrWhiteSpace(plex.Token))
        {
            await RefreshPlexAsync(plex, cancellationToken);
            return;
        }

        await RefreshJellyfinAsync(state.Jellyfin, cancellationToken);
    }

    public Task<MediaServerRefreshSummary> RefreshConfiguredServersAsync(
        CancellationToken cancellationToken)
        => RefreshConfiguredServersAsync(cancellationToken, updateTrackIndex: true);

    public async Task<MediaServerRefreshSummary> RefreshConfiguredServersAsync(
        CancellationToken cancellationToken,
        bool updateTrackIndex)
    {
        var state = await _authService.LoadAsync();
        var configuredServers = 0;
        var refreshedServers = 0;
        var failures = new List<string>();

        if (HasPlexConfiguration(state.Plex))
        {
            configuredServers++;
            if (await RefreshPlexAsync(state.Plex, updateTrackIndex, cancellationToken))
            {
                refreshedServers++;
            }
            else
            {
                failures.Add(PlexService);
            }
        }

        if (HasJellyfinConfiguration(state.Jellyfin))
        {
            configuredServers++;
            if (await RefreshJellyfinAsync(state.Jellyfin, updateTrackIndex, cancellationToken))
            {
                refreshedServers++;
            }
            else
            {
                failures.Add(JellyfinService);
            }
        }

        if (HasNavidromeConfiguration(state.Navidrome))
        {
            configuredServers++;
            if (await RefreshNavidromeAsync(state.Navidrome, updateTrackIndex, cancellationToken))
            {
                refreshedServers++;
            }
            else
            {
                failures.Add(NavidromeService);
            }
        }

        return new MediaServerRefreshSummary(configuredServers, refreshedServers, failures);
    }

    public async Task<IReadOnlyList<string>> GetConfiguredServicesAsync()
    {
        var state = await _authService.LoadAsync();
        var services = new List<string>(3);
        if (HasPlexConfiguration(state.Plex))
        {
            services.Add(PlexService);
        }
        if (HasJellyfinConfiguration(state.Jellyfin))
        {
            services.Add(JellyfinService);
        }
        if (HasNavidromeConfiguration(state.Navidrome))
        {
            services.Add(NavidromeService);
        }
        return services;
    }

    public async Task<TargetIdentityFetchResult> FetchTargetIdentitiesAsync(
        string service,
        long? folderId,
        bool resetFirst,
        CancellationToken cancellationToken,
        IReadOnlyCollection<long>? requestedTrackIds = null)
    {
        var normalizedService = (service ?? string.Empty).Trim().ToLowerInvariant();
        var deleted = 0;
        try
        {
            if (resetFirst)
            {
                deleted = await _libraryRepository.DeleteMediaServerTrackMetadataForScopeAsync(
                    normalizedService,
                    folderId,
                    cancellationToken);
                var resetCoverage = await _libraryRepository.GetTargetServerIdentityCoverageAsync(
                    [normalizedService],
                    folderId,
                    cancellationToken);
                StartTargetIdentityResetProgress(
                    normalizedService,
                    folderId,
                    resetCoverage.FirstOrDefault()
                    ?? new TargetServerIdentityCoverageDto(normalizedService, 0, 0, 0));
                await RebuildTrackMetadataIndexAsync(normalizedService, folderId, cancellationToken);
            }
            else
            {
                await UpdateTrackMetadataIndexAsync(
                    normalizedService,
                    folderId,
                    requestedTrackIds,
                    cancellationToken);
            }

            var coverage = await _libraryRepository.GetTargetServerIdentityCoverageAsync(
                [normalizedService],
                folderId,
                cancellationToken);
            return new TargetIdentityFetchResult(
                normalizedService,
                Success: true,
                deleted,
                coverage.FirstOrDefault(),
                Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return new TargetIdentityFetchResult(
                normalizedService,
                Success: false,
                deleted,
                Coverage: null,
                ex.Message);
        }
    }

    public async Task<bool> RequestLibraryRefreshAsync(
        string service,
        CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        return service.Trim().ToLowerInvariant() switch
        {
            PlexService => await RefreshPlexAsync(state.Plex, updateTrackIndex: false, cancellationToken: cancellationToken),
            JellyfinService => await RefreshJellyfinAsync(state.Jellyfin, updateTrackIndex: false, cancellationToken: cancellationToken),
            NavidromeService => await RefreshNavidromeAsync(state.Navidrome, updateTrackIndex: false, cancellationToken),
            _ => false
        };
    }

    public async Task<bool> IsLibraryScanRunningAsync(string service, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var running = service.Trim().ToLowerInvariant() switch
        {
            PlexService when HasPlexConfiguration(state.Plex)
                => await _plexApiClient.IsMusicLibraryRefreshingAsync(state.Plex!.Url!, state.Plex.Token!, cancellationToken),
            JellyfinService when HasJellyfinConfiguration(state.Jellyfin)
                => await _jellyfinApiClient.IsLibraryRefreshRunningAsync(state.Jellyfin!.Url!, state.Jellyfin.ApiKey!, cancellationToken),
            NavidromeService when HasNavidromeConfiguration(state.Navidrome)
                => await _navidromeApiClient.IsScanRunningAsync(
                    state.Navidrome!.Url!,
                    state.Navidrome.Username!,
                    state.Navidrome.Password!,
                    cancellationToken),
            _ => (bool?)null
        };

        return running != false;
    }

    public async Task<MediaServerIdentityIngestSummary> IngestConfiguredTargetIdentitiesAsync(
        CancellationToken cancellationToken)
    {
        var services = await GetConfiguredServicesAsync();
        var ingestedServers = 0;
        var failures = new List<string>();
        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await UpdateTrackMetadataIndexAsync(service, cancellationToken);
                ingestedServers++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Target identity ingest failed independently for {Service}.", service);
                failures.Add(service);
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Target identity ingest completed: configured={Configured} ingested={Ingested} failed={Failed}.",
                services.Count,
                ingestedServers,
                failures.Count);
        }

        return new MediaServerIdentityIngestSummary(services.Count, ingestedServers, failures);
    }

    public Task UpdateTrackMetadataIndexAsync(string service, CancellationToken cancellationToken)
        => UpdateTrackMetadataIndexAsync(service, folderId: null, cancellationToken);

    public async Task UpdateTrackMetadataIndexAsync(string service, long? folderId, CancellationToken cancellationToken)
        => await UpdateTrackMetadataIndexAsync(service, folderId, requestedTrackIds: null, cancellationToken);

    public async Task UpdateTrackMetadataIndexAsync(
        string service,
        long? folderId,
        IReadOnlyCollection<long>? requestedTrackIds,
        CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var normalizedService = service.Trim().ToLowerInvariant();
        switch (normalizedService)
        {
            case PlexService when HasPlexConfiguration(state.Plex):
                var sections = await GetPlexLibrarySectionsWithRetryAsync(state.Plex!, cancellationToken);
                var musicSections = sections
                    .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
                    .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                await UpdatePlexTrackMetadataIndexAsync(
                    state.Plex!,
                    musicSections,
                    folderId,
                    requestedTrackIds,
                    cancellationToken);
                break;
            case JellyfinService when HasJellyfinConfiguration(state.Jellyfin):
                await UpdateJellyfinTrackMetadataIndexAsync(
                    state.Jellyfin!,
                    folderId,
                    requestedTrackIds,
                    cancellationToken);
                break;
            case NavidromeService when HasNavidromeConfiguration(state.Navidrome):
                await UpdateNavidromeTrackMetadataIndexAsync(
                    state.Navidrome!,
                    folderId,
                    requestedTrackIds,
                    cancellationToken);
                break;
        }
    }

    public async Task RebuildTrackMetadataIndexAsync(string service, long? folderId, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var normalizedService = service.Trim().ToLowerInvariant();
        switch (normalizedService)
        {
            case PlexService when HasPlexConfiguration(state.Plex):
                var sections = await GetPlexLibrarySectionsWithRetryAsync(state.Plex!, cancellationToken);
                var musicSections = sections
                    .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
                    .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                await UpdatePlexTrackMetadataIndexAsync(
                    state.Plex!,
                    musicSections,
                    folderId,
                    requestedTrackIds: null,
                    cancellationToken);
                break;
            case JellyfinService when HasJellyfinConfiguration(state.Jellyfin):
                await UpdateJellyfinTrackMetadataIndexAsync(
                    state.Jellyfin!,
                    folderId,
                    requestedTrackIds: null,
                    cancellationToken);
                break;
            case NavidromeService when HasNavidromeConfiguration(state.Navidrome):
                await UpdateNavidromeTrackMetadataIndexAsync(
                    state.Navidrome!,
                    folderId,
                    requestedTrackIds: null,
                    cancellationToken);
                break;
            default:
                return;
        }

    }
    private Task<bool> RefreshPlexAsync(PlexAuth? plex, CancellationToken cancellationToken)
        => RefreshPlexAsync(plex, updateTrackIndex: true, cancellationToken: cancellationToken);

    private async Task<bool> RefreshPlexAsync(
        PlexAuth? plex,
        bool updateTrackIndex,
        CancellationToken cancellationToken)
    {
        if (!HasPlexConfiguration(plex))
        {
            return false;
        }

        var configuredPlex = plex!;
        var sections = await GetPlexLibrarySectionsWithRetryAsync(configuredPlex, cancellationToken);
        var musicSections = sections
            .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
            .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (musicSections.Count == 0)
        {
            _logger.LogWarning("Plex library refresh skipped because no music library sections were found.");
            return false;
        }

        var allSectionsRefreshed = true;
        foreach (var section in musicSections)
        {
            var refreshed = await RetryRefreshAsync(
                () => _plexApiClient.RefreshLibraryAsync(
                    configuredPlex.Url!,
                    configuredPlex.Token!,
                    section.Key,
                    cancellationToken),
                PlexService,
                section.Key,
                cancellationToken);
            if (!refreshed)
            {
                allSectionsRefreshed = false;
                _logger.LogWarning(
                    "Plex library refresh request failed for section {SectionKey} ({SectionTitle}).",
                    section.Key,
                    section.Title);
            }
        }

        try
        {
            if (updateTrackIndex)
            {
                await UpdatePlexTrackMetadataIndexAsync(
                    configuredPlex,
                    musicSections,
                    folderId: null,
                    requestedTrackIds: null,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Plex library refresh was requested, but the local Plex track metadata index could not be updated.");
        }

        return allSectionsRefreshed;
    }

    private async Task<List<PlexLibrarySection>> GetPlexLibrarySectionsWithRetryAsync(
        PlexAuth plex,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RefreshAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sections = await _plexApiClient.GetLibrarySectionsAsync(
                plex.Url!,
                plex.Token!,
                cancellationToken);
            if (sections.Count > 0)
            {
                return sections;
            }

            if (attempt < RefreshAttemptCount)
            {
                _logger.LogWarning(
                    "Plex library section discovery returned no sections on attempt {Attempt}/{AttemptCount}; retrying.",
                    attempt,
                    RefreshAttemptCount);
                await Task.Delay(RefreshRetryDelay, cancellationToken);
            }
        }

        return [];
    }

    private async Task UpdatePlexTrackMetadataIndexAsync(
        PlexAuth plex,
        List<PlexLibrarySection> musicSections,
        long? folderId,
        IReadOnlyCollection<long>? requestedTrackIds,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured || musicSections.Count == 0)
        {
            return;
        }

        var localIndex = await LoadTargetIdentityLocalIndexAsync(
            PlexService,
            folderId,
            requestedTrackIds,
            cancellationToken);
        if (localIndex.Tracks.Count == 0 || localIndex.MissingTrackIds.Count == 0)
        {
            return;
        }

        StartTargetIdentityProgress(PlexService, folderId, localIndex);
        try
        {
            var mappedCount = 0;
            var seenRatingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in musicSections)
            {
                var offset = 0;
                while (!cancellationToken.IsCancellationRequested && localIndex.MissingTrackIds.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await _plexApiClient.GetLibraryTracksAsync(
                        plex.Url!,
                        plex.Token!,
                        section.Key,
                        offset,
                        PlexTrackPageSize,
                        cancellationToken);
                    if (page.Count == 0)
                    {
                        if (offset == 0)
                        {
                            _logger.LogWarning(
                                "Plex identity ingest found no tracks in section {SectionKey} ({SectionTitle}).",
                                section.Key,
                                section.Title);
                        }

                        break;
                    }

                    var tracks = page
                        .Where(track => !string.IsNullOrWhiteSpace(track.RatingKey)
                                        && !string.IsNullOrWhiteSpace(track.FilePath)
                                        && seenRatingKeys.Add(track.RatingKey))
                        .Select(static track => new TargetTrackIdentityCandidate(
                            track.RatingKey,
                            track.FilePath,
                            track.Title,
                            track.Artist,
                            track.Album,
                            track.DurationMs > 0 ? checked((int)Math.Min(track.DurationMs, int.MaxValue)) : null,
                            track.SizeBytes))
                        .ToList();
                    var ingest = await IngestTargetTracksAsync(PlexService, folderId, tracks, localIndex, cancellationToken);
                    mappedCount += ingest.Mapped;

                    if (page.Count < PlexTrackPageSize)
                    {
                        break;
                    }

                    offset += PlexTrackPageSize;
                }

                if (localIndex.MissingTrackIds.Count == 0)
                {
                    break;
                }
            }

            LogIdentityIngest(PlexService, mappedCount, localIndex.MissingTrackIds.Count);
        }
        finally
        {
            CompleteTargetIdentityProgress(PlexService, folderId, localIndex);
        }
    }

    private Task<bool> RefreshJellyfinAsync(JellyfinAuth? jellyfin, CancellationToken cancellationToken)
        => RefreshJellyfinAsync(jellyfin, updateTrackIndex: true, cancellationToken: cancellationToken);

    private async Task<bool> RefreshJellyfinAsync(
        JellyfinAuth? jellyfin,
        bool updateTrackIndex,
        CancellationToken cancellationToken)
    {
        if (!HasJellyfinConfiguration(jellyfin))
        {
            return false;
        }

        var refreshed = await RetryRefreshAsync(
            () => _jellyfinApiClient.RefreshLibraryAsync(
                jellyfin!.Url!,
                jellyfin.ApiKey!,
                cancellationToken),
            JellyfinService,
            sectionKey: null,
            cancellationToken);
        if (!refreshed)
        {
            _logger.LogWarning("Jellyfin library refresh request failed.");
            return false;
        }

        try
        {
            if (updateTrackIndex)
            {
                await UpdateJellyfinTrackMetadataIndexAsync(
                    jellyfin!,
                    folderId: null,
                    requestedTrackIds: null,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Jellyfin library refresh was requested, but the local Jellyfin track metadata index could not be updated.");
        }

        return refreshed;
    }

    private async Task UpdateJellyfinTrackMetadataIndexAsync(
        JellyfinAuth jellyfin,
        long? folderId,
        IReadOnlyCollection<long>? requestedTrackIds,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return;
        }

        var localIndex = await LoadTargetIdentityLocalIndexAsync(
            JellyfinService,
            folderId,
            requestedTrackIds,
            cancellationToken);
        if (localIndex.Tracks.Count == 0 || localIndex.MissingTrackIds.Count == 0)
        {
            return;
        }

        var userId = jellyfin.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            var currentUser = await _jellyfinApiClient.GetCurrentUserAsync(
                jellyfin.Url!,
                jellyfin.ApiKey!,
                cancellationToken);
            userId = currentUser?.Id;
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("Jellyfin identity ingest skipped because Jellyfin user id is missing.");
            return;
        }

        var libraries = await _jellyfinApiClient.GetLibrariesAsync(
            jellyfin.Url!,
            jellyfin.ApiKey!,
            cancellationToken);
        var musicLibraries = libraries
            .Where(IsJellyfinMusicLibrary)
            .Select(library => (Id: library.LibraryId, library.Name))
            .Where(library => !string.IsNullOrWhiteSpace(library.Id))
            .ToList();
        if (musicLibraries.Count == 0)
        {
            _logger.LogWarning(
                "Jellyfin identity ingest found no music libraries; paging all audio items instead.");
            musicLibraries.Add((Id: null, Name: "all-audio"));
        }

        StartTargetIdentityProgress(JellyfinService, folderId, localIndex);
        try
        {
            var mappedCount = 0;
            var seenItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in musicLibraries)
            {
                var offset = 0;
                while (!cancellationToken.IsCancellationRequested && localIndex.MissingTrackIds.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await _jellyfinApiClient.GetAudioTracksAsync(
                        jellyfin.Url!,
                        jellyfin.ApiKey!,
                        userId,
                        library.Id,
                        offset,
                        PlexTrackPageSize,
                        cancellationToken);
                    if (page.Count == 0)
                    {
                        if (offset == 0)
                        {
                            _logger.LogWarning(
                                "Jellyfin identity ingest found no tracks in library {LibraryId} ({LibraryName}).",
                                library.Id,
                                library.Name);
                        }

                        break;
                    }

                    var tracks = page
                        .Where(track => !string.IsNullOrWhiteSpace(track.Id)
                                        && !string.IsNullOrWhiteSpace(track.FilePath)
                                        && seenItemIds.Add(track.Id))
                        .Select(static track => new TargetTrackIdentityCandidate(
                            track.Id,
                            track.FilePath!,
                            track.Name,
                            track.Artist,
                            track.Album,
                            track.DurationMs,
                            SizeBytes: null))
                        .ToList();
                    var ingest = await IngestTargetTracksAsync(JellyfinService, folderId, tracks, localIndex, cancellationToken);
                    mappedCount += ingest.Mapped;

                    if (page.Count < PlexTrackPageSize)
                    {
                        break;
                    }

                    offset += PlexTrackPageSize;
                }

                if (localIndex.MissingTrackIds.Count == 0)
                {
                    break;
                }
            }

            LogIdentityIngest(JellyfinService, mappedCount, localIndex.MissingTrackIds.Count);
        }
        finally
        {
            CompleteTargetIdentityProgress(JellyfinService, folderId, localIndex);
        }
    }

    private async Task UpdateNavidromeTrackMetadataIndexAsync(
        NavidromeAuth navidrome,
        long? folderId,
        IReadOnlyCollection<long>? requestedTrackIds,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return;
        }

        var localIndex = await LoadTargetIdentityLocalIndexAsync(
            NavidromeService,
            folderId,
            requestedTrackIds,
            cancellationToken);
        if (localIndex.Tracks.Count == 0 || localIndex.MissingTrackIds.Count == 0)
        {
            return;
        }

        var sessionToken = await _navidromeApiClient.LoginNativeApiAsync(
            navidrome.Url!,
            navidrome.Username!,
            navidrome.Password!,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            _logger.LogWarning("Navidrome identity ingest skipped because native API login failed.");
            return;
        }

        StartTargetIdentityProgress(NavidromeService, folderId, localIndex);
        try
        {
            var mappedCount = 0;
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            while (!cancellationToken.IsCancellationRequested && localIndex.MissingTrackIds.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _navidromeApiClient.GetLibraryTracksAsync(
                    navidrome.Url!,
                    sessionToken,
                    offset,
                    PlexTrackPageSize,
                    cancellationToken);
                if (page.Count == 0)
                {
                    if (offset == 0)
                    {
                        _logger.LogWarning(
                            "Navidrome identity ingest found no songs. Native /api/song may be unavailable or the library is empty.");
                    }

                    break;
                }

                var tracks = page
                    .Where(track => !string.IsNullOrWhiteSpace(track.Id)
                                    && !string.IsNullOrWhiteSpace(track.FilePath)
                                    && seenIds.Add(track.Id))
                    .Select(static track => new TargetTrackIdentityCandidate(
                        track.Id,
                        track.FilePath!,
                        track.Title,
                        track.Artist,
                        Album: null,
                        track.DurationMs,
                        track.SizeBytes))
                    .ToList();
                var ingest = await IngestTargetTracksAsync(NavidromeService, folderId, tracks, localIndex, cancellationToken);
                mappedCount += ingest.Mapped;

                if (page.Count < PlexTrackPageSize)
                {
                    break;
                }

                offset += PlexTrackPageSize;
            }

            LogIdentityIngest(NavidromeService, mappedCount, localIndex.MissingTrackIds.Count);
        }
        finally
        {
            CompleteTargetIdentityProgress(NavidromeService, folderId, localIndex);
        }
    }

    private async Task<(int Mapped, int Unmapped, int Ambiguous)> IngestTargetTracksAsync(
        string service,
        long? folderId,
        IReadOnlyList<TargetTrackIdentityCandidate> tracks,
        TargetIdentityLocalIndex localIndex,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return (0, 0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var mappedTracks = new List<(TargetTrackIdentityCandidate Track, long LocalTrackId)>();
        var ambiguous = new List<(string TargetItemId, string FilePath, IReadOnlyList<long> TiedTrackIds)>();
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (localIndex.MissingTrackIds.Count == 0)
            {
                break;
            }

            var resolution = localIndex.ResolveCandidate(track);
            if (resolution.TrackId is > 0
                && localIndex.MissingTrackIds.Contains(resolution.TrackId.Value))
            {
                mappedTracks.Add((track, resolution.TrackId.Value));
                localIndex.MissingTrackIds.Remove(resolution.TrackId.Value);
                ReportTargetIdentityProgress(service, folderId, localIndex);

                // Learn the server-root -> local-root transform from this bind so later
                // items from the same library resolve deterministically.
                if (!string.IsNullOrWhiteSpace(resolution.DerivedServerRoot)
                    && !string.IsNullOrWhiteSpace(resolution.DerivedLocalRoot)
                    && !localIndex.PathMappings.ContainsKey(resolution.DerivedServerRoot))
                {
                    await _libraryRepository.UpsertMediaServerPathMappingAsync(
                        service,
                        resolution.DerivedServerRoot,
                        resolution.DerivedLocalRoot,
                        CancellationToken.None);
                }
            }
            else if (resolution.Ambiguous)
            {
                ambiguous.Add((track.TargetItemId, track.FilePath, resolution.TiedTrackIds));
            }
        }

        if (ambiguous.Count > 0)
        {
            _logger.LogWarning(
                "{Service} identity ingest left {AmbiguousCount} server item(s) ambiguous between multiple local tracks; they were not bound.",
                service,
                ambiguous.Count);
        }

        await PersistTargetIdentityMappingsAsync(service, mappedTracks, now, cancellationToken);
        return (mappedTracks.Count, tracks.Count - mappedTracks.Count, ambiguous.Count);
    }

    private void LogIdentityIngest(string service, int mappedCount, int unmappedCount)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{Service} track identity ingest updated: mappedTracks={MappedTracks} unmappedTracks={UnmappedTracks}.",
                service,
                mappedCount,
                unmappedCount);
        }
    }

    private async Task PersistTargetIdentityMappingsAsync(
        string service,
        IReadOnlyList<(TargetTrackIdentityCandidate Track, long LocalTrackId)> mappedTracks,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (mappedTracks.Count == 0)
        {
            return;
        }

        await _libraryRepository.UpsertMediaServerTrackMetadataAsync(
            mappedTracks
                .Select(track => new MediaServerTrackMetadataUpsertDto(
                    track.LocalTrackId,
                    service,
                    track.Track.TargetItemId,
                    track.Track.FilePath,
                    now))
                .ToList(),
            cancellationToken);
    }

    private static bool IsJellyfinMusicLibrary(JellyfinLibrarySection library)
    {
        if (string.IsNullOrWhiteSpace(library.CollectionType))
        {
            return false;
        }

        return string.Equals(library.CollectionType, "music", StringComparison.OrdinalIgnoreCase)
               || string.Equals(library.CollectionType, "audio", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TargetIdentityLocalIndex> LoadTargetIdentityLocalIndexAsync(
        string service,
        long? folderId,
        IReadOnlyCollection<long>? requestedTrackIds,
        CancellationToken cancellationToken)
    {
        await _libraryRepository.DeleteOrphanedMediaServerTrackMetadataAsync(service, cancellationToken);
        var mappings = await _libraryRepository.GetMediaServerPathMappingsAsync(service, cancellationToken);
        var mappingLookup = mappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ServerRoot))
            .ToDictionary(
                mapping => mapping.ServerRoot,
                mapping => mapping.LocalRoot,
                StringComparer.OrdinalIgnoreCase);
        return TargetIdentityLocalIndex.Build(
            await _libraryRepository.GetTargetServerIdentityLocalTracksAsync(service, folderId, cancellationToken),
            requestedTrackIds,
            mappingLookup);
    }

    private async Task<bool> RetryRefreshAsync(
        Func<Task<bool>> refresh,
        string service,
        string? sectionKey,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RefreshAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await refresh())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "{Service} library refresh attempt {Attempt}/{AttemptCount} failed for section {SectionKey}.",
                    service,
                    attempt,
                    RefreshAttemptCount,
                    sectionKey ?? "all");
            }

            if (attempt < RefreshAttemptCount)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(16, RefreshRetryDelay.TotalSeconds * (1 << (attempt - 1))));
                await Task.Delay(delay, cancellationToken);
            }
        }

        return false;
    }

    private Task<bool> RefreshNavidromeAsync(NavidromeAuth? navidrome, CancellationToken cancellationToken)
        => RefreshNavidromeAsync(navidrome, updateTrackIndex: true, cancellationToken);

    private async Task<bool> RefreshNavidromeAsync(
        NavidromeAuth? navidrome,
        bool updateTrackIndex,
        CancellationToken cancellationToken)
    {
        if (!HasNavidromeConfiguration(navidrome))
        {
            return false;
        }

        var refreshed = await RetryRefreshAsync(
            () => _navidromeApiClient.StartScanAsync(
                navidrome!.Url!,
                navidrome.Username!,
                navidrome.Password!,
                cancellationToken),
            NavidromeService,
            sectionKey: null,
            cancellationToken);
        if (!refreshed)
        {
            _logger.LogWarning("Navidrome library refresh request failed.");
            return false;
        }

        try
        {
            if (updateTrackIndex)
            {
                await UpdateNavidromeTrackMetadataIndexAsync(
                    navidrome!,
                    folderId: null,
                    requestedTrackIds: null,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Navidrome library refresh was requested, but the local Navidrome track identity index could not be updated.");
        }

        return refreshed;
    }

    private static bool HasPlexConfiguration(PlexAuth? plex) =>
        plex != null
        && !string.IsNullOrWhiteSpace(plex.Url)
        && !string.IsNullOrWhiteSpace(plex.Token);

    private static bool HasJellyfinConfiguration(JellyfinAuth? jellyfin) =>
        jellyfin != null
        && !string.IsNullOrWhiteSpace(jellyfin.Url)
        && !string.IsNullOrWhiteSpace(jellyfin.ApiKey);

    private static bool HasNavidromeConfiguration(NavidromeAuth? navidrome) =>
        navidrome != null
        && !string.IsNullOrWhiteSpace(navidrome.Url)
        && !string.IsNullOrWhiteSpace(navidrome.Username)
        && !string.IsNullOrWhiteSpace(navidrome.Password);
}

public sealed record MediaServerRefreshSummary(
    int ConfiguredServerCount,
    int RefreshedServerCount,
    IReadOnlyList<string> FailedServers)
{
    public bool IsComplete => ConfiguredServerCount == RefreshedServerCount;
}

public sealed record MediaServerIdentityIngestSummary(
    int ConfiguredServerCount,
    int IngestedServerCount,
    IReadOnlyList<string> FailedServers)
{
    public bool IsComplete => ConfiguredServerCount == IngestedServerCount;
}
