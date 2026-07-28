using System.Text.RegularExpressions;
using System.Text.Json;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Resolves the one local library track used by watchlist sync and dedupe when
/// the indexed metadata matcher finds more than one equally credible file.
/// </summary>
public sealed class WatchlistLocalIdentityResolver : ILocalTrackAmbiguityResolver
{
    private static readonly TimeSpan RecognitionCacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex SpotifyTrackRegex =
        new(@"(?:open\.spotify\.com/track/|spotify:track:)(?<id>[A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex AppleTrackRegex =
        new(@"(?:[?&]i=|/song/[^/]+/)(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex DeezerTrackRegex =
        new(@"(?:deezer\.com/(?:[a-z]{2}/)?track/)(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);

    private readonly LibraryRepository _repository;
    private readonly ShazamRecognitionService _shazam;
    private readonly AutoTagProfileResolutionService _profileResolution;
    private readonly AutoTagLibraryOrganizer _libraryOrganizer;
    private readonly ILogger<WatchlistLocalIdentityResolver> _logger;

    public WatchlistLocalIdentityResolver(
        LibraryRepository repository,
        ShazamRecognitionService shazam,
        AutoTagProfileResolutionService profileResolution,
        AutoTagLibraryOrganizer libraryOrganizer,
        ILogger<WatchlistLocalIdentityResolver> logger)
    {
        _repository = repository;
        _shazam = shazam;
        _profileResolution = profileResolution;
        _libraryOrganizer = libraryOrganizer;
        _logger = logger;
    }

    public async Task<LibraryRepository.LocalTrackIdentityResult> ResolveAsync(
        LibraryRepository.LibraryExistenceInput input,
        LibraryRepository.LocalTrackIdentityResult initial,
        CancellationToken cancellationToken)
    {
        if (!initial.IsAmbiguous || initial.CandidateTrackIds.Count < 2)
        {
            return initial;
        }

        var candidates = (await _repository.GetLocalTrackResolutionCandidatesAsync(
                initial.CandidateTrackIds,
                cancellationToken))
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.FilePath) && File.Exists(candidate.FilePath))
            .ToList();
        if (candidates.Count == 0)
        {
            return initial;
        }
        if (candidates.Count == 1)
        {
            return new LibraryRepository.LocalTrackIdentityResult(
                candidates[0].TrackId,
                "existing_file",
                "Selected the only competing local identity whose indexed audio file still exists.",
                initial.CandidateTrackIds,
                candidates[0].QualityRank);
        }

        var profileState = await _profileResolution.LoadNormalizedStateAsync(cancellationToken: cancellationToken);
        var cached = await _repository.GetShazamTrackCacheByTrackIdsAsync(
            candidates.Select(static candidate => candidate.TrackId).ToArray(),
            cancellationToken);
        var ranked = new List<RankedCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = await ResolveEvidenceAsync(candidate, cached.GetValueOrDefault(candidate.TrackId), cancellationToken);
            ranked.Add(Rank(input, candidate, evidence, ResolveProfileTagScore(profileState, candidate)));
        }
        foreach (var recordingGroup in ranked
                     .Where(static candidate => !string.IsNullOrWhiteSpace(ResolveRecordingIdentity(candidate)))
                     .GroupBy(ResolveRecordingIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var sharedIdentityScore = recordingGroup.Max(static candidate => candidate.SourceIdentityScore);
            foreach (var member in recordingGroup.ToArray())
            {
                ranked[ranked.IndexOf(member)] = member with { SourceIdentityScore = sharedIdentityScore };
            }
        }

        var eligible = ranked
            .Where(static candidate => !candidate.ConflictingRecording)
            .OrderByDescending(static candidate => candidate.SourceIdentityScore)
            .ThenByDescending(static candidate => candidate.VariantScore)
            .ThenByDescending(static candidate => candidate.ReleaseScore)
            .ThenByDescending(static candidate => candidate.Candidate.QualityRank)
            .ThenByDescending(static candidate => candidate.ProfileTagScore)
            .ThenByDescending(static candidate => candidate.Candidate.MetadataRichness)
            .ThenBy(static candidate => candidate.Candidate.TrackId)
            .ToList();
        if (eligible.Count == 0)
        {
            return initial with
            {
                Reason = "Shazam identified the competing local files as different recordings; manual review is required."
            };
        }

        var winner = eligible[0];
        await QuarantineConfirmedDuplicatesAsync(winner, eligible.Skip(1), cancellationToken);
        return new LibraryRepository.LocalTrackIdentityResult(
            winner.Candidate.TrackId,
            "shazam_ranked",
            BuildReason(winner),
            candidates.Select(static candidate => candidate.TrackId).ToArray(),
            winner.Candidate.QualityRank);
    }

    private async Task QuarantineConfirmedDuplicatesAsync(
        RankedCandidate winner,
        IEnumerable<RankedCandidate> remaining,
        CancellationToken cancellationToken)
    {
        var winnerIdentity = ResolveRecordingIdentity(winner);
        if (string.IsNullOrWhiteSpace(winnerIdentity))
        {
            return;
        }

        var profileState = await _profileResolution.LoadNormalizedStateAsync(cancellationToken: cancellationToken);
        foreach (var duplicate in remaining)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(ResolveRecordingIdentity(duplicate), winnerIdentity, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Normalize(duplicate.Candidate.Album), Normalize(winner.Candidate.Album), StringComparison.OrdinalIgnoreCase)
                || string.Equals(duplicate.Candidate.FilePath, winner.Candidate.FilePath, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(duplicate.Candidate.FilePath)
                || !File.Exists(duplicate.Candidate.FilePath))
            {
                continue;
            }

            try
            {
                var sourcePath = duplicate.Candidate.FilePath;
                var destinationPath = await _libraryOrganizer.QuarantineConfirmedDuplicateAsync(
                    duplicate.Candidate.RootPath,
                    sourcePath,
                    ResolveDuplicatesFolderName(profileState, duplicate.Candidate.FolderId),
                    cancellationToken);
                await _repository.AddLocalDuplicateResolutionEventAsync(
                    winner.Candidate.TrackId,
                    duplicate.Candidate.TrackId,
                    sourcePath,
                    destinationPath,
                    destinationPath == null ? "not_moved" : "moved",
                    destinationPath == null ? "The duplicate file was no longer available for quarantine." : null,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                await _repository.AddLocalDuplicateResolutionEventAsync(
                    winner.Candidate.TrackId,
                    duplicate.Candidate.TrackId,
                    duplicate.Candidate.FilePath,
                    null,
                    "failed",
                    ex.Message,
                    CancellationToken.None);
                _logger.LogWarning(
                    ex,
                    "Confirmed duplicate local track {TrackId} could not be moved to the configured Enhancement duplicate folder.",
                    duplicate.Candidate.TrackId);
            }
        }
    }

    private static string ResolveRecordingIdentity(RankedCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Evidence?.Isrc))
        {
            return $"isrc:{Normalize(candidate.Evidence.Isrc)}";
        }
        if (!string.IsNullOrWhiteSpace(candidate.Evidence?.ShazamTrackId))
        {
            return $"shazam:{Normalize(candidate.Evidence.ShazamTrackId)}";
        }
        return string.Empty;
    }

    private static string ResolveDuplicatesFolderName(
        AutoTagProfileResolutionService.ResolvedState state,
        long? folderId)
    {
        if (!folderId.HasValue)
        {
            return DuplicateCleanerService.DuplicatesFolderName;
        }

        var profile = AutoTagProfileResolutionService.ResolveFolderProfile(state, folderId.Value);
        if (profile?.AutoTag?.Data == null
            || !profile.AutoTag.Data.TryGetValue("enhancement", out var enhancement)
            || enhancement.ValueKind != JsonValueKind.Object
            || !enhancement.TryGetProperty("folderUniformity", out var folderUniformity)
            || folderUniformity.ValueKind != JsonValueKind.Object
            || !folderUniformity.TryGetProperty("duplicatesFolderName", out var folderName)
            || folderName.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(folderName.GetString()))
        {
            return DuplicateCleanerService.DuplicatesFolderName;
        }

        return folderName.GetString()!.Trim();
    }

    private async Task<RecognitionEvidence?> ResolveEvidenceAsync(
        LibraryRepository.LocalTrackResolutionCandidate candidate,
        ShazamTrackCacheDto? cache,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(candidate.FilePath);
        var cacheMatchesFile = CacheMatchesFile(cache, file);
        if (cacheMatchesFile
            && cache is { Status: "matched", ScannedAtUtc: not null }
            && cache.ScannedAtUtc.Value >= DateTimeOffset.UtcNow.Subtract(RecognitionCacheLifetime))
        {
            return new RecognitionEvidence(
                cache.ShazamTrackId,
                cache.Title,
                cache.Artist,
                cache.Isrc,
                cache.SpotifyId,
                cache.AppleId,
                cache.DeezerId,
                cache.Album,
                cache.ReleaseDate,
                cache.Explicit);
        }
        if (cacheMatchesFile
            && cache is { Status: "no_match", ScannedAtUtc: not null }
            && cache.ScannedAtUtc.Value >= DateTimeOffset.UtcNow.Subtract(RecognitionCacheLifetime))
        {
            return null;
        }

        if (!_shazam.IsAvailable || string.IsNullOrWhiteSpace(candidate.FilePath) || !File.Exists(candidate.FilePath))
        {
            return null;
        }

        try
        {
            var recognition = await _shazam.RecognizeAsync(candidate.FilePath, cancellationToken);
            var evidence = recognition == null ? null : BuildEvidence(recognition);
            await _repository.UpsertTrackShazamCacheAsync(
                new LibraryRepository.TrackShazamCacheUpsertInput(
                    candidate.TrackId,
                    recognition == null ? "no_match" : "matched",
                    recognition?.TrackId,
                    recognition?.Title,
                    recognition?.Artist,
                    recognition?.Isrc,
                    null,
                    DateTimeOffset.UtcNow,
                    recognition == null ? "No Shazam fingerprint match." : null,
                    file.FullName,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    evidence?.SpotifyId,
                    evidence?.AppleId,
                    evidence?.DeezerId,
                    evidence?.Album,
                    evidence?.ReleaseDate,
                    evidence?.Explicit),
                cancellationToken);
            return evidence;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Shazam review resolution failed for local track {TrackId}.", candidate.TrackId);
            return null;
        }
    }

    private static RecognitionEvidence BuildEvidence(ShazamRecognitionInfo recognition)
    {
        var deezerUrl = recognition.Tags
            .Where(static entry => entry.Key.Contains("deezer", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static entry => entry.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        return new RecognitionEvidence(
            recognition.TrackId,
            recognition.Title,
            recognition.Artist,
            recognition.Isrc,
            ExtractId(SpotifyTrackRegex, recognition.SpotifyUrl),
            ExtractId(AppleTrackRegex, recognition.AppleMusicUrl),
            ExtractId(DeezerTrackRegex, deezerUrl),
            recognition.Album,
            recognition.ReleaseDate,
            recognition.Explicit);
    }

    private static RankedCandidate Rank(
        LibraryRepository.LibraryExistenceInput input,
        LibraryRepository.LocalTrackResolutionCandidate candidate,
        RecognitionEvidence? evidence,
        int profileTagScore)
    {
        var normalizedSource = Normalize(input.Source);
        var requestedSourceId = Normalize(input.SourceId);
        candidate.SourceIds.TryGetValue(normalizedSource, out var indexedSourceId);
        var recognizedSourceId = normalizedSource switch
        {
            "spotify" => evidence?.SpotifyId,
            "apple" or "itunes" => evidence?.AppleId,
            "deezer" => evidence?.DeezerId,
            _ => null
        };

        var sourceIdentityScore = 0;
        if (!string.IsNullOrWhiteSpace(requestedSourceId)
            && string.Equals(Normalize(indexedSourceId), requestedSourceId, StringComparison.OrdinalIgnoreCase))
        {
            sourceIdentityScore = 300;
        }
        else if (!string.IsNullOrWhiteSpace(requestedSourceId)
                 && string.Equals(Normalize(recognizedSourceId), requestedSourceId, StringComparison.OrdinalIgnoreCase))
        {
            sourceIdentityScore = 250;
        }
        else if (!string.IsNullOrWhiteSpace(input.Isrc)
                 && string.Equals(Normalize(input.Isrc), Normalize(evidence?.Isrc ?? candidate.Isrc), StringComparison.OrdinalIgnoreCase))
        {
            sourceIdentityScore = 200;
        }
        else if (evidence != null
                 && TrackTitleMatcher.TitlesMatch(input.TrackTitle ?? string.Empty, evidence.Title ?? string.Empty)
                 && TrackTitleMatcher.ArtistsMatch(input.ArtistName ?? string.Empty, evidence.Artist ?? string.Empty))
        {
            sourceIdentityScore = 100;
        }

        var conflictingRecording = evidence != null
            && sourceIdentityScore == 0
            && (!TrackTitleMatcher.TitlesMatch(input.TrackTitle ?? string.Empty, evidence.Title ?? string.Empty)
                || !TrackTitleMatcher.ArtistsMatch(input.ArtistName ?? string.Empty, evidence.Artist ?? string.Empty));
        var variantScore = ScoreVariant(
            input.TrackTitle,
            evidence?.Title ?? candidate.Title,
            input.Explicit,
            evidence?.Explicit);
        var releaseScore = ScoreRelease(input.AlbumTitle, evidence?.Album ?? candidate.Album);
        return new RankedCandidate(
            candidate,
            sourceIdentityScore,
            variantScore,
            releaseScore,
            profileTagScore,
            conflictingRecording,
            evidence != null,
            evidence);
    }

    internal static int ScoreVariant(
        string? requestedTitle,
        string? candidateTitle,
        bool? requestedExplicit,
        bool? candidateExplicit)
    {
        var requestedMarkers = ExtractVariantMarkers(requestedTitle);
        var candidateMarkers = ExtractVariantMarkers(candidateTitle);
        if (!requestedMarkers.SetEquals(candidateMarkers))
        {
            return 0;
        }
        return requestedExplicit.HasValue
               && candidateExplicit.HasValue
               && requestedExplicit.Value != candidateExplicit.Value
            ? 0
            : 100;
    }

    internal static int ScoreRelease(string? requestedAlbum, string? candidateAlbum)
    {
        if (string.IsNullOrWhiteSpace(requestedAlbum))
        {
            return 0;
        }

        if (string.Equals(Normalize(requestedAlbum), Normalize(candidateAlbum), StringComparison.OrdinalIgnoreCase))
        {
            return 150;
        }

        var requestedEdition = ExtractEditionMarkers(requestedAlbum);
        var candidateEdition = ExtractEditionMarkers(candidateAlbum);
        return requestedEdition.SetEquals(candidateEdition)
               && TrackTitleMatcher.TitlesMatch(requestedAlbum, candidateAlbum ?? string.Empty)
            ? 100
            : 0;
    }

    private static HashSet<string> ExtractVariantMarkers(string? value)
    {
        var normalized = Normalize(value);
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in new[] { "live", "remix", "acoustic", "instrumental", "radio edit", "extended", "clean", "explicit", "remaster" })
        {
            if (normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(marker);
            }
        }
        return markers;
    }

    private static HashSet<string> ExtractEditionMarkers(string? value)
    {
        var normalized = Normalize(value);
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in new[] { "deluxe", "extended", "anniversary", "remaster", "compilation", "ep", "single" })
        {
            if (normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(marker);
            }
        }
        return markers;
    }

    private static string BuildReason(RankedCandidate winner)
        => winner.UsedShazam
            ? "Resolved competing local files using Shazam recording identity, release context, audio quality, and metadata completeness."
            : "Resolved equivalent local files using release context, audio quality, metadata completeness, and stable library order.";

    private static int ResolveProfileTagScore(
        AutoTagProfileResolutionService.ResolvedState state,
        LibraryRepository.LocalTrackResolutionCandidate candidate)
    {
        var profile = candidate.FolderId.HasValue
            ? AutoTagProfileResolutionService.ResolveFolderProfile(state, candidate.FolderId.Value)
            : state.DefaultProfile;
        var config = profile?.TagConfig;
        if (config == null)
        {
            return 0;
        }

        return ScorePopulatedProfileTags(config, candidate.PopulatedTags);
    }

    internal static int ScorePopulatedProfileTags(
        UnifiedTagConfig config,
        IReadOnlySet<string> populatedTags)
    {
        var score = 0;
        foreach (var property in typeof(UnifiedTagConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.PropertyType == typeof(TagSource)
                && (TagSource)(property.GetValue(config) ?? TagSource.None) != TagSource.None
                && populatedTags.Contains(property.Name))
            {
                score++;
            }
        }
        return score;
    }

    private static string? ExtractId(Regex regex, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var match = regex.Match(value);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    internal static (string? SpotifyId, string? AppleId, string? DeezerId) ExtractPlatformIds(
        string? spotifyUrl,
        string? appleUrl,
        string? deezerUrl)
        => (
            ExtractId(SpotifyTrackRegex, spotifyUrl),
            ExtractId(AppleTrackRegex, appleUrl),
            ExtractId(DeezerTrackRegex, deezerUrl));

    internal static bool CacheMatchesFile(ShazamTrackCacheDto? cache, FileInfo file)
    {
        if (cache == null || string.IsNullOrWhiteSpace(cache.FilePath))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(cache.FilePath), file.FullName, StringComparison.OrdinalIgnoreCase)
                   && cache.FileSize == file.Length
                   && cache.FileModifiedUtc.HasValue
                   && cache.FileModifiedUtc.Value.UtcDateTime == file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record RecognitionEvidence(
        string? ShazamTrackId,
        string? Title,
        string? Artist,
        string? Isrc,
        string? SpotifyId,
        string? AppleId,
        string? DeezerId,
        string? Album,
        string? ReleaseDate,
        bool? Explicit);

    private sealed record RankedCandidate(
        LibraryRepository.LocalTrackResolutionCandidate Candidate,
        int SourceIdentityScore,
        int VariantScore,
        int ReleaseScore,
        int ProfileTagScore,
        bool ConflictingRecording,
        bool UsedShazam,
        RecognitionEvidence? Evidence);
}
