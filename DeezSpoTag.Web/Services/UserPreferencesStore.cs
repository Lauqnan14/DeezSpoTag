using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;

namespace DeezSpoTag.Web.Services;

public sealed class UserPreferencesStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _dbConnectionString;
    private readonly string _legacyFilePath;
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
        _legacyFilePath = Path.Join(dataRoot, "user-preferences.json");
        var dbPath = Path.Join(dataRoot, "user-preferences.db");
        var connectionBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _dbConnectionString = connectionBuilder.ToString();
    }

    public async Task<UserPreferencesDto> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_dbConnectionString);
            await connection.OpenAsync();
            await EnsureSchemaAsync(connection);
            await MigrateLegacyJsonIfNeededAsync(connection);

            var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM user_preferences WHERE id = 1;";
            var result = await command.ExecuteScalarAsync();
            if (result is not string json || string.IsNullOrWhiteSpace(json))
            {
                return new UserPreferencesDto();
            }

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
            await using var connection = new SqliteConnection(_dbConnectionString);
            await connection.OpenAsync();
            await EnsureSchemaAsync(connection);

            var json = JsonSerializer.Serialize(prefs, _jsonOptions);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO user_preferences(id, payload_json, updated_utc)
                VALUES (1, @payload, @updatedUtc)
                ON CONFLICT(id) DO UPDATE SET
                    payload_json = excluded.payload_json,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("@payload", json);
            command.Parameters.AddWithValue("@updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();

            if (File.Exists(_legacyFilePath))
            {
                File.Delete(_legacyFilePath);
            }
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

    private static async Task EnsureSchemaAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS user_preferences (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                payload_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task MigrateLegacyJsonIfNeededAsync(SqliteConnection connection)
    {
        var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM user_preferences WHERE id = 1;";
        var existingCount = Convert.ToInt64(await check.ExecuteScalarAsync() ?? 0L);
        if (existingCount > 0 || !File.Exists(_legacyFilePath))
        {
            return;
        }

        try
        {
            var legacyJson = await File.ReadAllTextAsync(_legacyFilePath);
            var migrated = JsonSerializer.Deserialize<UserPreferencesDto>(legacyJson, _jsonOptions) ?? new UserPreferencesDto();
            var payloadJson = JsonSerializer.Serialize(migrated, _jsonOptions);

            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO user_preferences(id, payload_json, updated_utc)
                VALUES (1, @payload, @updatedUtc);
                """;
            insert.Parameters.AddWithValue("@payload", payloadJson);
            insert.Parameters.AddWithValue("@updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();

            File.Delete(_legacyFilePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy user preferences JSON.");
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

    // Settings / Media server library PIN
    public string? MediaServerLibraryPinHash { get; set; }
    public string? MediaServerLibraryPinSalt { get; set; }
}
