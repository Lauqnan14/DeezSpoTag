using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Sonic;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// The surface the Vibe Analysis panel uses to drive Sonic Analysis.
///
/// <para>Everything here is about reporting and maintenance: how much of the
/// library is embedded, what is out of date, and how to bring it up to date. It
/// deliberately owns no selection behaviour, because a neighbour query belongs to
/// the similarity service and playlist building belongs to whatever is generating
/// the playlist.</para>
/// </summary>
public sealed class SonicAnalysisService
{
    private readonly LibraryRepository _repository;
    private readonly ISonicSimilarityService _similarity;
    private readonly ISonicSimilarityIndex _index;
    private readonly SonicAnalysisSettingsStore _settingsStore;
    private readonly TrackAnalysisBackgroundService _analysisService;
    private readonly ILogger<SonicAnalysisService> _logger;

    public SonicAnalysisService(
        LibraryRepository repository,
        ISonicSimilarityService similarity,
        ISonicSimilarityIndex index,
        SonicAnalysisSettingsStore settingsStore,
        TrackAnalysisBackgroundService analysisService,
        ILogger<SonicAnalysisService> logger)
    {
        _repository = repository;
        _similarity = similarity;
        _index = index;
        _settingsStore = settingsStore;
        _analysisService = analysisService;
        _logger = logger;
    }

    public Task<SonicAnalysisSettingsDto> GetSettingsAsync() => _settingsStore.LoadAsync();

    /// <summary>
    /// Applies new settings.
    ///
    /// Enabling Sonic also applies to the running analysis service so the flag
    /// reaches the analyzer subprocess without a restart; disabling it pauses
    /// nothing, because a vector already written stays valid and readable.
    /// </summary>
    public async Task<SonicAnalysisSettingsDto> SaveSettingsAsync(
        SonicAnalysisSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        var saved = await _settingsStore.SaveAsync(settings);
        await _analysisService.ApplySonicSettingsAsync(saved, cancellationToken);
        return saved;
    }

    /// <summary>Coverage and index state across every enabled library.</summary>
    public async Task<SonicAnalysisStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync();
        var identity = SonicModelIdentity.Current;
        var libraries = await _repository.GetEnabledLibraryScopesAsync(cancellationToken);

        var perLibrary = new List<SonicLibraryStatusDto>(libraries.Count);
        foreach (var (libraryId, name) in libraries)
        {
            var coverage = await _similarity.GetCoverageAsync(libraryId, cancellationToken);
            var stale = await _repository.CountStaleSonicEmbeddingsAsync(
                libraryId,
                identity.ModelId,
                identity.ModelVersion,
                identity.EmbeddingVersion,
                cancellationToken);

            perLibrary.Add(new SonicLibraryStatusDto
            {
                LibraryId = libraryId,
                LibraryName = name,
                TotalTracks = coverage.TotalTracks,
                TracksAnalyzed = coverage.TracksAnalyzed,
                TracksWithEmbedding = Math.Max(0, coverage.TracksWithEmbedding - stale),
                TracksUnavailable = coverage.TracksUnavailable,
                StaleEmbeddings = stale,
            });
        }

        var metrics = _index.CurrentMetrics;
        return new SonicAnalysisStatusDto
        {
            Enabled = settings.Enabled,
            ModelId = identity.ModelId,
            ModelVersion = identity.ModelVersion,
            EmbeddingVersion = identity.EmbeddingVersion,
            Dimensions = metrics?.Dimensions ?? 1280,
            ReanalyzeBatchSize = settings.ReanalyzeBatchSize,
            Libraries = perLibrary,
            IndexVectorCount = metrics?.VectorCount ?? 0,
            IndexMegabytes = metrics?.ApproximateMegabytes ?? 0d,
            IndexBuildMilliseconds = metrics?.BuildMilliseconds ?? 0,
        };
    }

    /// <summary>
    /// Rebuilds the similarity index for every library that has vectors.
    ///
    /// This only reloads vectors; it does not re-embed anything. Re-embedding is
    /// <see cref="ReanalyzeAsync"/>, and conflating the two would make a cheap
    /// maintenance action look like a full analysis run.
    /// </summary>
    public async Task<SonicAnalysisStatusDto> RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        var libraries = await _repository.GetEnabledLibraryScopesAsync(cancellationToken);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var vectors = 0;

        foreach (var (libraryId, _) in libraries)
        {
            var metrics = await _similarity.RebuildIndexAsync(libraryId, cancellationToken);
            vectors += metrics.VectorCount;
        }

        started.Stop();
        _logger.LogInformation(
            "Sonic similarity index rebuilt: {VectorCount} vectors across {LibraryCount} libraries in {ElapsedMs} ms.",
            vectors,
            libraries.Count,
            started.ElapsedMilliseconds);

        return await GetStatusAsync(cancellationToken);
    }

    /// <summary>
    /// Queues a bounded batch of tracks whose embedding is missing or stale.
    ///
    /// The batch is deliberately capped by the configured size. Sonic is a
    /// second inference pass per track, and a whole-library catch-up would
    /// monopolise the analysis queue.
    /// </summary>
    public async Task<SonicReanalyzeResultDto> ReanalyzeAsync(
        long? libraryId = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync();
        if (!settings.Enabled)
        {
            return new SonicReanalyzeResultDto(
                false,
                "Sonic Analysis is disabled. Enable it before re-analyzing.",
                0,
                0,
                0);
        }

        if (!settings.ReanalyzeStale)
        {
            return new SonicReanalyzeResultDto(
                false,
                "Automatic re-analysis of stale embeddings is turned off.",
                0,
                0,
                0);
        }

        var identity = SonicModelIdentity.Current;
        var scopes = await _repository.GetEnabledLibraryScopesAsync(cancellationToken);
        var libraries = libraryId is { } requested
            ? scopes.Where(scope => scope.LibraryId == requested).ToList()
            : scopes.ToList();

        if (libraryId is { } unknownLibraryId && libraries.Count == 0)
        {
            return new SonicReanalyzeResultDto(
                false,
                $"Library {unknownLibraryId} is not an enabled audio library.",
                0,
                0,
                0);
        }

        var queued = 0;
        var missing = 0;
        var stale = 0;
        var remaining = settings.ReanalyzeBatchSize;

        foreach (var (scopeId, _) in libraries)
        {
            if (remaining <= 0)
            {
                break;
            }

            var candidates = await _repository.GetStaleSonicTracksAsync(
                scopeId,
                identity.ModelId,
                identity.ModelVersion,
                identity.EmbeddingVersion,
                remaining,
                cancellationToken);

            foreach (var candidate in candidates)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (candidate.HasEmbedding)
                {
                    stale++;
                }
                else
                {
                    missing++;
                }

                // The analysis service owns the decode, the subprocess and the
                // write. This path only decides what is worth re-analysing, so a
                // Sonic change can never diverge from how analysis actually runs.
                if (await _analysisService.AnalyzeTrackByIdAsync(candidate.TrackId, cancellationToken))
                {
                    queued++;
                }

                remaining--;
            }
        }

        return new SonicReanalyzeResultDto(
            true,
            $"Queued {queued} of {missing + stale} tracks for Sonic re-analysis.",
            queued,
            missing,
            stale);
    }
}

public sealed record SonicReanalyzeResultDto(
    bool Accepted,
    string Message,
    int Queued,
    int Missing,
    int Stale);
