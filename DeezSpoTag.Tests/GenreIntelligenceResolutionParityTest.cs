using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.ArtistLocation;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Proves the three paths that interpret a file agree on what the resolver was told.
/// </summary>
/// <remarks>
/// <para>
/// The AutoTag terminal stage, the cleanup preview and the Style Construction
/// preview's cleanup stage all used to assemble their own resolver arguments, and
/// they did not match: AutoTag passed artist-location context, the pre-AutoTag
/// snapshot for carry-forward and removals from both snapshots, while the previews
/// passed none of those and took their settings from a different place. A track page
/// could therefore show a different answer from the run that would write the file.
/// </para>
/// <para>
/// The comparison is on resolver output, not on UI text, and covers the inputs that
/// actually changed the answer: blocked values, carry-forward, normalization
/// aliases, preservation, parent derivation, the genre cap, locks, mappings, rules
/// and location.
/// </para>
/// </remarks>
public sealed class GenreIntelligenceResolutionParityTest : IDisposable
{
    private readonly Fixture _fixture = Fixture.Create();

    public void Dispose() => _fixture.Dispose();

    // ---------------------------------------------------------------- parity

    [Fact]
    public async Task TheAutoTagStageAndTheCleanupPreviewAgreeOnTheSameFile()
    {
        await Given(new StoredState
        {
            Genres = ["Hip Hop", "Worldwide", "Trap", "My Personal Genre"],
            Styles = ["Drill"],
            BlockList = ["Worldwide"],
            PreAutoTagGenres = ["Rap", "My Personal Genre"]
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        // The interesting content, so parity cannot pass by both being empty.
        Assert.Contains("Hip-Hop", autotag.Genres);
        Assert.Contains("Trap", autotag.Styles);
        Assert.Contains("My Personal Genre", preview.Removed
            .Concat(preview.Decisions.Select(item => item.OriginalValue)));
        Assert.NotEmpty(preview.Removed);
    }

    [Fact]
    public async Task ABlockedValueIsRemovedAndReportedIdenticallyOnEveryPath()
    {
        await Given(new StoredState
        {
            Genres = ["Hip-Hop", "Worldwide"],
            BlockList = ["Worldwide"]
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.DoesNotContain("Worldwide", autotag.Genres);
        Assert.DoesNotContain(preview.Removed, item => !item.StartsWith("Worldwide", StringComparison.Ordinal));
        Assert.Contains(preview.Removed, item => item.StartsWith("Worldwide", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACustomValueCarriedForwardFromBeforeAutoTagIsAnAutoTagOnlyConcern()
    {
        await Given(new StoredState
        {
            // The platform replaced the field, so only the uninterpretable value is
            // carried forward.
            PreAutoTagGenres = ["Rap", "My Personal Genre"],
            Genres = ["Bongo Flava"]
        });

        var autotag = await ResolveAutoTagAsync();
        var preview = await _fixture.Service.GetCleanupPreviewAsync(42, CancellationToken.None);
        Assert.NotNull(preview);

        // Carry-forward is a property of a run, not of the file: the pre-AutoTag
        // snapshot is keyed by job id and file path, and a track page has no job. The
        // shared context therefore takes the snapshot as a parameter and the preview
        // passes none, which makes this difference explicit rather than accidental.
        Assert.Equal(new[] { "My Personal Genre" },
            autotag.Resolution.Preserved.Select(item => item.Value).ToArray());
        Assert.Equal(new[] { "Bongo Flava" }, preview.AfterGenres.ToArray());
    }

    [Fact]
    public async Task ACarriedForwardValueIsNeverResurrectedWhenItIsBlocked()
    {
        await Given(new StoredState
        {
            PreAutoTagGenres = ["My Personal Genre"],
            Genres = ["Bongo Flava"],
            BlockList = ["My Personal Genre"]
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.Empty(autotag.Preserved);
        Assert.DoesNotContain("My Personal Genre", preview.AfterGenres);
    }

    [Fact]
    public async Task AUserAliasPreferenceIsAppliedIdenticallyOnEveryPath()
    {
        await Given(new StoredState
        {
            Genres = ["hindie"],
            AliasRules = [new PersonalGenreAliasRule("hindie", "Indian Indie")]
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        // The value lands in Style, because that is what the taxonomy says it is. The
        // preview carries the display spelling the writer would use.
        Assert.Equal(new[] { "Indian Indie" }, preview.AfterStyles.ToArray());
        Assert.Contains(autotag.Decisions, decision => decision.CanonicalValue == "Indian Indie");
    }

    [Fact]
    public async Task PreserveUnmappedTagsIsHonouredIdenticallyOnEveryPath()
    {
        await Given(new StoredState
        {
            Genres = ["A Value Nobody Has Ever Heard Of"],
            PreserveUnmappedTags = true
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.Single(autotag.Preserved);
    }

    [Fact]
    public async Task TurningPreservationOffDropsTheValueIdenticallyOnEveryPath()
    {
        await Given(new StoredState
        {
            Genres = ["A Value Nobody Has Ever Heard Of"],
            PreserveUnmappedTags = false
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.Empty(autotag.Preserved);
    }

    [Fact]
    public async Task ParentDerivationIsAppliedIdenticallyOnEveryPath()
    {
        await Given(new StoredState
        {
            Styles = ["Kenyan Drill"],
            IncludeParentGenres = true
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.Contains("Hip-Hop", autotag.Genres);
        Assert.Contains(autotag.Classifications,
            item => item.Kind == PersonalGenreTaxonKind.Genre && item.Status == "derived_parent");
    }

    [Fact]
    public async Task TheGenreCapIsAppliedIdenticallyOnEveryPath()
    {
        await Given(new StoredState
        {
            Genres = ["Hip-Hop", "Bongo Flava", "Amapiano", "Reggae", "Gospel"],
            MaxGenres = 2
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.Equal(2, autotag.Genres.Count);
    }

    [Fact]
    public async Task ExplicitAutoTagProfileSettingsTakePrecedenceOverSavedPreviewSettings()
    {
        await Given(new StoredState
        {
            Genres = ["Bongo Flava", "Amapiano", "Reggae", "Gospel", "My Personal Genre"],
            Styles = ["Kenyan Drill"],
            MaxGenres = 2,
            PreserveUnmappedTags = false,
            IncludeParentGenres = true
        });

        _fixture.GenreIntelligence.MaxGenres = 3;
        _fixture.GenreIntelligence.PreserveUnmappedTags = true;
        _fixture.GenreIntelligence.IncludeParentGenres = false;

        var autotag = await ResolveAutoTagAsync();
        var preview = await _fixture.Service.GetCleanupPreviewAsync(42, CancellationToken.None);

        Assert.Equal(3, autotag.Resolution.Genres.Count);
        Assert.Contains(autotag.Resolution.Preserved, item => item.Value == "My Personal Genre");
        Assert.DoesNotContain(autotag.Resolution.Classifications, item => item.Status == "derived_parent");
        Assert.NotNull(preview);
        Assert.Equal(2, preview.AfterGenres.Count);
        Assert.DoesNotContain("My Personal Genre", preview.AfterGenres);
    }

    [Fact]
    public async Task AUserLockIsAppliedIdenticallyOnEveryPath()
    {
        await Given(new StoredState { Genres = ["Bongo Flava"] });
        await _fixture.Store.SaveLockAsync(new PersonalGenreLock(42, "amapiano"));

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.Equal(new[] { "Amapiano" }, autotag.Genres.ToArray());
    }

    [Fact]
    public async Task AUserMappingIsAppliedIdenticallyOnEveryPath()
    {
        await Given(new StoredState { Genres = ["Something Unmapped"] });
        await _fixture.Store.UpsertMappingAsync(
            new PersonalGenreMapping(1, "Something Unmapped", "bongo-flava"));

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.Equal(new[] { "Bongo Flava" }, autotag.Genres.ToArray());
    }

    [Fact]
    public async Task AUserRuleIsAppliedIdenticallyOnEveryPath()
    {
        await Given(new StoredState { Genres = ["Kenyan Drill"] });
        await _fixture.Store.UpsertRuleAsync(new PersonalGenreRule(1, "Kenyan Drill", "hip-hop"));

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        Assert.Equal(new[] { "Hip-Hop" }, autotag.Genres.ToArray());
        Assert.Equal(new[] { "1" }, autotag.AppliedRuleIds.ToArray());
    }

    [Fact]
    public async Task ArtistLocationCorroborationIsIdenticalOnEveryPath()
    {
        await Given(new StoredState
        {
            Genres = ["Atlanta Bass"],
            StoredLocation = ("Atlanta", "Georgia", "United States")
        });

        var (autotag, preview) = await ResolveBothWaysAsync();

        AssertSameResolution(autotag, preview);
        // Location annotates the reason for a term already in the file and introduces
        // nothing, which is what makes its absence from a provider-free preview a
        // wording difference rather than a classification difference.
        Assert.All(autotag.Decisions, decision =>
            Assert.Contains("Location context", decision.Reason, StringComparison.Ordinal));
        Assert.Contains(autotag.Decisions,
            decision => decision.Reason.Contains("Atlanta", StringComparison.Ordinal));
        Assert.Contains("Atlanta Bass", preview.AfterStyles);
    }

    [Fact]
    public async Task APreviewDoesNotIntroduceAnythingFromAnArtistsLocation()
    {
        await Given(new StoredState
        {
            Genres = ["Hip-Hop"],
            StoredLocation = ("Atlanta", "Georgia", "United States")
        });

        var (_, preview) = await ResolveBothWaysAsync();

        // The file mentions no place, so there is nothing to corroborate and no term
        // appears that the file did not already hold.
        Assert.Equal(new[] { "Hip-Hop" }, preview.AfterGenres.ToArray());
        Assert.Empty(preview.AfterStyles);
        Assert.Empty(preview.Moved);
    }

    [Fact]
    public async Task TheConstructionPreviewRunsTheSameCleanupStage()
    {
        await Given(new StoredState
        {
            Genres = ["Hip Hop", "Worldwide", "My Personal Genre"],
            PreAutoTagGenres = ["My Personal Genre"],
            BlockList = ["Worldwide"]
        });

        var (autotag, _) = await ResolveBothWaysAsync();
        var construction = await _fixture.Service.GetStyleConstructionPreviewAsync(42, CancellationToken.None);

        Assert.NotNull(construction);
        // The evidence builder derives every fact from this cleanup, so parity of the
        // cleanup is what makes the evidence describe the same file.
        var fact = Assert.Single(construction.Snapshot.SemanticFacts);
        Assert.Equal("Hip-Hop", fact.CanonicalValue);
        Assert.Equal(PersonalGenreTaxonKind.Genre, fact.Kind);
        Assert.Equal(autotag.PrimaryGenre, fact.CanonicalValue);
    }

    [Fact]
    public async Task TheConstructionPreviewReportsTheSameBlockedValuesAsTheCleanupPreview()
    {
        await Given(new StoredState
        {
            Genres = ["Hip-Hop", "Worldwide"],
            BlockList = ["Worldwide"]
        });

        var cleanup = await _fixture.Service.GetCleanupPreviewAsync(42, CancellationToken.None);
        var autotag = await ResolveAutoTagAsync();

        Assert.NotNull(cleanup);
        // The AutoTag stage now builds its preview from the same removals the resolver
        // was given, so the two reports cannot disagree about what was blocked.
        Assert.Equal(
            cleanup.Removed.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            autotag.Removed.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Assert.Contains(cleanup.Removed, item => item.StartsWith("Worldwide", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BothPreviewsRemainReadOnlyWhileResolving()
    {
        await Given(new StoredState { Genres = ["Hip-Hop", "My Personal Genre"] });

        var before = await _fixture.DatabaseStateAsync();
        var bytes = await File.ReadAllBytesAsync(_fixture.AudioPath);

        await _fixture.Service.GetCleanupPreviewAsync(42, CancellationToken.None);
        await _fixture.Service.GetStyleConstructionPreviewAsync(42, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(_fixture.AudioPath));
        Assert.Equal(before, await _fixture.DatabaseStateAsync());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Resolves the same file through both paths and asserts they agree.
    /// </summary>
    private async Task<(PersonalGenreResolution Resolution, GenreCleanupPreview Preview)> ResolveBothWaysAsync()
    {
        var autotag = await ResolveAutoTagAsync();
        var preview = await _fixture.Service.GetCleanupPreviewAsync(42, CancellationToken.None);
        Assert.NotNull(preview);
        return (autotag.Resolution, preview);
    }

    private async Task<(PersonalGenreResolution Resolution, string[] Removed)> ResolveAutoTagAsync()
    {
        var postSnapshot = PersonalGenreService.ReadFileSnapshot(_fixture.AudioPath);
        var preSnapshot = await _fixture.Store.GetSnapshotAsync(
            "job", _fixture.AudioPath, GenreSnapshotStage.PreAutoTag, CancellationToken.None);

        var resolved = await _fixture.Service.ResolveFileAsync(
            42, _fixture.AudioPath, postSnapshot, preSnapshot, _fixture.GenreIntelligence, CancellationToken.None);

        var preview = DeezSpoTag.Web.Services.AutoTag.GenreCleanupPreviewBuilder.Build(
            resolved.Result.Resolution,
            postSnapshot.Observations,
            resolved.Context.RemovedByNormalization);

        return (resolved.Result.Resolution, preview.Removed.ToArray());
    }

    private static void AssertSameResolution(PersonalGenreResolution left, GenreCleanupPreview preview)
    {
        // The preview carries the write plan, which is derived from the resolution, so
        // comparing them is what proves the two paths produced the same decision state.
        var plan = DeezSpoTag.Web.Services.AutoTag.GenreSemanticTagIo.PlanFields(left);
        Assert.Equal(
            plan.TryGetValue(PersonalGenreTaxonKind.Genre, out var genres) ? genres.ToArray() : [],
            preview.AfterGenres.ToArray());
        Assert.Equal(
            plan.TryGetValue(PersonalGenreTaxonKind.Style, out var styles) ? styles.ToArray() : [],
            preview.AfterStyles.ToArray());
    }

    private async Task Given(StoredState state)
    {
        using (var file = TagLib.File.Create(_fixture.AudioPath))
        {
            file.Tag.Genres = state.Genres.ToArray();
            file.Save();
        }

        if (state.Styles.Count > 0)
        {
            // Written through the same encoder a real run uses, so the fixture is read
            // back from the field the resolver actually reads.
            LocalAutoTagRunner.SetSemanticRawTagForTest(_fixture.AudioPath, "STYLE", state.Styles.ToArray());
        }

        if (state.PreAutoTagGenres.Count > 0)
        {
            var pre = new GenreSemanticSnapshot(
                state.PreAutoTagGenres.Select((value, index) =>
                    new GenreTagObservation(value, PersonalGenreTaxonKind.Genre, index)).ToArray(),
                DateTimeOffset.UtcNow);
            await _fixture.Store.SaveSnapshotAsync(
                "job", _fixture.AudioPath, GenreSnapshotStage.PreAutoTag, pre, 42, CancellationToken.None);
        }

        // Parity requires identical effective settings. AutoTag deliberately uses
        // the run's profile, while the offline preview uses the stored preferences.
        _fixture.GenreIntelligence.MaxGenres = state.MaxGenres;
        _fixture.GenreIntelligence.PreserveUnmappedTags = state.PreserveUnmappedTags;
        _fixture.GenreIntelligence.IncludeParentGenres = state.IncludeParentGenres;

        await _fixture.Store.SaveSettingsAsync(new PersonalGenreSettings(
            Enabled: true,
            MaxGenres: state.MaxGenres,
            PreserveUnmappedTags: state.PreserveUnmappedTags,
            IncludeParentGenres: state.IncludeParentGenres,
            NormalizeGenreTags: true,
            GenreTagAliasRules: state.AliasRules,
            GenreTagBlockList: state.BlockList));

        if (state.StoredLocation is { } location)
        {
            await _fixture.Overrides.SetAsync(1, location.Item2, location.Item1, "US");
        }
    }

    private sealed record StoredState
    {
        public List<string> Genres { get; init; } = [];
        public List<string> Styles { get; init; } = [];
        public List<string> PreAutoTagGenres { get; init; } = [];
        public List<string>? BlockList { get; init; }
        public List<PersonalGenreAliasRule>? AliasRules { get; init; }
        public bool PreserveUnmappedTags { get; init; } = true;
        public bool IncludeParentGenres { get; init; }
        public int MaxGenres { get; init; } = 3;
        public (string City, string Region, string Country)? StoredLocation { get; init; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly string _connectionString;

        public string AudioPath { get; }
        public PersonalGenreStore Store { get; }
        public PersonalGenreService Service { get; }
        public ArtistLocationOverrideStore Overrides { get; }
        public ArtistLocationResolver Locations { get; }

        /// <summary>The AutoTag profile settings, which the write path prefers.</summary>
        public AutoTagGenreIntelligenceSettings GenreIntelligence { get; } = new()
        {
            Enabled = true,
            MaxGenres = 3,
            PreserveUnmappedTags = true,
            IncludeParentGenres = false
        };

        private Fixture(string directory)
        {
            Assert.Null(Environment.GetEnvironmentVariable("LIBRARY_DB"));
            _directory = directory;
            AudioPath = Path.Combine(directory, "track.flac");
            _connectionString = "Data Source=" + Path.Combine(directory, "library.db") + ";Pooling=False";
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Library"] = _connectionString })
                .Build();
            Store = new(configuration);
            Overrides = new(configuration, NullLogger<ArtistLocationOverrideStore>.Instance);
            // The AutoTag policy resolves through this chain. The stub is deliberately
            // offline, so the test can never reach a provider even on the path that is
            // allowed to.
            Locations = new ArtistLocationResolver(
                NullLogger<ArtistLocationResolver>.Instance, audiomack: new FixedLocationSource());
            Service = new(
                Store,
                new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance),
                NullLogger<PersonalGenreService>.Instance,
                artistLocations: Locations,
                artistLocationOverrides: Overrides);
        }

        /// <summary>A location source that answers from memory and never opens a socket.</summary>
        private sealed class FixedLocationSource : DeezSpoTag.Web.Services.ArtistLocation.IArtistLocationSource
        {
            public string SourceName => "stub";

            public Task<DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResult?> ResolveAsync(
                long artistId,
                string? artistName,
                CancellationToken cancellationToken = default)
                => Task.FromResult<DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResult?>(
                    new("Atlanta, Georgia, United States", "Atlanta", "United States", "US", SourceName)
                    {
                        Region = "Georgia",
                        ResolutionMethod = "stub"
                    });
        }

        public static Fixture Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "gi-parity-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            var fixture = new Fixture(directory);
            try
            {
                var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
                foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", fixture.AudioPath })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, error);

                using var connection = new SqliteConnection(fixture._connectionString);
                connection.Open();
                using var schema = typeof(LibraryRepository).Assembly
                    .GetManifestResourceStream("DeezSpoTag.Services.Library.Schema.library.sql")!;
                using var reader = new StreamReader(schema);
                using var command = connection.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
                command.CommandText = """
                INSERT INTO artist(id,name) VALUES(1,'Album Artist');
                INSERT INTO album(id,artist_id,title) VALUES(1,1,'Album');
                INSERT INTO track(id,album_id,title) VALUES(42,1,'Track');
                INSERT INTO folder(id,root_path,display_name) VALUES(1,@directory,'Fixture');
                INSERT INTO audio_file(id,path,folder_id) VALUES(1,@path,1);
                INSERT INTO track_local(track_id,audio_file_id) VALUES(42,1);
                """;
                command.Parameters.AddWithValue("directory", directory);
                command.Parameters.AddWithValue("path", fixture.AudioPath);
                command.ExecuteNonQuery();
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public async Task<string> DatabaseStateAsync()
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'personal_genre_%' ORDER BY name;
                """;
            var names = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) names.Add(reader.GetString(0));

            var dump = new Dictionary<string, string>();
            foreach (var name in names)
            {
                await using var rows = connection.CreateCommand();
                rows.CommandText = $"SELECT * FROM {name} ORDER BY rowid;";
                var table = new List<string>();
                await using (var reader = await rows.ExecuteReaderAsync())
                    while (await reader.ReadAsync())
                        table.Add(JsonSerializer.Serialize(
                            Enumerable.Range(0, reader.FieldCount)
                                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index).ToString()).ToArray()));
                dump[name] = string.Join("\n", table);
            }

            return JsonSerializer.Serialize(dump);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
