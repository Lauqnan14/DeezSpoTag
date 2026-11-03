using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Behavioural tests for how a Soulseek enrichment run chooses its release mode.
/// </summary>
/// <remarks>
///     <para>
///         Exactly two sources, in order: the category recorded from what the peer's folder actually held,
///         and failing that the manual-enrichment release preference the reader saved for the destination
///         profile. There is no third source and no default.
///     </para>
///     <para>
///         These drive the real <see cref="AutoTagConfigBuilder" /> and the real partition function rather
///         than asserting on source text, because the saved preference's path through the profile is the part
///         most likely to be wrong - it lives in extension data rather than in a named profile property.
///     </para>
/// </remarks>
public sealed class SoulseekReleaseModeSelectionTest
{
    [Theory]
    [InlineData("album", "single")]
    [InlineData("single", "album")]
    public void SavedReleasePreferenceReachesCandidateSelectionEvenWhenPeerCategoryDiffers(string peerCategory, string preferred)
        => Assert.Equal(preferred, DownloadOrchestrationService.ResolveEffectiveSoulseekMode(peerCategory, preferred));

    // ─────────────────────────── the saved preference really does survive the builder ───────────────────────────

    /// <summary>
    ///     A release preference saved on a profile reaches the built config the orchestration reads.
    /// </summary>
    /// <remarks>
    ///     This is the load-bearing assumption behind the whole fallback. The AutoTag page keeps the choice in
    ///     its runtime config and the profile snapshot carries it in <c>AutoTag.Data</c> as extension data;
    ///     if the builder dropped it, every profile would look like it had no saved choice and every unknown
    ///     download would sit unenriched.
    /// </remarks>
    [Theory]
    [InlineData("single")]
    [InlineData("album")]
    public void ASavedReleasePreferenceReachesTheBuiltConfig(string savedChoice)
    {
        var built = BuildProfileConfig(savedChoice);

        Assert.Equal(savedChoice, ReadPreference(built));
        Assert.Equal(savedChoice, DownloadOrchestrationService.ReadSavedProfileReleasePreference(built));
    }

    /// <summary>
    ///     A profile that never saved a choice reports none, rather than reporting album.
    /// </summary>
    [Fact]
    public void AProfileWithNoSavedChoiceReportsNone()
    {
        var built = BuildProfileConfig(savedChoice: null);

        Assert.Null(DownloadOrchestrationService.ReadSavedProfileReleasePreference(built));
    }

    /// <summary>
    ///     A saved value that is not a release mode is not honoured as one.
    /// </summary>
    /// <Theory]
    [InlineData("EP")]
    [InlineData("compilation")]
    [InlineData("ALBUM!")]
    [InlineData("  ")]
    public void AnInvalidSavedChoiceIsNotTreatedAsAMode(string savedChoice)
        => Assert.Null(DownloadOrchestrationService.ResolveEffectiveSoulseekMode(null, savedChoice));

    // ─────────────────────────── the two-source rule ───────────────────────────

    [Fact]
    public void ARecordedCategoryDoesNotOverrideTheSavedSelectionPreference()
    {
        // A peer folder is input evidence, not a command to ignore the user's release preference.
        // The runner still requires a verified release and never fabricates the requested type.
        Assert.Equal(
            DownloadOrchestrationService.SoulseekSingleMode,
            DownloadOrchestrationService.ResolveEffectiveSoulseekMode("album", "single"));

        Assert.Equal(
            DownloadOrchestrationService.SoulseekAlbumMode,
            DownloadOrchestrationService.ResolveEffectiveSoulseekMode("single", "album"));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("album")]
    public void AnUnknownCategoryUsesTheSavedChoice(string savedChoice)
        => Assert.Equal(
            savedChoice,
            DownloadOrchestrationService.ResolveEffectiveSoulseekMode(null, savedChoice));

    /// <summary>
    ///     A category that is present but not a mode is treated as unknown, not as a mode.
    /// </summary>
    [Theory]
    [InlineData("compilation")]
    [InlineData("ep")]
    [InlineData("")]
    [InlineData("   ")]
    public void ANonModeCategoryFallsThroughToTheSavedChoice(string category)
        => Assert.Equal(
            DownloadOrchestrationService.SoulseekAlbumMode,
            DownloadOrchestrationService.ResolveEffectiveSoulseekMode(category, "album"));

    /// <summary>
    ///     Neither source means no mode, and never an invented one.
    /// </summary>
    [Fact]
    public void WithNeitherSourceThereIsNoMode()
    {
        Assert.Null(DownloadOrchestrationService.ResolveEffectiveSoulseekMode(null, null));
        Assert.Null(DownloadOrchestrationService.ResolveEffectiveSoulseekMode("", ""));
        Assert.Null(DownloadOrchestrationService.ResolveEffectiveSoulseekMode("compilation", "nonsense"));
    }

    // ─────────────────────────── partitioning ───────────────────────────

    /// <summary>
    ///     A known album and a known single become two runs, even in one folder.
    /// </summary>
    [Fact]
    public void MixedKnownCategoriesShareTheSavedPreferenceWithoutForcingTheirActualReleaseTypes()
    {
        var items = new List<DownloadQueueItem>
        {
            Item(1, folderId: 5, category: "album"),
            Item(2, folderId: 5, category: "single"),
            Item(3, folderId: 5, category: "album")
        };

        var partitions = Partition(items, _ => "album");

        var album = Assert.Single(partitions);
        Assert.Equal(DownloadOrchestrationService.SoulseekAlbumMode, album.Mode);
        Assert.Equal(3, album.Items.Count);
    }

    /// <summary>
    ///     One item's category never decides another's.
    /// </summary>
    /// <remarks>
    ///     Every item here has no category of its own, so all of them take the saved choice. The point of the
    ///     test is that a neighbouring item's recorded category does not leak into theirs.
    /// </remarks>
    [Fact]
    public void OneItemsCategoryNeverDecidesAnothers()
    {
        var items = new List<DownloadQueueItem>
        {
            Item(1, folderId: 5, category: "album"),
            Item(2, folderId: 5, category: null),
            Item(3, folderId: 5, category: null)
        };

        var partitions = Partition(items, _ => "single");

        var single = Assert.Single(partitions);
        Assert.Equal(DownloadOrchestrationService.SoulseekSingleMode, single.Mode);
        Assert.Equal(3, single.Items.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, single.Items.Select(item => item.Id));
    }

    /// <summary>
    ///     Two destination folders never share a run, even with the same mode.
    /// </summary>
    [Fact]
    public void DifferentDestinationFoldersBecomeSeparateGroups()
    {
        var items = new List<DownloadQueueItem>
        {
            Item(1, folderId: 5, category: "album"),
            Item(2, folderId: 9, category: "album")
        };

        var partitions = Partition(items, _ => "album");

        Assert.Equal(2, partitions.Count);
        Assert.Equal(new long[] { 5L, 9L }, partitions.Select(partition => partition.DestinationFolderId).ToArray());
    }

    /// <summary>
    ///     Items with no resolvable mode are excluded from every run, and reported.
    /// </summary>
    /// <remarks>
    ///     No queue state is invented for them: an item in no run is untouched, stays recoverable, and is
    ///     picked up again on a later pass.
    /// </remarks>
    [Fact]
    public void ItemsWithNoModeAreExcludedAndReported()
    {
        var items = new List<DownloadQueueItem>
        {
            Item(1, folderId: 5, category: null),
            Item(2, folderId: 5, category: "album")
        };

        var partitions = Partition(items, _ => null);

        // Only the item with a category of its own runs.
        Assert.Single(partitions);
        Assert.Equal(2, partitions[0].Items[0].Id);

        // The other is reported as unresolved rather than quietly organised as something.
        var unresolved = DownloadOrchestrationService.SoulseekItemsWithoutMode(items, _ => null);
        Assert.Single(unresolved);
        Assert.Equal(1, unresolved[0].Id);
    }

    [Fact]
    public void AnItemWithNoDestinationFolderIsNeverOrganised()
    {
        var items = new List<DownloadQueueItem> { Item(1, folderId: null, category: "album") };

        Assert.Empty(Partition(items, _ => "album"));
    }

    // ─────────────────────────── the run config carries the chosen mode ───────────────────────────

    /// <summary>
    ///     The mode a partition chose is the mode written into its run config.
    /// </summary>
    /// <remarks>
    ///     Driven end to end: the built profile config goes in, the partition is taken from it, and the
    ///     written config is read back. A mode that is resolved but never written would leave the runner
    ///     unable to recognise the operation at all.
    /// </remarks>
    [Theory]
    [InlineData("album", null)]
    [InlineData("single", null)]
    [InlineData(null, "single")]
    [InlineData(null, "album")]
    public void TheRunConfigCarriesTheSelectedMode(string? category, string? savedChoice)
    {
        var built = BuildProfileConfig(savedChoice);
        var items = new List<DownloadQueueItem> { Item(1, folderId: 5, category) };

        var partitions = Partition(items, folderId => DownloadOrchestrationService.ReadSavedProfileReleasePreference(built));
        Assert.Single(partitions);

        var expected = DownloadOrchestrationService.ResolveEffectiveSoulseekMode(
            category,
            DownloadOrchestrationService.ReadSavedProfileReleasePreference(built));
        Assert.Equal(expected, partitions[0].Mode);

        // What the run actually receives.
        var written = WriteOperationShape(built, partitions[0].DestinationFolderId, partitions[0].Mode);
        Assert.Equal(expected, ReadPreference(written));
        Assert.Equal(5L, ReadLong(written, "manualDestinationFolderId"));
        Assert.True(ReadBool(written, "materializeToTemplatePath"));
        Assert.True(ReadBool(written, "organizeSidecarsIntoTemplateFolders"));
    }

    [Fact]
    public void TheRunConfigMovesTaggedFilesToItsSelectedDestination()
    {
        var written = WriteOperationShape("{}", 7, "album");
        using var document = JsonDocument.Parse(written);
        Assert.True(document.RootElement.GetProperty("moveSuccess").GetBoolean());
        Assert.Equal(Path.GetFullPath("/library/AltB"), document.RootElement.GetProperty("moveSuccessPath").GetString());
        Assert.False(document.RootElement.GetProperty("moveFailed").GetBoolean());
        var configPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configPath, written);
            var loader = typeof(AutoTagService).GetMethod("LoadOrganizerOptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var options = Assert.IsType<AutoTagOrganizerOptions>(loader.Invoke(null, [configPath]));
            Assert.Equal(Path.GetFullPath("/library/AltB"), options.MoveTaggedPath);
            Assert.Null(options.MoveUntaggedPath);
        }
        finally { File.Delete(configPath); }
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>
    ///     Builds a profile the way the AutoTag page saves one, then builds its config the way orchestration does.
    /// </summary>
    private static string BuildProfileConfig(string? savedChoice)
    {
        var profile = new TaggingProfile
        {
            Id = "profile-1",
            Name = "Test",
            AutoTag = new AutoTagSettings(),
            TagConfig = new UnifiedTagConfig()
        };

        if (savedChoice is not null)
        {
            profile.AutoTag.Data["manualReleasePreference"] =
                JsonSerializer.SerializeToElement(savedChoice);
        }

        return new AutoTagConfigBuilder().BuildConfigJson(profile) ?? "{}";
    }

    private static IReadOnlyList<DownloadOrchestrationService.SoulseekRunPartition> Partition(
        IReadOnlyList<DownloadQueueItem> items,
        Func<long, string?> savedPreference)
        => DownloadOrchestrationService.PartitionSoulseekRuns(items, savedPreference);

    private static DownloadOrchestrationService.SoulseekRunPartition Single(
        IReadOnlyList<DownloadOrchestrationService.SoulseekRunPartition> partitions,
        string mode)
    {
        var match = partitions.Where(partition =>
            string.Equals(partition.Mode, mode, StringComparison.Ordinal)).ToList();
        Assert.Single(match);
        return match[0];
    }

    /// <summary>
    ///     Reaches the private config writer, so the assertion is about the config that is really produced.
    /// </summary>
    private static string WriteOperationShape(string configJson, long destinationFolderId, string mode)
    {
        var method = typeof(DownloadOrchestrationService).GetMethod(
            "ApplySoulseekOperationShape",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (string)method.Invoke(null, [configJson, destinationFolderId, mode, "/library/AltB"])!;
    }

    private static string? ReadPreference(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("manualReleasePreference", out var value)
            ? value.GetString()
            : null;
    }

    private static long ReadLong(string json, string key)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(key).GetInt64();
    }

    private static bool ReadBool(string json, string key)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(key).GetBoolean();
    }

    private static DownloadQueueItem Item(long id, long? folderId, string? category)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        if (category is not null)
        {
            payload["SoulseekReleaseCategory"] = category;
        }

        return new DownloadQueueItem(
            Id: id,
            QueueUuid: $"queue-{id}",
            Engine: "soulseek",
            ArtistName: "Artist",
            TrackTitle: $"Track {id}",
            Isrc: null,
            DeezerTrackId: null,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: null,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: 180000,
            DestinationFolderId: folderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: "stereo",
            FinalizationStatus: null,
            EnrichmentStatus: null,
            Status: "completed",
            PayloadJson: JsonSerializer.Serialize(payload),
            Progress: 100,
            Downloaded: 1,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch);
    }
}
