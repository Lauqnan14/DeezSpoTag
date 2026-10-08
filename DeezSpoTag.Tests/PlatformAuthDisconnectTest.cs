using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("DataRoot Environment")]
public sealed class PlatformAuthDisconnectTest : IDisposable
{
    private readonly string _root;

    public PlatformAuthDisconnectTest()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-jellyfin-disconnect-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ClearingJellyfin_RemovesLiveAndLegacyDebugCopies_AndRestartMigrationDoesNotRestore()
    {
        var dataRoot = Path.Join(_root, "DeezSpoTag.Workers", "Data");
        var livePath = Path.Join(dataRoot, "autotag", "jellyfin.json");
        var debugPath = Path.Join(_root, "DeezSpoTag.Workers", "bin", "Debug", "net10.0", "Data", "autotag", "jellyfin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(debugPath)!);

        var payload = JsonSerializer.Serialize(new
        {
            url = "http://192.168.28.24:8096",
            apiKey = "nas-key",
            username = "nas-user"
        });
        await File.WriteAllTextAsync(livePath, payload);
        await File.WriteAllTextAsync(debugPath, payload);
        await File.WriteAllTextAsync(Path.Join(dataRoot, "autotag", "plex.json"), """{"url":"http://plex","token":"plex-token"}""");

        var auth = new PlatformAuthService(
            new OverrideWebHostEnvironment(dataRoot),
            NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Join(_root, "keys"))));

        await auth.UpdateAsync(state =>
        {
            state.Jellyfin = null;
            return 0;
        });

        Assert.False(File.Exists(livePath));
        Assert.False(File.Exists(debugPath));
        Assert.Null((await auth.LoadAsync()).Jellyfin);

        await File.WriteAllTextAsync(debugPath, payload);
        AppDataPathResolver.CopyLegacyWorkersData(
            Path.Join(_root, "DeezSpoTag.Workers", "bin", "Debug", "net10.0", "Data"),
            dataRoot);

        Assert.False(File.Exists(livePath));
        Assert.Null((await auth.LoadAsync()).Jellyfin);
    }

    /// <summary>
    ///     Logging out of Soulseek must take the source inactive immediately, not on the next cache expiry.
    /// </summary>
    /// <remarks>
    ///     Checked against the controller source because the property is about a call being made on the
    ///     disconnect path. A behavioural test would have to observe the twenty-second cache window, which is
    ///     exactly the delay this rule exists to remove. The service half - that a stale in-flight probe is
    ///     discarded - is covered by SoulseekConnectionServiceTest.
    /// </remarks>
    [Fact]
    public void LoggingOutOfSoulseekInvalidatesTheCachedEligibilityImmediately()
    {
        // Resolved from the test assembly's own location, not from the temp root this fixture uses for data.
        var controller = File.ReadAllText(Path.Join(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Controllers/Api/PlatformAuthApiController.cs"));

        // Scoped to the shared platform disconnect action. Two actions end in the same return, so the search
        // starts at the action rather than at the return.
        var actionStart = controller.IndexOf(
            "public async Task<IActionResult> Disconnect(string platform,",
            StringComparison.Ordinal);
        Assert.True(actionStart > 0, "The platform disconnect action was not found.");

        var disconnectEnd = controller.IndexOf(
            "return Ok(new { disconnected = true });",
            actionStart,
            StringComparison.Ordinal);
        Assert.True(disconnectEnd > actionStart, "The platform disconnect action has no return.");

        // The invalidation has to sit inside that action, before it returns.
        var invalidate = controller.IndexOf(
            "InvalidateSoulseekEligibility();",
            actionStart,
            StringComparison.Ordinal);
        Assert.True(
            invalidate > actionStart && invalidate < disconnectEnd,
            "Disconnecting Soulseek does not drop the cached eligibility before returning.");

        // Saving credentials has to do the same, because the cached answer described the old details. Bounded by
        // the save action's own return so a call in some later action cannot satisfy the search.
        var save = controller.IndexOf("public async Task<IActionResult> SaveSoulseek", StringComparison.Ordinal);
        Assert.True(save > 0, "The Soulseek save action was not found.");

        var saveEnd = controller.IndexOf("return Ok(new { saved = true", save, StringComparison.Ordinal);
        Assert.True(saveEnd > save, "The Soulseek save action has no return.");

        var saveInvalidate = controller.IndexOf("InvalidateSoulseekEligibility();", save, StringComparison.Ordinal);
        Assert.True(
            saveInvalidate > save && saveInvalidate < saveEnd,
            "Saving Soulseek credentials does not drop the cached eligibility before returning.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed class OverrideWebHostEnvironment(string rootPath) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string WebRootPath { get; set; } = rootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = rootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string? AppDataRoot { get; } = rootPath;
    }
}
