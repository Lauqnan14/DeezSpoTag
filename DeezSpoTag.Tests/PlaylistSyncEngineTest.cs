using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The shared half of a playlist push. The reference keeps this in one place so a new platform
/// only implements its own create/read/write; these pin the behaviour every destination inherits.
/// </summary>
public sealed class PlaylistSyncEngineTest
{
    private static readonly PlaylistSyncBinding Binding = new("plex", "source-pl");

    private static PlaylistSyncEngine EngineWithBaseline(
        IPlaylistSyncTarget target, int? baseline, List<(string Service, string PlaylistId)>? bindings = null)
        => new(
            new PlaylistSyncTargetRegistry(new[] { target }),
            (_, _, _) => Task.FromResult<string?>(null),
            (service, _, playlistId, _) =>
            {
                bindings?.Add((service, playlistId));
                return Task.CompletedTask;
            },
            (_, _, _) => Task.FromResult(baseline));

    [Fact]
    public async Task Sync_HoldsRemovals_WhenTheDestinationReadLooksCollapsed()
    {
        // The app last recorded 10 entries on this destination. The live read returns 3, which is
        // under the 0.4 collapse fraction, so this is a broken read and not a real deletion. The
        // pass must add only, and say why.
        var target = new FakeTarget("plex", new Dictionary<string, List<string>> { ["pl-1"] = new() { "a", "b", "c" } });
        var outcome = await EngineWithBaseline(target, baseline: 10)
            .SyncAsync("plex", "Road", null, new[] { "z" }, appendMissingOnly: false, Binding, "pl-1");

        Assert.True(outcome.Success);
        Assert.True(target.LastWrite!.AppendMissingOnly);
        Assert.Equal(0, outcome.RemovedCount);
        Assert.Contains("looked incomplete", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_StillRemoves_WhenTheReadIsInLineWithTheRecordedBaseline()
    {
        // 8 of 10 is above the fraction, so this is a real change and mirror mode proceeds.
        var items = new Dictionary<string, List<string>> { ["pl-1"] = Enumerable.Range(0, 8).Select(i => $"old{i}").ToList() };
        var target = new FakeTarget("plex", items);
        var outcome = await EngineWithBaseline(target, baseline: 10)
            .SyncAsync("plex", "Road", null, new[] { "z" }, appendMissingOnly: false, Binding, "pl-1");

        Assert.True(outcome.Success);
        Assert.False(target.LastWrite!.AppendMissingOnly);
    }

    [Fact]
    public async Task Sync_DoesNotHoldRemovals_WhenThereIsNoRecordedBaseline()
    {
        // Never seen this playlist before, so there is no evidence a short read is a real deletion.
        var items = new Dictionary<string, List<string>> { ["pl-1"] = new() { "only" } };
        var target = new FakeTarget("plex", items);
        var outcome = await EngineWithBaseline(target, baseline: null)
            .SyncAsync("plex", "Road", null, new[] { "z" }, appendMissingOnly: false, Binding, "pl-1");

        Assert.True(outcome.Success);
        Assert.False(target.LastWrite!.AppendMissingOnly);
    }

    [Fact]
    public void CollapsePredicate_UsesTheAppsExistingFraction()
    {
        // The app used 0.4 before the engine existed; the port keeps it.
        Assert.Equal(0.4, PlaylistSyncEngine.RemovalReadCollapseFraction, 3);
        Assert.True(PlaylistSyncEngine.IsReadIncompleteForRemoval(baselineCount: 10, currentCount: 3));
        Assert.False(PlaylistSyncEngine.IsReadIncompleteForRemoval(baselineCount: 10, currentCount: 4));
        Assert.False(PlaylistSyncEngine.IsReadIncompleteForRemoval(baselineCount: 0, currentCount: 0));
    }

    [Fact]
    public void CapRemovals_HoldsEverythingWhenTheCapIsZero()
    {
        // The reference treats max_removals = 0 as "removal mirroring is off", not "remove none
        // this pass". Nothing is dropped either way; the count is reported.
        Assert.Empty(PlaylistSyncEngine.CapRemovals(new[] { 1, 2, 3 }, maxRemovals: 0));
    }

    [Fact]
    public void CapRemovals_KeepsTheExcessForALaterPass()
    {
        var removals = Enumerable.Range(0, 50).ToList();
        Assert.Equal(30, PlaylistSyncEngine.CapRemovals(removals, 30).Count);
        Assert.Equal(Enumerable.Range(0, 10), PlaylistSyncEngine.CapRemovals(removals, 10));
    }
    private sealed class FakeTarget : IPlaylistSyncTarget
    {
        private readonly Dictionary<string, List<string>> _items;

        public FakeTarget(string service, Dictionary<string, List<string>>? items = null)
        {
            TargetId = service;
            _items = items ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        public string TargetId { get; }

        public PlaylistTargetKind TargetKind { get; init; } = PlaylistTargetKind.Library;

        public string Name { get; set; } = "Existing";

        public int CreateCount { get; private set; }

        public int WriteCount { get; private set; }

        public PlaylistMembershipWrite? LastWrite { get; private set; }

        public bool Unreadable { get; set; }

        public bool LookupTransient { get; set; }

        public bool WriteFails { get; set; }

        /// <summary>Set to make creation return null, with this as the reason it reports.</summary>
        public string? CreateFailsWith { get; set; }

        public Task<TargetPlaylistLookup<string>> FindPlaylistAsync(string? playlistId, string name, CancellationToken cancellationToken)
        {
            if (LookupTransient)
            {
                return Task.FromResult(TargetPlaylistLookup<string>.Unavailable());
            }

            if (!string.IsNullOrWhiteSpace(playlistId) && _items.ContainsKey(playlistId))
            {
                return Task.FromResult(TargetPlaylistLookup<string>.Found(playlistId));
            }

            var byName = _items.FirstOrDefault(pair =>
                string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(byName.Key is null
                ? TargetPlaylistLookup<string>.Missing()
                : TargetPlaylistLookup<string>.Found(byName.Key));
        }

        public Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken cancellationToken)
        {
            CreateCount++;
            if (CreateFailsWith is not null)
            {
                return Task.FromResult<string?>(null);
            }

            _items[name] = new List<string>();
            return Task.FromResult<string?>(name);
        }

        public string? CreateFailureDetail => CreateFailsWith;

        public Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
            => Unreadable
                ? Task.FromResult(TargetPlaylistItemsRead.Unreadable)
                : Task.FromResult(TargetPlaylistItemsRead.Ok(
                    _items.TryGetValue(playlistId, out var list) ? list.ToList() : new List<string>()));

        public Task<PlaylistMembershipWriteResult> WriteMembershipAsync(PlaylistMembershipWrite write, CancellationToken cancellationToken)
        {
            WriteCount++;
            LastWrite = write;
            if (WriteFails)
            {
                return Task.FromResult(PlaylistMembershipWriteResult.Failed("boom"));
            }

            _items[write.PlaylistId] = write.AppendMissingOnly
                ? _items[write.PlaylistId].Concat(write.OrderedItemIds).Distinct(StringComparer.Ordinal).ToList()
                : write.OrderedItemIds.ToList();
            return Task.FromResult(PlaylistMembershipWriteResult.Ok(
                write.OrderedItemIds.Count,
                write.AppendMissingOnly ? 0 : write.CurrentItemIds.Count));
        }
    }

    private static (PlaylistSyncEngine Engine, List<(string Service, string PlaylistId)> Bindings) Build(
        params IPlaylistSyncTarget[] targets)
    {
        var bindings = new List<(string, string)>();
        var engine = new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(targets),
            (_, _, _) => Task.FromResult<string?>(null),
            (service, _, playlistId, _) =>
            {
                bindings.Add((service, playlistId));
                return Task.CompletedTask;
            });
        return (engine, bindings);
    }

    /// <summary>
    /// A self-hosted library and a streaming platform are both destinations, but they are not the
    /// same kind of thing: a library is also a source, a platform is not. The model recorded them
    /// identically, under a column called target_service, which made the two indistinguishable.
    /// This pins that the distinction is actually carried.
    /// </summary>
    [Fact]
    public void Registry_TellsLibrariesApartFromPlatforms()
    {
        var registry = new PlaylistSyncTargetRegistry(new IPlaylistSyncTarget[]
        {
            new FakeTarget("plex"),
            new FakeTarget("jellyfin"),
            new FakeTarget("navidrome"),
            new FakeTarget("ytmusic") { TargetKind = PlaylistTargetKind.Platform },
        });

        Assert.Equal(
            new[] { "jellyfin", "navidrome", "plex" },
            registry.OfKind(PlaylistTargetKind.Library).Select(t => t.TargetId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "ytmusic" },
            registry.OfKind(PlaylistTargetKind.Platform).Select(t => t.TargetId));
        Assert.Equal(PlaylistTargetKind.Platform, registry.Find("ytmusic")!.TargetKind);
    }

    [Fact]
    public async Task Sync_WritesMembershipAndRecordsTheBinding()
    {
        var target = new FakeTarget("plex", new Dictionary<string, List<string>> { ["pl-1"] = new() });
        var (engine, bindings) = Build(target);

        var outcome = await engine.SyncAsync("plex", "Road", "desc", new[] { "a", "b" }, appendMissingOnly: false, Binding, "pl-1");

        Assert.True(outcome.Success);
        Assert.Equal("pl-1", outcome.PlaylistId);
        Assert.Equal(new[] { "plex:pl-1" }, bindings.Select(b => b.Service + ":" + b.PlaylistId));
    }

    [Fact]
    public async Task Sync_RefusesAnUnreadableTarget_AndWritesNothing()
    {
        // The safety crux. A read that failed is not an empty playlist; mirroring against "empty"
        // would delete every track on the destination.
        var target = new FakeTarget("plex", new Dictionary<string, List<string>> { ["pl-1"] = new() })
        {
            Unreadable = true
        };
        var (engine, _) = Build(target);

        var outcome = await engine.SyncAsync("plex", "Road", null, new[] { "a" }, appendMissingOnly: false, Binding, "pl-1");

        Assert.False(outcome.Success);
        Assert.Equal(0, target.WriteCount);
        Assert.Contains("could not be read", outcome.Message, StringComparison.Ordinal);
        Assert.True(outcome.Retryable);
    }

    [Fact]
    public async Task Sync_RefusesWhenNoTracksResolved()
    {
        var target = new FakeTarget("plex", new Dictionary<string, List<string>> { ["pl-1"] = new() });
        var (engine, _) = Build(target);

        var outcome = await engine.SyncAsync("plex", "Road", null, Array.Empty<string>(), appendMissingOnly: false, Binding, "pl-1");

        Assert.False(outcome.Success);
        Assert.Equal(0, target.WriteCount);
    }

    [Fact]
    public async Task Sync_AppendModeIsPassedThroughAndRemovesNothing()
    {
        var target = new FakeTarget("plex", new Dictionary<string, List<string>> { ["pl-1"] = new() { "keep" } });
        var (engine, _) = Build(target);

        var outcome = await engine.SyncAsync("plex", "Road", null, new[] { "new" }, appendMissingOnly: true, Binding, "pl-1");

        Assert.True(outcome.Success);
        Assert.True(target.LastWrite!.AppendMissingOnly);
        Assert.Equal(0, outcome.RemovedCount);
    }

    [Fact]
    public async Task Sync_CreatesThePlaylistWhenNoneExists()
    {
        var target = new FakeTarget("plex");
        var (engine, bindings) = Build(target);

        var outcome = await engine.SyncAsync("plex", "Brand New", null, new[] { "a" }, appendMissingOnly: false, Binding);

        Assert.True(outcome.Success);
        Assert.Equal(1, target.CreateCount);
        Assert.Equal("Brand New", outcome.PlaylistId);
        Assert.Single(bindings);
    }

    [Fact]
    public async Task Sync_ReportsWhyCreationFailed_WhenTheDestinationCanSay()
    {
        // A playlist that was created but could not be filed into the destination's library fails
        // the same way as one that was never created. Without the reason, the user looks for a
        // creation problem that did not happen.
        var target = new FakeTarget("spotify")
        {
            CreateFailsWith = "The playlist was created but Spotify did not list it in your library.",
        };
        var (engine, bindings) = Build(target);

        var outcome = await engine.SyncAsync("spotify", "Road", null, new[] { "a" }, appendMissingOnly: false, Binding);

        Assert.False(outcome.Success);
        Assert.Contains("did not list it in your library", outcome.Message!, StringComparison.Ordinal);
        // Nothing was bound: a binding to a playlist id that was never returned would be a guess.
        Assert.Empty(bindings);
    }

    [Fact]
    public async Task Sync_ReusesThePlaylistItCreatedOnAPreviousPass_RatherThanCreatingASecond()
    {
        // The repeat-sync case. With no id supplied, the target is asked by name and the playlist
        // the first pass created is found, so a second pass does not duplicate it.
        var items = new Dictionary<string, List<string>> { ["Road"] = new() };
        var target = new FakeTarget("plex", items);
        var (engine, _) = Build(target);

        var first = await engine.SyncAsync("plex", "Road", null, new[] { "a" }, appendMissingOnly: false, Binding);
        var second = await engine.SyncAsync("plex", "Road", null, new[] { "a" }, appendMissingOnly: false, Binding);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(0, target.CreateCount);
        Assert.Equal(first.PlaylistId, second.PlaylistId);
    }

    [Fact]
    public async Task Sync_DoesNotCreateWhenAnUnreadableTargetCannotBeRead()
    {
        var target = new FakeTarget("plex") { LookupTransient = true };
        var (engine, _) = Build(target);

        var outcome = await engine.SyncAsync("plex", "Road", null, new[] { "a" }, appendMissingOnly: false, Binding);

        Assert.False(outcome.Success);
        Assert.Equal(0, target.CreateCount);
        Assert.Equal(0, target.WriteCount);
    }

    [Fact]
    public async Task Sync_ReportsAnUnconfiguredServiceRatherThanThrowing()
    {
        var (engine, _) = Build(new FakeTarget("plex"));

        var outcome = await engine.SyncAsync("spotify", "Road", null, new[] { "a" }, appendMissingOnly: false, Binding);

        Assert.False(outcome.Success);
        Assert.Contains("not configured", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_ResolvesTheKnownPlaylistIdFromTheLinkStoreWhenTheCallerHasNone()
    {
        // The binding written by an earlier pass, or by Step 1's backfill, is what stops a repeat
        // sync creating a duplicate when the caller supplies no id.
        var target = new FakeTarget("ytmusic", new Dictionary<string, List<string>> { ["yt-1"] = new() });
        var engine = new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(new[] { target }),
            (_, _, _) => Task.FromResult<string?>("yt-1"),
            (_, _, _, _) => Task.CompletedTask);

        var outcome = await engine.SyncAsync("ytmusic", "Mix", null, new[] { "v1" }, appendMissingOnly: false, Binding);

        Assert.True(outcome.Success);
        Assert.Equal("yt-1", outcome.PlaylistId);
        Assert.Equal(0, target.CreateCount);
    }

    [Fact]
    public async Task Sync_DoesNotRecordABindingWhenTheWriteFails()
    {
        var target = new FakeTarget("plex", new Dictionary<string, List<string>> { ["pl-1"] = new() })
        {
            WriteFails = true
        };
        var (engine, bindings) = Build(target);

        var outcome = await engine.SyncAsync("plex", "Road", null, new[] { "a" }, appendMissingOnly: false, Binding, "pl-1");

        Assert.False(outcome.Success);
        Assert.Empty(bindings);
    }
}
