using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Registration helpers for the Soulseek download engine.
/// </summary>
/// <remarks>
///     <para>
///         The engine is registered from here, not from a single host, because
///         <see cref="DeezSpoTagServiceExtensions.AddDeezSpoTagQueue"/> registers
///         <see cref="SoulseekEngineProcessor"/> for every host. The workers host therefore has to be able to
///         satisfy this processor's dependencies, otherwise resolving the processor collection throws and takes
///         the whole download queue down with it.
///     </para>
///     <para>
///         There is deliberately no feature flag. No other download engine has one, so the only way a user
///         switches Soulseek off is the way they switch every other engine off: not selecting it as a source,
///         or clearing it in the custom engine order.
///     </para>
/// </remarks>
public static class SoulseekServiceExtensions
{
    /// <summary>
    ///     Registers the Soulseek transport adapter and the services the engine and its maintenance task need.
    /// </summary>
    /// <remarks>
    ///     The credential provider is registered as <see cref="NullSoulseekCredentialProvider"/> by default. A
    ///     host that owns the encrypted credential store replaces it afterwards with its own registration; the
    ///     later registration wins, which is why the web tier calls this before adding its provider.
    /// <para>
    ///     A host without a hub - the workers host - still resolves the engine, so the publisher falls back to
    ///     the existing null object rather than requiring a host-specific implementation.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSoulseekDownloadEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSlskdClient();
        services.AddSingleton<ISoulseekCredentialProvider>(NullSoulseekCredentialProvider.Instance);
        services.AddSingleton<ISoulseekRealtimePublisher>(NullSoulseekRealtimePublisher.Instance);

        services.AddSingleton<ISoulseekConnectionService>(sp => new SoulseekConnectionService(
            sp.GetRequiredService<ISlskdClient>(),
            sp.GetRequiredService<ISoulseekCredentialProvider>(),
            sp.GetRequiredService<ILogger<SoulseekConnectionService>>(),
            sp.GetRequiredService<ISoulseekRealtimePublisher>()));
        services.AddSingleton<SoulseekRepository>();
        services.AddSingleton<SoulseekSettingsService>();
        services.AddSingleton<ISoulseekPeerPolicyService, SoulseekPeerPolicyService>();
        services.AddSingleton<ISoulseekResultScoringService, SoulseekResultScoringService>();
        services.AddSingleton<ISoulseekSearchService>(sp => new SoulseekSearchService(
            sp.GetRequiredService<ISlskdClient>(),
            sp.GetRequiredService<ISoulseekCredentialProvider>(),
            sp.GetRequiredService<ISoulseekConnectionService>(),
            sp.GetRequiredService<ISoulseekResultScoringService>(),
            sp.GetRequiredService<SoulseekSettingsService>(),
            sp.GetRequiredService<SoulseekRepository>(),
            sp.GetRequiredService<ILogger<SoulseekSearchService>>(),
            sp.GetRequiredService<ISoulseekRealtimePublisher>()));
        services.AddSingleton<ISoulseekTransferService, SoulseekTransferService>();
        services.AddSingleton<ISoulseekDownloadService, SoulseekDownloadService>();
        services.AddSingleton<ISoulseekShareService, SoulseekShareService>();

        // Housekeeping rides the existing queue loop through the generic maintenance hook, so no per-engine
        // background service is introduced.
        services.AddSingleton<IQueueMaintenanceTask>(sp => new SoulseekMaintenanceTask(
            sp.GetRequiredService<ISoulseekCredentialProvider>(),
            sp.GetRequiredService<ISoulseekPeerPolicyService>(),
            sp.GetRequiredService<ISoulseekSearchService>(),
            sp.GetRequiredService<ISoulseekTransferService>(),
            sp.GetRequiredService<SoulseekSettingsService>(),
            sp.GetRequiredService<ILogger<SoulseekMaintenanceTask>>()));

        return services;
    }
}
