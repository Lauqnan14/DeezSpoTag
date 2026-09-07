using Microsoft.Extensions.Options;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Wakes once a minute to check whether a scheduled Meloday slot is due. Playlist
/// generation itself happens once per scheduled occurrence, guarded by the persistent
/// per-instance run state (library + slot + mode).
/// </summary>
public sealed class MelodayHostedService : BackgroundService
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);

    private readonly MelodayService _melodayService;
    private readonly MelodayOptions _options;
    private readonly MelodaySettingsStore _settingsStore;
    private readonly MelodayRunStateStore _runStateStore;
    private readonly IConfiguration _configuration;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;
    private readonly ILogger<MelodayHostedService> _logger;

    public MelodayHostedService(
        MelodayService melodayService,
        IOptions<MelodayOptions> options,
        ILogger<MelodayHostedService> logger,
        MelodaySettingsStore settingsStore,
        MelodayRunStateStore runStateStore,
        DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator workCoordinator,
        IConfiguration configuration)
    {
        _melodayService = melodayService;
        _options = options.Value;
        _logger = logger;
        _settingsStore = settingsStore;
        _runStateStore = runStateStore;
        _workCoordinator = workCoordinator;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "Meloday"))
        {
            return;
        }

        var loggedDisabledState = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var effective = await _settingsStore.LoadAsync(_options);
                if (!effective.Enabled)
                {
                    loggedDisabledState = LogDisabledStateOnce(loggedDisabledState);
                    await _settingsStore.WaitForChangeAsync(stoppingToken);
                    continue;
                }
                loggedDisabledState = false;

                await RunDueSlotsAsync(effective, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Meloday schedule check failed.");
            }

            try
            {
                await _settingsStore.WaitForChangeAsync(Heartbeat, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private bool LogDisabledStateOnce(bool loggedDisabledState)
    {
        if (loggedDisabledState)
        {
            return true;
        }

        _logger.LogInformation("Meloday disabled; hosted service waiting for enable.");
        return true;
    }

    private async Task RunDueSlotsAsync(MelodayOptions effective, CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        var nowTime = TimeOnly.FromDateTime(now.DateTime);

        foreach (var library in effective.Libraries.Where(static schedule => schedule.Enabled))
        {
            foreach (var assignment in library.Slots)
            {
                var slot = effective.Slots.FirstOrDefault(candidate => string.Equals(
                    candidate.Id,
                    assignment.SlotId,
                    StringComparison.OrdinalIgnoreCase));
                if (slot is null)
                {
                    continue;
                }

                var stateKey = MelodayRunStateStore.Key(library.LibraryId, slot.Id, assignment.Mode);
                var state = await _runStateStore.GetAsync(stateKey, stoppingToken);
                if (!MelodayScheduleMath.IsDue(slot, today, nowTime, state, effective.MissedRunGraceMinutes))
                {
                    continue;
                }

                MelodayRunResult? result = null;
                await _workCoordinator.RunHeavyWorkAsync(
                    async token => result = await _melodayService.RunSlotAsync(library.LibraryId, slot.Id, assignment.Mode, token),
                    stoppingToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Meloday scheduled generation for {LibraryId}/{SlotId}/{Mode}: {Message}",
                        library.LibraryId,
                        slot.Id,
                        assignment.Mode,
                        result?.Message);
                }

                if (nowTime != TimeOnly.FromDateTime(DateTimeOffset.Now.DateTime))
                {
                    // The run crossed midnight; re-evaluate the day on the next heartbeat.
                    break;
                }
            }
        }
    }
}
