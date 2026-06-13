using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadControllerServices(
    DownloadQueueRepository queueRepository,
    DeezSpoTagSettingsService settingsService,
    DownloadOrchestrationService orchestrationService,
    IDeezSpoTagListener deezspotagListener,
    ISpotifyIdResolver spotifyIdResolver,
    DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
    DownloadDedupeService dedupeService)
{
    public DownloadQueueRepository QueueRepository { get; } = queueRepository;
    public DeezSpoTagSettingsService SettingsService { get; } = settingsService;
    public DownloadOrchestrationService OrchestrationService { get; } = orchestrationService;
    public IDeezSpoTagListener DeezSpoTagListener { get; } = deezspotagListener;
    public ISpotifyIdResolver SpotifyIdResolver { get; } = spotifyIdResolver;
    public DeezSpoTag.Services.Library.LibraryRepository LibraryRepository { get; } = libraryRepository;
    public DownloadDedupeService DedupeService { get; } = dedupeService;
}
