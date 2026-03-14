using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Authentication;

/// <summary>
/// Login storage service interface - exact port from deezspotag loginStorage.ts
/// </summary>
public interface ILoginStorageService
{
    Task<LoginData?> LoadLoginCredentialsAsync();
    Task SaveLoginCredentialsAsync(LoginData loginData);
    Task ResetLoginCredentialsAsync();
    Task ForceFixCorruptedFileAsync();
}

/// <summary>
/// Login storage service implementation - exact port from deezspotag loginStorage.ts
/// </summary>
public class LoginStorageService : ILoginStorageService
{
    private readonly string _configFolder;
    private readonly string _loginFilePath;
    private readonly ILogger<LoginStorageService> _logger;
    private static readonly JsonSerializerOptions LoginDeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions LoginSerializeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Default values exactly like deezspotag DEFAULTS
    private static readonly LoginData DefaultLoginData = new()
    {
        AccessToken = null,
        Arl = null,
        User = null
    };

    public LoginStorageService(ILogger<LoginStorageService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _configFolder = DeezSpoTagConfigPathResolver.GetConfigFolder();
        _loginFilePath = Path.Join(_configFolder, "login.json");

        _logger.LogDebug("Login storage initialized with path: {LoginFilePath}", _loginFilePath);
    }

    public async Task<LoginData?> LoadLoginCredentialsAsync()
    {
        try
        {
            if (!Directory.Exists(_configFolder))
            {
                Directory.CreateDirectory(_configFolder);
                _logger.LogDebug("Created config folder: {ConfigFolder}", _configFolder);
            }

            if (!System.IO.File.Exists(_loginFilePath))
            {
                _logger.LogDebug("Login file doesn't exist, resetting to defaults");
                await ResetLoginCredentialsAsync();
                return null;
            }

            var json = await File.ReadAllTextAsync(_loginFilePath);

            if (!json.TrimStart().StartsWith('{'))
            {
                _logger.LogWarning("Login file appears corrupted (missing opening brace), resetting to defaults");
                await ResetLoginCredentialsAsync();
                return null;
            }

            var loginData = JsonSerializer.Deserialize<LoginData>(json, LoginDeserializeOptions);

            _logger.LogDebug("Loaded login credentials from file");
            return loginData;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON syntax error in login file, resetting to defaults");
            await ResetLoginCredentialsAsync();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error loading login credentials");
            return null;
        }
    }

    public async Task SaveLoginCredentialsAsync(LoginData loginData)
    {
        try
        {
            var dataToSave = new LoginData
            {
                AccessToken = null,
                Arl = null,
                User = null
            };

            try
            {
                if (System.IO.File.Exists(_loginFilePath))
                {
                    var existingJson = await File.ReadAllTextAsync(_loginFilePath);
                    var existingData = JsonSerializer.Deserialize<LoginData>(existingJson, LoginDeserializeOptions);
                    if (existingData != null)
                    {
                        dataToSave = existingData;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load existing login data, using defaults");
            }

            if (!string.IsNullOrEmpty(loginData.Arl))
                dataToSave.Arl = loginData.Arl;

            if (!string.IsNullOrEmpty(loginData.AccessToken))
                dataToSave.AccessToken = loginData.AccessToken;

            if (loginData.User != null)
                dataToSave.User = loginData.User;

            if (!Directory.Exists(_configFolder))
            {
                Directory.CreateDirectory(_configFolder);
            }

            var json = JsonSerializer.Serialize(dataToSave, LoginSerializeOptions);
            await System.IO.File.WriteAllTextAsync(_loginFilePath, json);

            _logger.LogDebug("Saved login credentials to file");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error saving login credentials");
            throw new InvalidOperationException("Failed to save login credentials.", ex);
        }
    }

    public async Task ResetLoginCredentialsAsync()
    {
        try
        {
            if (!Directory.Exists(_configFolder))
            {
                Directory.CreateDirectory(_configFolder);
            }

            var json = JsonSerializer.Serialize(DefaultLoginData, LoginSerializeOptions);
            await System.IO.File.WriteAllTextAsync(_loginFilePath, json);

            _logger.LogDebug("Reset login credentials to defaults");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error resetting login credentials");
            throw new InvalidOperationException("Failed to reset login credentials.", ex);
        }
    }

    public async Task ForceFixCorruptedFileAsync()
    {
        try
        {
            if (!System.IO.File.Exists(_loginFilePath))
            {
                await ResetLoginCredentialsAsync();
                return;
            }

            var content = await File.ReadAllTextAsync(_loginFilePath);
            if (content.TrimStart().StartsWith('{'))
            {
                _logger.LogInformation("Login file is not corrupted");
                return;
            }

            _logger.LogWarning("Detected corrupted login file, attempting auto-fix");

            var json = JsonSerializer.Serialize(DefaultLoginData, LoginSerializeOptions);

            await File.WriteAllTextAsync(_loginFilePath, json);
            _logger.LogInformation("Login file repaired successfully");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error fixing corrupted login file");
            throw new InvalidOperationException("Failed to repair login credentials file.", ex);
        }
    }
}

public class LoginData
{
    public string? AccessToken { get; set; }
    public string? Arl { get; set; }
    public UserData? User { get; set; }
}

public class UserData
{
    public string Id { get; set; } = "0";
    public string Name { get; set; } = "";
    public string Picture { get; set; } = "";
    public string Country { get; set; } = "";
    public bool? CanStreamLossless { get; set; }
    public bool? CanStreamHq { get; set; }
    public string? LovedTracks { get; set; }
    public string? LicenseToken { get; set; }
}
