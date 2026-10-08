using System;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Services.Library.Sonic;

/// <summary>
/// Registration for Sonic similarity.
///
/// Exposed as one extension method so the concrete index and service can stay
/// internal to this assembly. The composition root should not need to name an
/// implementation to switch it, which is what keeps a later approximate index a
/// one-line change behind the same interface.
/// </summary>
public static class SonicSimilarityServiceExtensions
{
    public static IServiceCollection AddSonicSimilarity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISonicSimilarityIndex, ExactSonicSimilarityIndex>();
        services.AddSingleton<ISonicSimilarityService, SonicSimilarityService>();
        return services;
    }
}
