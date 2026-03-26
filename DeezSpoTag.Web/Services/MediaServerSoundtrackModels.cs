namespace DeezSpoTag.Web.Services;

public static class MediaServerSoundtrackConstants
{
    public const string PlexServer = "plex";
    public const string JellyfinServer = "jellyfin";
    public const string MovieCategory = "movie";
    public const string TvShowCategory = "tv_show";
}

public sealed class MediaServerSoundtrackSettings
{
    public Dictionary<string, MediaServerSoundtrackServerSettings> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MediaServerSoundtrackServerSettings
{
    public bool AutoIncludeNewLibraries { get; set; } = true;

    public Dictionary<string, MediaServerSoundtrackLibrarySettings> Libraries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MediaServerSoundtrackLibrarySettings
{
    public string LibraryId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = MediaServerSoundtrackConstants.MovieCategory;

    public bool Enabled { get; set; } = true;

    public bool Ignored { get; set; }

    public bool UserConfigured { get; set; }

    public DateTimeOffset? FirstDiscoveredUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }
}

public sealed class MediaServerSoundtrackServerDto
{
    public string ServerType { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Connected { get; set; }

    public bool AutoIncludeNewLibraries { get; set; } = true;

    public List<MediaServerSoundtrackLibraryDto> Libraries { get; set; } = new();
}

public sealed class MediaServerSoundtrackLibraryDto
{
    public string LibraryId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = MediaServerSoundtrackConstants.MovieCategory;

    public string CategoryLabel { get; set; } = "Movies";

    public bool Enabled { get; set; } = true;

    public bool Ignored { get; set; }

    public bool Connected { get; set; }

    public DateTimeOffset? FirstDiscoveredUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }
}

public sealed class MediaServerSoundtrackConfigurationDto
{
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<MediaServerSoundtrackServerDto> Servers { get; set; } = new();
}

public sealed class MediaServerSoundtrackLibraryPreferenceUpdateDto
{
    public string? LibraryId { get; set; }

    public bool? Enabled { get; set; }

    public bool? Ignored { get; set; }
}

public sealed class MediaServerSoundtrackServerPreferenceUpdateDto
{
    public string? ServerType { get; set; }

    public bool? AutoIncludeNewLibraries { get; set; }

    public List<MediaServerSoundtrackLibraryPreferenceUpdateDto> Libraries { get; set; } = new();
}

public sealed class MediaServerSoundtrackConfigurationUpdateRequest
{
    public List<MediaServerSoundtrackServerPreferenceUpdateDto> Servers { get; set; } = new();
}

public abstract class MediaServerLibraryContentBase
{
    public string ServerType { get; set; } = string.Empty;

    public string LibraryId { get; set; } = string.Empty;

    public string LibraryName { get; set; } = string.Empty;

    public string Category { get; set; } = MediaServerSoundtrackConstants.MovieCategory;

    public string ItemId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? ImageUrl { get; set; }
}

public sealed class MediaServerSoundtrackItemDto : MediaServerLibraryContentBase
{
    public string ServerLabel { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset? FirstSeenUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }

    public MediaServerSoundtrackMatchDto? Soundtrack { get; set; }
}

public sealed class MediaServerSoundtrackResolveRequest
{
    public string? ServerType { get; set; }

    public string? LibraryId { get; set; }

    public string? LibraryName { get; set; }

    public string? Category { get; set; }

    public string? ItemId { get; set; }

    public string? Title { get; set; }

    public int? Year { get; set; }

    public string? ImageUrl { get; set; }

    public string? ManualQuery { get; set; }
}

public sealed class MediaServerSoundtrackMatchDto
{
    public string Kind { get; set; } = "search";

    public string? DeezerId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public string Url { get; set; } = string.Empty;

    public string? CoverUrl { get; set; }

    public double Score { get; set; }

    public string? Provider { get; set; }

    public string? Reason { get; set; }

    public bool Locked { get; set; }

    public int RetryCount { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

public sealed class MediaServerSoundtrackItemsResponseDto
{
    public string Category { get; set; } = MediaServerSoundtrackConstants.MovieCategory;

    public int Total { get; set; }

    public List<MediaServerSoundtrackItemDto> Items { get; set; } = new();
}

public sealed class MediaServerSoundtrackLibrarySyncStateDto
{
    public string ServerType { get; set; } = string.Empty;

    public string LibraryId { get; set; } = string.Empty;

    public string Category { get; set; } = MediaServerSoundtrackConstants.MovieCategory;

    public string Status { get; set; } = "idle";

    public int LastOffset { get; set; }

    public int LastBatchCount { get; set; }

    public int TotalProcessed { get; set; }

    public DateTimeOffset? LastSyncUtc { get; set; }

    public DateTimeOffset? LastSuccessUtc { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MediaServerSoundtrackSyncStatusDto
{
    public bool SyncRunning { get; set; }

    public DateTimeOffset? LastSyncStartedUtc { get; set; }

    public DateTimeOffset? LastSyncCompletedUtc { get; set; }

    public int PendingJobs { get; set; }

    public List<MediaServerSoundtrackLibrarySyncStateDto> Libraries { get; set; } = new();
}

public sealed class MediaServerTvShowSeasonDto
{
    public string SeasonId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int? SeasonNumber { get; set; }

    public string? ImageUrl { get; set; }

    public int EpisodeCount { get; set; }
}

public abstract class MediaServerTvShowEpisodeBase
{
    public string EpisodeId { get; set; } = string.Empty;

    public string SeasonId { get; set; } = string.Empty;

    public string SeasonTitle { get; set; } = string.Empty;

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? ImageUrl { get; set; }
}

public sealed class MediaServerTvShowEpisodeDto : MediaServerTvShowEpisodeBase
{
    public MediaServerSoundtrackMatchDto? Soundtrack { get; set; }
}

public sealed class MediaServerTvShowEpisodesResponseDto
{
    public string ServerType { get; set; } = string.Empty;

    public string ServerLabel { get; set; } = string.Empty;

    public string LibraryId { get; set; } = string.Empty;

    public string LibraryName { get; set; } = string.Empty;

    public string ShowId { get; set; } = string.Empty;

    public string ShowTitle { get; set; } = string.Empty;

    public string? ShowImageUrl { get; set; }

    public string? SelectedSeasonId { get; set; }

    public int TotalEpisodes { get; set; }

    public List<MediaServerTvShowSeasonDto> Seasons { get; set; } = new();

    public List<MediaServerTvShowEpisodeDto> Episodes { get; set; } = new();
}

public sealed class MediaServerLibraryDescriptor
{
    public string ServerType { get; set; } = string.Empty;

    public string LibraryId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = MediaServerSoundtrackConstants.MovieCategory;

    public bool Connected { get; set; }
}

public sealed class MediaServerContentItem : MediaServerLibraryContentBase;

public sealed class MediaServerTvShowSeasonItem
{
    public string SeasonId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int? SeasonNumber { get; set; }

    public string? ImageUrl { get; set; }
}

public sealed class MediaServerTvShowEpisodeItem : MediaServerTvShowEpisodeBase;
