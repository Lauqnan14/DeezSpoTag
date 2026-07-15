using System.Text.Json;
using System.Text.Json.Serialization;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

public class AutoTagMetadataService
{
    private readonly ILogger<AutoTagMetadataService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly object _cacheLock = new();
    private string? _cachedJson;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;
    private readonly PortedPlatformRegistry _portedPlatforms;

    public AutoTagMetadataService(PortedPlatformRegistry portedPlatforms, ILogger<AutoTagMetadataService> logger)
    {
        _logger = logger;
        _portedPlatforms = portedPlatforms;
    }

    public Task<string?> GetPlatformsJsonAsync()
    {
        lock (_cacheLock)
        {
            if (_cachedJson != null
                && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return Task.FromResult<string?>(_cachedJson);
            }
        }

        try
        {
            var merged = BuildPlatformsJson();
            if (string.IsNullOrWhiteSpace(merged))
            {
                _logger.LogWarning("Platform metadata unavailable.");
                return Task.FromResult<string?>(null);
            }

            JsonSerializer.Deserialize<JsonElement>(merged, _jsonOptions);

            lock (_cacheLock)
            {
                _cachedJson = merged;
                _cacheExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            }

            return Task.FromResult<string?>(merged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to read platform metadata.");
            return Task.FromResult<string?>(null);
        }
    }

    private string? BuildPlatformsJson()
    {
        var ported = _portedPlatforms.DescribeAll();
        if (ported.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(ported, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });
    }
}
