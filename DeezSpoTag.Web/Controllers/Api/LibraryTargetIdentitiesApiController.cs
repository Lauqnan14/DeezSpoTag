using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/target-identities")]
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
public sealed class LibraryTargetIdentitiesApiController : ControllerBase
{
    private static readonly string[] SupportedServices = ["plex", "jellyfin", "navidrome"];

    private readonly LibraryRepository _repository;
    private readonly PlatformAuthService _authService;
    private readonly MediaServerLibraryRefreshService _refreshService;

    public LibraryTargetIdentitiesApiController(
        LibraryRepository repository,
        PlatformAuthService authService,
        MediaServerLibraryRefreshService refreshService)
    {
        _repository = repository;
        _authService = authService;
        _refreshService = refreshService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] long? folderId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var services = await BuildServiceStatesAsync(cancellationToken);
        var coverage = await _repository.GetTargetServerIdentityCoverageAsync(
            SupportedServices,
            folderId,
            cancellationToken);
        var coverageByService = coverage.ToDictionary(static item => item.Service, StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            folderId,
            services = services.Select(service =>
            {
                coverageByService.TryGetValue(service.Service, out var serviceCoverage);
                return new
                {
                    service.Service,
                    service.Label,
                    service.Connected,
                    Coverage = serviceCoverage ?? new TargetServerIdentityCoverageDto(service.Service, 0, 0, 0),
                    Progress = _refreshService.GetTargetIdentityRefreshProgress(service.Service, folderId)
                };
            }).ToList()
        });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(
        [FromBody] TargetIdentityRefreshRequest? request,
        CancellationToken cancellationToken)
        => await RefreshInternalAsync(request, resetFirst: false, cancellationToken);

    [HttpPost("reset-refresh")]
    public async Task<IActionResult> ResetAndRefresh(
        [FromBody] TargetIdentityRefreshRequest? request,
        CancellationToken cancellationToken)
        => await RefreshInternalAsync(request, resetFirst: true, cancellationToken);

    private async Task<IActionResult> RefreshInternalAsync(
        TargetIdentityRefreshRequest? request,
        bool resetFirst,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var services = await BuildServiceStatesAsync(cancellationToken);
        var selected = NormalizeServices(request?.Services);
        if (selected.Count == 0)
        {
            selected = services.Where(static service => service.Connected).Select(static service => service.Service).ToList();
        }

        var connected = services
            .Where(static service => service.Connected)
            .Select(static service => service.Service)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runnable = selected.Where(connected.Contains).ToList();
        if (runnable.Count == 0)
        {
            return BadRequest(new { error = "No connected target server was selected." });
        }

        var resultTasks = runnable.Select(async service =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _refreshService.FetchTargetIdentitiesAsync(
                service,
                request?.FolderId,
                resetFirst,
                cancellationToken);
        });
        var results = (await Task.WhenAll(resultTasks)).ToList();

        var refreshedServices = results.Count(static result => result.Success);

        return Ok(new
        {
            reset = resetFirst,
            requested = selected,
            processed = runnable,
            refreshed = refreshedServices,
            failed = runnable.Count - refreshedServices,
            results
        });
    }

    private async Task<List<TargetIdentityServiceState>> BuildServiceStatesAsync(CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        return
        [
            new("plex", "Plex", IsPlexConnected(state.Plex)),
            new("jellyfin", "Jellyfin", IsJellyfinConnected(state.Jellyfin)),
            new("navidrome", "Navidrome", IsNavidromeConnected(state.Navidrome))
        ];
    }

    private static List<string> NormalizeServices(IReadOnlyCollection<string>? services)
    {
        if (services == null || services.Count == 0)
        {
            return [];
        }

        return services
            .Select(static service => (service ?? string.Empty).Trim().ToLowerInvariant())
            .Where(static service => SupportedServices.Contains(service, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPlexConnected(PlexAuth? plex)
        => plex != null
           && !string.IsNullOrWhiteSpace(plex.Url)
           && !string.IsNullOrWhiteSpace(plex.Token);

    private static bool IsJellyfinConnected(JellyfinAuth? jellyfin)
        => jellyfin != null
           && !string.IsNullOrWhiteSpace(jellyfin.Url)
           && !string.IsNullOrWhiteSpace(jellyfin.ApiKey);

    private static bool IsNavidromeConnected(NavidromeAuth? navidrome)
        => navidrome != null
           && !string.IsNullOrWhiteSpace(navidrome.Url)
           && !string.IsNullOrWhiteSpace(navidrome.Username)
           && !string.IsNullOrWhiteSpace(navidrome.Password);

    public sealed record TargetIdentityRefreshRequest(
        IReadOnlyCollection<string>? Services,
        long? FolderId);

    private sealed record TargetIdentityServiceState(
        string Service,
        string Label,
        bool Connected);

}
