using DeezSpoTag.Services.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Services.Extensions;

/// <summary>
/// Service collection extensions for authentication services
/// </summary>
public static class AuthenticationServiceExtensions
{
    /// <summary>
    /// Add authentication services to the service collection
    /// </summary>
    public static IServiceCollection AddDeezSpoTagAuthentication(this IServiceCollection services)
    {
        // Register crypto service
        services.AddSingleton<ICryptoService, CryptoService>();

        // Register unified Deezer authentication service
        services.AddScoped<IDeezerAuthenticationService, DeezerAuthenticationService>();

        return services;
    }
}