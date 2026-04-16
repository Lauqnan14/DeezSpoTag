using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Services.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlatformCapabilitiesStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public PlatformCapabilitiesStoreTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-capabilities-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void RecordDownloadTags_CanonicalizesAppleAliasToItunes()
    {
        var store = new PlatformCapabilitiesStore(_tempRoot);

        store.RecordDownloadTags("apple", new[] { "title", "syncedLyrics" });

        var snapshot = store.GetSnapshot();
        Assert.True(snapshot.Platforms.ContainsKey("itunes"));
        Assert.False(snapshot.Platforms.ContainsKey("apple"));
        Assert.Contains(snapshot.Platforms["itunes"].DownloadTags, tag => string.Equals(tag, "title", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Platforms["itunes"].DownloadTags, tag => string.Equals(tag, "syncedLyrics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadSnapshot_MergesLegacyAliasEntriesIntoCanonicalPlatform()
    {
        var capabilitiesDir = Path.Join(_tempRoot, "autotag");
        Directory.CreateDirectory(capabilitiesDir);
        var capabilitiesPath = Path.Join(capabilitiesDir, "platform-capabilities.json");
        var now = DateTimeOffset.UtcNow;
        var legacySnapshot = new PlatformCapabilitiesSnapshot
        {
            UpdatedAt = now,
            Platforms = new Dictionary<string, PlatformCapabilitiesEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["apple"] = new PlatformCapabilitiesEntry
                {
                    UpdatedAt = now.AddMinutes(-1),
                    DownloadTags = new List<string> { "title", "syncedLyrics" }
                },
                ["itunes"] = new PlatformCapabilitiesEntry
                {
                    UpdatedAt = now,
                    DownloadTags = new List<string> { "cover" },
                    SupportedTags = new List<string> { "unsyncedLyrics" }
                }
            }
        };

        File.WriteAllText(
            capabilitiesPath,
            JsonSerializer.Serialize(
                legacySnapshot,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));

        var store = new PlatformCapabilitiesStore(_tempRoot);
        var snapshot = store.GetSnapshot();

        Assert.True(snapshot.Platforms.ContainsKey("itunes"));
        Assert.False(snapshot.Platforms.ContainsKey("apple"));
        Assert.Contains(snapshot.Platforms["itunes"].DownloadTags, tag => string.Equals(tag, "title", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Platforms["itunes"].DownloadTags, tag => string.Equals(tag, "cover", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Platforms["itunes"].SupportedTags, tag => string.Equals(tag, "unsyncedLyrics", StringComparison.OrdinalIgnoreCase));

        var persisted = JsonSerializer.Deserialize<PlatformCapabilitiesSnapshot>(File.ReadAllText(capabilitiesPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(persisted);
        Assert.DoesNotContain(persisted!.Platforms.Keys, key => string.Equals(key, "apple", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(persisted.Platforms.Keys, key => string.Equals(key, "itunes", StringComparison.OrdinalIgnoreCase));
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
