using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class MoodMixService
{
    private readonly LibraryRepository _repository;
    private readonly MoodMixPreferencesStore _preferencesStore;
    private static readonly Random Rng = new();

    public MoodMixService(LibraryRepository repository, MoodMixPreferencesStore preferencesStore)
    {
        _repository = repository;
        _preferencesStore = preferencesStore;
    }

    public async Task<IReadOnlyList<MoodPresetDto>> GetPresetsAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _repository.GetMoodBucketCountsAsync(cancellationToken);
        var presets = new List<MoodPresetDto>();

        foreach (var (id, config) in MoodBucketService.MoodConfigs)
        {
            counts.TryGetValue(id, out var count);
            presets.Add(new MoodPresetDto(id, config.Name, $"Tracks that match a {config.Name.ToLowerInvariant()} vibe.", new[] { id }, count));
        }

        return presets;
    }

    public async Task<MoodMixResponseDto> GenerateMixAsync(MoodMixRequestDto request, CancellationToken cancellationToken)
    {
        var preferences = await _preferencesStore.LoadAsync();
        var presetId = request.PresetId ?? preferences.PresetId ?? "relaxed";
        var limit = Math.Clamp(request.Limit ?? preferences.Limit ?? 50, 10, 200);
        var libraryId = request.LibraryId ?? preferences.LibraryId;

        if (!MoodBucketService.MoodConfigs.TryGetValue(presetId, out var config))
        {
            config = MoodBucketService.MoodConfigs["relaxed"];
            presetId = "relaxed";
        }

        // Get top-100 scored tracks from mood bucket, then randomly sample
        var pool = await _repository.GetMoodBucketTrackIdsAsync(presetId, 100, libraryId, cancellationToken);

        if (pool.Count == 0)
        {
            return new MoodMixResponseDto(config.Name, $"Tracks that match a {config.Name.ToLowerInvariant()} vibe.", Array.Empty<MixTrackDto>());
        }

        // Shuffle and take requested limit
        var shuffled = pool.OrderBy(_ => Rng.Next()).Take(limit).ToList();
        var trackIds = shuffled.Select(item => item.TrackId).ToList();

        var tracks = await _repository.GetTrackSummariesAsync(trackIds, cancellationToken);
        return new MoodMixResponseDto(config.Name, $"Tracks that match a {config.Name.ToLowerInvariant()} vibe.", tracks);
    }
}
