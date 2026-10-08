using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace DeezSpoTag.Web.Services;

public sealed class VibeAnalysisSettingsStore
{
    private readonly string _settingsPath;
    private readonly ILogger<VibeAnalysisSettingsStore> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private VibeAnalysisSettingsDto? _cached;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public VibeAnalysisSettingsStore(
        IWebHostEnvironment env,
        IConfiguration configuration,
        ILogger<VibeAnalysisSettingsStore> logger)
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
        _settingsPath = Path.Join(dataDir, "settings.json");
    }

    public async Task<VibeAnalysisSettingsDto> LoadAsync()
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
                _cached = VibeAnalysisSettingsDto.Defaults();
                return _cached;
            }

            var json = await File.ReadAllTextAsync(_settingsPath);
            _cached = Normalize(JsonSerializer.Deserialize<VibeAnalysisSettingsDto>(json, _jsonOptions)
                ?? VibeAnalysisSettingsDto.Defaults());
            return _cached;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Failed to load vibe analysis settings from {Path}.", _settingsPath);
            _cached ??= VibeAnalysisSettingsDto.Defaults();
            return _cached;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<VibeAnalysisSettingsDto> SaveAsync(VibeAnalysisSettingsDto settings)
    {
        await _sync.WaitAsync();
        try
        {
            _cached = Normalize(settings);
            var json = JsonSerializer.Serialize(_cached, _jsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Previously this was logged and then reported to the caller as a
            // success, so the UI showed settings as saved while nothing was written.
            // A settings write that does not persist is a real failure and must be
            // reported, otherwise the next restart silently reverts the change.
            _logger.LogError(ex, "Failed to save vibe analysis settings to {Path}.", _settingsPath);
            throw new IOException($"Failed to persist vibe analysis settings to {_settingsPath}.", ex);
        }
        finally
        {
            _sync.Release();
        }

        return _cached!;
    }

    private static VibeAnalysisSettingsDto Normalize(VibeAnalysisSettingsDto settings)
    {
        return new VibeAnalysisSettingsDto(
            settings.Enabled,
            Math.Clamp(settings.BatchSize, 10, 500),
            Math.Clamp(settings.IntervalMinutes, 5, 240))
        {
            UseLibraryOrder = settings.UseLibraryOrder,

            // De-duplicated and order-preserving. A repeated id in the list would make
            // a library appear twice in the analysis order while the rest of the list
            // shifted behind it.
            LibraryOrder = settings.LibraryOrder?
                .Distinct()
                .Where(static id => id > 0)
                .ToArray() ?? Array.Empty<long>(),
        };
    }
}

/// <summary>
/// Vibe analysis settings.
///
/// <para>The library order lives here as a body property rather than a positional
/// parameter: the three positional fields are pinned by a source-text guardrail, and
/// folding the order into the list would reorder analysis behind a panel that does not
/// offer one.</para>
/// </summary>
public sealed record VibeAnalysisSettingsDto(
    bool Enabled,
    int BatchSize,
    int IntervalMinutes)
{
    /// <summary>
    /// Whether <see cref="LibraryOrder"/> decides the order libraries are analysed in.
    ///
    /// <para>Off means alphabetical by name, which is the safe default: it cannot
    /// exclude a library by being stale.</para>
    /// </summary>
    public bool UseLibraryOrder { get; init; }

    /// <summary>
    /// Folder ids in the order libraries should be analysed.
    ///
    /// <para>Reorders only. A library missing from this list is still analysed, in
    /// alphabetical position — see <c>ResolveAnalysisFolderOrder</c>, which appends
    /// rather than drops. A stored list is allowed to go stale without silently
    /// narrowing what gets analysed.</para>
    /// </summary>
    public IReadOnlyList<long> LibraryOrder { get; init; } = Array.Empty<long>();

    public static VibeAnalysisSettingsDto Defaults() => new(false, 50, 30);
}
