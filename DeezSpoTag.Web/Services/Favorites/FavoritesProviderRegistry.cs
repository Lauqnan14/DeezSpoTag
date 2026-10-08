using Microsoft.Extensions.Options;

namespace DeezSpoTag.Web.Services.Favorites;

/// <summary>
/// Ordered set of favorites providers. One failing or unconfigured provider must never hide the
/// others, so each entry is resolved independently and carries its own availability reason.
/// </summary>
public sealed class FavoritesProviderRegistry
{
    private static readonly string[] DefaultOrder =
    [
        "spotify", "deezer", "qobuz", "appleMusic", "ytmusic", "tidal", "discogs"
    ];

    private readonly IReadOnlyList<IFavoritesProvider> _ordered;

    public FavoritesProviderRegistry(IEnumerable<IFavoritesProvider> providers)
    {
        var byKey = new Dictionary<string, IFavoritesProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers ?? [])
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.Key))
            {
                continue;
            }

            byKey[provider.Key] = provider;
        }

        var ordered = new List<IFavoritesProvider>(byKey.Count);
        foreach (var provider in DefaultOrder
                     .Select(key =>
                     {
                         byKey.Remove(key, out var removed);
                         return removed;
                     })
                     .Where(provider => provider is not null))
        {
            ordered.Add(provider!);
        }

        // Anything registered outside the default order still renders, after the known ones.
        ordered.AddRange(byKey.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase));
        _ordered = ordered;
    }

    public IReadOnlyList<IFavoritesProvider> Providers => _ordered;
}
