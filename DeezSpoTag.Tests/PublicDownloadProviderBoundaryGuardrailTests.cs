using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PublicDownloadProviderBoundaryGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void PublicDownloadProviders_AreNotProbedByBackgroundHealthServices()
    {
        Assert.False(File.Exists(SourcePath("DeezSpoTag.Web/Services/QobuzPublicProviderHealthService.cs")));
        Assert.False(File.Exists(SourcePath("DeezSpoTag.Web/Services/TidalPublicProviderHealthService.cs")));

        var program = ReadSource("DeezSpoTag.Web/Program.cs");
        Assert.DoesNotContain("QobuzPublicProviderHealthService", program, StringComparison.Ordinal);
        Assert.DoesNotContain("TidalPublicProviderHealthService", program, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicDownloadProviders_AreCheckedThroughExistingRegistries()
    {
        var controller = ReadSource("DeezSpoTag.Web/Controllers/Api/PlatformAuthApiController.cs");

        Assert.Contains("CheckQobuzProviders", controller, StringComparison.Ordinal);
        Assert.Contains("CheckTidalProviders", controller, StringComparison.Ordinal);
        Assert.Contains("CheckEnabledProvidersAsync", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicDownloadProviders_AreNotExposedAsCompatibilityStreamResolutionEndpoint()
    {
        Assert.False(File.Exists(SourcePath("DeezSpoTag.Web/Controllers/Api/QobuzDlDownloadCompatibilityApiController.cs")));

        var qobuzService = ReadSource("DeezSpoTag.Services/Download/Qobuz/QobuzDownloadService.cs");
        Assert.DoesNotContain("ResolveStreamUrlByTrackIdAsync", qobuzService, StringComparison.Ordinal);
        Assert.DoesNotContain("IsrcAvailableAsync", qobuzService, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDownloadUrlWithRetryAsync", qobuzService, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetDownloadUrlForQualityAsync", qobuzService, StringComparison.Ordinal);
        Assert.DoesNotContain("IsProviderStreamAcceptableAsync", qobuzService, StringComparison.Ordinal);
    }

    [Fact]
    public void QueuePreResolution_DoesNotTouchDownloadProviderApis()
    {
        Assert.False(File.Exists(SourcePath("DeezSpoTag.Web/Services/DownloadQueuePreResolutionService.cs")));
    }

    [Fact]
    public void TidalDownloadProviderFallback_OnlyExistsInsideDownloadCandidatePath()
    {
        var tidalService = ReadSource("DeezSpoTag.Services/Download/Tidal/TidalDownloadService.cs");

        Assert.DoesNotContain("CheckPublicProvidersAsync", tidalService, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckProviderHealthEndpointAsync", tidalService, StringComparison.Ordinal);

        var candidatePathIndex = tidalService.IndexOf("private async Task<IReadOnlyList<string>> GetDownloadUrlCandidatesAsync", StringComparison.Ordinal);
        var credentialIndex = tidalService.IndexOf("TryFetchManifestFromCredentialApiAsync", candidatePathIndex, StringComparison.Ordinal);
        var providerIndex = tidalService.IndexOf("_providerSource.GetRotatedProviderRecordsAsync", candidatePathIndex, StringComparison.Ordinal);

        Assert.True(candidatePathIndex >= 0);
        Assert.True(credentialIndex > candidatePathIndex);
        Assert.True(providerIndex > credentialIndex);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(SourcePath(relativePath));

    private static string SourcePath(string relativePath)
        => Path.Combine(RepoRoot, relativePath);
}
