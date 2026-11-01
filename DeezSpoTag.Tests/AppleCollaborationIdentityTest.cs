using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public class AppleCollaborationIdentityTest
{
    [Fact]
    public void CollaborationCredit_IsNotStoredAsThisArtistsAppleId()
    {
        using var document = JsonDocument.Parse("""
            {
              "relationships": {
                "artists": {
                  "data": [
                    { "id": "670534462", "attributes": { "name": "Metro Boomin" } },
                    { "id": "894820464", "attributes": { "name": "21 Savage" } }
                  ]
                }
              }
            }
            """);
        var method = typeof(AppleArtistBiographyService).GetMethod(
            "TryReadMatchingRelatedArtistId",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var matchedArgs = new object?[] { document.RootElement, "21 Savage", null };
        Assert.True((bool)method!.Invoke(null, matchedArgs)!);
        Assert.Equal("894820464", matchedArgs[2]);

        var rejectedArgs = new object?[] { document.RootElement, "Metro Boomin", null };
        Assert.True((bool)method.Invoke(null, rejectedArgs)!);
        Assert.Equal("670534462", rejectedArgs[2]);

        var absentArgs = new object?[] { document.RootElement, "Future", null };
        Assert.False((bool)method.Invoke(null, absentArgs)!);
        Assert.Equal(string.Empty, absentArgs[2]);
    }

    [Fact]
    public void RefreshCache_UsesTheExistingIdentityMatcher()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var refresh = File.ReadAllText(Path.Combine(root!.FullName, "DeezSpoTag.Web", "Services", "ArtistMetadataCacheRefreshService.cs"));
        Assert.Contains("await _artworkCatalog.EnsureProviderIdentityAsync(artistId, artist.Name, name, names, token)", refresh, StringComparison.Ordinal);
        var catalog = File.ReadAllText(Path.Combine(root.FullName, "DeezSpoTag.Web", "Services", "ArtistArtworkCatalogService.cs"));
        var identityStage = catalog[catalog.IndexOf("public async Task EnsureProviderIdentityAsync", StringComparison.Ordinal)..];
        Assert.Contains("await EnsureMatchedSourceIdsAsync", identityStage, StringComparison.Ordinal);
        Assert.Contains("new HashSet<string>(StringComparer.OrdinalIgnoreCase) { provider }", identityStage, StringComparison.Ordinal);
        Assert.Contains("DropCollaboratorSourceIdsAsync", refresh, StringComparison.Ordinal);
        Assert.Contains("removed collaborator id", refresh, StringComparison.Ordinal);
    }
}
