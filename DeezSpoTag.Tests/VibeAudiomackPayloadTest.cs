using System;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Web.Services.Audiomack;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Audiomack is the primary Vibe semantic authority, so its structured fields must
/// survive parsing exactly: PrimaryGenre, Subgenres (style), Moods — including
/// values DeezSpoTag has never seen — and absent fields must never fail Vibe.
/// </summary>
public sealed class VibeAudiomackPayloadTest
{
    private static string FixturesRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Join(directory, "DeezSpoTag.Tests", "Fixtures", "Audiomack");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Audiomack fixtures directory was not found.");
    }

    private static AudiomackSongCandidate Load(string fileName)
    {
        var json = File.ReadAllText(Path.Join(FixturesRoot(), fileName));
        using var document = JsonDocument.Parse(json);
        return AudiomackSongCandidate.FromJson(document.RootElement)
            ?? throw new InvalidOperationException($"Fixture {fileName} did not parse.");
    }

    [Fact]
    public void FullPayload_GenreSubgenresMoodsSurviveParsing()
    {
        var candidate = Load("track-full-vibe.json");
        var metadata = AudiomackVibeMetadataService.MapCandidate(candidate, 0.93);

        Assert.Equal("afrosounds", metadata.PrimaryGenre);
        Assert.Equal(new[] { "amapiano", "afrobeats" }, metadata.Subgenres);
        Assert.Equal(new[] { "happy", "party" }, metadata.Moods);
        Assert.Equal("Amapiano Nights", metadata.Title);
        Assert.Contains("Piano Pusha", metadata.Artists);
        Assert.Equal(0.93, metadata.MatchConfidence);
    }

    [Fact]
    public void UnknownNewSubgenreAndMood_SurviveAsTypedEvidence()
    {
        var candidate = Load("track-full-vibe.json");
        candidate = candidate with
        {
            Subgenres = new[] { "gengetone" },
            Moods = new[] { "stargazing" }
        };

        var metadata = AudiomackVibeMetadataService.MapCandidate(candidate, 0.9);

        Assert.Equal(new[] { "gengetone" }, metadata.Subgenres);
        Assert.Equal(new[] { "stargazing" }, metadata.Moods);
    }

    [Fact]
    public void PartialPayload_MissingFieldsDoNotFail()
    {
        var candidate = Load("track-partial-vibe.json");
        var metadata = AudiomackVibeMetadataService.MapCandidate(candidate, 0.88);

        Assert.Equal("afrosounds", metadata.PrimaryGenre);
        Assert.Empty(metadata.Subgenres);
        Assert.Empty(metadata.Moods);
    }

    [Fact]
    public void OtherGenrePayload_HasItsOwnTypedEvidence()
    {
        var candidate = Load("track-other-genre-vibe.json");
        var metadata = AudiomackVibeMetadataService.MapCandidate(candidate, 0.91);

        Assert.Equal("gospel", metadata.PrimaryGenre);
        Assert.Equal(new[] { "contemporary gospel" }, metadata.Subgenres);
        Assert.Equal(new[] { "uplifting", "reflective" }, metadata.Moods);
        Assert.Equal(3, metadata.RawTags.Count);
    }

    [Fact]
    public void AudiobookPrimaryGenre_IsNotMusicEvidence_TagDisplayBecomesStyle()
    {
        var candidate = Load("track-page-song.json") with { Genre = "Audiobook" };
        var metadata = AudiomackVibeMetadataService.MapCandidate(candidate, 0.9);

        Assert.Null(metadata.PrimaryGenre);
        Assert.Equal(new[] { "Amapiano" }, metadata.Subgenres);
        Assert.DoesNotContain(metadata.Subgenres, value => value.Contains("Tanzania", StringComparison.OrdinalIgnoreCase));
    }
}
