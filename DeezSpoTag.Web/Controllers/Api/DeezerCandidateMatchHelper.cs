using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using Newtonsoft.Json.Linq;

namespace DeezSpoTag.Web.Controllers.Api;

internal sealed record DeezerCandidateValidationOptions(
    double MinimumArtistScore,
    bool RejectDerivativeArtistName,
    bool ApplyVeryLowAlbumGuard,
    string FailureLogMessage);

internal sealed record DeezerFallbackSearchOptions(
    bool ExcludeDerivativeArtistCandidates,
    bool PreferBestAlbumMatch,
    string SearchFailureLogMessage);

internal sealed record DeezerCandidateSource(
    string? SourceTitle,
    string? SourceArtist,
    string? SourceAlbum,
    string? SourceIsrc,
    int? SourceDurationMs);

internal sealed record DeezerCandidateValidationHandlers(
    Func<string, CancellationToken, Task<(bool fetched, ApiTrack? track)>> TryGetValidationCandidateAsync,
    Func<string?, string?, string?, bool> SourceAllowsDerivative,
    Func<ApiTrack, bool> IsDerivativeCandidate,
    Func<string?, bool> IsDerivativeArtistName);

internal sealed record DeezerFallbackSearchHandlers(
    Func<string, CancellationToken, Task<bool>> IsPlausibleCandidateAsync,
    Func<string, string?, CancellationToken, Task<double>> GetAlbumMatchScoreAsync,
    Func<string, CancellationToken, Task<(bool fetched, ApiTrack? track)>> TryGetValidationCandidateAsync,
    Func<string?, string?, string?, bool> SourceAllowsDerivative,
    Func<string?, bool> IsDerivativeArtistName);

internal static class DeezerCandidateMatchHelper
{
    private const string CandidateValidationFailureTemplate = "{FailureLogMessage}. DeezerId: {DeezerId}";
    private const string CandidateSearchFailureTemplate = "{FailureLogMessage}. Query: {Query}";
    private const string CandidateLoadFailureTemplate = "{FailureLogMessage}. DeezerId: {DeezerId}";

    internal static async Task<bool> IsPlausibleCandidateAsync(
        string deezerId,
        DeezerCandidateSource source,
        DeezerCandidateValidationHandlers handlers,
        ILogger logger,
        DeezerCandidateValidationOptions options,
        CancellationToken cancellationToken)
    {
        var hasValidationInputs =
            !string.IsNullOrWhiteSpace(source.SourceTitle)
            || !string.IsNullOrWhiteSpace(source.SourceArtist)
            || !string.IsNullOrWhiteSpace(source.SourceAlbum)
            || !string.IsNullOrWhiteSpace(source.SourceIsrc)
            || source.SourceDurationMs is > 0;
        if (!hasValidationInputs)
        {
            return true;
        }

        try
        {
            var (fetched, candidate) = await handlers.TryGetValidationCandidateAsync(deezerId, cancellationToken);
            if (!fetched || candidate == null)
            {
                return false;
            }

            var allowsDerivative = handlers.SourceAllowsDerivative(source.SourceTitle, source.SourceArtist, source.SourceAlbum);
            if (RejectDerivativeCandidate(allowsDerivative, candidate, handlers.IsDerivativeCandidate))
            {
                return false;
            }

            if (RejectDerivativeArtistNameCandidate(options, allowsDerivative, candidate, handlers.IsDerivativeArtistName))
            {
                return false;
            }

            var scoreContext = BuildCandidateScoreContext(source.SourceTitle, source.SourceArtist, candidate);
            if (RejectByTitle(scoreContext))
            {
                return false;
            }

            if (RejectByArtist(scoreContext, options.MinimumArtistScore))
            {
                return false;
            }

            if (RejectByDuration(source.SourceDurationMs, candidate.Duration, scoreContext.EffectiveTitleScore))
            {
                return false;
            }

            if (RejectByAlbum(source.SourceAlbum, candidate.Album?.Title, scoreContext, options.ApplyVeryLowAlbumGuard))
            {
                return false;
            }

            if (RejectByIsrc(source.SourceIsrc, candidate.Isrc))
            {
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, CandidateValidationFailureTemplate, options.FailureLogMessage, deezerId);
            }
            return false;
        }
    }

    private static bool RejectDerivativeCandidate(
        bool allowsDerivative,
        ApiTrack candidate,
        Func<ApiTrack, bool> isDerivativeCandidate)
    {
        return !allowsDerivative && isDerivativeCandidate(candidate);
    }

    private static bool RejectDerivativeArtistNameCandidate(
        DeezerCandidateValidationOptions options,
        bool allowsDerivative,
        ApiTrack candidate,
        Func<string?, bool> isDerivativeArtistName)
    {
        return options.RejectDerivativeArtistName
            && !allowsDerivative
            && isDerivativeArtistName(candidate.Artist?.Name);
    }

    private static CandidateScoreContext BuildCandidateScoreContext(
        string? sourceTitle,
        string? sourceArtist,
        ApiTrack candidate)
    {
        var sourceTitleNorm = ResolveDeezerApiController.NormalizeGuardTitle(sourceTitle);
        var candidateTitleNorm = ResolveDeezerApiController.NormalizeGuardTitle($"{candidate.Title} {candidate.TitleVersion}".Trim());
        var titleScore = ResolveDeezerApiController.ComputeSimilarity(sourceTitleNorm, candidateTitleNorm);
        var relaxedTitleScore = ResolveDeezerApiController.ComputeSimilarity(
            ResolveDeezerApiController.NormalizeRelaxedTitleToken(sourceTitleNorm),
            ResolveDeezerApiController.NormalizeRelaxedTitleToken(candidateTitleNorm));
        var sourceArtistNorm = ResolveDeezerApiController.NormalizeGuardArtist(sourceArtist);
        var artistScore = ResolveDeezerApiController.GetBestArtistScore(sourceArtistNorm, candidate);
        var featuredArtistScore = ResolveDeezerApiController.GetBestFeaturedArtistScore(sourceTitle, candidate);
        return new CandidateScoreContext(
            sourceTitleNorm,
            sourceArtistNorm,
            Math.Max(titleScore, relaxedTitleScore),
            Math.Max(artistScore, featuredArtistScore));
    }

    private static bool RejectByTitle(CandidateScoreContext context)
    {
        return !string.IsNullOrWhiteSpace(context.SourceTitleNorm)
            && context.EffectiveTitleScore < 0.62d;
    }

    private static bool RejectByArtist(CandidateScoreContext context, double minimumArtistScore)
    {
        if (string.IsNullOrWhiteSpace(context.SourceArtistNorm))
        {
            return false;
        }

        if (context.EffectiveArtistScore < minimumArtistScore)
        {
            return true;
        }

        var sourceTitleTokenCount = context.SourceTitleNorm
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
        var hasShortOrGenericTitle = sourceTitleTokenCount <= 1 || context.SourceTitleNorm.Length <= 6;
        if (hasShortOrGenericTitle && context.EffectiveArtistScore < 0.52d)
        {
            return true;
        }

        return context.EffectiveArtistScore < 0.46d && context.EffectiveTitleScore < 0.90d;
    }

    private static bool RejectByDuration(int? sourceDurationMs, int candidateDuration, double effectiveTitleScore)
    {
        if (sourceDurationMs is not > 0 || candidateDuration <= 0)
        {
            return false;
        }

        var sourceSeconds = (int)Math.Round(sourceDurationMs.Value / 1000d);
        var durationDiff = Math.Abs(sourceSeconds - candidateDuration);
        return durationDiff > 24 && effectiveTitleScore < 0.90d;
    }

    private static bool RejectByAlbum(
        string? sourceAlbum,
        string? candidateAlbum,
        CandidateScoreContext context,
        bool applyVeryLowAlbumGuard)
    {
        var sourceAlbumNorm = ResolveDeezerApiController.NormalizeGuardToken(sourceAlbum);
        var candidateAlbumNorm = ResolveDeezerApiController.NormalizeGuardToken(candidateAlbum);
        if (string.IsNullOrWhiteSpace(sourceAlbumNorm) || string.IsNullOrWhiteSpace(candidateAlbumNorm))
        {
            return false;
        }

        var albumScore = ResolveDeezerApiController.ComputeSimilarity(sourceAlbumNorm, candidateAlbumNorm);
        if (applyVeryLowAlbumGuard && albumScore < 0.25d && context.EffectiveArtistScore < 0.90d)
        {
            return true;
        }

        return albumScore < 0.35d
            && context.EffectiveArtistScore < 0.60d
            && context.EffectiveTitleScore < 0.86d;
    }

    private static bool RejectByIsrc(string? sourceIsrc, string? candidateIsrc)
    {
        var normalizedSourceIsrc = ResolveDeezerApiController.NormalizeIsrc(sourceIsrc);
        var normalizedCandidateIsrc = ResolveDeezerApiController.NormalizeIsrc(candidateIsrc);
        return !string.IsNullOrWhiteSpace(normalizedSourceIsrc)
            && !string.IsNullOrWhiteSpace(normalizedCandidateIsrc)
            && !string.Equals(normalizedSourceIsrc, normalizedCandidateIsrc, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<string?> ResolveFromSearchFallbackAsync(
        DeezerClient deezerClient,
        DeezerCandidateSource source,
        DeezerFallbackSearchHandlers handlers,
        ILogger logger,
        DeezerFallbackSearchOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.SourceTitle))
        {
            return null;
        }

        foreach (var query in BuildSearchQueriesForSource(source))
        {
            var result = await TrySearchTrackAsync(deezerClient, query, logger, options.SearchFailureLogMessage);
            var preferredCandidate = await ResolvePreferredFallbackCandidateAsync(
                result,
                source,
                handlers,
                options,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(preferredCandidate))
            {
                return preferredCandidate;
            }
        }

        return null;
    }

    private static List<string> BuildSearchQueriesForSource(DeezerCandidateSource source)
    {
        var cleanedTitle = ResolveDeezerApiController.NormalizeGuardTitle(source.SourceTitle);
        var relaxedTitle = ResolveDeezerApiController.NormalizeFallbackSearchTitle(cleanedTitle);
        var leadTitle = ResolveDeezerApiController.ExtractLeadFallbackTitle(source.SourceTitle);
        return BuildSearchQueries(source.SourceTitle, source.SourceArtist, cleanedTitle, relaxedTitle, leadTitle);
    }

    private static async Task<DeezerSearchResult?> TrySearchTrackAsync(
        DeezerClient deezerClient,
        string query,
        ILogger logger,
        string failureLogMessage)
    {
        try
        {
            return await deezerClient.SearchTrackAsync(query, new ApiOptions
            {
                Limit = 8,
                Strict = false
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, CandidateSearchFailureTemplate, failureLogMessage, query);
            }
            return null;
        }
    }

    private static async Task<string?> ResolvePreferredFallbackCandidateAsync(
        DeezerSearchResult? result,
        DeezerCandidateSource source,
        DeezerFallbackSearchHandlers handlers,
        DeezerFallbackSearchOptions options,
        CancellationToken cancellationToken)
    {
        if (result?.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        string? firstPlausible = null;
        double bestPlausibleAlbumScore = -1d;
        string? bestAlbumCandidate = null;
        var allowsDerivative = handlers.SourceAllowsDerivative(source.SourceTitle, source.SourceArtist, source.SourceAlbum);

        foreach (var candidateId in ResolveDeezerApiController.EnumerateSearchResultTrackIds(result))
        {
            if (!await handlers.IsPlausibleCandidateAsync(candidateId, cancellationToken))
            {
                continue;
            }

            if (await ShouldSkipDerivativeArtistCandidateAsync(
                    candidateId,
                    allowsDerivative,
                    handlers,
                    options,
                    cancellationToken))
            {
                continue;
            }

            var albumScore = await handlers.GetAlbumMatchScoreAsync(candidateId, source.SourceAlbum, cancellationToken);
            if (albumScore >= 0.55d)
            {
                return candidateId;
            }

            if (options.PreferBestAlbumMatch && albumScore > bestPlausibleAlbumScore)
            {
                bestPlausibleAlbumScore = albumScore;
                bestAlbumCandidate = candidateId;
            }

            firstPlausible ??= candidateId;
        }

        return options.PreferBestAlbumMatch
            ? bestAlbumCandidate ?? firstPlausible
            : firstPlausible;
    }

    private static async Task<bool> ShouldSkipDerivativeArtistCandidateAsync(
        string candidateId,
        bool allowsDerivative,
        DeezerFallbackSearchHandlers handlers,
        DeezerFallbackSearchOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.ExcludeDerivativeArtistCandidates || allowsDerivative)
        {
            return false;
        }

        var (fetched, candidateTrack) = await handlers.TryGetValidationCandidateAsync(candidateId, cancellationToken);
        return fetched
            && candidateTrack != null
            && handlers.IsDerivativeArtistName(candidateTrack.Artist?.Name);
    }

    internal static async Task<double> GetAlbumMatchScoreAsync(
        string deezerId,
        string? sourceAlbum,
        Func<string, CancellationToken, Task<(bool fetched, ApiTrack? track)>> tryGetValidationCandidateAsync,
        CancellationToken cancellationToken)
    {
        var sourceAlbumNorm = ResolveDeezerApiController.NormalizeGuardToken(sourceAlbum);
        if (string.IsNullOrWhiteSpace(sourceAlbumNorm))
        {
            return 0d;
        }

        var (fetched, candidate) = await tryGetValidationCandidateAsync(deezerId, cancellationToken);
        if (!fetched || candidate == null)
        {
            return 0d;
        }

        var candidateAlbumNorm = ResolveDeezerApiController.NormalizeGuardToken(candidate.Album?.Title);
        if (string.IsNullOrWhiteSpace(candidateAlbumNorm))
        {
            return 0d;
        }

        return ResolveDeezerApiController.ComputeSimilarity(sourceAlbumNorm, candidateAlbumNorm);
    }

    internal static async Task<(bool fetched, ApiTrack? track)> TryGetValidationCandidateAsync(
        DeezerClient deezerClient,
        ILogger logger,
        string deezerId,
        string failureLogMessage,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidate = await deezerClient.GetTrack(deezerId);
                return (true, candidate);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var gatewayCandidate = await TryGetGatewayValidationCandidateAsync(deezerClient, deezerId, cancellationToken);
                if (gatewayCandidate != null)
                {
                    return (true, gatewayCandidate);
                }

                if (attempt == maxAttempts)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(ex, CandidateLoadFailureTemplate, failureLogMessage, deezerId);
                    }
                    return (false, null);
                }

                await Task.Delay(Math.Min(1000, attempt * 250), cancellationToken);
            }
        }

        return (false, null);
    }

    private static List<string> BuildSearchQueries(
        string? sourceTitle,
        string? sourceArtist,
        string cleanedTitle,
        string relaxedTitle,
        string leadTitle)
    {
        var queries = new List<string>();

        void AddQuery(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            var normalized = query.Trim();
            if (normalized.Length < 2)
            {
                return;
            }

            if (!queries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(normalized);
            }
        }

        AddQuery($"{sourceArtist} {sourceTitle}");
        AddQuery($"{sourceArtist} {cleanedTitle}");
        AddQuery($"{sourceArtist} {relaxedTitle}");
        AddQuery($"{sourceArtist} {leadTitle}");
        AddQuery(sourceTitle);
        AddQuery(cleanedTitle);
        AddQuery(relaxedTitle);
        AddQuery(leadTitle);
        return queries;
    }

    private static async Task<ApiTrack?> TryGetGatewayValidationCandidateAsync(
        DeezerClient deezerClient,
        string deezerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var trackData = await deezerClient.GetTrackAsync(deezerId, cancellationToken);
            if (trackData == null)
            {
                return null;
            }

            return new ApiTrack
            {
                Id = deezerId,
                Title = TryGetStringValue(trackData, "SNG_TITLE") ?? string.Empty,
                Duration = TryGetIntValue(trackData, "DURATION"),
                Isrc = TryGetStringValue(trackData, "ISRC") ?? string.Empty,
                Artist = new ApiArtist
                {
                    Name = TryGetStringValue(trackData, "ART_NAME") ?? string.Empty
                },
                Album = new ApiAlbum
                {
                    Title = TryGetStringValue(trackData, "ALB_TITLE") ?? string.Empty
                }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetStringValue(Dictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw == null)
        {
            return null;
        }

        return raw.ToString()?.Trim();
    }

    private static int TryGetIntValue(Dictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw == null)
        {
            return 0;
        }

        if (raw is int intValue)
        {
            return intValue;
        }

        if (raw is long longValue)
        {
            return (int)longValue;
        }

        return int.TryParse(raw.ToString(), out var parsed) ? parsed : 0;
    }

    private sealed record CandidateScoreContext(
        string SourceTitleNorm,
        string SourceArtistNorm,
        double EffectiveTitleScore,
        double EffectiveArtistScore);
}
