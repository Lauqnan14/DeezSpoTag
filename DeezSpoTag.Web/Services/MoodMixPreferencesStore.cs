using System.Text.Json;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class MoodMixPreferencesStore
{
    private static readonly JsonSerializerOptions IndentedJsonSerializerOptions = new() { WriteIndented = true };
    private readonly ILogger<MoodMixPreferencesStore> _logger;
    private readonly string _filePath;

    public MoodMixPreferencesStore(IWebHostEnvironment env, ILogger<MoodMixPreferencesStore> logger)
    {
        _logger = logger;
        var dataDir = Path.Join(AppDataPaths.GetDataRoot(env), "mixes");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Join(dataDir, "mood-preferences.json");
    }

    public async Task<MoodMixPreferencesDto> LoadAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new MoodMixPreferencesDto(null, null, null);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<MoodMixPreferencesDto>(json)
                ?? new MoodMixPreferencesDto(null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load mood mix preferences.");
            return new MoodMixPreferencesDto(null, null, null);
        }
    }

    public async Task SaveAsync(MoodMixPreferencesDto preferences)
    {
        try
        {
            var json = JsonSerializer.Serialize(preferences, IndentedJsonSerializerOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save mood mix preferences.");
        }
    }
}
