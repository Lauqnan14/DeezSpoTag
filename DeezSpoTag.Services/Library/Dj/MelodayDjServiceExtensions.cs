using System;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// Registers the DJ engine that Meloday uses.
/// </summary>
/// <remarks>
/// <para>Each strategy is registered as its own singleton and resolved as an
/// <see cref="IMelodayDjStrategy"/> collection, so nothing downstream holds a hardcoded
/// list of them. That is what makes a new kind of DJ a one-line change: the catalogue
/// discovers it, the Target Libraries selector lists it, and Random DJ can pick it,
/// with no settings schema, UI array or switch statement involved.</para>
///
/// <para>There is no scheduler, no run-state store, no definition CRUD and no
/// publication path registered here. A DJ is a personality applied inside Meloday's
/// existing generation, not a second product with its own cadence and its own playlists
/// — so a DJ playlist is written by the same <c>mix_cache</c> and published by the same
/// <c>PlaylistSyncService</c> as every other Meloday playlist, and there is nothing here
/// that could publish one separately.</para>
/// </remarks>
public static class MelodayDjServiceExtensions
{
    public static IServiceCollection AddMelodayDj(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<AnchorDjStrategy>();
        services.AddSingleton<CompanionDjStrategy>();
        services.AddSingleton<JourneyDjStrategy>();

        services.AddSingleton<IMelodayDjStrategy>(provider => provider.GetRequiredService<AnchorDjStrategy>());
        services.AddSingleton<IMelodayDjStrategy>(provider => provider.GetRequiredService<CompanionDjStrategy>());
        services.AddSingleton<IMelodayDjStrategy>(provider => provider.GetRequiredService<JourneyDjStrategy>());

        services.AddSingleton<IDjStrategyCatalog, DjStrategyCatalog>();
        services.AddSingleton<MelodayDjContextBuilder>();
        services.AddSingleton<DjSonicAffinityResolver>();

        return services;
    }
}
