using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     Registration helpers for the SoundCloud download engine.
/// </summary>
/// <remarks>
///     <para>
///         Registered from <see cref="DeezSpoTagServiceExtensions.AddDeezSpoTagQueue"/> rather than from a
///         single host, so every host that runs the download queue - the web tier and the workers host alike -
///         can resolve the engine's dependencies. A host that cannot satisfy them would take the whole queue
///         down with it, because the processor collection is resolved as a unit.
///     </para>
///     <para>
///         The credential provider is registered as <see cref="NullSoundCloudCredentialProvider"/> by default,
///         so a host without the protected credential store keeps public-only operation rather than failing.
///         The web tier replaces it afterwards with its own registration; the later registration wins.
///     </para>
/// </remarks>
public static class SoundCloudServiceExtensions
{
    /// <summary>
    ///     Registers the SoundCloud transport, protocol client, download service, and queue processor.
    /// </summary>
    public static IServiceCollection AddSoundCloudDownloadEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(SoundCloudClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(SoundCloudClient.UserAgent);
        });

        services.AddHttpClient(SoundCloudClient.StreamHttpClientName, client =>
        {
            // Segments are small and numerous, so a short per-request timeout is right here even though the
            // protocol calls above need a longer one.
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(SoundCloudClient.UserAgent);
        });

        services.AddSingleton<ISoundCloudCredentialProvider>(NullSoundCloudCredentialProvider.Instance);
        services.AddSingleton<ISoundCloudClient, SoundCloudClient>();
        services.AddSingleton<SoundCloudDownloadService>();

        return services;
    }
}