using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Services.Download;

/// <summary>
/// Durable record of when a public download API session was last verified.
/// </summary>
/// <remarks>
/// <para>
/// Verification-driven retry needs to answer one question after a process restart: has the public
/// API session this item was waiting on been verified since? That question cannot be answered from
/// memory, so the record is persisted through <see cref="DeezSpoTagSettingsService"/> alongside
/// the rest of the app configuration.
/// </para>
/// <para>
/// A slug with no entry means NEVER verified and is deliberately treated as unverified. Downloading
/// through a public API whose session was never verified cannot succeed, so an absent record must
/// not be read as "probably fine".
/// </para>
/// <para>
/// This store only records state. It does not gate enqueues and does not decide whether a download
/// may run; that remains the job of the existing watchlist readiness gate and the queue
/// orchestrator.
/// </para>
/// </remarks>
public sealed class PublicApiSessionVerificationStore
{
    /// <summary>Public download APIs that require a verified session before they can serve audio.</summary>
    public static readonly IReadOnlyList<string> PublicApiSlugs = new[] { "qobuz", "tidal", "amazon" };

    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly object _sync = new();

    public PublicApiSessionVerificationStore(DeezSpoTagSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Normalizes a provider slug to the key used in the persisted record. Returns false for a
    /// blank or non-public slug so callers cannot accidentally record an unrelated provider.
    /// </summary>
    public static bool TryNormalizeSlug(string? slug, out string normalized)
    {
        normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length > 0
            && PublicApiSlugs.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records that a session verification just completed. This is the only write path: a slug
    /// appears in the record only after a real verification, never optimistically.
    /// </summary>
    public void RecordVerified(string slug, DateTimeOffset? verifiedAtUtc = null)
    {
        if (!TryNormalizeSlug(slug, out var normalized))
        {
            return;
        }

        var stamp = (verifiedAtUtc ?? DateTimeOffset.UtcNow)
            .UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        lock (_sync)
        {
            var settings = _settingsService.LoadSettings();
            settings.PublicApiSessionVerifiedAt ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            settings.PublicApiSessionVerifiedAt[normalized] = stamp;
            _settingsService.SaveSettings(settings);
        }
    }

    /// <summary>
    /// Returns the last verification time for a public API, or null when it was never verified.
    /// </summary>
    public DateTimeOffset? GetLastVerifiedAtUtc(string slug)
    {
        if (!TryNormalizeSlug(slug, out var normalized))
        {
            return null;
        }

        var settings = _settingsService.LoadSettings();
        var recorded = settings.PublicApiSessionVerifiedAt;
        if (recorded == null)
        {
            return null;
        }

        var raw = recorded.TryGetValue(normalized, out var value) ? value : null;
        return TryParseTimestamp(raw, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// True when a public API has no verification record. Treated as unverified everywhere, because
    /// a session that was never verified cannot serve a download.
    /// </summary>
    public bool IsUnverified(string slug)
        => GetLastVerifiedAtUtc(slug) is null;

    /// <summary>
    /// True when at least one enabled public API has no verification record. This is the enqueue
    /// stamp condition: the item was queued while something it depends on was not yet usable.
    /// </summary>
    public bool HasAnyUnverifiedPublicApi(IEnumerable<string>? enabledSlugs = null)
    {
        var slugs = NormalizeSlugs(enabledSlugs);
        return slugs.Any(IsUnverified);
    }

    /// <summary>
    /// True when the given slug has been verified at or after <paramref name="since"/>. An item
    /// stamped while unverified is releasable only by a verification that happened afterwards.
    /// </summary>
    public bool WasVerifiedSince(string slug, DateTimeOffset? since)
    {
        var verifiedAt = GetLastVerifiedAtUtc(slug);
        if (verifiedAt is null)
        {
            return false;
        }

        return since is null || verifiedAt.Value >= since.Value;
    }

    private static IEnumerable<string> NormalizeSlugs(IEnumerable<string>? enabledSlugs)
    {
        if (enabledSlugs is null)
        {
            return PublicApiSlugs;
        }

        var normalized = enabledSlugs
            .Where(static slug => TryNormalizeSlug(slug, out _))
            .Select(static slug => slug.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count > 0 ? normalized : PublicApiSlugs;
    }

    private static bool TryParseTimestamp(string? raw, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AllowWhiteSpaces,
            out parsed);
}
