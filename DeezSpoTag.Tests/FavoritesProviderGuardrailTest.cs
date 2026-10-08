using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The Favorites tab renders every registered provider from the API response. These guardrails
/// pin the shape of that contract, because the failure they cover is silent: a provider that
/// returns nothing simply does not appear, which reads as "that platform has no favorites" rather
/// than "the read is broken".
/// </summary>
public sealed class FavoritesProviderGuardrailTest
{
    [Fact]
    public void FavoritesApi_ReturnsEveryRegisteredProviderUnderProviders()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "FavoritesApiController.cs");

        // A provider array, not a fixed key per platform: adding a platform must not mean editing
        // the controller's response shape.
        Assert.Contains("providers", controller, StringComparison.Ordinal);
        Assert.Contains("_registry.Providers", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("response?.spotify", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify =", controller, StringComparison.Ordinal);

        // The client needs the branding to build a section without per-platform markup.
        Assert.Contains("displayName = provider.DisplayName", controller, StringComparison.Ordinal);
        Assert.Contains("iconPath = provider.IconPath", controller, StringComparison.Ordinal);
        Assert.Contains("key = provider.Key", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void FavoritesApi_OneFailingProviderDoesNotHideTheOthers()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "FavoritesApiController.cs");

        // Resolved per provider, so one platform throwing cannot empty the whole tab.
        Assert.Contains("private static async Task<FavoritesResult> ResolveAsync", controller, StringComparison.Ordinal);
        Assert.Contains("when (ex is not OperationCanceledException)", controller, StringComparison.Ordinal);
        Assert.Contains("favorites unavailable.", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void FavoritesView_HasNoPerPlatformMarkupOrElementIds()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "MediaManagement", "Index.cshtml");

        // Sections are built from the response. A hardcoded spotify/deezer block here is the shape
        // that forced a code change per platform.
        Assert.DoesNotContain("spotifyFavoritesSection", view, StringComparison.Ordinal);
        Assert.DoesNotContain("deezerFavoritesSection", view, StringComparison.Ordinal);
        Assert.DoesNotContain("spotifyFavoritePlaylists", view, StringComparison.Ordinal);
        Assert.DoesNotContain("deezerFavoriteTracks", view, StringComparison.Ordinal);
        Assert.Contains("favoritesContainer", view, StringComparison.Ordinal);
    }

    [Fact]
    public void FavoritesClient_BuildsSectionsFromTheResponse()
    {
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library.js");

        Assert.Contains("function createFavoritesProviderSection", script, StringComparison.Ordinal);
        Assert.Contains("Array.isArray(response?.providers)", script, StringComparison.Ordinal);

        // A collection the platform does not have must not render an empty heading.
        Assert.DoesNotContain("setFavoriteProviderVisibility", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setFavoritesStatus", script, StringComparison.Ordinal);
        Assert.DoesNotContain("favoritesPlaylistEmpty", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyFavorites_ReadsTheAccountLibraryNotTheHomeFeed()
    {
        var service = ReadSource("DeezSpoTag.Web", "Services", "SpotifyFavoritesService.cs");

        // The home feed has no "Liked Songs" or "Your playlists" section, so filtering it for
        // personal titles always produced an empty shelf while reporting the account as connected.
        Assert.DoesNotContain("FetchHomeFeedWithBlobAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractPersonalFavorites", service, StringComparison.Ordinal);
        Assert.DoesNotContain("PersonalSectionKeywords", service, StringComparison.Ordinal);

        Assert.Contains("FetchLibraryLikedTracksAsync", service, StringComparison.Ordinal);
        Assert.Contains("FetchLibraryPlaylistsAsync", service, StringComparison.Ordinal);

        // An empty read must be reported as unavailable, not as a connected empty account.
        Assert.Contains("Spotify library unavailable.", service, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyLibrary_ReadsAreAccountScopedAndSkipUnusableRows()
    {
        var client = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");

        Assert.Contains("public async Task<List<SpotifyLibraryPlaylistSummary>> FetchLibraryPlaylistsAsync", client, StringComparison.Ordinal);

        // The Web API lists only readable playlists; the library query is the account's own.
        Assert.Contains("[\"filters\"] = new[] { \"Playlists\" }", client, StringComparison.Ordinal);
        Assert.Contains("[\"flatten\"] = true", client, StringComparison.Ordinal);

        // A follow-uri contains a further colon and is not an addressable playlist.
        Assert.Contains("Contains(':', StringComparison.Ordinal)", client, StringComparison.Ordinal);
        // A followed-or-deleted tombstone is an absence, not a row to render.
        Assert.Contains("\"Playlist\"", client, StringComparison.Ordinal);
        Assert.Contains("IsSpotifyImageHostUrl", client, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyLibrary_ArtworkIsTakenFromThePlaylistCoverNotTheOwnerAvatar()
    {
        var client = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");

        // The library listing carries the owner's avatar as well as the playlist cover. Taking the
        // first "url" in document order picks the avatar, which is identical on every row and so
        // renders the same picture on every card.
        var method = SliceMethod(client,
            "private static string? FindLibraryPlaylistImageUrl",
            "private static bool TryReadStringPath");
        Assert.DoesNotContain("stack.Push", method, StringComparison.Ordinal);
        Assert.Contains("\"images\", \"items\", 0, \"sources\", 0, \"url\"", method, StringComparison.Ordinal);
        Assert.Contains("IsSpotifyImageHostUrl(candidate)", method, StringComparison.Ordinal);

        // The chosen path is checked before any fallback, and a miss at any depth is safe.
        Assert.Contains("private static bool TryReadStringPath", client, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not slice between '{startMarker}' and '{endMarker}'.");
        return source[start..end];
    }

    [Fact]
    public void EveryFavoritesProvider_ImplementsTheSharedContract()
    {
        var providers = new[]
        {
            "DeezerFavoritesService.cs",
            "SpotifyFavoritesService.cs",
            "Favorites/QobuzFavoritesService.cs",
            "Favorites/AppleMusicFavoritesService.cs",
            "Favorites/TidalFavoritesService.cs",
            "Favorites/YouTubeMusicFavoritesService.cs",
            "Favorites/DiscogsFavoritesService.cs"
        };

        var seenKeys = new List<string>();
        foreach (var file in providers)
        {
            var parts = new List<string> { "DeezSpoTag.Web", "Services" };
            parts.AddRange(file.Split('/'));
            var source = ReadSource(parts.ToArray());
            Assert.Contains("IFavoritesProvider", source, StringComparison.Ordinal);

            var key = ExtractKey(source);
            Assert.True(key is not null, $"{file} must expose a stable Key.");
            Assert.False(seenKeys.Contains(key), $"Duplicate favorites provider key '{key}'.");
            seenKeys.Add(key!);
        }

        // Amazon has no public user-library API and is deliberately not a sync target.
        Assert.DoesNotContain(seenKeys, key => key.Contains("amazon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FavoritesProviders_AreRegisteredAndConsumedThroughTheInterface()
    {
        var program = ReadSource("DeezSpoTag.Web", "Program.cs");

        Assert.Contains("Favorites.IFavoritesProvider", program, StringComparison.Ordinal);
        Assert.Contains("Favorites.FavoritesProviderRegistry", program, StringComparison.Ordinal);

        // Typed clients register the provider itself; a parallel singleton would silently win.
        Assert.Contains("AddHttpClient<DeezSpoTag.Web.Services.Favorites.QobuzFavoritesService>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<DeezSpoTag.Web.Services.Favorites.QobuzFavoritesService>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<DeezSpoTag.Web.Services.Favorites.DiscogsFavoritesService>", program, StringComparison.Ordinal);
    }

    private static string? ExtractKey(string source)
    {
        var marker = "public string Key => \"";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = source.IndexOf('"', start);
        return end < 0 ? null : source[start..end];
    }

    private static string ReadSource(params string[] relativeParts)
        => File.ReadAllText(Path.Join(ResolveRepoRoot(), Path.Join(relativeParts)));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && directory is not null; depth++)
        {
            if (Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
