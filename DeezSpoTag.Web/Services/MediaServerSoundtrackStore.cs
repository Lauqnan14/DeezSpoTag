using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerSoundtrackStore
{
    private const string FolderName = "media-server";
    private const string FileName = "soundtracks.json";

    private readonly string _storePath;
    private readonly ILogger<MediaServerSoundtrackStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public MediaServerSoundtrackStore(IWebHostEnvironment environment, ILogger<MediaServerSoundtrackStore> logger)
    {
        _logger = logger;
        var dataRoot = AppDataPaths.GetDataRoot(environment);
        var directory = Path.Join(dataRoot, FolderName);
        Directory.CreateDirectory(directory);
        _storePath = Path.Join(directory, FileName);
    }

    public async Task<MediaServerSoundtrackSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await LoadNoLockAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(MediaServerSoundtrackSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await SaveNoLockAsync(settings, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MediaServerSoundtrackSettings> UpdateAsync(
        Func<MediaServerSoundtrackSettings, bool> mutator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadNoLockAsync(cancellationToken);
            var changed = mutator(settings);
            if (changed)
            {
                await SaveNoLockAsync(settings, cancellationToken);
            }

            return settings;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<MediaServerSoundtrackSettings> LoadNoLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return new MediaServerSoundtrackSettings();
            }

            await using var stream = File.OpenRead(_storePath);
            var model = await JsonSerializer.DeserializeAsync<MediaServerSoundtrackSettings>(stream, _jsonOptions, cancellationToken)
                ?? new MediaServerSoundtrackSettings();
            Normalize(model);
            return model;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed loading soundtrack store from {Path}", _storePath);
            return new MediaServerSoundtrackSettings();
        }
    }

    private async Task SaveNoLockAsync(MediaServerSoundtrackSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            Normalize(settings);
            await using var stream = File.Create(_storePath);
            await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed saving soundtrack store to {Path}", _storePath);
        }
    }

    private static void Normalize(MediaServerSoundtrackSettings settings)
    {
        settings.Servers ??= new Dictionary<string, MediaServerSoundtrackServerSettings>(StringComparer.OrdinalIgnoreCase);

        var serverEntries = settings.Servers.ToArray();
        settings.Servers = new Dictionary<string, MediaServerSoundtrackServerSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (serverType, serverSettings) in serverEntries)
        {
            if (string.IsNullOrWhiteSpace(serverType))
            {
                continue;
            }

            var normalizedServer = serverSettings ?? new MediaServerSoundtrackServerSettings();
            normalizedServer.Libraries ??= new Dictionary<string, MediaServerSoundtrackLibrarySettings>(StringComparer.OrdinalIgnoreCase);

            var libraries = normalizedServer.Libraries.ToArray();
            normalizedServer.Libraries = new Dictionary<string, MediaServerSoundtrackLibrarySettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var (libraryId, librarySettings) in libraries)
            {
                var normalizedLibraryId = NormalizeText(libraryId);
                if (string.IsNullOrWhiteSpace(normalizedLibraryId))
                {
                    continue;
                }

                var normalizedLibrary = librarySettings ?? new MediaServerSoundtrackLibrarySettings();
                normalizedLibrary.LibraryId = normalizedLibraryId;
                normalizedLibrary.Name = NormalizeText(normalizedLibrary.Name);
                normalizedLibrary.Category = NormalizeCategory(normalizedLibrary.Category);
                normalizedServer.Libraries[normalizedLibraryId] = normalizedLibrary;
            }

            settings.Servers[NormalizeText(serverType)] = normalizedServer;
        }
    }

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeCategory(string? value)
    {
        var normalized = NormalizeText(value).ToLowerInvariant();
        if (normalized == MediaServerSoundtrackConstants.TvShowCategory)
        {
            return MediaServerSoundtrackConstants.TvShowCategory;
        }

        return MediaServerSoundtrackConstants.MovieCategory;
    }
}
