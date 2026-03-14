using DeezSpoTag.Workers;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Extensions;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Tagging;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Utils;

var contentRoot = ResolveContentRoot();
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = contentRoot
});

// Always load worker-local appsettings so worker toggles do not depend on Web appsettings.
var workerSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var workerEnvSettingsPath = Path.Combine(
    AppContext.BaseDirectory,
    $"appsettings.{builder.Environment.EnvironmentName}.json");
builder.Configuration.AddJsonFile(workerSettingsPath, optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(workerEnvSettingsPath, optional: true, reloadOnChange: true);

var dataDir = AppDataPathResolver.EnsureConfiguredDataAndConfigRoots(AppDataPathResolver.GetDefaultWorkersDataDir());
Directory.CreateDirectory(dataDir);
builder.Configuration["DataDirectory"] = dataDir;

var libraryDbPath = AppDataPathResolver.ResolveDbPathStrict(
    dataDir,
    "library",
    "deezspotag.db");
var libraryConnectionString = $"Data Source={libraryDbPath}";
builder.Configuration["ConnectionStrings:Library"] = libraryConnectionString;
Environment.SetEnvironmentVariable("LIBRARY_DB", libraryConnectionString);

var queueDbPath = AppDataPathResolver.ResolveDbPathStrict(
    dataDir,
    "queue",
    "queue.db");
var queueConnectionString = $"Data Source={queueDbPath}";
builder.Configuration["ConnectionStrings:Queue"] = queueConnectionString;
Environment.SetEnvironmentVariable("QUEUE_DB", queueConnectionString);

var enableContentSync = builder.Configuration.GetValue("Workers:ContentSync:Enabled", true);
var enableFileTaggingWorker = builder.Configuration.GetValue("Workers:FileTagging:Enabled", false);
var enableRealtimeLibraryScanner = builder.Configuration.GetValue("Workers:RealtimeLibraryScanner:Enabled", true);

// Add worker services
if (enableContentSync)
{
    builder.Services.AddHostedService<ContentSyncWorker>();
}
else
{
    Console.WriteLine("ℹ️  Workers.ContentSync disabled by configuration.");
}

if (enableFileTaggingWorker)
{
    builder.Services.AddSingleton<TaggingJobStore>();
    builder.Services.AddSingleton<FileTaggingWorker>();
    builder.Services.AddSingleton<ITaggingJobQueue>(sp => sp.GetRequiredService<FileTaggingWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FileTaggingWorker>());
}
else
{
    Console.WriteLine("ℹ️  Workers.FileTagging disabled by configuration.");
}

builder.Services.AddDeezSpoTagServices();
builder.Services.Configure<QobuzApiConfig>(builder.Configuration.GetSection("Qobuz"));
builder.Services.AddScoped<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>(sp =>
    new DeezSpoTag.Integrations.Deezer.DeezerSessionManager(
        sp.GetRequiredService<ILogger<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>>(),
        () => sp.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>().LoadSettings()));
builder.Services.AddScoped<DeezSpoTag.Integrations.Deezer.DeezerApiService>(sp =>
{
    var service = new DeezSpoTag.Integrations.Deezer.DeezerApiService(
        sp.GetRequiredService<ILogger<DeezSpoTag.Integrations.Deezer.DeezerApiService>>());
    service.SetSessionManager(sp.GetRequiredService<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>());
    return service;
});
builder.Services.AddScoped<DeezSpoTag.Integrations.Deezer.DeezerGatewayService>();
builder.Services.AddScoped<DeezSpoTag.Integrations.Deezer.DeezerClient>();
builder.Services.AddScoped<DeezSpoTag.Services.Download.AuthenticatedDeezerService>();
builder.Services.AddDownloadEngine();
builder.Services.AddSingleton<AudioQualitySignalAnalyzer>();

if (enableRealtimeLibraryScanner)
{
    Console.WriteLine("ℹ️  Workers.RealtimeLibraryScanner requires Web host services and is disabled in Workers host.");
}
else
{
    Console.WriteLine("ℹ️  Workers.RealtimeLibraryScanner disabled by configuration.");
}

builder.Services.AddHostedService<DeezSpoTag.Services.Download.Shared.DeezSpoTagQueueBackgroundService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Qobuz.IQobuzDownloadService, DeezSpoTag.Services.Download.Qobuz.QobuzDownloadService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Qobuz.QobuzEngineProcessor>();
builder.Services.AddHostedService<DeezSpoTag.Services.Download.Qobuz.QobuzQueueBackgroundService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Tidal.TidalDownloadService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Tidal.TidalEngineProcessor>();
builder.Services.AddHostedService<DeezSpoTag.Services.Download.Tidal.TidalQueueBackgroundService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Amazon.IAmazonDownloadService, DeezSpoTag.Services.Download.Amazon.AmazonDownloadService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Amazon.AmazonEngineProcessor>();
builder.Services.AddHostedService<DeezSpoTag.Services.Download.Amazon.AmazonQueueBackgroundService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.IAppleDownloadService, DeezSpoTag.Services.Download.Apple.AppleDownloadService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleWebPlaybackClient>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleKeyService>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleHlsDownloader>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleExternalToolRunner>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleWidevineCdm>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleWidevineLicenseClient>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleWrapperDecryptor>();
builder.Services.AddSingleton<DeezSpoTag.Services.Download.Apple.AppleEngineProcessor>();
builder.Services.AddHostedService<DeezSpoTag.Services.Download.Apple.AppleQueueBackgroundService>();

var host = builder.Build();

Console.WriteLine("🚀 DeezSpoTag Workers starting...");

await host.RunAsync();

static string ResolveContentRoot()
{
    var baseDir = AppContext.BaseDirectory;
    var candidate = Path.GetFullPath(Path.Join(baseDir, "..", "..", "..", "..", "DeezSpoTag.Web"));
    if (Directory.Exists(candidate))
    {
        return candidate;
    }

    return Directory.GetCurrentDirectory();
}
