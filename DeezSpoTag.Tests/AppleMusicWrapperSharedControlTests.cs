using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleMusicWrapperSharedControlTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"deezspotag-wrapper-test-{Guid.NewGuid():N}");
    private readonly string? _previousMode = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_CONTROL_MODE");
    private readonly string? _previousDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_SHARED_DATA_DIR");
    private readonly string? _previousSessionDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_SHARED_SESSION_DIR");
    private readonly string? _previousHelper = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_HELPER");

    [Fact]
    public async Task StartExternalWrapperLoginAsync_SharedModeQueuesLoginWithoutDockerHelper()
    {
        var dataDir = Path.Combine(_tempRoot, "data");
        var sessionDir = Path.Combine(_tempRoot, "session");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(sessionDir);

        Environment.SetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_CONTROL_MODE", "shared");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_SHARED_DATA_DIR", dataDir);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_SHARED_SESSION_DIR", sessionDir);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_HELPER", "/does/not/exist/apple-wrapperctl.sh");

        var service = new AppleMusicWrapperService(
            new TestWebHostEnvironment(),
            platformAuthService: null!,
            NullLogger<AppleMusicWrapperService>.Instance);

        var method = typeof(AppleMusicWrapperService).GetMethod(
            "StartExternalWrapperLoginAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(service, new object[] { "user@example.com", "password", CancellationToken.None })!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.True((bool)result.GetType().GetField("Item1")!.GetValue(result)!);
        Assert.Null((string?)result.GetType().GetField("Item2")!.GetValue(result));

        var loginFile = Path.Combine(dataDir, "wrapper-login.txt");
        Assert.True(File.Exists(loginFile));
        var payload = await File.ReadAllTextAsync(loginFile);
        Assert.Contains("email_b64=", payload, StringComparison.Ordinal);
        Assert.Contains("password_b64=", payload, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_CONTROL_MODE", _previousMode);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_SHARED_DATA_DIR", _previousDataDir);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_SHARED_SESSION_DIR", _previousSessionDir);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_HELPER", _previousHelper);

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Directory.GetCurrentDirectory();
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
