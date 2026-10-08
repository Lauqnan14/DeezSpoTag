namespace DeezSpoTag.Web.Services.Favorites;

/// <summary>
/// One platform's saved content for the Favorites tab. Providers are discovered through DI and
/// rendered generically, so adding a platform means adding a provider rather than new markup,
/// new response keys and new element ids in the client.
/// </summary>
public interface IFavoritesProvider
{
    /// <summary>Stable key used by the API response and the client, for example "spotify".</summary>
    string Key { get; }

    /// <summary>Human readable platform name shown in the section header.</summary>
    string DisplayName { get; }

    /// <summary>Brand icon shown next to the platform name.</summary>
    string IconPath { get; }

    Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken);
}
