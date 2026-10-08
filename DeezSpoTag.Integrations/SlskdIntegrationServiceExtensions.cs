using DeezSpoTag.Integrations.Soulseek;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Integrations;

/// <summary>
///     Registration helpers for the <c>slskd</c> transport adapter.
/// </summary>
public static class SlskdIntegrationServiceExtensions
{
    /// <summary>The name of the configured <see cref="HttpClient"/> used for slskd calls.</summary>
    public const string HttpClientName = "slskd";

    /// <summary>Default request timeout for short slskd control calls.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Registers the mechanical <c>slskd</c> API client.
    /// </summary>
    /// <remarks>
    ///     The client is registered against a named, fully configured <see cref="HttpClient"/> so that
    ///     timeouts and handler behaviour come from DI rather than from per-call hard-coding. A longer
    ///     timeout is set for the download client separately by the transfer service.
    /// </remarks>
    public static IServiceCollection AddSlskdClient(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, client => client.Timeout = DefaultTimeout);
        services.AddHttpClient<ISlskdClient, SlskdClient>(HttpClientName);
        return services;
    }
}
