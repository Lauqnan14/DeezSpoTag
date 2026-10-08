using DeezSpoTag.Web.Services.Updates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
/// Reports the running build version and the newest release on the configured branch.
/// </summary>
/// <remarks>
/// Anonymous because the sidebar renders for signed-out visitors too, so the version stays visible
/// before login. The endpoint exposes no user data, only repository coordinates and the comparison
/// result.
/// </remarks>
[ApiController]
[AllowAnonymous]
public sealed class AppVersionApiController : ControllerBase
{
    private readonly UpdateCheckService _updateCheckService;

    public AppVersionApiController(UpdateCheckService updateCheckService)
    {
        _updateCheckService = updateCheckService;
    }

    /// <summary>
    /// Returns the running version, the branch it is reported against, and the newest release on
    /// that branch. Honours the shared minimum check interval, so repeated calls are cheap.
    /// </summary>
    [HttpGet("api/app-version")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var status = await _updateCheckService.GetStatusAsync(cancellationToken);
        return Ok(new
        {
            currentVersion = status.CurrentVersion,
            status.Branch,
            status.BranchUrl,
            status.RepositoryUrl,
            status.LatestVersion,
            status.LatestUrl,
            status.UpdateAvailable,
            status.CheckedUtc,
            status.IsStale
        });
    }
}
