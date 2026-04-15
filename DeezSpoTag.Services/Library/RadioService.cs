namespace DeezSpoTag.Services.Library;

public sealed class RadioService
{
    private const string DiscoveryType = "discovery";
    private const string FavoritesType = "favorites";
    private const string ShuffleType = "shuffle";
    private const string WorkoutType = "workout";
    private const string MoodType = "mood";
    private const string DecadeType = "decade";
    private const string HighEnergyMood = "high-energy";
    private const string RelaxedMood = "relaxed";
    private const string FocusMood = "focus";
    private const string MellowMood = "mellow";
    private const string BrightMood = "bright";
    private const string DarkMood = "dark";
    private const string BalancedMood = "balanced";
    private readonly LibraryRepository _repository;

    public RadioService(LibraryRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<RadioStationDto>> GetStationsAsync(
        long plexUserId,
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        var stations = new List<RadioStationDto>
        {
            new(DiscoveryType, "Discovery", "Lesser-played tracks from your library.", DiscoveryType, null, 0),
            new(FavoritesType, "Favorites", "Most played tracks in your library.", FavoritesType, null, 0),
            new(ShuffleType, "Shuffle", "A random blend from your library.", ShuffleType, null, 0),
            new(WorkoutType, "Workout", "High energy tracks for workouts.", WorkoutType, null, 0),
            new($"{MoodType}-{HighEnergyMood}", "High Energy", "Fast, energetic tracks.", MoodType, HighEnergyMood, 0),
            new($"{MoodType}-{RelaxedMood}", "Relaxed", "Lower energy, laid-back tracks.", MoodType, RelaxedMood, 0),
            new($"{MoodType}-{FocusMood}", "Focus", "Steady tempo tracks for focus.", MoodType, FocusMood, 0),
            new($"{MoodType}-{MellowMood}", "Mellow", "Soft, slow-paced tracks.", MoodType, MellowMood, 0),
            new($"{MoodType}-{BrightMood}", "Bright", "Brighter, high-frequency tracks.", MoodType, BrightMood, 0),
            new($"{MoodType}-{DarkMood}", "Dark", "Darker, lower-frequency tracks.", MoodType, DarkMood, 0),
            new($"{MoodType}-{BalancedMood}", "Balanced", "Mid-energy tracks with steady tempo.", MoodType, BalancedMood, 0)
        };

        var decades = await _repository.GetDecadesAsync(libraryId, 15, cancellationToken);
        foreach (var decade in decades)
        {
            var label = $"{decade.Decade}s";
            stations.Add(new RadioStationDto(
                $"decade-{decade.Decade}",
                label,
                $"Tracks released in the {label}.",
                DecadeType,
                decade.Decade.ToString(),
                decade.TrackCount));
        }

        return stations;
    }

    public async Task<RadioDetailDto?> GetStationAsync(
        string type,
        string? value,
        long plexUserId,
        long libraryId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var station = ResolveStation(type, value);
        if (station is null)
        {
            return null;
        }

        var trackIds = await ResolveTrackIdsAsync(station, plexUserId, libraryId, limit, cancellationToken);
        if (trackIds.Count == 0)
        {
            return null;
        }

        var tracks = await _repository.GetTrackSummariesAsync(trackIds, cancellationToken);
        var stationWithCount = station with { TrackCount = tracks.Count };
        return new RadioDetailDto(stationWithCount, tracks);
    }

    private static RadioStationDto? ResolveStation(string type, string? value)
    {
        return type switch
        {
            DiscoveryType => new RadioStationDto(DiscoveryType, "Discovery", "Lesser-played tracks from your library.", DiscoveryType, null, 0),
            FavoritesType => new RadioStationDto(FavoritesType, "Favorites", "Most played tracks in your library.", FavoritesType, null, 0),
            ShuffleType => new RadioStationDto(ShuffleType, "Shuffle", "A random blend from your library.", ShuffleType, null, 0),
            WorkoutType => new RadioStationDto(WorkoutType, "Workout", "High energy tracks for workouts.", WorkoutType, null, 0),
            MoodType when string.Equals(value, HighEnergyMood, StringComparison.OrdinalIgnoreCase)
                => new RadioStationDto($"{MoodType}-{HighEnergyMood}", "High Energy", "Fast, energetic tracks.", MoodType, HighEnergyMood, 0),
            MoodType when string.Equals(value, RelaxedMood, StringComparison.OrdinalIgnoreCase)
                => new RadioStationDto($"{MoodType}-{RelaxedMood}", "Relaxed", "Lower energy, laid-back tracks.", MoodType, RelaxedMood, 0),
            MoodType when string.Equals(value, FocusMood, StringComparison.OrdinalIgnoreCase)
                => new RadioStationDto($"{MoodType}-{FocusMood}", "Focus", "Steady tempo tracks for focus.", MoodType, FocusMood, 0),
            MoodType when string.Equals(value, MellowMood, StringComparison.OrdinalIgnoreCase)
                => new RadioStationDto($"{MoodType}-{MellowMood}", "Mellow", "Soft, slow-paced tracks.", MoodType, MellowMood, 0),
            MoodType when string.Equals(value, BrightMood, StringComparison.OrdinalIgnoreCase)
                => new RadioStationDto($"{MoodType}-{BrightMood}", "Bright", "Brighter, high-frequency tracks.", MoodType, BrightMood, 0),
            MoodType when string.Equals(value, DarkMood, StringComparison.OrdinalIgnoreCase)
                => new RadioStationDto($"{MoodType}-{DarkMood}", "Dark", "Darker, lower-frequency tracks.", MoodType, DarkMood, 0),
            MoodType when string.Equals(value, BalancedMood, StringComparison.OrdinalIgnoreCase)
                => new RadioStationDto($"{MoodType}-{BalancedMood}", "Balanced", "Mid-energy tracks with steady tempo.", MoodType, BalancedMood, 0),
            DecadeType when int.TryParse(value, out var decade)
                => new RadioStationDto($"{DecadeType}-{decade}", $"{decade}s", $"Tracks released in the {decade}s.", DecadeType, decade.ToString(), 0),
            _ => null
        };
    }

    private async Task<IReadOnlyList<long>> ResolveTrackIdsAsync(
        RadioStationDto station,
        long plexUserId,
        long libraryId,
        int limit,
        CancellationToken cancellationToken)
    {
        switch (station.Type)
        {
            case DiscoveryType:
                {
                    var unplayed = await _repository.GetUnplayedTrackIdsAsync(plexUserId, libraryId, limit, cancellationToken);
                    if (unplayed.Count >= limit)
                    {
                        return unplayed;
                    }
                    var leastPlayed = await _repository.GetLeastPlayedTrackIdsAsync(plexUserId, libraryId, limit, cancellationToken);
                    return MergeAndTrim(unplayed, leastPlayed, limit);
                }
            case FavoritesType:
                {
                    var favorites = await _repository.GetMostPlayedTrackIdsAsync(plexUserId, libraryId, limit, cancellationToken);
                    if (favorites.Count > 0)
                    {
                        return favorites;
                    }
                    return await _repository.GetRandomTrackIdsAsync(libraryId, limit, cancellationToken);
                }
            case ShuffleType:
                return await _repository.GetRandomTrackIdsAsync(libraryId, limit, cancellationToken);
            case WorkoutType:
                {
                    var energetic = await _repository.GetTracksByAnalysisAsync(
                        new LibraryRepository.TrackAnalysisFilter(
                            libraryId,
                            MinEnergy: 0.65,
                            MaxEnergy: null,
                            MinBpm: 115,
                            MaxBpm: null,
                            MinSpectralCentroid: null,
                            MaxSpectralCentroid: null,
                            Limit: limit),
                        cancellationToken);
                    if (energetic.Count >= Math.Max(5, limit / 2))
                    {
                        return energetic;
                    }
                    var fallback = await _repository.GetRandomTrackIdsAsync(libraryId, limit, cancellationToken);
                    return MergeAndTrim(energetic, fallback, limit);
                }
            case MoodType:
                {
                    var moodValue = (station.Value ?? string.Empty).ToLowerInvariant();
                    IReadOnlyList<long> moodTracks = moodValue switch
                    {
                        HighEnergyMood => await _repository.GetTracksByAnalysisAsync(
                            new LibraryRepository.TrackAnalysisFilter(
                                libraryId, 0.6, null, 120, null, null, null, limit),
                            cancellationToken),
                        RelaxedMood => await _repository.GetTracksByAnalysisAsync(
                            new LibraryRepository.TrackAnalysisFilter(
                                libraryId, null, 0.4, null, 90, null, null, limit),
                            cancellationToken),
                        FocusMood => await _repository.GetTracksByAnalysisAsync(
                            new LibraryRepository.TrackAnalysisFilter(
                                libraryId, null, 0.5, 70, 110, null, null, limit),
                            cancellationToken),
                        MellowMood => await _repository.GetTracksByAnalysisAsync(
                            new LibraryRepository.TrackAnalysisFilter(
                                libraryId, null, 0.35, null, 85, null, 2200, limit),
                            cancellationToken),
                        BrightMood => await _repository.GetTracksByAnalysisAsync(
                            new LibraryRepository.TrackAnalysisFilter(
                                libraryId, 0.4, null, null, null, 3200, null, limit),
                            cancellationToken),
                        DarkMood => await _repository.GetTracksByAnalysisAsync(
                            new LibraryRepository.TrackAnalysisFilter(
                                libraryId, null, 0.45, null, 100, null, 2000, limit),
                            cancellationToken),
                        BalancedMood => await _repository.GetTracksByAnalysisAsync(
                            new LibraryRepository.TrackAnalysisFilter(
                                libraryId, 0.4, 0.7, 85, 125, 2200, 3200, limit),
                            cancellationToken),
                        _ => Array.Empty<long>()
                    };
                    if (moodTracks.Count >= Math.Max(5, limit / 2))
                    {
                        return moodTracks;
                    }
                    var fallback = await _repository.GetRandomTrackIdsAsync(libraryId, limit, cancellationToken);
                    return MergeAndTrim(moodTracks, fallback, limit);
                }
            case DecadeType:
                {
                    if (!int.TryParse(station.Value, out var decade))
                    {
                        return Array.Empty<long>();
                    }
                    var decadeTracks = await _repository.GetTracksByDecadeAsync(libraryId, decade, limit, cancellationToken);
                    if (decadeTracks.Count > 0)
                    {
                        return decadeTracks;
                    }
                    return await _repository.GetRandomTrackIdsAsync(libraryId, limit, cancellationToken);
                }
            default:
                return Array.Empty<long>();
        }
    }

    private static List<long> MergeAndTrim(IReadOnlyList<long> primary, IReadOnlyList<long> fallback, int limit)
    {
        var merged = new List<long>(primary);
        foreach (var id in fallback)
        {
            if (merged.Count >= limit)
            {
                break;
            }
            if (!merged.Contains(id))
            {
                merged.Add(id);
            }
        }
        return merged;
    }
}
