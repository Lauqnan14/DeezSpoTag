using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class ExternalFileImportService
{
    private readonly ILogger<ExternalFileImportService> _logger;
    private readonly TaggingProfileService _profileService;

    public ExternalFileImportService(
        ILogger<ExternalFileImportService> logger,
        TaggingProfileService profileService)
    {
        _logger = logger;
        _profileService = profileService;
    }

    public async Task<ExternalImportPreviewResult> PreviewAsync(ExternalImportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return new ExternalImportPreviewResult(false, "Source path is required.", new List<ExternalImportPreviewItem>());
        }

        if (!Directory.Exists(request.SourcePath))
        {
            return new ExternalImportPreviewResult(false, "Source path does not exist.", new List<ExternalImportPreviewItem>());
        }

        var profile = await ResolveProfileAsync(request.ProfileId);
        if (profile == null)
        {
            return new ExternalImportPreviewResult(false, "No tagging profile available.", new List<ExternalImportPreviewItem>());
        }

        _logger.LogInformation("External import preview requested for Path");
        return new ExternalImportPreviewResult(true, null, new List<ExternalImportPreviewItem>());
    }

    public async Task<ExternalImportResult> ImportAsync(ExternalImportRequest request, CancellationToken cancellationToken)
    {
        var preview = await PreviewAsync(request, cancellationToken);
        if (!preview.Success)
        {
            return new ExternalImportResult(false, preview.Message ?? "Preview failed.", 0, 0);
        }

        _logger.LogInformation("External import started for Path");
        return new ExternalImportResult(true, null, 0, 0);
    }

    private async Task<TaggingProfile?> ResolveProfileAsync(string? profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var byId = await _profileService.GetByIdAsync(profileId);
            if (byId != null)
            {
                return byId;
            }
        }

        return await _profileService.GetDefaultAsync();
    }
}

public sealed record ExternalImportRequest(
    string SourcePath,
    long? TargetFolderId,
    bool? RunAutoTag,
    string? ProfileId);

public sealed record ExternalImportPreviewItem(
    string SourcePath,
    string DestinationPath,
    string? Reason);

public sealed record ExternalImportPreviewResult(
    bool Success,
    string? Message,
    List<ExternalImportPreviewItem> Items);

public sealed record ExternalImportResult(
    bool Success,
    string? Message,
    int Imported,
    int Skipped);
