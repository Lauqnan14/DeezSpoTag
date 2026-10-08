namespace DeezSpoTag.Core.Constants;

/// <summary>
///     The health states a public metadata provider can be reported in.
/// </summary>
/// <remarks>
///     Amazon, Qobuz and Tidal each run their own provider registry, and each used to spell this
///     vocabulary out as raw literals at every use - twenty-one times per registry, three times
///     over. The states are persisted as a provider's status and compared against strings coming
///     back from health probes, so a copy that drifts is not cosmetic: a probe reporting
///     <c>rate_limited</c> would stop matching a registry that had drifted to
///     <c>ratelimited</c>, and the provider would be recorded as healthy while it is throttled.
///     <para>
///         These values are the single definition for that vocabulary. They are deliberately not
///         an enum: the states are compared as strings against probe output and stored as text,
///         so they have to stay strings to keep those comparisons and stored rows working.
///     </para>
/// </remarks>
public static class ProviderHealthStatus
{
    /// <summary>The provider answered its health check and is serving normally.</summary>
    public const string Online = "online";

    /// <summary>The provider answered, but not in a way that can be served - a permanent failure.</summary>
    public const string Offline = "offline";

    /// <summary>The provider is reachable but degraded; requests may still succeed.</summary>
    public const string Degraded = "degraded";

    /// <summary>A temporary failure that is expected to clear on its own, such as a 5xx response.</summary>
    public const string Transient = "transient";

    /// <summary>The provider is throttling us. Reported from HTTP 429.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The health check did not complete inside its deadline.</summary>
    public const string Timeout = "timeout";
}
