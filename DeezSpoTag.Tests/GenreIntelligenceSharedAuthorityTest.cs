using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// One authority for the shipped Genre block list, and a typed reader for the
/// legacy snapshot stage.
/// </summary>
/// <remarks>
/// These pin two things that used to be true only by accident: a second, shorter
/// default block list that no caller could reach, and a snapshot-stage enum member
/// whose only justification was history.
/// </remarks>
public sealed class GenreIntelligenceSharedAuthorityTest
{
    // ---------------------------------------------------------------- one block-list authority

    [Fact]
    public void TheAutoTagFallbackBlockListIsTheSharedNormalizationDefault()
    {
        // The runner keeps a fallback for a caller that supplies no block list. It used
        // to be its own shorter list, so a caller reaching the fallback would have
        // blocked less than every other path.
        var field = typeof(LocalAutoTagRunner).GetField(
            "BlockedGenres", BindingFlags.NonPublic | BindingFlags.Static)!;
        var actual = Assert.IsType<HashSet<string>>(field.GetValue(null));

        Assert.Equal(
            GenreTagAliasNormalizer.DefaultBlockedGenres.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryUnconfiguredBlockListResolvesToTheSameValues()
    {
        // The three ways a caller can arrive at "no configured block list" must agree.
        var fromNullList = GenreTagAliasNormalizer.NormalizeBlockedValues(null);
        var fromNullSettings = GenreNormalizationSnapshot.Create(true, null, null).BlockList;
        var fromDefaultSnapshot = GenreNormalizationSnapshot.Default.BlockList;

        var expected = GenreTagAliasNormalizer.DefaultBlockedGenres;
        Assert.Equal(expected, fromNullList);
        Assert.Equal(expected, fromNullSettings);
        Assert.Equal(expected, fromDefaultSnapshot);
    }

    [Fact]
    public void TheBlockListAppliesEvenWhenNormalizationIsSwitchedOff()
    {
        // The toggle governs spelling preferences, not the block list, so a user who
        // turns normalization off does not silently start receiving blocked values.
        var off = GenreNormalizationSnapshot.Create(false, null, null);
        Assert.Empty(off.AliasMap);
        Assert.Equal(GenreTagAliasNormalizer.DefaultBlockedGenres, off.BlockList);
    }

    [Fact]
    public void AnExplicitlyEmptyBlockListStaysEmptyRatherThanFallingBack()
    {
        // NULL means "never configured"; [] is the instruction "block nothing".
        var empty = GenreNormalizationSnapshot.Create(true, null, []);
        Assert.Empty(empty.BlockList);
    }

    // ---------------------------------------------------------------- the legacy snapshot stage

    /// <summary>
    /// The legacy stage is the typed way to read provider-era checkpoints.
    /// </summary>
    /// <remarks>
    /// The file-source migration copies any leftover provider evidence into
    /// <c>personal_genre_snapshot</c> under stage <c>'legacy'</c> so an interrupted run
    /// can still be read instead of dropping a checkpoint a resumed run may need. The
    /// stage is written by raw SQL, so without this enum member there would be no
    /// typed way to fetch those rows back.
    /// </remarks>
    [Fact]
    public void TheLegacyStageNameIsTheOneTheMigrationWrites()
    {
        Assert.Equal("legacy", StageName(GenreSnapshotStage.Legacy));
        Assert.Equal("preautotag", StageName(GenreSnapshotStage.PreAutoTag));
        Assert.Equal("postautotag", StageName(GenreSnapshotStage.PostAutoTag));
    }

    [Fact]
    public async Task ALegacyCheckpointWrittenByTheMigrationIsReadableThroughTheTypedStage()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("gi-legacy-").FullName;
        try
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Library"] = "Data Source=" +
                        System.IO.Path.Combine(directory, "library.db") + ";Pooling=False"
                }).Build();
            var store = new PersonalGenreStore(configuration);

            // Reproduce exactly what the migration does: provider-era evidence becomes a
            // snapshot row under the 'legacy' stage.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             "Data Source=" + System.IO.Path.Combine(directory, "library.db")))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE personal_genre_autotag (
                        job_id TEXT NOT NULL, file_path TEXT NOT NULL, evidence_json TEXT NOT NULL,
                        track_id BIGINT NULL, resolution_json TEXT NULL,
                        write_status TEXT NOT NULL DEFAULT 'pending', resolved_at_utc TEXT NULL,
                        PRIMARY KEY (job_id, file_path));
                    INSERT INTO personal_genre_autotag (job_id, file_path, evidence_json, track_id)
                    VALUES ('old-job', '/music/track.flac',
                            '[{"RawValue":"Bongo Flava","InputField":1,"Order":0,"Origin":0}]',
                            42);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // Reading through the store creates the schema and runs the same migration a
            // real start performs.
            await store.GetSnapshotAsync(
                "old-job", "/music/track.flac", GenreSnapshotStage.Legacy, default);

            var read = await store.GetSnapshotAsync(
                "old-job", "/music/track.flac", GenreSnapshotStage.Legacy, default);

            Assert.NotNull(read);
            Assert.Equal("Bongo Flava", Assert.Single(read!.Observations).RawValue);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(directory, recursive: true); }
            catch (System.IO.IOException) { /* best effort */ }
        }
    }

    private static string StageName(GenreSnapshotStage stage)
    {
        var method = typeof(PersonalGenreStore).GetMethod(
            "StageName", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [stage])!;
    }
}
