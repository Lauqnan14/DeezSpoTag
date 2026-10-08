using DeezSpoTag.Services.Download.SoundCloud;
using DeezSpoTag.Services.Matching;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
///     Matches a local audio file to a SoundCloud track.
/// </summary>
/// <remarks>
///     <para>
///         Uses the shared <see cref="ISoundCloudClient" />, the same one the download engine and the central
///         identity resolver use, so there is exactly one implementation of SoundCloud HTTP, client-id
///         discovery and error handling.
///     </para>
///     <para>
///         Returns an <see cref="AutoTagMatchResult" /> and never touches a file. All writing, overwrite
///         policy and identity ownership stay with the existing AutoTag writer.
///     </para>
/// </remarks>
public sealed class SoundcloudMatcher
{
    private const int IsrcAuthority = 2;
    private const int SearchAuthority = 1;

    private static readonly string[] TrackIdTagKeys =
    {
        "SOUNDCLOUD_TRACK_ID",
        "SOUNDCLOUD_TRACKID",
        "SOUNDCLOUD_ID",
        "SOUNDCLOUDID"
    };

    private static readonly string[] TrackUrlTagKeys =
    {
        "SOUNDCLOUD_URL",
        "SOUNDCLOUD_URI",
        "SOURCEURL",
        "SOURCE_URL",
        "URL"
    };

    private readonly ISoundCloudClient _client;
    private readonly ILogger<SoundcloudMatcher> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoundcloudMatcher" /> class.</summary>
    public SoundcloudMatcher(ISoundCloudClient client, ILogger<SoundcloudMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    ///     Finds the best SoundCloud match for a local file.
    /// </summary>
    /// <param name="info">The local file's tags.</param>
    /// <param name="matchingConfig">Shared matching strictness.</param>
    /// <param name="config">SoundCloud-specific tunables.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The match, or <see langword="null" /> when nothing acceptable was found.</returns>
    public async Task<AutoTagMatchResult?> MatchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        SoundcloudMatchConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(matchingConfig);

        var resolvedConfig = NormalizeConfig(config);

        if (resolvedConfig.MatchById)
        {
            var byIdentity = await TryMatchByIdentityAsync(info, cancellationToken).ConfigureAwait(false);
            if (byIdentity is not null)
            {
                _logger.LogDebug("soundcloud: autotag match strategy=id");
                return byIdentity;
            }

            // A recognised SoundCloud identity that will not resolve is treated as authoritative and
            // unresolved: the file declares which track it is. Searching on title instead would tag it from an
            // unrelated upload, so the run reports no match rather than guessing.
            if (HasEmbeddedIdentity(info))
            {
                _logger.LogDebug(
                    "soundcloud: autotag match strategy=id_unresolved; not falling back to search");
                return null;
            }
        }

        return await TryMatchBySearchAsync(info, matchingConfig, resolvedConfig, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AutoTagMatchResult?> TryMatchByIdentityAsync(
        AutoTagAudioInfo info,
        CancellationToken cancellationToken)
    {
        var seededUrl = TryResolveSoundCloudUrl(info);
        var seededId = TryResolveSoundCloudId(info);

        if (string.IsNullOrWhiteSpace(seededUrl) && string.IsNullOrWhiteSpace(seededId))
        {
            return null;
        }

        // A URN or numeric id is resolved through the api-v2 batch endpoint, because a permalink cannot be
        // reconstructed from an id: SoundCloud permalinks are /user/slug, not /{id}.
        var track = string.IsNullOrWhiteSpace(seededId)
            ? await _client.ResolveTrackAsync(seededUrl!, cancellationToken).ConfigureAwait(false)
            : await _client.ResolveTrackByIdAsync(seededId!, cancellationToken).ConfigureAwait(false);

        if (track is null || !IsTrackResource(track))
        {
            return null;
        }

        _logger.LogDebug("soundcloud: resolved track urn={Urn}", track.Urn);

        return new AutoTagMatchResult
        {
            Accuracy = 1.0d,
            MatchStrategy = "id",
            Track = ToAutoTagTrack(track, info)
        };
    }

    private async Task<AutoTagMatchResult?> TryMatchBySearchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        SoundcloudMatchConfig config,
        CancellationToken cancellationToken)
    {
        var query = BuildSearchQuery(info);
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var results = await _client.SearchTracksAsync(query, config.SearchLimit, cancellationToken)
            .ConfigureAwait(false);

        var candidates = new List<SoundcloudCandidate>();
        foreach (var track in results)
        {
            if (!IsTrackResource(track))
            {
                continue;
            }

            // An exact ISRC on a candidate outranks an ordinary text candidate. This is evaluated on the
            // returned candidates rather than as a search term, because SoundCloud's documented search takes a
            // free-text q and offers no isrc: filter.
            var authority = !string.IsNullOrWhiteSpace(info.Isrc)
                            && string.Equals(
                                NormalizeIsrc(track.Isrc),
                                NormalizeIsrc(info.Isrc),
                                StringComparison.Ordinal)
                ? IsrcAuthority
                : SearchAuthority;

            candidates.Add(new SoundcloudCandidate(ToAutoTagTrack(track, info), authority, track.Urn));
        }

        var dedupedCandidates = DedupeAndRank(candidates);
        var selection = SelectBestCandidate(info, dedupedCandidates, matchingConfig);
        if (selection is null)
        {
            _logger.LogDebug("soundcloud: autotag no acceptable candidate for query {Query}", query);
            return null;
        }

        var chosen = selection.Track;
        var strategy = chosen.Authority == IsrcAuthority ? "isrc" : "text";
        _logger.LogDebug(
            "soundcloud: autotag match strategy={Strategy} accuracy={Accuracy:0.###} urn={Urn} "
            + "localIsrc={LocalIsrc} candidates={CandidateCount} query={Query}",
            strategy,
            selection.Accuracy,
            chosen.Urn,
            string.IsNullOrWhiteSpace(info.Isrc) ? "(none)" : info.Isrc,
            dedupedCandidates.Count,
            query);

        return new AutoTagMatchResult
        {
            Accuracy = selection.Accuracy,
            MatchStrategy = strategy,
            Track = chosen.Track
        };
    }

    /// <summary>
    ///     Collapses duplicate tracks and ranks the ISRC-authoritative ones first.
    /// </summary>
    private static List<SoundcloudCandidate> DedupeAndRank(IReadOnlyList<SoundcloudCandidate> candidates)
        => candidates
            .GroupBy(candidate => candidate.Urn, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Authority).First())
            .OrderByDescending(candidate => candidate.Authority)
            .ToList();

    private static OneTaggerMatching.MatchSelection<SoundcloudCandidate>? SelectBestCandidate(
        AutoTagAudioInfo info,
        IReadOnlyList<SoundcloudCandidate> candidates,
        AutoTagMatchingConfig matchingConfig)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        return OneTaggerMatching.MatchTrack(
            info,
            candidates,
            matchingConfig,
            new OneTaggerMatching.TrackSelectors<SoundcloudCandidate>(
                candidate => candidate.Track.Title,
                _ => null,
                candidate => candidate.Track.Artists,
                candidate => candidate.Track.Duration,
                candidate => candidate.Track.ReleaseDate),
            matchArtist: true);
    }

    private static string BuildSearchQuery(AutoTagAudioInfo info)
    {
        var artist = OneTaggerMatching.CleanArtistSearching(
            info.Artists.Count > 0 ? string.Join(" ", info.Artists) : info.Artist);
        var title = OneTaggerMatching.CleanTitle(info.Title);
        return $"{artist} {title}".Trim();
    }

    /// <summary>
    ///     Maps a SoundCloud track onto the shared tag model.
    /// </summary>
    /// <remarks>
    ///     Artist comes from <see cref="SoundCloudTrack.PreferredArtist" />, so the recording artist outranks
    ///     the uploader handle. Only <c>genre</c> is mapped: <c>tag_list</c> is free-form and mixes genres,
    ///     styles, moods and promotional text, so it is deliberately not spread across Style or Mood. Album is
    ///     left null because SoundCloud's <c>release</c> names the track, not an album.
    /// </remarks>
    internal static AutoTagTrack ToAutoTagTrack(SoundCloudTrack track, AutoTagAudioInfo? local = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        var mapped = new AutoTagTrack
        {
            Title = track.Title,
            Url = string.IsNullOrWhiteSpace(track.PermalinkUrl) ? null : track.PermalinkUrl,
            // The URN is the identity. The numeric id is never written, because it is not stable.
            TrackId = string.IsNullOrWhiteSpace(track.Urn) ? null : track.Urn,
            Art = string.IsNullOrWhiteSpace(track.ArtworkUrl) ? null : track.ArtworkUrl,
            Duration = track.DurationMs > 0 ? TimeSpan.FromMilliseconds(track.DurationMs) : null,
            Isrc = string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc,
            Label = string.IsNullOrWhiteSpace(track.Label) ? null : track.Label,
            ReleaseDate = track.ReleaseDate?.UtcDateTime,
            Bpm = track.Bpm > 0 ? track.Bpm : null,
            Key = string.IsNullOrWhiteSpace(track.KeySignature) ? null : track.KeySignature
        };

        var artist = track.PreferredArtist;
        if (!string.IsNullOrWhiteSpace(artist))
        {
            mapped.Artists.Add(artist);
        }

        if (!string.IsNullOrWhiteSpace(track.Genre))
        {
            mapped.Genres.Add(track.Genre);
        }

        mapped.Other["SOURCE"] = new List<string> { "SOUNDCLOUD" };
        if (!string.IsNullOrWhiteSpace(track.Urn))
        {
            mapped.Other["SOURCEID"] = new List<string> { track.Urn };
            mapped.Other["SOUNDCLOUD_TRACK_ID"] = new List<string> { track.Urn };
        }

        if (!string.IsNullOrWhiteSpace(track.PermalinkUrl))
        {
            mapped.Other["SOUNDCLOUD_URL"] = new List<string> { track.PermalinkUrl };
        }

        // Only fills gaps the API left; never overwrites what SoundCloud actually returned.
        if (string.IsNullOrWhiteSpace(mapped.Title))
        {
            mapped.Title = local?.Title ?? string.Empty;
        }

        if (mapped.Artists.Count == 0 && !string.IsNullOrWhiteSpace(local?.Artist))
        {
            mapped.Artists.Add(local!.Artist);
        }

        return mapped;
    }

    private static bool IsTrackResource(SoundCloudTrack track)
        => !string.IsNullOrWhiteSpace(track.Urn)
           && track.Urn.StartsWith("soundcloud:tracks:", StringComparison.OrdinalIgnoreCase);

    private static string? TryResolveSoundCloudUrl(AutoTagAudioInfo info)
    {
        foreach (var key in TrackIdTagKeys)
        {
            var value = AutoTagTagValueReader.ReadFirstTagValue(info, [key]);
            var url = NormalizeToUrl(value);
            if (url is not null)
            {
                return url;
            }
        }

        foreach (var key in TrackUrlTagKeys)
        {
            var value = AutoTagTagValueReader.ReadFirstTagValue(info, [key]);
            var url = NormalizeToUrl(value);
            if (url is not null)
            {
                return url;
            }
        }

        return null;
    }

    private static bool HasEmbeddedIdentity(AutoTagAudioInfo info)
        => TrackIdTagKeys.Concat(TrackUrlTagKeys)
            .Any(key => !string.IsNullOrWhiteSpace(AutoTagTagValueReader.ReadFirstTagValue(info, [key])));

    /// <summary>
    ///     Turns a stored identity value into a resolvable URL.
    /// </summary>
    /// <remarks>
    ///     Only real links qualify. A URN or bare id is deliberately not turned into
    ///     <c>soundcloud.com/{id}</c>, because that is not a permalink - SoundCloud addresses tracks as
    ///     <c>/user/slug</c> - and fetching it would resolve to the wrong resource or nothing at all. Those
    ///     values go through <see cref="TryResolveSoundCloudId" /> instead.
    /// </remarks>
    private static string? NormalizeToUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : null;
    }

    /// <summary>
    ///     Reads a stored URN or numeric id, which is the form <c>SOUNDCLOUD_TRACK_ID</c> is written in.
    /// </summary>
    private static string? TryResolveSoundCloudId(AutoTagAudioInfo info)
    {
        foreach (var key in TrackIdTagKeys)
        {
            var value = AutoTagTagValueReader.ReadFirstTagValue(info, [key]);
            if (!string.IsNullOrWhiteSpace(value) && NormalizeToUrl(value) is null)
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string NormalizeIsrc(string? value)
        => TrackCandidateValidator.NormalizeIsrc(value);

    private static SoundcloudMatchConfig NormalizeConfig(SoundcloudMatchConfig? config)
        => new()
        {
            MatchById = config?.MatchById ?? false,
            SearchLimit = Math.Clamp(config?.SearchLimit ?? 12, 5, 30)
        };

    private sealed record SoundcloudCandidate(AutoTagTrack Track, int Authority, string Urn);
}