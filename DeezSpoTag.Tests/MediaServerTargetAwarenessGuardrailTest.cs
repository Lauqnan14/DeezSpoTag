using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The Meloday and artist metadata cards on the Activities Media Operations tab offer the same three
/// media servers. Neither should make the user pick when there is nothing to pick: one connected
/// server is the only answer there is, and a server that is not connected cannot receive anything
/// at all.
///
/// Several connected servers is a real choice, so those cards are left alone, and a target the user
/// already chose or saved is never overridden.
/// </summary>
public sealed class MediaServerTargetAwarenessGuardrailTest
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string ReadSource(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    [Fact]
    public void ConnectedServers_ReusesTheSameCheckTheSyncWritersUse()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "MediaServerScanApiController.cs");
        var targets = ReadSource("DeezSpoTag.Web", "Services", "MediaServerTargetServices.cs");

        // Delegating to the writer-side check is the point: a surface must never offer a server the
        // sync path would reject as unconfigured.
        Assert.Contains("[HttpGet(\"connected-servers\")]", controller, StringComparison.Ordinal);
        Assert.Contains("_playlistSyncService.GetConfiguredTargetServicesAsync(cancellationToken)", controller, StringComparison.Ordinal);

        // The configured list also carries YouTube Music, which is not a media server.
        Assert.Contains("MediaServerTargetServices.Contains", controller, StringComparison.Ordinal);
        Assert.Contains("YouTube Music", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("ytmusic", targets, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MediaOperationsTab_AsksTheServerWhichMediaServersAreConnected()
    {
        var activities = ReadSource("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");

        Assert.Contains("async function applyConnectedMediaServerTargets(", activities, StringComparison.Ordinal);
        Assert.Contains("loadConnectedMediaServerServices", activities, StringComparison.Ordinal);
        Assert.Contains("/api/media-server/connected-servers", activities, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaOperationsTab_SelectsTheOnlyConnectedServerAndDisablesTheRest()
    {
        var activities = ReadSource("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");
        var body = ExtractFunction(activities, "async function applyConnectedMediaServerTargets");

        // A server that cannot receive anything must not look selectable.
        Assert.Contains("input.disabled = !isConnected;", body, StringComparison.Ordinal);
        Assert.Contains("if (!isConnected) {", body, StringComparison.Ordinal);

        // One connected server is the only answer, so it is chosen without asking.
        Assert.Contains("if (connected.length > 1) {", body, StringComparison.Ordinal);
        Assert.Contains("onlyConnected.checked = true;", body, StringComparison.Ordinal);

        // A target the user already chose or saved is never overridden.
        Assert.Contains("if (hasUsableSelection && hasSavedChoice) {", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_UsesTheConnectedServerShortcutInsteadOfDemandingATarget()
    {
        var meloday = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "meloday.js");

        Assert.Contains("melodayApplyConnectedServerShortcut", meloday, StringComparison.Ordinal);
        Assert.Contains("globalThis.applyConnectedMediaServerTargets", meloday, StringComparison.Ordinal);
        Assert.Contains("apply('data-meloday-target-server', hasSavedChoice)", meloday, StringComparison.Ordinal);

        // The existing validation stays: several servers is still a real choice.
        Assert.Contains("throw new Error('Select at least one Meloday target server.');", meloday, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistMetadata_ReplacesTheHardcodedPlexDefaultWithTheConnectedServer()
    {
        var activities = ReadSource("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");
        var controls = ReadSource("DeezSpoTag.Web", "Views", "Shared", "_ArtistMetadataUpdaterControls.cshtml");

        // Plex ships checked in the markup, which is wrong for anyone not running Plex.
        Assert.Contains("data-metadata-target=\"plex\" checked", controls, StringComparison.Ordinal);
        Assert.Contains("applyConnectedMediaServerTargets('data-metadata-target', savedTargets.length > 0)", activities, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistMetadataFolder_SelectsTheOnlyMusicLibrary()
    {
        var activities = ReadSource("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");
        var body = ExtractFunction(activities, "async function loadMetadataUpdaterFolders");

        // One music library makes "all of them" and "that one" the same run, so show the folder.
        // Several still start on the "All music libraries" default.
        Assert.Contains("if (folders.length === 1 && folders[0]?.id != null) {", body, StringComparison.Ordinal);
        Assert.Contains("folderSelect.value = onlyFolderId;", body, StringComparison.Ordinal);
        Assert.Contains("if (savedValue) {", body, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find {signature}");
        var bodyStart = source.IndexOf('{', start);
        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(start, index - start + 1);
                }
            }
        }

        return source.Substring(start);
    }
}
