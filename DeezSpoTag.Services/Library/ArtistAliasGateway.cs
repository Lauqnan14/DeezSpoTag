using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Services.Library;

/// <summary>
/// Process-wide static access to artist alias resolution for components that
/// are not DI-constructed (download path-template processors, static helpers).
/// Configured at host startup from the DI container; resolution falls back to
/// the trimmed input when not configured or the library DB is unavailable.
/// </summary>
public static class ArtistAliasGateway
{
    private static volatile ArtistAliasService? _service;

    public static void Configure(ArtistAliasService service)
    {
        _service = service;
        // Warm the snapshot so synchronous resolution hits immediately.
        _ = service.GetAliasMapAsync(CancellationToken.None);
    }

    /// <summary>Resolves a single artist name to the user's preferred name (sync, cached).</summary>
    public static string Resolve(string? name)
    {
        var service = _service;
        return service == null
            ? (name ?? string.Empty).Trim()
            : service.ResolvePreferred(name);
    }

    /// <summary>
    /// Rewrites a combined credit ("A feat. B") so every alias part becomes the
    /// preferred name, preserving separators (sync, cached snapshot).
    /// </summary>
    public static string ResolveCredit(string? credit)
    {
        var service = _service;
        return service == null
            ? (credit ?? string.Empty).Trim()
            : service.ResolveCredit(credit);
    }

    /// <summary>
    /// Rewrites a combined credit ("A feat. B") so every alias part becomes the
    /// preferred name, preserving separators.
    /// </summary>
    public static Task<string> ResolveCreditAsync(string? credit, CancellationToken cancellationToken = default)
    {
        var service = _service;
        return service == null
            ? Task.FromResult((credit ?? string.Empty).Trim())
            : service.ResolveCreditAsync(credit, cancellationToken);
    }
}
