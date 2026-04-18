using System;
using System.Reflection;
using System.Text.Json.Nodes;
using DeezSpoTag.Web.Controllers.Api;
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

    [Fact]
    public void TryNormalizeStartRequest_AllowsMissingConfig_WhenProfileIsSelected()
    {
        var request = new AutoTagStartRequest
        {
            Path = "/tmp",
            ProfileId = "profile-1"
        };

        var arguments = new object?[] { request, null, null, null };
        var success = Assert.IsType<bool>(TryNormalizeStartRequestMethod.Invoke(null, arguments));

        Assert.True(success);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(arguments[1])));
        Assert.IsType<JsonObject>(arguments[2]);
    }

    [Fact]
    public void TryNormalizeStartRequest_RejectsMissingConfig_WithoutProfile()
    {
        var request = new AutoTagStartRequest
        {
            Path = "/tmp",
            ProfileId = null
        };

        var arguments = new object?[] { request, null, null, null };
        var success = Assert.IsType<bool>(TryNormalizeStartRequestMethod.Invoke(null, arguments));

        Assert.False(success);
        Assert.IsType<BadRequestObjectResult>(arguments[3]);
    }
}
