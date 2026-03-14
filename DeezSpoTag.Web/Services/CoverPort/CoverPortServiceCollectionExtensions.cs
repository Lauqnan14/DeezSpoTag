namespace DeezSpoTag.Web.Services.CoverPort;

public static class CoverPortServiceCollectionExtensions
{
    public static IServiceCollection AddCoverPortingServices(this IServiceCollection services)
    {
        services.AddSingleton<CoverSourceHttpService>();
        services.AddSingleton<CoverPerceptualHashService>();

        services.AddTransient<CoverArtArchiveCoverSource>();
        services.AddTransient<ItunesCoverSource>();
        services.AddTransient<DeezerCoverSource>();
        services.AddTransient<DiscogsCoverSource>();
        services.AddTransient<LastFmCoverSource>();
        services.AddTransient<ICoverSource>(sp => sp.GetRequiredService<CoverArtArchiveCoverSource>());
        services.AddTransient<ICoverSource>(sp => sp.GetRequiredService<ItunesCoverSource>());
        services.AddTransient<ICoverSource>(sp => sp.GetRequiredService<DeezerCoverSource>());
        services.AddTransient<ICoverSource>(sp => sp.GetRequiredService<DiscogsCoverSource>());
        services.AddTransient<ICoverSource>(sp => sp.GetRequiredService<LastFmCoverSource>());
        services.AddSingleton<CoverSearchAndDownloadService>();
        services.AddSingleton<CoverLibraryMaintenanceService>();

        return services;
    }
}
