using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Matching;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

/// <summary>A source track, in the shape a streaming platform can be searched by.</summary>
public sealed record PlatformIdentityTrack(
    long LocalTrackId,
    string? Isrc,
    string Name,
    string Artists,
    string Album,
    int? DurationMs);

/// <summary>
/// Turns a local track into the catalog id a streaming platform knows it by.
/// <para>
/// This is the missing half of platform sync. A playlist destination can only be written to when
/// every track has an id ON THAT PLATFORM, and the app's identity store is otherwise only ever
/// written by a media-server library refresh - which never runs for a streaming account. Without
/// this, every platform target reports that no track has an identity and nothing is written.
/// </para>
/// <para>
/// Ported from the reference's per-target <c>resolve()</c>, with one important difference in where
/// the work happens: the reference resolves inside its own target object with a private cache file,
/// while this resolves through the resolution the app ALREADY has for each service and records the
/// answer in the app's existing identity table. Both platforms and credentials are therefore the
/// ones the rest of the app uses, and the next pass reads the answer from the database instead of
/// searching again.
/// </para>
/// <para>
/// The order is the reference's and matters: an ISRC query first, because it is a hard
/// cross-provider identifier, then name-and-artist queries scored by the app's existing
/// <see cref="TrackCandidateValidator"/>. Nothing is recorded from a query alone - a search result
/// that does not validate is discarded, because a wrong identity here silently writes the wrong
/// track to the user's playlist.
/// </para>
/// </summary>
public sealed class PlatformTrackIdentityResolver
{
    /// <summary>
    ///     The platform ids, aliased to the one canonical definition.
    /// </summary>
    /// <remarks>
    ///     spotify, deezer, qobuz and tidal were declared here as a second byte-identical copy of
    ///     <see cref="DownloadTagSourceHelper" />, which is the vocabulary stored source ids and
    ///     download-tag settings are normalised against. This resolver compares those stored ids,
    ///     so a copy that drifted from the one it is compared to would stop matching and no
    ///     candidate would ever be written. The shorter names are kept because they read better
    ///     here; the value is defined once.
    ///     <para>
    ///         Apple Music is deliberately not aliased: its platform id is "applemusic", which is a
    ///         different value from the "apple" download source and is not interchangeable with it.
    ///     </para>
    /// </remarks>
    public const string SpotifyService = DownloadTagSourceHelper.SpotifySource;

    public const string DeezerService = DownloadTagSourceHelper.DeezerSource;

    public const string QobuzService = DownloadTagSourceHelper.QobuzSource;

    public const string AppleMusicService = "applemusic";

    public const string TidalService = DownloadTagSourceHelper.TidalSource;

    /// <summary>
    /// A search is allowed this many candidates. The scoring rejects almost all of them, so a wider
    /// page costs latency and rate limit for nothing; the reference uses the same bound.
    /// </summary>
    private const int SearchLimit = 8;

    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyPathfinderMetadataClient? _spotify;
    private readonly DeezerClient? _deezer;
    private readonly QobuzTrackResolver? _qobuz;
    private readonly AppleMusicCatalogService? _apple;
    private readonly ILogger<PlatformTrackIdentityResolver>? _logger;

    public PlatformTrackIdentityResolver(
        LibraryRepository libraryRepository,
        SpotifyPathfinderMetadataClient? spotify = null,
        DeezerClient? deezer = null,
        QobuzTrackResolver? qobuz = null,
        AppleMusicCatalogService? apple = null,
        ILogger<PlatformTrackIdentityResolver>? logger = null)
    {
        _libraryRepository = libraryRepository;
        _spotify = spotify;
        _deezer = deezer;
        _qobuz = qobuz;
        _apple = apple;
        _logger = logger;
    }

    /// <summary>Whether this resolver can search the given service at all.</summary>
    public bool Supports(string? targetService) => (targetService ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        SpotifyService => _spotify is not null,
        DeezerService => _deezer is not null,
        QobuzService => _qobuz is not null,
        AppleMusicService => _apple is not null,
        _ => false,
    };

    /// <summary>
    /// The platform id for each track that has one, searching only for the ones that do not.
    /// <para>
    /// An already-recorded identity is returned untouched and is never re-searched. A platform
    /// catalog id does not change, so re-deriving it on every pass would spend a search per track
    /// per destination to arrive at the same answer.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<long, string>> ResolveAsync(
        string targetService,
        IReadOnlyList<PlatformIdentityTrack> tracks,
        CancellationToken cancellationToken)
    {
        var service = (targetService ?? string.Empty).Trim().ToLowerInvariant();
        var resolved = new Dictionary<long, string>();
        if (tracks.Count == 0)
        {
            return resolved;
        }

        var localIds = tracks
            .Where(static track => track.LocalTrackId > 0)
            .Select(static track => track.LocalTrackId)
            .Distinct()
            .ToList();
        if (localIds.Count == 0)
        {
            return resolved;
        }

        var recorded = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
            service,
            localIds,
            cancellationToken).ConfigureAwait(false);

        var pending = new List<PlatformIdentityTrack>();
        foreach (var track in tracks)
        {
            if (track.LocalTrackId <= 0)
            {
                continue;
            }

            if (recorded.TryGetValue(track.LocalTrackId, out var existing) && !string.IsNullOrWhiteSpace(existing))
            {
                resolved[track.LocalTrackId] = existing;
                continue;
            }

            // Two entries for the same local track would otherwise search twice and could record
            // different ids; the first one to resolve wins, as it does in the database read.
            if (pending.Any(other => other.LocalTrackId == track.LocalTrackId))
            {
                continue;
            }

            pending.Add(track);
        }

        if (pending.Count == 0)
        {
            return resolved;
        }

        if (!Supports(service))
        {
            _logger?.LogInformation(
                "No identity search is wired for {Service}; {Count} track(s) stay unresolved there.",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(service),
                pending.Count);
            return resolved;
        }

        var discovered = new List<MediaServerTrackMetadataUpsertDto>();
        foreach (var track in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var itemId = await SearchAsync(service, track, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            resolved[track.LocalTrackId] = itemId!;
            discovered.Add(new MediaServerTrackMetadataUpsertDto(
                track.LocalTrackId,
                service,
                itemId!,
                string.Empty,
                DateTimeOffset.UtcNow));
        }

        if (discovered.Count > 0)
        {
            await _libraryRepository.UpsertMediaServerTrackMetadataAsync(discovered, cancellationToken)
                .ConfigureAwait(false);
        }

        return resolved;
    }

    private async Task<string?> SearchAsync(
        string service,
        PlatformIdentityTrack track,
        CancellationToken cancellationToken)
    {
        try
        {
            return service switch
            {
                SpotifyService => await ResolveSpotifyAsync(track, cancellationToken).ConfigureAwait(false),
                DeezerService => await ResolveDeezerAsync(track, cancellationToken).ConfigureAwait(false),
                QobuzService => await ResolveQobuzAsync(track, cancellationToken).ConfigureAwait(false),
                AppleMusicService => await ResolveAppleAsync(track, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // One track's search failing is that track's problem, not the pass's. Returning null
            // leaves it unresolved and the next pass retries it.
            _logger?.LogWarning(ex, "Searching {Service} for {Track} failed.",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(service),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Name));
            return null;
        }
    }

    private async Task<string?> ResolveSpotifyAsync(PlatformIdentityTrack track, CancellationToken cancellationToken)
    {
        if (_spotify is null)
        {
            return null;
        }

        var source = ToMatchSource(track);

        // The ISRC query is the hard cross-provider identifier; a validated hit is not a guess.
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            var byIsrc = await _spotify.SearchTracksAsync($"isrc:{track.Isrc}", SearchLimit, cancellationToken)
                .ConfigureAwait(false);
            var exact = Select(byIsrc, source);
            if (exact is not null)
            {
                return exact;
            }
        }

        var queries = new List<string>();
        var byField = $"track:{track.Name} artist:{track.Artists}".Trim();
        if (byField.Length > 0)
        {
            queries.Add(byField);
        }

        var combined = $"{track.Name} {track.Artists}".Trim();
        if (combined.Length > 0 && !queries.Contains(combined, StringComparer.Ordinal))
        {
            queries.Add(combined);
        }

        foreach (var query in queries)
        {
            var results = await _spotify.SearchTracksAsync(query, SearchLimit, cancellationToken).ConfigureAwait(false);
            var match = Select(results, source);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private async Task<string?> ResolveDeezerAsync(PlatformIdentityTrack track, CancellationToken cancellationToken)
    {
        if (_deezer is null)
        {
            return null;
        }

        var source = ToMatchSource(track);

        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            var byIsrc = await _deezer.GetTrackByIsrcAsync(track.Isrc).ConfigureAwait(false);
            if (byIsrc is not null && Accepts(source, byIsrc))
            {
                return byIsrc.Id;
            }
        }

        var byMetadata = await _deezer
            .GetTrackIdFromMetadataAsync(track.Artists, track.Name, track.Album, track.DurationMs)
            .ConfigureAwait(false);

        // A metadata search returns one id with no metadata to check it against, so it is only
        // trusted when the caller supplied enough to search with at all.
        return string.IsNullOrWhiteSpace(byMetadata) || byMetadata == "0"
            ? null
            : byMetadata;
    }

    private async Task<string?> ResolveQobuzAsync(PlatformIdentityTrack track, CancellationToken cancellationToken)
    {
        // The resolver already applies the ISRC-then-metadata order and its own scoring, so its
        // answer is the validated one rather than a raw search hit.
        var resolution = await _qobuz!.ResolveTrackAsync(
            track.Isrc,
            track.Name,
            track.Artists,
            track.Album,
            track.DurationMs,
            cancellationToken).ConfigureAwait(false);

        return resolution is null || resolution.Track.Id <= 0
            ? null
            : resolution.Track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string?> ResolveAppleAsync(PlatformIdentityTrack track, CancellationToken cancellationToken)
    {
        if (_apple is null || string.IsNullOrWhiteSpace(track.Isrc))
        {
            // Only the ISRC lookup is used. Apple's catalog search is storefront- and
            // language-scoped, and guessing either one returns a confidently wrong answer rather
            // than no answer, so the resolver reports nothing instead of guessing.
            return null;
        }

        // The account's own storefront when the session can report one, otherwise the catalog default.
// Passing no token is deliberate: the account endpoint answers for an unknown token only by
// costing a 2FA prompt, and the default storefront resolves the same catalog id for the
// overwhelming majority of accounts.
        var storefront = await _apple.ResolveStorefrontAsync(null, null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(storefront))
        {
            return null;
        }

        using var payload = await _apple
            .GetSongByIsrcAsync(track.Isrc, storefront!, "en-US", cancellationToken)
            .ConfigureAwait(false);

        var song = ReadAppleSongId(payload.RootElement);
        if (song is null)
        {
            return null;
        }

        var accepted = Accepts(
            ToMatchSource(track),
            song.Value.Id,
            song.Value.Name,
            song.Value.Artist,
            song.Value.Album,
            song.Value.DurationMs,
            song.Value.Isrc);
        return accepted ? song.Value.Id : null;
    }

    /// <summary>
    /// The first candidate the app's own validator accepts, or null.
    /// <para>
    /// The highest-scoring candidate is taken rather than the first acceptable one, because a
    /// search page is unordered and the first row is frequently a remix or a live cut that happens
    /// to clear the artist check.
    /// </para>
    /// </summary>
    private static string? Select(
        IReadOnlyList<SpotifyTrackSummary> candidates,
        TrackMatchSource source)
    {
        string? bestId = null;
        var bestScore = double.MinValue;
        foreach (var candidate in candidates)
        {
            var result = TrackCandidateValidator.Validate(source, ToMatchCandidate(candidate));
            if (result.Accepted && result.Score > bestScore)
            {
                bestId = candidate.Id;
                bestScore = result.Score;
            }
        }

        return bestId;
    }

    private static bool Accepts(TrackMatchSource source, ApiTrack candidate)
        => TrackCandidateValidator.Validate(
            source,
            new TrackMatchCandidate(
                candidate.Id,
                candidate.Isrc,
                candidate.Title,
                candidate.Artist?.Name,
                candidate.Album?.Title,
                candidate.Duration > 0 ? candidate.Duration * 1000 : null)).Accepted;

    private static bool Accepts(
        TrackMatchSource source,
        string id,
        string? title,
        string? artist,
        string? album,
        int? durationMs,
        string? isrc)
        => TrackCandidateValidator.Validate(
            source,
            new TrackMatchCandidate(id, isrc, title, artist, album, durationMs)).Accepted;

    private static TrackMatchSource ToMatchSource(PlatformIdentityTrack track)
        => new(track.Isrc, track.Name, track.Artists, track.Album, track.DurationMs);

    private static TrackMatchCandidate ToMatchCandidate(SpotifyTrackSummary candidate)
        => new(
            candidate.Id,
            candidate.Isrc,
            candidate.Name,
            candidate.Artists,
            candidate.Album,
            candidate.DurationMs);

    /// <summary>
    /// The catalog id of an Apple song row, or null when the lookup found nothing.
    /// <para>
    /// An ISRC that matches nothing comes back as an empty data array rather than an error, so this
    /// is the only place that distinguishes "matched" from "did not match". Returning a blank id
    /// instead of null would record an identity that addresses nothing.
    /// </para>
    /// </summary>
    public static (string Id, string? Name, string? Artist, string? Album, int? DurationMs, string? Isrc)? ReadAppleSongId(
        JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return null;
        }

        var song = data[0];
        if (!song.TryGetProperty("id", out var id) || string.IsNullOrWhiteSpace(id.ToString()))
        {
            return null;
        }

        var attributes = song.TryGetProperty("attributes", out var value) ? value : default;
        var name = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("name", out var nameValue)
            ? nameValue.ToString()
            : null;

        string? artist = null;
        if (attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty("artistName", out var artistValue))
        {
            artist = artistValue.ToString();
        }

        var album = attributes.ValueKind == JsonValueKind.Object
                    && attributes.TryGetProperty("albumName", out var albumValue)
            ? albumValue.ToString()
            : null;

        int? durationMs = null;
        if (attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty("durationInMillis", out var durationValue))
        {
            durationMs = durationValue.TryGetInt32(out var parsed) ? parsed : null;
        }

        var isrc = attributes.ValueKind == JsonValueKind.Object
                   && attributes.TryGetProperty("isrc", out var isrcValue)
            ? isrcValue.ToString()
            : null;

        return (id.ToString(), name, artist, album, durationMs, isrc);
    }
}
