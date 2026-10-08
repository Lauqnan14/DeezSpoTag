using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
///     Soulseek folder sharing.
/// </summary>
/// <remarks>
///     <para>
///         Read and reporting live here. The one piece of <em>state</em> this controller writes is the per-folder
///         share configuration, and it is the same single place the folder tab writes, so there is no second
///         competing toggle.
///     </para>
///     <para>
///         Nothing here configures slskd. slskd has no share-write API, so <c>sync</c> reconciles and reports and
///         returns a configuration block for the user to apply; it never claims to have changed anything
///         remote.
///     </para>
/// </remarks>
[Route("api/v1/soulseek/shares")]
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[EnableRateLimiting("DefaultApi")]
public sealed class SoulseekFolderShareApiController : ControllerBase
{
    private readonly ISoulseekShareService _shareService;
    private readonly SoulseekSettingsService _settingsService;

    /// <summary>Initializes a new instance of the <see cref="SoulseekFolderShareApiController"/> class.</summary>
    public SoulseekFolderShareApiController(
        ISoulseekShareService shareService,
        SoulseekSettingsService settingsService)
    {
        _shareService = shareService;
        _settingsService = settingsService;
    }

    /// <summary>Lists every folder with its Soulseek share state, so the UI can render the folder tab.</summary>
    [HttpGet("folders")]
    public async Task<IActionResult> GetFolders(CancellationToken cancellationToken)
    {
        if (!_settingsService.IsLibraryConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var all = await _settingsService.GetAllFoldersAsync(cancellationToken);
        return Ok(new
        {
            folders = all.Select(folder => ToFolderShareResponse(folder, folder.SoulseekShareEnabled)),
            libraryConfigured = true
        });
    }

    /// <summary>Updates one folder's share alias and filters.</summary>
    [HttpPut("folders/{folderId:long}")]
    public async Task<IActionResult> UpdateFolder(
        long folderId,
        [FromBody] UpdateFolderShareRequest request,
        CancellationToken cancellationToken)
    {
        if (!_settingsService.IsLibraryConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var updated = await _settingsService.UpdateFolderShareAsync(
            folderId,
            request?.Alias,
            request?.Include,
            request?.Exclude,
            cancellationToken);

        return updated is null
            ? NotFound(new { error = "Folder not found." })
            : Ok(ToFolderShareResponse(updated, updated.SoulseekShareEnabled));
    }

    /// <summary>Enables or disables sharing for one folder. This is the folder tab's only toggle.</summary>
    [HttpPatch("folders/{folderId:long}/enabled")]
    public async Task<IActionResult> SetEnabled(
        long folderId,
        [FromBody] SetFolderShareEnabledRequest request,
        CancellationToken cancellationToken)
    {
        if (!_settingsService.IsLibraryConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var updated = await _settingsService.SetFolderShareEnabledAsync(folderId, request?.Enabled == true, cancellationToken);
        return updated is null
            ? NotFound(new { error = "Folder not found." })
            : Ok(ToFolderShareResponse(updated, updated.SoulseekShareEnabled));
    }

    /// <summary>
    ///     Reconciles the desired shares against what slskd serves, and returns a configuration block.
    /// </summary>
    /// <remarks>
    ///     This does not modify the slskd instance. It reports drift and hands back the block to apply.
    /// </remarks>
    [HttpPost("sync")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        var reconciliation = await _shareService.ReconcileAsync(cancellationToken);
        return Ok(ToReconciliationResponse(reconciliation));
    }

    /// <summary>Asks slskd to rescan its shares so newly added files become searchable.</summary>
    [HttpPost("scan")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> Scan(CancellationToken cancellationToken)
    {
        var started = await _shareService.RequestRescanAsync(cancellationToken);
        return started
            ? Ok(new { started = true })
            : StatusCode(503, new { error = "Could not start a Soulseek share rescan." });
    }

    /// <summary>Records a scan outcome against a single folder.</summary>
    [HttpPost("folders/{folderId:long}/scan")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> ScanFolder(long folderId, CancellationToken cancellationToken)
    {
        if (!_settingsService.IsLibraryConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var folder = await _settingsService.RecordFolderShareScanAsync(
            folderId,
            "ok",
            DateTimeOffset.UtcNow,
            cancellationToken);

        return folder is null
            ? NotFound(new { error = "Folder not found." })
            : Ok(ToFolderShareResponse(folder, folder.SoulseekShareEnabled));
    }

    /// <summary>Reads the last share scan state.</summary>
    [HttpGet("scan/status")]
    public async Task<IActionResult> GetScanStatus(CancellationToken cancellationToken)
    {
        var status = await _shareService.GetScanStatusAsync(cancellationToken);
        return Ok(new
        {
            status.IsRunning,
            status.LastScanUtc,
            status.ShareCount,
            status.FileCount,
            status.Error,
            status.SlskdUnavailable
        });
    }

    /// <summary>Previews every folder that would be shared.</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(CancellationToken cancellationToken)
    {
        var reconciliation = await _shareService.ReconcileAsync(cancellationToken);
        return Ok(new
        {
            generatedAtUtc = reconciliation.GeneratedAtUtc,
            enabled = reconciliation.EnabledFolders.Select(ToDesiredResponse),
            skipped = reconciliation.SkippedFolders.Select(entry => new { folderId = entry.FolderId, reason = entry.Reason }),
            missing = reconciliation.MissingFromSlskd.Select(ToDesiredResponse),
            unexpected = reconciliation.UnexpectedlyShared,
            diagnostics = reconciliation.Diagnostics,
            generatedYaml = reconciliation.GeneratedYaml,
            reconciliation.SlskdUnavailable
        });
    }

    /// <summary>Previews one folder.</summary>
    [HttpGet("folders/{folderId:long}/preview")]
    public async Task<IActionResult> PreviewFolder(long folderId, CancellationToken cancellationToken)
    {
        var folder = await _settingsService.GetFolderShareAsync(folderId, cancellationToken);
        if (folder is null)
        {
            return NotFound(new { error = "Folder not found." });
        }

        var desired = await _settingsService.GetDesiredSharesAsync(cancellationToken);
        var thisFolder = desired.FirstOrDefault(share => share.FolderId == folderId);

        return Ok(new
        {
            folderId,
            folder.RootPath,
            folder.DisplayName,
            enabled = folder.SoulseekShareEnabled,
            folder.SoulseekShareAlias,
            include = folder.SoulseekShareInclude ?? [],
            exclude = folder.SoulseekShareExclude ?? [],
            generatedYaml = thisFolder is null ? null : _shareService.GenerateShareConfiguration([thisFolder])
        });
    }

    private static object ToFolderShareResponse(FolderDto folder, bool enabled) => new
    {
        folder.Id,
        folder.RootPath,
        folder.DisplayName,
        enabled,
        alias = folder.SoulseekShareAlias,
        include = folder.SoulseekShareInclude ?? [],
        exclude = folder.SoulseekShareExclude ?? [],
        scanStatus = folder.SoulseekShareScanStatus,
        scanAt = folder.SoulseekShareScanAt
    };

    private static object ToDesiredResponse(SoulseekDesiredShare share) => new
    {
        folderId = share.FolderId,
        localPath = share.LocalPath,
        alias = share.Alias,
        include = share.IncludeFilters ?? [],
        exclude = share.ExcludeFilters ?? []
    };

    private static object ToReconciliationResponse(SoulseekShareReconciliation reconciliation) => new
    {
        reconciliation.GeneratedAtUtc,
        enabled = reconciliation.EnabledFolders.Select(ToDesiredResponse),
        skipped = reconciliation.SkippedFolders.Select(entry => new { folderId = entry.FolderId, reason = entry.Reason }),
        missing = reconciliation.MissingFromSlskd.Select(ToDesiredResponse),
        unexpected = reconciliation.UnexpectedlyShared,
        diagnostics = reconciliation.Diagnostics,
        generatedYaml = reconciliation.GeneratedYaml,
        reconciliation.SlskdUnavailable
    };
}

/// <summary>The alias and filters for one folder's share.</summary>
public sealed record UpdateFolderShareRequest(string? Alias, IReadOnlyList<string>? Include, IReadOnlyList<string>? Exclude);

/// <summary>Whether a folder is shared to Soulseek.</summary>
public sealed record SetFolderShareEnabledRequest(bool? Enabled);
