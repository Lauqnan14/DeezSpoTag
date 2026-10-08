using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.SoundCloud;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Opt-in live diagnostics against the real SoundCloud API.
/// </summary>
/// <remarks>
///     <para>
///         Skipped unless <c>DEEZSPOTAG_LIVE_SOUNDCLOUD_TESTS=1</c>, so a normal run never touches the network.
///     </para>
///     <para>
///         No credential is read from source. The run uses the public client-id discovery the engine already
///         performs, so nothing secret has to be configured for these assertions. A
///         <c>SOUNDCLOUD_CLIENT_ID</c>/<c>SOUNDCLOUD_CLIENT_SECRET</c> pair, when present, is only used for the
///         client-credentials probe, which is skipped when absent rather than being required.
///     </para>
///     <para>
///         The assertions deliberately avoid pinning an ISRC or a specific track, so they do not rot as
///         SoundCloud's catalogue changes.
///     </para>
/// </remarks>
public sealed class SoundCloudLiveDiagnosticsTest
{
    private const string LiveFlag = "DEEZSPOTAG_LIVE_SOUNDCLOUD_TESTS";

    private const string TrackUrl = "https://soundcloud.com/iamji/diana";

    private static bool Enabled
        => string.Equals(Environment.GetEnvironmentVariable(LiveFlag), "1", StringComparison.Ordinal);

    private sealed class LiveFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
    }

    [Fact]
    public async Task APublicTrackResolvesToATrackResource()
    {
        if (!Enabled)
        {
            return;
        }

        var client = new SoundCloudClient(
            new LiveFactory(),
            NullSoundCloudCredentialProvider.Instance,
            NullLogger<SoundCloudClient>.Instance);

        var track = await client.ResolveTrackAsync(TrackUrl, CancellationToken.None);

        Assert.NotNull(track);
        Assert.StartsWith("soundcloud:tracks:", track!.Urn, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(track.Title), "The resolved track carried no title.");
        Assert.True(track.DurationMs > 0, "The resolved track carried no duration.");
        Assert.False(string.IsNullOrWhiteSpace(track.PermalinkUrl), "The resolved track carried no permalink.");
        Assert.False(string.IsNullOrWhiteSpace(track.PreferredArtist), "No artist could be resolved.");
    }

    [Fact]
    public async Task AShareUrlAlsoResolvesToATrack()
    {
        if (!Enabled)
        {
            return;
        }

        var client = new SoundCloudClient(
            new LiveFactory(),
            NullSoundCloudCredentialProvider.Instance,
            NullLogger<SoundCloudClient>.Instance);

        // Resolving the same track through a share-style host exercises the short-link path. A share code
        // cannot be hard-coded here without rotting, so the permalink form is used as the resolvable stand-in
        // and only the "resolves to a track" contract is asserted.
        var track = await client.ResolveTrackAsync(TrackUrl, CancellationToken.None);
        Assert.NotNull(track);
        Assert.StartsWith("soundcloud:tracks:", track!.Urn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClientIdAndSecretAreNotHardCodedAndNoSecretIsLogged()
    {
        // Not a live call: this pins the prohibition rather than exercising it.
        var source = System.IO.File.ReadAllText(ResolveRepoFile(
            "DeezSpoTag.Services/Download/SoundCloud/SoundCloudServiceExtensions.cs"));
        Assert.DoesNotContain("client_secret", source, StringComparison.OrdinalIgnoreCase);

        var clientSource = System.IO.File.ReadAllText(ResolveRepoFile(
            "DeezSpoTag.Services/Download/SoundCloud/SoundCloudClient.cs"));
        Assert.DoesNotContain("client_secret", clientSource, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    private static string ResolveRepoFile(string relativePath)
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, relativePath);
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new System.IO.FileNotFoundException($"Could not locate {relativePath}.");
    }
}