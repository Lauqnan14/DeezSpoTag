using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Settings for Sonic Analysis.
///
/// Deliberately a separate contract from <see cref="VibeAnalysisSettingsDto"/>.
/// That record is a three-field positional type that guardrail tests assert on
/// exactly, so extending it would break a build for a purely additive concern.
/// Sonic is an independent analysis domain that happens to run inside the same
/// pipeline, and keeping its configuration separate keeps it that way.
/// </summary>
public sealed record SonicAnalysisSettingsDto
{
    /// <summary>Whether the analyzer computes and stores embeddings at all.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether a stale or missing vector should be recomputed.</summary>
    public bool ReanalyzeStale { get; init; } = true;

    /// <summary>Upper bound on tracks re-embedded in one pass, so a first run
    /// after enabling does not monopolise the analysis queue.</summary>
    public int ReanalyzeBatchSize { get; init; } = 50;

    public static SonicAnalysisSettingsDto Defaults() => new()
    {
        Enabled = false,
        ReanalyzeStale = true,
        ReanalyzeBatchSize = 50,
    };

    public SonicAnalysisSettingsDto Normalize() => this with
    {
        ReanalyzeBatchSize = Math.Clamp(ReanalyzeBatchSize, 10, 500),
    };
}

/// <summary>Runtime facts about Sonic Analysis for the Vibe Analysis panel.</summary>
public sealed record SonicAnalysisStatusDto
{
    public bool Enabled { get; init; }
    public string ModelId { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public string EmbeddingVersion { get; init; } = string.Empty;
    public int Dimensions { get; init; }
    public int ReanalyzeBatchSize { get; init; }

    public IReadOnlyList<SonicLibraryStatusDto> Libraries { get; init; } = Array.Empty<SonicLibraryStatusDto>();

    public int TotalTracks => Libraries.Sum(library => library.TotalTracks);
    public int TotalEmbedded => Libraries.Sum(library => library.TracksWithEmbedding);
    public int TotalAnalyzed => Libraries.Sum(library => library.TracksAnalyzed);
    public int TotalStale => Libraries.Sum(library => library.StaleEmbeddings);
    public int TotalFailed => Libraries.Sum(library => library.TracksUnavailable);

    public double CoveragePercent => TotalTracks <= 0
        ? 0d
        : Math.Round(TotalEmbedded * 100d / TotalTracks, 1);

    /// <summary>
    /// Tracks that are analysed but whose vector is not currently usable, either
    /// because none was produced or because the file changed afterwards.
    /// </summary>
    /// <remarks>
    /// <see cref="TotalEmbedded"/> already counts only vectors that are fresh,
    /// because the builder subtracts stale ones per library before aggregating.
    /// Subtracting <see cref="TotalStale"/> again here would count those tracks
    /// twice and under-report the backlog, which is the number a person watches
    /// to decide whether a re-analysis pass is done.
    ///
    /// A stale track is therefore counted in both <see cref="PendingEmbeddings"/>
    /// and <see cref="TotalStale"/>. That is intended: it really is still waiting
    /// to be analysed, and the stale figure is what says why.
    /// </remarks>
    public int PendingEmbeddings => Math.Max(0, TotalAnalyzed - TotalEmbedded);

    public int IndexVectorCount { get; init; }
    public double IndexMegabytes { get; init; }
    public long IndexBuildMilliseconds { get; init; }
}

public sealed record SonicLibraryStatusDto
{
    public long LibraryId { get; init; }
    public string LibraryName { get; init; } = string.Empty;
    public int TotalTracks { get; init; }
    public int TracksAnalyzed { get; init; }

    /// <summary>
    /// Vectors that are usable right now, which means stale ones have already
    /// been subtracted. Coverage is reported from this figure, so an out-of-date
    /// vector is never counted as coverage.
    /// </summary>
    public int TracksWithEmbedding { get; init; }

    public int TracksUnavailable { get; init; }

    /// <summary>Embeddings whose source file has changed size or modification time.</summary>
    public int StaleEmbeddings { get; init; }

    public double CoveragePercent => TotalTracks <= 0
        ? 0d
        : Math.Round(TracksWithEmbedding * 100d / TotalTracks, 1);
}

/// <summary>
/// File-backed settings for Sonic Analysis, stored beside the Vibe analysis
/// settings but as its own file so neither feature can overwrite the other's.
///
/// Follows the established store shape: a singleton, a semaphore, a cached
/// value, and a change signal for the background loop.
/// </summary>
public sealed class SonicAnalysisSettingsStore
{
    private readonly string _settingsPath;
    private readonly ILogger<SonicAnalysisSettingsStore> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _changed = new(0, 1);
    private SonicAnalysisSettingsDto? _cached;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public SonicAnalysisSettingsStore(
        IWebHostEnvironment env,
        IConfiguration configuration,
        ILogger<SonicAnalysisSettingsStore> logger)
    {
        _logger = logger;

        var configuredDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (string.IsNullOrWhiteSpace(configuredDataDir))
        {
            configuredDataDir = configuration["DataDirectory"];
        }

        var baseDataDir = string.IsNullOrWhiteSpace(configuredDataDir)
            ? Path.Join(env.ContentRootPath, "Data")
            : configuredDataDir;

        var dataDir = Path.Join(baseDataDir, "analysis");
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Join(dataDir, "sonic-settings.json");
    }

    public async Task<SonicAnalysisSettingsDto> LoadAsync()
    {
        await _sync.WaitAsync();
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (!File.Exists(_settingsPath))
            {
                _cached = SonicAnalysisSettingsDto.Defaults();
                return _cached;
            }

            var json = await File.ReadAllTextAsync(_settingsPath);
            _cached = (JsonSerializer.Deserialize<SonicAnalysisSettingsDto>(json, JsonOptions)
                       ?? SonicAnalysisSettingsDto.Defaults())
                .Normalize();
            return _cached;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Failed to load Sonic analysis settings from {Path}.", _settingsPath);
            _cached ??= SonicAnalysisSettingsDto.Defaults();
            return _cached;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SonicAnalysisSettingsDto> SaveAsync(SonicAnalysisSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _sync.WaitAsync();
        try
        {
            _cached = settings.Normalize();
            await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(_cached, JsonOptions));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Reported rather than swallowed, matching the Vibe analysis store: a
            // settings write that does not persist is a real failure, and a
            // silent one reverts on the next restart.
            _logger.LogError(ex, "Failed to save Sonic analysis settings to {Path}.", _settingsPath);
            throw new IOException($"Failed to persist Sonic analysis settings to {_settingsPath}.", ex);
        }
        finally
        {
            _sync.Release();
        }

        if (_changed.CurrentCount == 0)
        {
            _changed.Release();
        }

        return _cached;
    }

    /// <summary>Waits for a settings change, or for the timeout, whichever comes first.</summary>
    public async Task WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _changed.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller treats cancellation as "wake and re-evaluate".
        }
    }
}
