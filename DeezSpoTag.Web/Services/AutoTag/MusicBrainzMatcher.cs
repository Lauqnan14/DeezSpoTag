using System.Globalization;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Utils;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusicBrainzMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly (string Key, Regex Pattern)[] VariantPatterns =
    [
        ("instrumental", CreateVariantRegex(@"\binstrumental\b")),
        ("radio", CreateVariantRegex(@"\bradio\s+(edit|version|mix)\b")),
        ("club", CreateVariantRegex(@"\bclub\s+(version|mix|edit)\b")),
        ("extended", CreateVariantRegex(@"\bextended(?:\s+(mix|version|edit))?\b")),
        ("live", CreateVariantRegex(@"\blive\b")),
        ("acoustic", CreateVariantRegex(@"\bacoustic\b")),
        ("karaoke", CreateVariantRegex(@"\bkaraoke\b")),
        ("remix", CreateVariantRegex(@"\bremix(ed)?\b")),
        ("remaster", CreateVariantRegex(@"\bremaster(ed)?\b"))
    ];

    private readonly MusicBrainzClient _client;
    private readonly ILogger<MusicBrainzMatcher> _logger;

    public MusicBrainzMatcher(MusicBrainzClient client, ILogger<MusicBrainzMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        MusicBrainzMatchConfig config,
        CancellationToken cancellationToken)
    {
        var matchStartedAt = DateTimeOffset.UtcNow;
        var resolvedConfig = NormalizeConfig(config);
        var preferences = MusicBrainzPreferences.FromConfig(resolvedConfig);

        if (resolvedConfig.MatchById)
        {
            var byIdResult = await TryMatchRecordingIdAsync(info, matchingConfig, resolvedConfig, preferences, cancellationToken);
            if (byIdResult != null)
            {
                return byIdResult;
            }
        }

        if (resolvedConfig.UseIsrcFirst && !string.IsNullOrWhiteSpace(info.Isrc))
        {
            var isrcResult = await TryMatchIsrcAsync(info, matchingConfig, resolvedConfig, preferences, cancellationToken);
            if (isrcResult != null)
            {
                return isrcResult;
            }
        }

        var queries = BuildQueries(info).ToList();
        for (var queryIndex = 0; queryIndex < queries.Count; queryIndex++)
        {
            if (DateTimeOffset.UtcNow - matchStartedAt >= TextPhaseSoftDeadline)
            {
                break;
            }

            var results = await _client.SearchAsync(queries[queryIndex], resolvedConfig.SearchLimit, cancellationToken);
            if (results?.Recordings is null || results.Recordings.Count == 0)
            {
                continue;
            }

            var tracks = results.Recordings
                .Take(resolvedConfig.SearchLimit)
                .Select(r => ToTrack(r, preferences))
                .ToList();
            var result = await TryBuildMatchResultAsync(info, tracks, matchingConfig, resolvedConfig, preferences, cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        if (!resolvedConfig.UseIsrcFirst && !string.IsNullOrWhiteSpace(info.Isrc))
        {
            return await TryMatchIsrcAsync(info, matchingConfig, resolvedConfig, preferences, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// The text phase stops issuing further search queries at this point, keeping
    /// distance from the platform's hard 45s match budget (consecutive timeouts trip
    /// the run's platform circuit breaker).
    /// </summary>
    private static readonly TimeSpan TextPhaseSoftDeadline = TimeSpan.FromSeconds(30);

    private static readonly Regex LuceneSpecialCharacters = new(
        @"[+\-&|!(){}\[\]^""~*?:\\/]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly HashSet<string> PerformerPrefixAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "guest",
        "additional",
        "minor",
        "solo"
    };


    private async Task<AutoTagMatchResult?> TryMatchRecordingIdAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        MusicBrainzMatchConfig config,
        MusicBrainzPreferences preferences,
        CancellationToken cancellationToken)
    {
        foreach (var recordingId in GetRecordingIds(info))
        {
            try
            {
                var recording = await _client.GetRecordingAsync(recordingId, cancellationToken);
                if (recording == null || string.IsNullOrWhiteSpace(recording.Id))
                {
                    continue;
                }

                var track = ToTrack(recording, preferences);
                await ExtendTrackAsync(info, track, preferences, cancellationToken);
                if (!IsCandidateCompatibleWithSource(info, track, matchingConfig, config))
                {
                    continue;
                }

                return new AutoTagMatchResult
                {
                    Accuracy = 1.0,
                    Track = ToAutoTagTrack(track),
                    MatchStrategy = "id"
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "MusicBrainz ID lookup failed for {RecordingId}", recordingId);
                }
            }
        }

        return null;
    }

    private async Task<AutoTagMatchResult?> TryMatchIsrcAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        MusicBrainzMatchConfig config,
        MusicBrainzPreferences preferences,
        CancellationToken cancellationToken)
    {
        var query = $"isrc:{info.Isrc}";
        var results = await _client.SearchAsync(query, config.SearchLimit, cancellationToken);
        if (results?.Recordings is null)
        {
            return null;
        }

        var tracks = results.Recordings
            .Take(config.SearchLimit)
            .Select(r => ToTrack(r, preferences))
            .ToList();
        return await TryBuildMatchResultAsync(info, tracks, matchingConfig, config, preferences, cancellationToken);
    }

    private async Task<AutoTagMatchResult?> TryBuildMatchResultAsync(
        AutoTagAudioInfo info,
        List<MusicBrainzTrack> tracks,
        AutoTagMatchingConfig matchingConfig,
        MusicBrainzMatchConfig config,
        MusicBrainzPreferences preferences,
        CancellationToken cancellationToken)
    {
        // Ranked candidates: when the best-scored candidate fails the compatibility
        // gate (e.g. a variant-titled recording), fall through to the next-ranked
        // candidate instead of dropping MusicBrainz for the file.
        var candidates = MatchTracks(info, tracks, matchingConfig, config);
        foreach (var candidate in candidates)
        {
            // The compatibility gate only reads title/artists/duration, which are
            // populated before the release lookup — gate first, extend only the winner.
            if (!IsCandidateCompatibleWithSource(info, candidate.Track, matchingConfig, config))
            {
                continue;
            }

            await ExtendTrackAsync(info, candidate.Track, preferences, cancellationToken);
            if (!IsCandidateCompatibleWithSource(info, candidate.Track, matchingConfig, config))
            {
                continue;
            }

            return new AutoTagMatchResult
            {
                Accuracy = candidate.Accuracy,
                Track = ToAutoTagTrack(candidate.Track)
            };
        }

        return null;
    }

    private static List<string> BuildQueries(AutoTagAudioInfo info)
    {
        var title = OneTaggerMatching.CleanTitle(info.Title);
        var artist = OneTaggerMatching.CleanArtistSearching(info.Artist);
        var titleEscaped = EscapeQuery(title);
        var artistEscaped = EscapeQuery(artist);

        var queries = new List<string>
        {
            $"{artistEscaped} {titleEscaped}~",
            $"recording:\"{titleEscaped}\" AND artist:\"{artistEscaped}\"",
            $"recording:\"{titleEscaped}\"",
            $"\"{titleEscaped}\" AND artist:\"{artistEscaped}\""
        };

        return queries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string EscapeQuery(string input)
        => LuceneSpecialCharacters.Replace(input, match => "\\" + match.Value);

    private static List<MatchCandidate> MatchTracks(AutoTagAudioInfo info, List<MusicBrainzTrack> tracks, AutoTagMatchingConfig config, MusicBrainzMatchConfig providerConfig)
    {
        var ranked = OneTaggerMatching.MatchTrackRanked(
            info,
            tracks,
            WithRankingCompatibility(config, providerConfig),
            new OneTaggerMatching.TrackSelectors<MusicBrainzTrack>(
                track => track.Title,
                _ => null,
                track => track.Artists.Count > 0 ? track.Artists : track.AlbumArtists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: true);

        return ranked
            .Select(match => new MatchCandidate(match.Accuracy, match.Track))
            .ToList();
    }

    /// <summary>
    /// Ranking compatibility: only the duration window is widened by the configured floor
    /// (via the same Math.Max combination used by the identity gate). The strictness used
    /// for score admission stays the global value — the provider artist floor is enforced
    /// by the compatibility gate that runs on every ranked candidate, not by rescoring.
    /// </summary>
    private static AutoTagMatchingConfig WithRankingCompatibility(AutoTagMatchingConfig config, MusicBrainzMatchConfig? providerConfig)
    {
        var configuredDurationFloor = Math.Clamp(providerConfig?.MinDurationDifferenceSeconds ?? 45, 0, 300);
        return new AutoTagMatchingConfig
        {
            Strictness = config.Strictness,
            MatchDuration = config.MatchDuration,
            MaxDurationDifferenceSeconds = Math.Max(config.MaxDurationDifferenceSeconds, configuredDurationFloor),
            MultipleMatches = config.MultipleMatches,
            PreferredReleaseType = config.PreferredReleaseType
        };
    }

    private static MusicBrainzTrack ToTrack(Recording recording, MusicBrainzPreferences preferences)
    {
        var release = SelectBestReleaseSmall(recording.Releases ?? new List<ReleaseSmall>(), recording.FirstReleaseDate, preferences);
        var artistIds = CreditIds(recording.ArtistCredit);
        var albumArtistIds = CreditIds(release?.ArtistCredit);
        var track = new MusicBrainzTrack
        {
            Title = recording.Title,
            Artists = recording.ArtistCredit?.Select(a => a.Name).ToList() ?? new List<string>(),
            AlbumArtists = release?.ArtistCredit?.Select(a => a.Name).ToList() ?? new List<string>(),
            Album = release?.Title,
            Url = $"https://musicbrainz.org/recording/{recording.Id}",
            TrackId = recording.Id,
            ReleaseId = release?.Id ?? string.Empty,
            RecordingId = recording.Id,
            ArtistId = artistIds.FirstOrDefault(),
            ArtistIds = artistIds,
            AlbumArtistId = albumArtistIds.FirstOrDefault(),
            AlbumArtistIds = albumArtistIds,
            AlbumId = release?.Id,
            Duration = recording.Length.HasValue ? TimeSpan.FromMilliseconds(recording.Length.Value) : TimeSpan.Zero,
            ReleaseYear = ParseYear(recording.FirstReleaseDate),
            ReleaseDate = ParseDate(recording.FirstReleaseDate),
            RecordingGenres = GenreNames(recording.Genres)
        };

        ApplyIsrcs(track, recording.Isrcs, fileIsrc: null);
        SetOtherValue(track.Other, "ORIGINALDATE", recording.FirstReleaseDate);
        track.RecordingRelations = recording.Relations;
        track.Aliases = recording.Aliases ?? new List<Alias>();
        track.ArtistCredits = recording.ArtistCredit;

        var artistSortName = recording.ArtistCredit?
            .Select(credit => credit.Artist.SortName)
            .FirstOrDefault(sortName => !string.IsNullOrWhiteSpace(sortName));
        if (!string.IsNullOrWhiteSpace(artistSortName))
        {
            AddOtherValue(track.Other, "artistsort", artistSortName);
        }

        return track;
    }

    private async Task ExtendTrackAsync(
        AutoTagAudioInfo info,
        MusicBrainzTrack track,
        MusicBrainzPreferences preferences,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.TrackId))
        {
            return;
        }

        ApplyIsrcs(track, track.Isrcs, info.Isrc);

        try
        {
            var releases = await _client.GetReleasesAsync(track.TrackId!, cancellationToken);
            if (releases == null)
            {
                return;
            }

            // The runner supplies the established album's provider-specific ID here.
            // Honor it before the preliminary recording release; without an anchor,
            // rank the detailed candidates by country order and ordinary release scores.
            var fileAlbumId = ReadFileAlbumId(info);
            var release = SelectBestRelease(
                releases.Releases,
                track.ReleaseDate,
                preferences,
                fileAlbumId,
                info.Album);
            if (release == null)
            {
                return;
            }

            track.Album = release.Title;
            track.ReleaseId = release.Id;
            track.AlbumId = release.Id;
            track.ReleaseDate = ParseDate(release.Date) ?? track.ReleaseDate;
            track.AlbumArtists = release.ArtistCredit?.Select(a => a.Name).ToList() ?? track.AlbumArtists;
            var albumArtistIds = CreditIds(release.ArtistCredit);
            if (albumArtistIds.Count > 0)
            {
                track.AlbumArtistIds = albumArtistIds;
                track.AlbumArtistId = albumArtistIds[0];
            }

            await ApplyCoverArtAsync(track, release, cancellationToken);
            ApplyLabelInfo(track, release);
            ApplyTrackPosition(track, release);
            ApplyReleaseMetadata(track, release, preferences);
            await ApplyRelationshipsAsync(track, cancellationToken);
            ApplyGenres(track, release);
            ApplyLocalizedAliases(track, preferences);

            var albumArtistSortName = release.ArtistCredit?
                .Select(credit => credit.Artist.SortName)
                .FirstOrDefault(sortName => !string.IsNullOrWhiteSpace(sortName));
            if (!string.IsNullOrWhiteSpace(albumArtistSortName))
            {
                AddOtherValue(track.Other, "albumartistsort", albumArtistSortName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to extend MusicBrainz track.");
        }
    }

    /// <summary>
    /// Derives the relationship credits MusicBrainz knows for the recording the way
    /// Picard does, then stores them in the tags this app already writes. Composer and
    /// lyricist relationships usually hang off the work. Performer and technical roles
    /// hang off the recording. Mastering and recording stay unmapped.
    /// </summary>
    private async Task ApplyRelationshipsAsync(MusicBrainzTrack track, CancellationToken cancellationToken)
    {
        try
        {
            if (track.RecordingRelations is not { Count: > 0 } && !string.IsNullOrWhiteSpace(track.TrackId))
            {
                var recording = await _client.GetRecordingAsync(track.TrackId!, cancellationToken);
                if (recording?.Relations is { Count: > 0 })
                {
                    track.RecordingRelations = recording.Relations;
                }

                if (recording?.Genres is { Count: > 0 })
                {
                    track.RecordingGenres = GenreNames(recording.Genres);
                }
            }

            if (track.RecordingRelations is not { Count: > 0 })
            {
                return;
            }

            var composers = new List<string>();
            var composerIds = new List<string>();
            var lyricists = new List<string>();
            var writers = new List<string>();
            var involvedPeople = new List<string>();

            void AddComposer(string? name, string? artistId)
            {
                if (!string.IsNullOrWhiteSpace(name) && !composers.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    composers.Add(name);
                }

                if (!string.IsNullOrWhiteSpace(artistId) && !composerIds.Contains(artistId, StringComparer.OrdinalIgnoreCase))
                {
                    composerIds.Add(artistId);
                }
            }

            void AddLyricist(string name)
            {
                if (!lyricists.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    lyricists.Add(name);
                }

                track.Lyricist ??= name;
            }

            void AddInvolved(string role, string name)
            {
                involvedPeople.Add($"{role}: {name}");
            }

            void CollectArtistRelation(Relation relation, bool instrumental)
            {
                var name = relation.Artist?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                var type = relation.Type ?? string.Empty;
                if (type.Equals("instrument", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("vocal", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("performer", StringComparison.OrdinalIgnoreCase))
                {
                    var attributes = FormatPerformerAttributes(relation.Attributes, type, relation.AttributeCredits);
                    AddInvolved(attributes.Length > 0 ? $"Performer ({attributes})" : "Performer", name);
                    return;
                }

                if (type.Equals("composer", StringComparison.OrdinalIgnoreCase))
                {
                    AddComposer(name, relation.Artist!.Id);
                    return;
                }

                if (type.Equals("writer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!writers.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        writers.Add(name);
                    }

                    return;
                }

                if (type.Equals("lyricist", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("librettist", StringComparison.OrdinalIgnoreCase))
                {
                    if (!instrumental)
                    {
                        AddLyricist(name);
                    }

                    return;
                }

                if (type.Equals("remixer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!track.Remixers.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        track.Remixers.Add(name);
                    }

                    return;
                }

                if (TryMapInvolvedRole(type, out var role))
                {
                    AddInvolved(role, name);
                }
            }

            foreach (var relation in track.RecordingRelations)
            {
                if (relation.TargetType?.Equals("artist", StringComparison.OrdinalIgnoreCase) == true)
                {
                    CollectArtistRelation(relation, instrumental: false);
                    continue;
                }

                if (relation.TargetType?.Equals("work", StringComparison.OrdinalIgnoreCase) != true
                    || string.IsNullOrWhiteSpace(relation.Work?.Id))
                {
                    continue;
                }

                var work = relation.Work!;
                var instrumental = relation.Type?.Equals("performance", StringComparison.OrdinalIgnoreCase) == true
                    && (relation.Attributes ?? new List<string>()).Any(attribute =>
                        string.Equals(attribute, "instrumental", StringComparison.OrdinalIgnoreCase));
                AddOtherValue(track.Other, "MUSICBRAINZ_WORKID", work.Id);
                AddOtherValue(track.Other, "WORK", work.Title);
                AddOtherValues(track.Other, "ISWC", work.Iswcs);
                if (instrumental)
                {
                    AddOtherValue(track.Other, "language", "zxx");
                }
                else if (work.Languages is { Count: > 0 })
                {
                    AddOtherValues(track.Other, "language", work.Languages);
                }
                else
                {
                    AddOtherValue(track.Other, "language", work.Language);
                }

                foreach (var workRelation in (work.Relations ?? new List<Relation>())
                             .Where(candidate => candidate.TargetType?.Equals("artist", StringComparison.OrdinalIgnoreCase) == true))
                {
                    CollectArtistRelation(workRelation, instrumental);
                }
            }

            if (composers.Count > 0)
            {
                AddOtherValues(track.Other, "composer", composers);
            }

            if (composerIds.Count > 0)
            {
                AddOtherValues(track.Other, "MUSICBRAINZ_COMPOSERID", composerIds);
            }

            if (lyricists.Count > 0)
            {
                AddOtherValues(track.Other, "lyricist", lyricists);
            }

            if (writers.Count > 0)
            {
                AddOtherValues(track.Other, "writer", writers);
            }

            if (involvedPeople.Count > 0)
            {
                AddOtherValues(track.Other, "involvedPeople", involvedPeople);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to derive MusicBrainz relationship credits for {RecordingId}", track.RecordingId);
            }
        }
    }

    private static bool TryMapInvolvedRole(string type, out string role)
    {
        switch (type.ToLowerInvariant())
        {
            case "audio":
            case "sound":
            case "live sound":
            case "engineer":
                role = "Engineer";
                return true;
            case "mix":
            case "mixer":
                role = "Mixer";
                return true;
            case "mix-dj":
                role = "DJ-Mixer";
                return true;
            case "conductor":
                role = "Conductor";
                return true;
            case "producer":
                role = "Producer";
                return true;
            case "arranger":
            case "instrument arranger":
            case "orchestrator":
            case "vocal arranger":
                role = "Arranger";
                return true;
            case "audio director":
            case "video director":
                role = "Director";
                return true;
            case "chorus master":
                role = "Performer (chorus master)";
                return true;
            case "concertmaster":
                role = "Performer (concertmaster)";
                return true;
            case "performing orchestra":
                role = "Performer (orchestra)";
                return true;
            default:
                role = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// Picard splits relation attributes into guest/additional/solo prefixes and instrument
    /// nouns, substitutes <c>attribute-credits</c>, and joins several nouns with "and".
    /// </summary>
    private static string FormatPerformerAttributes(
        IReadOnlyList<string>? attributes,
        string relationType,
        IReadOnlyDictionary<string, string>? attributeCredits)
    {
        var prefixes = new List<string>();
        var nouns = new List<string>();
        foreach (var raw in attributes ?? Array.Empty<string>())
        {
            var attribute = raw?.Trim() ?? string.Empty;
            if (attribute.Length == 0)
            {
                continue;
            }

            if (attributeCredits != null
                && attributeCredits.TryGetValue(attribute, out var credit)
                && !string.IsNullOrWhiteSpace(credit))
            {
                attribute = credit.Trim();
            }

            if (PerformerPrefixAttributes.Contains(attribute))
            {
                prefixes.Add(attribute);
            }
            else
            {
                nouns.Add(attribute);
            }
        }

        string result;
        if (nouns.Count > 1)
        {
            result = string.Join(", ", nouns.Take(nouns.Count - 1)) + " and " + nouns[^1];
        }
        else if (nouns.Count == 1)
        {
            result = nouns[0];
        }
        else if (relationType.Equals("instrument", StringComparison.OrdinalIgnoreCase))
        {
            result = "instruments";
        }
        else if (relationType.Equals("vocal", StringComparison.OrdinalIgnoreCase))
        {
            result = "vocals";
        }
        else
        {
            result = string.Empty;
        }

        var prefix = string.Join(" ", prefixes);
        return string.Join(" ", new[] { prefix, result }.Where(part => part.Length > 0));
    }

    /// <summary>
    /// Replaces the canonical title/artist names with the localized alias matching the
    /// user's preferred locales (Picard's "translate titles/names using these locales").
    /// Only runs when the user enabled aliases and picked locales — the canonical
    /// names stay untouched otherwise.
    /// </summary>
    private static void ApplyLocalizedAliases(MusicBrainzTrack track, MusicBrainzPreferences preferences)
    {
        if (!preferences.UseAliases || preferences.PreferredLocales.Count == 0)
        {
            return;
        }

        var localizedTitle = FindLocalizedAliasName(track.Aliases, preferences.PreferredLocales);
        if (!string.IsNullOrWhiteSpace(localizedTitle))
        {
            track.Title = localizedTitle;
        }

        // Artist localization walks the recording's artist credits in order, replacing
        // each credited artist name with its localized alias when one exists.
        var credits = track.ArtistCredits;
        if (credits is { Count: > 0 })
        {
            for (var index = 0; index < credits.Count && index < track.Artists.Count; index++)
            {
                var localizedArtist = FindLocalizedAliasName(credits[index].Artist.Aliases, preferences.PreferredLocales);
                if (!string.IsNullOrWhiteSpace(localizedArtist))
                {
                    track.Artists[index] = localizedArtist;
                }
            }
        }
    }

    private static string? FindLocalizedAliasName(List<Alias>? aliases, IReadOnlyList<string> preferredLocales)
    {
        if (aliases is not { Count: > 0 })
        {
            return null;
        }

        foreach (var localeMatches in preferredLocales
                     .Select(preferredLocale => aliases
                         .Where(alias => !string.IsNullOrWhiteSpace(alias.Name)
                             && !string.IsNullOrWhiteSpace(alias.Locale)
                             && (string.Equals(alias.Locale, preferredLocale, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(alias.Locale.Split('_')[0], preferredLocale, StringComparison.OrdinalIgnoreCase)))
                         .ToList())
                     .Where(matches => matches.Count > 0))
        {
            var primary = localeMatches.FirstOrDefault(alias => alias.Primary == true);
            return (primary ?? localeMatches.First()).Name;
        }

        return null;
    }

    private static bool IsTitleCompatibleWithSource(
        AutoTagAudioInfo info,
        MusicBrainzTrack track,
        AutoTagMatchingConfig config)
    {
        // The canonical title and every localized alias are candidate titles: a file
        // titled with the localized name still matches the canonical recording.
        var candidateTitles = new List<string> { track.Title };
        if (track.Aliases is { Count: > 0 })
        {
            candidateTitles.AddRange(track.Aliases
                .Select(alias => alias.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
        }
        foreach (var candidateTitle in candidateTitles)
        {
            if (string.IsNullOrWhiteSpace(candidateTitle) || !IsVariantCompatible(info.Title, candidateTitle))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(info.Title) || string.IsNullOrWhiteSpace(candidateTitle))
            {
                return true;
            }

            if (HasCompatibleTitleIdentity(info.Title, candidateTitle, config))
            {
                return true;
            }
        }

        return false;
    }

    private async Task ApplyCoverArtAsync(MusicBrainzTrack track, Release release, CancellationToken cancellationToken)
    {
        if (release.CoverArtArchive.Front)
        {
            track.Art = $"https://coverartarchive.org/release/{release.Id}/front";
            return;
        }

        if (release.CoverArtArchive.Count <= 0 || string.IsNullOrWhiteSpace(release.Id))
        {
            return;
        }

        var frontImage = await _client.TryGetFrontCoverImageUrlAsync(release.Id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(frontImage))
        {
            track.Art = frontImage;
        }
    }

    private static void ApplyLabelInfo(MusicBrainzTrack track, Release release)
    {
        var labels = new List<string>();
        var catalogs = new List<string>();
        foreach (var info in release.LabelInfo ?? new List<LabelInfo>())
        {
            var name = info.Label?.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && !labels.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                labels.Add(name);
            }

            var catalog = info.CatalogNumber?.Trim();
            if (!string.IsNullOrWhiteSpace(catalog) && !catalogs.Contains(catalog, StringComparer.OrdinalIgnoreCase))
            {
                catalogs.Add(catalog);
            }
        }

        track.Labels = labels;
        track.CatalogNumbers = catalogs;
        track.Label = labels.FirstOrDefault();
        track.CatalogNumber = catalogs.FirstOrDefault();
    }

    private static void ApplyTrackPosition(MusicBrainzTrack track, Release release)
    {
        var trackEntry = release.Media
            .SelectMany(media => media.Tracks.Select(trackInfo => new { Media = media, Track = trackInfo }))
            .FirstOrDefault(item => item.Track.Recording.Id == track.TrackId);
        if (trackEntry == null)
        {
            return;
        }

        track.TrackNumber = trackEntry.Track.Position;
        if (!string.IsNullOrWhiteSpace(trackEntry.Track.Id))
        {
            track.MediumTrackId = trackEntry.Track.Id.Trim();
        }

        AddOtherValue(track.Other, "MUSICBRAINZ_TRACKNUMBER", trackEntry.Track.Number);
        AddOtherValue(track.Other, "DISCSUBTITLE", trackEntry.Media.Title);
        if (trackEntry.Media.Position.HasValue)
        {
            track.DiscNumber = trackEntry.Media.Position.Value;
        }

        var total = trackEntry.Media.TrackCount ?? trackEntry.Media.Tracks.Count;
        if (total > 0)
        {
            track.TrackTotal = total;
        }
    }

    private static void ApplyReleaseMetadata(MusicBrainzTrack track, Release release, MusicBrainzPreferences preferences)
    {
        if (release.Media.Count > 0)
        {
            track.DiscNumber ??= 1;
            track.DiscTotal = release.Media.Count;
        }

        if (release.ReleaseGroup != null)
        {
            AddOtherValue(track.Other, "MUSICBRAINZ_RELEASEGROUPID", release.ReleaseGroup.Id);
            track.ReleaseGroupId = release.ReleaseGroup.Id;
            track.ReleaseType = AutoTagReleaseCategory.Resolve(
                release.ReleaseGroup.PrimaryType,
                release.ReleaseGroup.SecondaryTypes,
                track.TrackTotal);
            AddOtherValue(track.Other, "RELEASETYPE", track.ReleaseType);
            var originalDate = !string.IsNullOrWhiteSpace(release.ReleaseGroup.FirstReleaseDate)
                ? release.ReleaseGroup.FirstReleaseDate
                : ReadOtherValue(track.Other, "ORIGINALDATE");
            SetOtherValue(track.Other, "ORIGINALDATE", originalDate);
        }

        if (!string.IsNullOrWhiteSpace(release.Barcode))
        {
            AddOtherValue(track.Other, "BARCODE", release.Barcode);
            track.Barcode = release.Barcode;
        }

        AddOtherValue(track.Other, "MUSICBRAINZ_ALBUMID", release.Id);
        AddOtherValue(track.Other, "RELEASESTATUS", release.Status);
        AddOtherValue(track.Other, "RELEASEDATE", release.Date);
        AddOtherValue(track.Other, "SCRIPT", release.TextRepresentation?.Script);
        AddOtherValue(track.Other, "RELEASELANGUAGE", release.TextRepresentation?.Language);
        AddOtherValues(track.Other, "MEDIA", release.Media.Select(media => media.Format));
        track.AlbumId = release.Id;
        track.ReleaseStatus = release.Status;
        track.ReleaseCountry = release.Country;
        var preferredEventCountry = FirstPreferredEventCountry(release, preferences);
        if (!string.IsNullOrWhiteSpace(preferredEventCountry))
        {
            track.ReleaseCountry = preferredEventCountry;
        }

        AddOtherValue(track.Other, "RELEASECOUNTRY", track.ReleaseCountry);
        track.Media = release.Media
            .Select(media => media.Format)
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .Select(format => format!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ApplyGenres(MusicBrainzTrack track, Release release)
    {
        var names = new List<string>();
        AddGenreNames(names, track.RecordingGenres);
        AddGenreNames(names, GenreNames(release.Genres));
        AddGenreNames(names, GenreNames(release.ReleaseGroup?.Genres));
        track.Genres = names;
    }

    private static void ApplyIsrcs(MusicBrainzTrack track, IEnumerable<string>? isrcs, string? fileIsrc)
    {
        var values = (isrcs ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(fileIsrc))
        {
            var matchIndex = values.FindIndex(value => string.Equals(value, fileIsrc.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matchIndex > 0)
            {
                var matched = values[matchIndex];
                values.RemoveAt(matchIndex);
                values.Insert(0, matched);
            }
        }

        track.Isrcs = values;
        track.Isrc = values.FirstOrDefault();
    }

    private static List<string> CreditIds(IEnumerable<ArtistCredit>? credits)
    {
        var ids = new List<string>();
        if (credits == null)
        {
            return ids;
        }

        foreach (var id in credits
                     .Select(credit => credit.Artist?.Id)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Select(id => id!.Trim()))
        {
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static List<string> GenreNames(IEnumerable<Genre>? genres)
    {
        var names = new List<string>();
        AddGenreNames(names, genres?.Select(genre => genre.Name));
        return names;
    }

    private static void AddGenreNames(List<string> names, IEnumerable<string>? values)
    {
        if (values == null)
        {
            return;
        }

        foreach (var name in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value.Trim()))
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }
        return int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static DateTime? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        if (DateTime.TryParseExact(date, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return parsed;
        }
        return DateTime.TryParseExact(date, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
            ? parsed
            : null;
    }

    private static bool IsCompilation(List<string>? types)
    {
        return types != null && types.Any(t => string.Equals(t, "compilation", StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseSmall? SelectBestReleaseSmall(List<ReleaseSmall> releases, string? preferredDate, MusicBrainzPreferences preferences)
    {
        var preferredYear = ParseYear(preferredDate);
        return releases
            .OrderBy(r => ReleaseCountryTier(r.Country, preferences.PreferredCountries))
            .ThenByDescending(r => ScoreReleaseSmall(r, preferredYear, preferences))
            .ThenBy(r => r.Date ?? "9999-99-99", StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int ScoreReleaseSmall(ReleaseSmall release, int? preferredYear, MusicBrainzPreferences preferences)
    {
        return ScoreReleaseCommon(
            release.ReleaseGroup?.SecondaryTypes,
            release.Status,
            release.ReleaseGroup?.PrimaryType,
            release.Date,
            preferredYear,
            preferences);
    }

    private static string? ReadFileAlbumId(AutoTagAudioInfo info)
    {
        foreach (var value in new[] { "MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_ALBUM_ID", "ALBUMID", "MB_ALBUM_ID" }
                     .Where(key => info.Tags.TryGetValue(key, out var values) && values is { Count: > 0 })
                     .Select(key => info.Tags[key].FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)))
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            return value!.Trim();
        }

        return null;
    }

    private static Release? SelectBestRelease(
        List<Release> releases,
        DateTime? preferredDate,
        MusicBrainzPreferences preferences,
        string? preferredReleaseId,
        string? preferredAlbum)
    {
        if (!string.IsNullOrWhiteSpace(preferredReleaseId))
        {
            var exact = releases.FirstOrDefault(release =>
                string.Equals(release.Id, preferredReleaseId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }
        }

        var preferredYear = preferredDate?.Year;
        return releases
            .OrderBy(r => BestCountryTier(r, preferences.PreferredCountries))
            .ThenByDescending(r => ScoreRelease(r, preferredYear, preferences, preferredAlbum))
            .ThenBy(r => r.Date ?? "9999-99-99", StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int ScoreRelease(Release release, int? preferredYear, MusicBrainzPreferences preferences, string? preferredAlbum)
    {
        var score = ScoreReleaseCommon(
            release.ReleaseGroup?.SecondaryTypes,
            release.Status,
            release.ReleaseGroup?.PrimaryType,
            release.Date,
            preferredYear,
            preferences);
        score += ScoreFormatRank(release.Media, preferences.PreferredFormats) * preferences.FormatWeight;

        var totalTracks = release.Media.Sum(m => m.TrackCount ?? m.Tracks.Count);
        if (totalTracks > 0)
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(preferredAlbum)
            && !string.IsNullOrWhiteSpace(release.Title))
        {
            // Compare the album CORE (edition markers stripped) so "Album (Deluxe)"
            // scores against the deluxe release, not against the standard album;
            // then reward matching edition intent and penalize edition mismatch.
            var albumScore = AutoTagSimilarity.ComputeScore(
                AlbumTitleNormalizer.CoreTitle(preferredAlbum),
                AlbumTitleNormalizer.CoreTitle(release.Title));
            if (albumScore >= 0.90d)
            {
                score += 8;
            }
            else if (albumScore < 0.55d)
            {
                score -= 6;
            }

            var preferredEditions = AlbumTitleNormalizer.EditionIntent(preferredAlbum);
            if (preferredEditions.Count > 0)
            {
                score += AlbumTitleNormalizer.EditionIntent(release.Title).SetEquals(preferredEditions)
                    ? 4
                    : -5;
            }
        }

        return score;
    }

    private static int ScoreReleaseCommon(
        List<string>? secondaryTypes,
        string? status,
        string? primaryType,
        string? releaseDate,
        int? preferredYear,
        MusicBrainzPreferences preferences)
    {
        var score = 0;
        if (preferences.ExcludeCompilations)
        {
            score += IsCompilation(secondaryTypes)
                ? -preferences.CompilationPenaltyWeight
                : Math.Max(1, preferences.CompilationPenaltyWeight / 2);
        }

        if (preferences.PreferOfficial)
        {
            score += string.Equals(status, "Official", StringComparison.OrdinalIgnoreCase)
                ? preferences.OfficialWeight
                : PenaltyFromWeight(preferences.OfficialWeight);
        }

        if (preferences.PreferredPrimaryType != null)
        {
            var resolvedPrimaryType = primaryType ?? string.Empty;
            score += string.Equals(resolvedPrimaryType, preferences.PreferredPrimaryType, StringComparison.OrdinalIgnoreCase)
                ? preferences.PrimaryTypeWeight
                : PenaltyFromWeight(preferences.PrimaryTypeWeight);
        }

        var year = ParseYear(releaseDate);
        if (preferences.PreferReleaseYear && preferredYear.HasValue && year.HasValue)
        {
            score -= Math.Abs(preferredYear.Value - year.Value) * preferences.YearWeight;
        }

        return score;
    }

    private static int PenaltyFromWeight(int weight)
    {
        if (weight <= 0)
        {
            return 0;
        }

        return -Math.Max(1, weight / 3);
    }

    private static int BestCountryTier(Release release, IReadOnlyList<string> preferredCountries)
    {
        var best = ReleaseCountryTier(release.Country, preferredCountries);
        foreach (var country in EnumerateEventCountries(release))
        {
            var tier = ReleaseCountryTier(country, preferredCountries);
            if (tier < best)
            {
                best = tier;
            }
        }

        return best;
    }

    private static string? FirstPreferredEventCountry(Release release, MusicBrainzPreferences preferences)
    {
        var eventCountries = EnumerateEventCountries(release).ToList();
        foreach (var preferred in preferences.PreferredCountries)
        {
            if (eventCountries.Any(country => string.Equals(country, preferred, StringComparison.OrdinalIgnoreCase)))
            {
                return preferred;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateEventCountries(Release release)
    {
        if (release.ReleaseEvents is not { Count: > 0 })
        {
            yield break;
        }

        foreach (var releaseEvent in release.ReleaseEvents)
        {
            var code = releaseEvent.Country;
            if (string.IsNullOrWhiteSpace(code))
            {
                code = releaseEvent.Area?.Iso3166_1Codes?.FirstOrDefault();
            }

            if (!string.IsNullOrWhiteSpace(code))
            {
                yield return code.Trim();
            }
        }
    }

    private static int ReleaseCountryTier(string? releaseCountry, IReadOnlyList<string> preferredCountries)
    {
        for (var index = 0; index < preferredCountries.Count; index++)
        {
            if (string.Equals(preferredCountries[index], releaseCountry, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        // Other/unknown countries compete only after every listed country.
        return preferredCountries.Count;
    }

    private static int ScoreFormatRank(List<ReleaseMedia> media, IReadOnlyList<string> preferredFormats)
    {
        if (preferredFormats.Count == 0)
        {
            return 0;
        }

        var formats = media
            .Select(m => m.Format)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (formats.Count == 0)
        {
            return -1;
        }

        var best = int.MinValue;
        foreach (var format in formats)
        {
            var score = -1;
            for (var index = 0; index < preferredFormats.Count; index++)
            {
                if (string.Equals(preferredFormats[index], format, StringComparison.OrdinalIgnoreCase))
                {
                    score = (preferredFormats.Count - index) * 2;
                    break;
                }
            }

            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    internal static bool IsVariantCompatible(string? sourceTitle, string? candidateTitle)
    {
        var sourceMarkers = ExtractVariantMarkers(sourceTitle);
        var candidateMarkers = ExtractVariantMarkers(candidateTitle);
        return sourceMarkers.SetEquals(candidateMarkers);
    }

    private static bool IsCandidateCompatibleWithSource(
        AutoTagAudioInfo info,
        MusicBrainzTrack track,
        AutoTagMatchingConfig config,
        MusicBrainzMatchConfig? providerConfig)
    {
        if (!IsTitleCompatibleWithSource(info, track, config))
        {
            return false;
        }

        var artistFloor = Math.Clamp(providerConfig?.MinStrictness ?? 65, 0, 100) / 100.0;
        var sourceArtists = info.Artists.Count > 0
            ? info.Artists
            : string.IsNullOrWhiteSpace(info.Artist) ? [] : new List<string> { info.Artist };
        var candidateArtists = track.Artists.Count > 0 ? track.Artists : track.AlbumArtists;
        if (sourceArtists.Count > 0
            && candidateArtists.Count > 0
            && !OneTaggerMatching.MatchArtist(sourceArtists, candidateArtists, Math.Clamp(config.Strictness, artistFloor, Math.Max(0.98d, artistFloor))))
        {
            return false;
        }

        var configuredDurationFloor = Math.Clamp(providerConfig?.MinDurationDifferenceSeconds ?? 45, 0, 300);
        if (info.DurationSeconds is > 0
            && track.Duration > TimeSpan.Zero
            && Math.Abs(info.DurationSeconds.Value - (int)Math.Round(track.Duration.TotalSeconds)) > Math.Max(config.MaxDurationDifferenceSeconds, configuredDurationFloor))
        {
            return false;
        }

        return true;
    }

    private static bool HasCompatibleTitleIdentity(
        string sourceTitle,
        string candidateTitle,
        AutoTagMatchingConfig config)
    {
        _ = config;
        return TrackTitleMatcher.HasCompatibleTitleIdentity(sourceTitle, candidateTitle);
    }

    /// <summary>
    /// Variant markers are extracted from the whole title, not only from parenthesized
    /// segments: forms like "Song - Live at Wembley" (unparenthesized, non-trailing)
    /// previously escaped the gate and let MB adopt variant-titled recordings.
    /// </summary>
    private static HashSet<string> ExtractVariantMarkers(string? title)
    {
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(title))
        {
            return markers;
        }

        foreach (var (key, pattern) in VariantPatterns)
        {
            if (pattern.IsMatch(title))
            {
                markers.Add(key);
            }
        }

        return markers;
    }

    private static string? ReadOtherValue(List<(string Key, List<string> Values)> other, string key)
    {
        var index = other.FindIndex(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        return index < 0
            ? null
            : other[index].Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static void SetOtherValue(List<(string Key, List<string> Values)> other, string key, string? value)
    {
        var index = other.FindIndex(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(value))
        {
            if (index >= 0)
            {
                other.RemoveAt(index);
            }

            return;
        }

        var normalized = new List<string> { value.Trim() };
        if (index < 0)
        {
            other.Add((key, normalized));
            return;
        }

        other[index] = (other[index].Key, normalized);
    }

    private static void AddOtherValue(List<(string Key, List<string> Values)> other, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddOtherValues(other, key, [value]);
    }

    private static void AddOtherValues(List<(string Key, List<string> Values)> other, string key, IEnumerable<string?>? values)
    {
        if (values == null)
        {
            return;
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return;
        }

        var index = other.FindIndex(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            other.Add((key, normalized));
            return;
        }

        var existing = other[index].Values.ToList();
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        existing.AddRange(normalized.Where(seen.Add));
        other[index] = (other[index].Key, existing);
    }

    private static Dictionary<string, List<string>> BuildOtherDictionary(MusicBrainzTrack track)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in track.Other)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Values.Count == 0)
            {
                continue;
            }

            if (!result.TryGetValue(item.Key, out var values))
            {
                result[item.Key] = item.Values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                continue;
            }

            var seen = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            values.AddRange(item.Values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Where(seen.Add));
        }

        return result;
    }

    private static Regex CreateVariantRegex(string pattern)
        => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    private static List<string> GetRecordingIds(AutoTagAudioInfo info)
    {
        var result = new List<string>();
        var keys = new[]
        {
            "MUSICBRAINZ_RECORDING_ID",
            "MUSICBRAINZ_RECORDINGID",
            "MUSICBRAINZ_TRACK_ID",
            "RECORDINGID"
        };

        foreach (var key in keys)
        {
            if (!info.Tags.TryGetValue(key, out var values) || values == null)
            {
                continue;
            }

            foreach (var normalized in values
                .Select(static value => value?.Trim())
                .Where(static normalized => !string.IsNullOrWhiteSpace(normalized))
                .Where(normalized => Guid.TryParse(normalized, out _) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase)))
            {
                result.Add(normalized!);
            }
        }

        return result;
    }

    private static MusicBrainzMatchConfig NormalizeConfig(MusicBrainzMatchConfig config)
    {
        var resolved = config ?? new MusicBrainzMatchConfig();
        if (resolved.SearchLimit < 5)
        {
            resolved.SearchLimit = 5;
        }
        else if (resolved.SearchLimit > 100)
        {
            resolved.SearchLimit = 100;
        }

        resolved.OfficialWeight = ClampWeight(resolved.OfficialWeight, 0, 30);
        resolved.CompilationPenaltyWeight = ClampWeight(resolved.CompilationPenaltyWeight, 0, 40);
        resolved.PrimaryTypeWeight = ClampWeight(resolved.PrimaryTypeWeight, 0, 30);
        resolved.CountryWeight = ClampWeight(resolved.CountryWeight, 0, 20);
        resolved.FormatWeight = ClampWeight(resolved.FormatWeight, 0, 20);
        resolved.YearWeight = ClampWeight(resolved.YearWeight, 0, 10);

        return resolved;
    }

    private static int ClampWeight(int value, int min, int max)
    {
        return Math.Min(max, Math.Max(min, value));
    }

    private static AutoTagTrack ToAutoTagTrack(MusicBrainzTrack track)
    {
        return new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            AlbumArtists = track.AlbumArtists.ToList(),
            Album = track.Album,
            Url = string.IsNullOrWhiteSpace(track.Url) ? null : track.Url,
            TrackId = track.TrackId,
            ReleaseId = track.ReleaseId,
            RecordingId = track.RecordingId,
            ArtistId = track.ArtistId,
            ArtistIds = track.ArtistIds.ToList(),
            AlbumArtistId = track.AlbumArtistId,
            AlbumArtistIds = track.AlbumArtistIds.ToList(),
            MediumTrackId = string.IsNullOrWhiteSpace(track.MediumTrackId) ? null : track.MediumTrackId,
            ReleaseGroupId = track.ReleaseGroupId,
            AlbumId = track.AlbumId,
            ReleaseStatus = track.ReleaseStatus,
            ReleaseCountry = track.ReleaseCountry,
            Barcode = track.Barcode,
            Media = track.Media.ToList(),
            Duration = track.Duration,
            TrackNumber = track.TrackNumber,
            TrackTotal = track.TrackTotal,
            ReleaseType = AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal),
            DiscNumber = track.DiscNumber,
            DiscTotal = track.DiscTotal,
            Isrc = track.Isrc,
            Isrcs = track.Isrcs.ToList(),
            Label = track.Label,
            Labels = track.Labels.ToList(),
            CatalogNumber = track.CatalogNumber,
            CatalogNumbers = track.CatalogNumbers.ToList(),
            Genres = track.Genres.ToList(),
            Art = track.Art,
            ReleaseDate = track.ReleaseDate,
            Lyricist = string.IsNullOrWhiteSpace(track.Lyricist) ? null : track.Lyricist,
            Remixers = track.Remixers.ToList(),
            Other = BuildOtherDictionary(track)
        };
    }

    private sealed record MatchCandidate(double Accuracy, MusicBrainzTrack Track);

    private sealed class MusicBrainzPreferences
    {
        private MusicBrainzPreferences()
        {
        }

        public bool PreferOfficial { get; init; }
        public bool ExcludeCompilations { get; init; }
        public bool PreferReleaseYear { get; init; }
        public string? PreferredPrimaryType { get; init; }
        public IReadOnlyList<string> PreferredCountries { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> PreferredFormats { get; init; } = Array.Empty<string>();
        public bool UseAliases { get; init; }
        public IReadOnlyList<string> PreferredLocales { get; init; } = Array.Empty<string>();
        public int OfficialWeight { get; init; }
        public int CompilationPenaltyWeight { get; init; }
        public int PrimaryTypeWeight { get; init; }
        public int FormatWeight { get; init; }
        public int YearWeight { get; init; }

        public static MusicBrainzPreferences FromConfig(MusicBrainzMatchConfig config)
        {
            var preferredType = string.IsNullOrWhiteSpace(config.PreferredPrimaryType)
                ? null
                : config.PreferredPrimaryType.Trim();
            if (string.Equals(preferredType, "Any", StringComparison.OrdinalIgnoreCase))
            {
                preferredType = null;
            }

            return new MusicBrainzPreferences
            {
                PreferOfficial = config.PreferOfficial,
                ExcludeCompilations = config.ExcludeCompilations,
                PreferReleaseYear = config.PreferReleaseYear,
                PreferredPrimaryType = preferredType,
                PreferredCountries = ParseCsv(string.IsNullOrWhiteSpace(config.PreferredReleaseCountries)
                    ? "US"
                    : config.PreferredReleaseCountries),
                PreferredFormats = ParseCsv(config.PreferredMediaFormats),
                UseAliases = config.UseAliases,
                PreferredLocales = ParseCsv(config.PreferredLocales),
                OfficialWeight = config.OfficialWeight,
                CompilationPenaltyWeight = config.CompilationPenaltyWeight,
                PrimaryTypeWeight = config.PrimaryTypeWeight,
                FormatWeight = config.FormatWeight,
                YearWeight = config.YearWeight
            };
        }

        private static IReadOnlyList<string> ParseCsv(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
