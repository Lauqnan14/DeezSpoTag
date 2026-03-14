namespace DeezSpoTag.Integrations.Qobuz;

public static class QobuzStoreManager
{
    public static readonly string[] Stores =
    {
        "ar-es",
        "au-en",
        "at-de",
        "be-fr",
        "be-nl",
        "br-pt",
        "ca-en",
        "ca-fr",
        "cl-es",
        "co-es",
        "dk-en",
        "fi-en",
        "fr-fr",
        "de-de",
        "ie-en",
        "it-it",
        "jp-ja",
        "lu-de",
        "lu-fr",
        "mx-es",
        "nl-nl",
        "nz-en",
        "no-en",
        "pt-pt",
        "es-es",
        "se-en",
        "ch-de",
        "ch-fr",
        "gb-en",
        "us-en"
    };

    public static bool IsValidStore(string? store)
        => !string.IsNullOrWhiteSpace(store)
           && Stores.Contains(store.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeStore(string? store, string defaultStore)
    {
        if (IsValidStore(store))
        {
            return store!.Trim();
        }

        return defaultStore;
    }

    public static string GetZone(string store)
    {
        var normalized = store.Trim();
        var dash = normalized.IndexOf('-');
        return dash > 0
            ? normalized[..dash].ToUpperInvariant()
            : normalized.ToUpperInvariant();
    }
}
