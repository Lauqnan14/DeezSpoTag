using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class UserPreferencesStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _filePath;
    private readonly ILogger<UserPreferencesStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public UserPreferencesStore(IWebHostEnvironment env, ILogger<UserPreferencesStore> logger)
    {
        _logger = logger;
        var dataRoot = AppDataPaths.GetDataRoot(env);
        Directory.CreateDirectory(dataRoot);
        _filePath = Path.Join(dataRoot, "user-preferences.json");
    }

    public async Task<UserPreferencesDto> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_filePath))
                return new UserPreferencesDto();

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<UserPreferencesDto>(json, _jsonOptions)
                ?? new UserPreferencesDto();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load user preferences.");
            return new UserPreferencesDto();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(UserPreferencesDto prefs)
    {
        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(prefs, _jsonOptions);
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save user preferences.");
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed class UserPreferencesDto
{
    // UI / Layout
    public string Theme { get; set; } = "blue";
    public bool SidebarCollapsed { get; set; } = false;
    public bool TabsPreferenceEnabled { get; set; } = true;
    public long? PwaPromptDismissedAt { get; set; }

    // AutoTag
    public List<string> AutoTagSelectedPlatforms { get; set; } = new();
    public JsonElement? AutoTagPreferences { get; set; }
    public string? AutoTagActiveProfileId { get; set; }

    // Download
    public string? DownloadDestinationFolderId { get; set; }
    public string? DownloadDestinationStereoFolderId { get; set; }
    public string? DownloadDestinationAtmosFolderId { get; set; }
    public string AppleDownloadNotificationMode { get; set; } = "detailed";

    // Remembered tab state
    public Dictionary<string, string> TabSelections { get; set; } = new();

    // QuickTag
    public List<string> QuickTagColumns { get; set; } = new();
    public string? QuickTagColumnPreset { get; set; }
    public string? QuickTagTagSourceProvider { get; set; }
    public string? QuickTagSourceTemplate { get; set; }
    public int? QuickTagPanelLeftWidth { get; set; }
    public int? QuickTagPanelRightWidth { get; set; }

    // LRC Editor
    public bool LrcEditorMerge { get; set; } = false;

    // Library
    public string? LibraryAlbumDestinationFolderId { get; set; }

    // Audio
    public int PreviewVolume { get; set; } = 100;

    // Activities / Schedule
    public string? SpotifyCacheSchedule { get; set; }
    public long? SpotifyCacheLastRun { get; set; }

    // Home / Search
    public JsonElement? SpotiflacRecentSearches { get; set; }
}
