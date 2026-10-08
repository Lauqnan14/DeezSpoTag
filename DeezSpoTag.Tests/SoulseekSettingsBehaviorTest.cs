using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Tests for the Soulseek settings model, its normalization, and the per-folder share state.
/// </summary>
/// <remarks>
///     The behaviour under test is the one the design requires: the settings must stay inside the single
///     existing quality system, the slskd API key must never live in <c>config.json</c>, and a folder must
///     default to *not* shared so sharing is always an explicit opt-in.
/// </remarks>
[Collection("Settings Config Isolation")]
public sealed class SoulseekSettingsBehaviorTest : IDisposable
{
    private readonly string _tempRoot;
    private readonly DeezSpoTagSettingsService _settingsService;

    public SoulseekSettingsBehaviorTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soulseek-settings-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", _tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _tempRoot);
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", null);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", null);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must not fail the run.
        }
    }

    [Fact]
    public void SettingsSection_IsOptInForAutomationAndUnknownQuality()
    {
        var settings = new SoulseekDownloadSettings();

        // Automation off: adding a selectable engine must not make it start downloading on its own.
        Assert.False(settings.AutomationEnabled);

        // Unknown quality off: an undeterminable quality must never be accepted implicitly.
        Assert.False(settings.AllowUnknownQuality);

        Assert.Empty(settings.BlockedUsers);
        Assert.Empty(settings.BlockedFilenamePatterns);
    }

    [Fact]
    public void PeerPolicyDefaults_RejectSlowBusyAndSlotlessPeers()
    {
        var settings = new SoulseekDownloadSettings();

        // A peer with no free slot cannot accept a download, so this defaults to on.
        Assert.True(settings.RequireFreeUploadSlot);
        Assert.True(settings.MaximumPeerQueueLength > 0);
        Assert.True(settings.PeerCooldownMinutes > 0);
        Assert.True(settings.SearchTimeoutSeconds is >= 5 and <= 600);
    }

    [Fact]
    public void SettingsSection_IsAttachedToDeezSpoTagSettings()
    {
        var settings = new DeezSpoTagSettings();

        Assert.NotNull(settings.Soulseek);
        Assert.Equal(30, settings.Soulseek.SearchTimeoutSeconds);
    }

    /// <summary>
    ///     Accepting a file from a peer has to be an explicit choice.
    /// </summary>
    /// <remarks>
    ///     A peer is a stranger, and these two switches are the whole difference between ignoring whatever
    ///     else is in the folder and fetching it. They default to off, and a clone carries them so a
    ///     settings read cannot silently reset the reader's choice.
    /// </remarks>
    [Fact]
    public void PeerSidecars_AreOffByDefaultAndSurviveAClone()
    {
        var defaults = new SoulseekDownloadSettings();
        Assert.False(defaults.UsePeerArtwork);
        Assert.False(defaults.UsePeerLyrics);

        var chosen = new SoulseekDownloadSettings { UsePeerArtwork = true, UsePeerLyrics = true };
        var copy = chosen.Clone();
        Assert.True(copy.UsePeerArtwork);
        Assert.True(copy.UsePeerLyrics);
    }

    [Fact]
    public void PeerSidecars_RoundTripThroughTheUpdate()
    {
        var service = CreateSettingsService();

        service.Update(new SoulseekDownloadSettings { UsePeerArtwork = true });
        Assert.True(service.GetSettings().UsePeerArtwork);
        Assert.False(service.GetSettings().UsePeerLyrics);

        service.Update(new SoulseekDownloadSettings { UsePeerArtwork = false, UsePeerLyrics = true });
        Assert.False(service.GetSettings().UsePeerArtwork);
        Assert.True(service.GetSettings().UsePeerLyrics);
    }

    private SoulseekSettingsService CreateSettingsService()
        => new(
            _settingsService,
            new LibraryRepository(new ConfigurationBuilder().Build(), NullLogger<LibraryRepository>.Instance),
            NullLogger<SoulseekSettingsService>.Instance);

    [Fact]
    public void SettingsSection_SerializesUnderItsOwnKeyAndCarriesNoSecret()
    {
        var json = JsonSerializer.Serialize(
            new DeezSpoTagSettings(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var document = JsonDocument.Parse(json);
        var soulseek = document.RootElement.GetProperty("soulseek");

        Assert.False(soulseek.TryGetProperty("stagingPath", out _));
        Assert.False(soulseek.TryGetProperty("qualityPreference", out _));
        Assert.False(soulseek.TryGetProperty("allowedQualities", out _));
        Assert.False(soulseek.TryGetProperty("allowedExtensions", out _));
        Assert.True(soulseek.TryGetProperty("requireFreeUploadSlot", out _));

        // The slskd URL and API key must never be reachable from the settings file; they live in the
        // encrypted platform auth state configured from the login page.
        Assert.False(soulseek.TryGetProperty("apiKey", out _));
        Assert.False(soulseek.TryGetProperty("baseUrl", out _));
    }

    [Fact]
    public void Clone_DeepCopiesTheLists()
    {
        var settings = new SoulseekDownloadSettings
        {
            BlockedUsers = { "spammer" }
        };

        var clone = settings.Clone();
        clone.BlockedUsers.Add("another");

        Assert.Single(settings.BlockedUsers);
    }

    [Fact]
    public void OutOfRangeNumbersFallBackToDefaults()
    {
        var defaults = new SoulseekDownloadSettings();
        var normalized = Normalize(new SoulseekDownloadSettings
        {
            SearchTimeoutSeconds = 0,
            MaximumPeerQueueLength = -5,
            PeerCooldownMinutes = -1,
            SearchRetentionMinutes = 0,
            MinimumPeerUploadSpeedBytesPerSecond = -1
        });

        Assert.Equal(defaults.SearchTimeoutSeconds, normalized.SearchTimeoutSeconds);
        Assert.Equal(defaults.MaximumPeerQueueLength, normalized.MaximumPeerQueueLength);
        Assert.Equal(defaults.PeerCooldownMinutes, normalized.PeerCooldownMinutes);
        Assert.Equal(defaults.SearchRetentionMinutes, normalized.SearchRetentionMinutes);
        Assert.Equal(
            defaults.MinimumPeerUploadSpeedBytesPerSecond,
            normalized.MinimumPeerUploadSpeedBytesPerSecond);
    }

    [Fact]
    public void InRangeNumbersSurviveUntouched()
    {
        var normalized = Normalize(new SoulseekDownloadSettings
        {
            SearchTimeoutSeconds = 45,
            MaximumPeerQueueLength = 0,
            PeerCooldownMinutes = 0,
            SearchRetentionMinutes = 5
        });

        Assert.Equal(45, normalized.SearchTimeoutSeconds);
        Assert.Equal(0, normalized.MaximumPeerQueueLength);
        Assert.Equal(0, normalized.PeerCooldownMinutes);
        Assert.Equal(5, normalized.SearchRetentionMinutes);
    }

    [Fact]
    public void LegacyRedundantQualitySettings_LoadAndNextSaveOmitsThem()
    {
        var configDirectory = Path.Join(_tempRoot, "deezspotag");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Join(configDirectory, "config.json");
        File.WriteAllText(
            configPath,
            """
            {
              "soulseek": {
                "stagingPath": "/legacy/slskd",
                "qualityPreference": "FLAC",
                "allowedQualities": ["FLAC"],
                "allowedExtensions": [".flac"],
                "searchTimeoutSeconds": 41
              }
            }
            """);

        var loaded = _settingsService.LoadSettings();
        Assert.Equal(41, loaded.Soulseek.SearchTimeoutSeconds);

        _settingsService.SaveSettings(loaded);
        using var saved = JsonDocument.Parse(File.ReadAllText(configPath));
        var soulseek = saved.RootElement.GetProperty("soulseek");
        Assert.False(soulseek.TryGetProperty("stagingPath", out _));
        Assert.False(soulseek.TryGetProperty("qualityPreference", out _));
        Assert.False(soulseek.TryGetProperty("allowedQualities", out _));
        Assert.False(soulseek.TryGetProperty("allowedExtensions", out _));
        Assert.Null(typeof(SoulseekDownloadSettings).GetProperty("StagingPath"));
    }

    [Fact]
    public void Normalization_RoundTripsThroughTheRealSettingsService()
    {
        Normalize(new SoulseekDownloadSettings
        {
            SearchTimeoutSeconds = 45
        });

        var reloaded = _settingsService.LoadSettings().Soulseek;

        Assert.Equal(45, reloaded.SearchTimeoutSeconds);
    }

    [Fact]
    public void FolderDto_DefaultsToNotShared()
    {
        // A folder built without the new arguments must read as not shared, so existing construction sites
        // and any legacy row behave safely.
        var folder = new FolderDto(
            Id: 1,
            RootPath: "/srv/music",
            DisplayName: "Music",
            Enabled: true,
            LibraryId: null,
            LibraryName: null,
            DesiredQuality: QualityCatalog.CdLossless,
            AutoTagProfileId: null,
            AutoTagEnabled: true,
            ConvertEnabled: false,
            ConvertFormat: null,
            ConvertBitrate: null);

        Assert.False(folder.SoulseekShareEnabled);
        Assert.Null(folder.SoulseekShareAlias);
        Assert.Null(folder.SoulseekShareInclude);
        Assert.Null(folder.SoulseekShareExclude);
        Assert.Null(folder.SoulseekShareScanStatus);
        Assert.Null(folder.SoulseekShareScanAt);
    }

    [Fact]
    public void FolderSchema_DeclaresShareColumnsDefaultingToDisabled()
    {
        var folderTable = ExtractCreateTable(
            ReadRepoFile("DeezSpoTag.Services", "Library", "Schema", "library.sql"),
            "folder");

        Assert.Contains("soulseek_share_enabled INTEGER NOT NULL DEFAULT 0", folderTable);
        Assert.Contains("soulseek_share_alias TEXT", folderTable);
        Assert.Contains("soulseek_share_include TEXT", folderTable);
        Assert.Contains("soulseek_share_exclude TEXT", folderTable);
        Assert.Contains("soulseek_share_scan_status TEXT", folderTable);
        Assert.Contains("soulseek_share_scan_at TEXT", folderTable);
    }

    [Fact]
    public void FolderMigration_AddsShareColumnsForExistingDatabases()
    {
        var source = ReadRepoFile("DeezSpoTag.Services", "Library", "LibraryDbService.cs");

        Assert.Contains("EnsureColumnAsync(connection, FolderTable, \"soulseek_share_enabled\"", source);
        Assert.Contains("EnsureColumnAsync(connection, FolderTable, \"soulseek_share_alias\"", source);
        Assert.Contains("EnsureColumnAsync(connection, FolderTable, \"soulseek_share_include\"", source);
        Assert.Contains("EnsureColumnAsync(connection, FolderTable, \"soulseek_share_exclude\"", source);
        Assert.Contains("EnsureColumnAsync(connection, FolderTable, \"soulseek_share_scan_status\"", source);
        Assert.Contains("EnsureColumnAsync(connection, FolderTable, \"soulseek_share_scan_at\"", source);
    }

    [Fact]
    public void SharedFoldersQuery_RequiresTheExplicitOptIn()
    {
        var source = ReadRepoFile("DeezSpoTag.Services", "Library", "LibraryRepository.cs");

        // The read path must filter on soulseek_share_enabled. That is what makes the folder row the source
        // of truth rather than a separate share registry DeezSpoTag could disagree with.
        Assert.Contains("GetSoulseekSharedFoldersAsync", source);
        Assert.Contains("folder.SoulseekShareEnabled", source);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Library", "Library")]
    [InlineData("  Library  ", "Library")]
    [InlineData("with/slash", null)]
    [InlineData("with\\backslash", null)]
    public void ShareAlias_RejectsPathSeparatorsSoTheLocalFolderNameStaysHidden(string? alias, string? expected)
    {
        // slskd exposes the alias instead of the local folder name and forbids separators in it, so an alias
        // containing one is dropped rather than written.
        Assert.Equal(expected, LibraryRepository.NormalizeShareAlias(alias));
    }

    [Fact]
    public void SoulseekStateLivesInItsOwnTablesAndNotOnDownloadTask()
    {
        var repository = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekRepository.cs");

        Assert.Contains("soulseek_search", repository);
        Assert.Contains("soulseek_candidate", repository);
        Assert.Contains("soulseek_transfer", repository);
        Assert.Contains("soulseek_peer_stat", repository);
        Assert.Contains("soulseek_share_scan_state", repository);

        // A Soulseek candidate is a peer filename, not a catalogue identity, so download_task must not gain
        // per-engine identity columns for it.
        var queueRepository = ReadRepoFile("DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRepository.cs");
        Assert.DoesNotContain("soulseek_track_id", queueRepository);
        Assert.DoesNotContain("soulseek_album_id", queueRepository);
        Assert.DoesNotContain("soulseek_artist_id", queueRepository);
    }

    [Fact]
    public void TransferCleanup_OnlyRemovesTerminalRows()
    {
        var method = ExtractMethod(
            ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekRepository.cs"),
            "DeleteTerminalTransfersOlderThanAsync");

        // An in-flight transfer must never be forgotten, or a running download could not be reconciled.
        Assert.Contains("is_terminal = 1", method);
    }

    private SoulseekDownloadSettings Normalize(SoulseekDownloadSettings soulseek)
    {
        _settingsService.SaveSettings(new DeezSpoTagSettings { Soulseek = soulseek });
        return _settingsService.LoadSettings().Soulseek;
    }

    private static string ExtractCreateTable(string schema, string table)
    {
        var marker = $"CREATE TABLE IF NOT EXISTS {table} (";
        var start = schema.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"table {table} not found");
        var end = schema.IndexOf(");", start, StringComparison.Ordinal);
        return schema.Substring(start, end - start);
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var start = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{methodName} not found");
        var next = source.IndexOf("    private ", start + methodName.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source.Substring(start, next - start);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var root = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(new[] { root }.Concat(parts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
