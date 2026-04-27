using System;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyUserAuthStoreResolutionTests : IDisposable
{
    private readonly string _tempRoot;

    public SpotifyUserAuthStoreResolutionTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), $"deezspotag-auth-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void ResolveActiveLibrespotBlobPath_UsesExplicitLibrespotPath()
    {
        var state = new SpotifyUserAuthState
        {
            ActiveAccount = "main",
            Accounts =
            [
                new SpotifyUserAccount
                {
                    Name = "main",
                    LibrespotBlobPath = Path.Join(_tempRoot, "main.json"),
                    WebPlayerBlobPath = Path.Join(_tempRoot, "main.web.json")
                }
            ]
        };

        var resolved = SpotifyUserAuthStore.ResolveActiveLibrespotBlobPath(state);
        Assert.Equal(state.Accounts[0].LibrespotBlobPath, resolved);
    }

    [Fact]
    public void ResolveActiveLibrespotBlobPath_DoesNotTreatWebPlayerBlobAsLibrespot()
    {
        var state = new SpotifyUserAuthState
        {
            ActiveAccount = "web",
            Accounts =
            [
                new SpotifyUserAccount
                {
                    Name = "web",
                    BlobPath = Path.Join(_tempRoot, "web-player.web.json")
                }
            ]
        };

        var resolved = SpotifyUserAuthStore.ResolveActiveLibrespotBlobPath(state);
        Assert.Null(resolved);
    }

    [Fact]
    public void EnsureActiveAccount_PrefersHealthyLibrespotAccountOverWebOnlyAccount()
    {
        var webBlobPath = Path.Join(_tempRoot, "web-player.web.json");
        var librespotBlobPath = Path.Join(_tempRoot, "librespot.json");
        var sharedWebBlobPath = Path.Join(_tempRoot, "shared.web.json");
        File.WriteAllText(webBlobPath, "{}");
        File.WriteAllText(librespotBlobPath, "{}");
        File.WriteAllText(sharedWebBlobPath, "{}");

        var state = new SpotifyUserAuthState
        {
            ActiveAccount = "web-only",
            Accounts =
            [
                new SpotifyUserAccount
                {
                    Name = "web-only",
                    WebPlayerBlobPath = webBlobPath
                },
                new SpotifyUserAccount
                {
                    Name = "full",
                    LibrespotBlobPath = librespotBlobPath,
                    WebPlayerBlobPath = sharedWebBlobPath
                }
            ]
        };

        var changed = SpotifyUserAuthStore.EnsureActiveAccount(state);

        Assert.True(changed);
        Assert.Equal("full", state.ActiveAccount);
        Assert.Equal(librespotBlobPath, SpotifyUserAuthStore.ResolveActiveLibrespotBlobPath(state));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
