namespace DeezSpoTag.Web.Services;

public sealed record FavoriteItem(
    string Id,
    string Name,
    string Type,
    string SourceUrl,
    string? ImageUrl,
    string? Subtitle,
    int? DurationMs);

public sealed record FavoritesResult(
    bool Available,
    string? Message,
    List<FavoriteItem> Albums,
    List<FavoriteItem> Playlists,
    List<FavoriteItem> Tracks);
