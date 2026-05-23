using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagJobsControllerRequestNormalizationTests
{
    private static readonly MethodInfo TryNormalizeStartRequestMethod =
        typeof(AutoTagJobsController).GetMethod(
            "TryNormalizeStartRequest",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AutoTagJobsController.TryNormalizeStartRequest not found.");
    private static readonly MethodInfo CreateStartJobResponseMethod =
        typeof(AutoTagJobsController).GetMethod(
            "CreateStartJobResponse",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AutoTagJobsController.CreateStartJobResponse not found.");

    [Fact]
    public void TryNormalizeStartRequest_AllowsMissingConfig_WhenProfileIsSelected()
    {
        var request = new AutoTagStartRequest
        {
            Path = "/tmp",
            ProfileId = "profile-1"
        };

        var arguments = new object?[] { request, null, null };
        var success = Assert.IsType<bool>(TryNormalizeStartRequestMethod.Invoke(null, arguments));

        Assert.True(success);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(arguments[1])));
        Assert.IsAssignableFrom<IActionResult>(arguments[2]);
    }

    [Fact]
    public void TryNormalizeStartRequest_RejectsMissingConfig_WithoutProfile()
    {
        var request = new AutoTagStartRequest
        {
            Path = "/tmp",
            ProfileId = null
        };

        var arguments = new object?[] { request, null, null };
        var success = Assert.IsType<bool>(TryNormalizeStartRequestMethod.Invoke(null, arguments));

        Assert.True(success);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(arguments[1])));
        Assert.IsAssignableFrom<IActionResult>(arguments[2]);
    }

    [Fact]
    public void TryNormalizeStartRequest_RejectsNullRequest()
    {
        var arguments = new object?[] { null, null, null };
        var success = Assert.IsType<bool>(TryNormalizeStartRequestMethod.Invoke(null, arguments));

        Assert.False(success);
        var result = Assert.IsType<BadRequestObjectResult>(arguments[2]);
        Assert.Equal("Invalid request.", result.Value);
    }

    [Theory]
    [InlineData("running", typeof(OkObjectResult))]
    [InlineData("blocked", typeof(ConflictObjectResult))]
    [InlineData("skipped", typeof(UnprocessableEntityObjectResult))]
    [InlineData("failed", typeof(ObjectResult))]
    public void CreateStartJobResponse_MapsExpectedStatuses(string status, Type expectedResultType)
    {
        var job = new AutoTagJob
        {
            Id = "job-1",
            Status = status,
            Error = status == "running" ? null : "not started"
        };

        var result = Assert.IsAssignableFrom<IActionResult>(CreateStartJobResponseMethod.Invoke(null, new object[] { job }));

        Assert.IsType(expectedResultType, result);
    }

    [Fact]
    public void StartAction_UsesTruthfulJobResponseMapping()
    {
        var source = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));

        Assert.Contains("public async Task<IActionResult> Start([FromBody] AutoTagStartRequest? request", source, StringComparison.Ordinal);
        Assert.Contains("return CreateStartJobResponse(job);", source, StringComparison.Ordinal);
        Assert.Contains("new ConflictObjectResult(payload)", source, StringComparison.Ordinal);
        Assert.Contains("new UnprocessableEntityObjectResult(payload)", source, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status500InternalServerError", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartScope_ActiveDownloadsReturnConflictBeforeStartJob()
    {
        var source = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));
        var activeDownloads = source.IndexOf("HasActiveDownloadsAsync(cancellationToken)", StringComparison.Ordinal);
        var conflict = source.IndexOf("StatusCode(409, \"Downloads are active. AutoTag cannot start until the queue is idle.\")", StringComparison.Ordinal);
        var validateCall = source.IndexOf("ValidateStartScopeAsync(normalizedPath, cancellationToken)", StringComparison.Ordinal);
        var startJob = source.IndexOf("_autoTagService.StartJob", StringComparison.Ordinal);

        Assert.True(activeDownloads > 0);
        Assert.True(conflict > activeDownloads);
        Assert.True(validateCall > 0);
        Assert.True(startJob > validateCall);
    }

    [Fact]
    public void StartAction_PassesEnhancementIntentThroughToService()
    {
        var source = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));
        var startJob = source.IndexOf("_autoTagService.StartJob", StringComparison.Ordinal);
        var runIntent = source.IndexOf("RunIntent: startRequest.RunIntent", StringComparison.Ordinal);

        Assert.True(startJob > 0);
        Assert.True(runIntent > startJob);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "DeezSpoTag.Web")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
