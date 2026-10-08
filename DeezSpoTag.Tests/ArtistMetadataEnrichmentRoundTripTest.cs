using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataEnrichmentRoundTripTest
{
    private static readonly string[] ArtistKeys = ["ARTISTCOUNTRY", "ARTISTCITY", "ARTISTREGION", "ARTISTLANGUAGE"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollaborationLocationRespectsSingleArtistPreference(bool singleArtist)
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            var track = BuildTrack();
            track.Artists = ["Artist", "Co-main Artist"];
            track.ArtistMetadataUsesSingleArtistPreference = singleArtist;
            track.ArtistMetadata = track.ArtistMetadata! with
            {
                ArtistId = null, BindingSource = "musicbrainz", ProviderArtistId = "provider-id",
                BindingReference = "matched-artist:musicbrainz:provider-id:verified-profile"
            };
            await RunWriterAsync(path, track, ["artistCountry", "artistCity"]);
            using var verify = TagLib.File.Create(path);
            Assert.Equal(singleArtist ? "Kenya" : null, ReadOne(verify, "flac", "ARTISTCOUNTRY"));
            Assert.Equal(singleArtist ? "Nairobi" : null, ReadOne(verify, "flac", "ARTISTCITY"));
            Assert.Equal(2, track.Artists.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public async Task WriterAndScannerRoundTripAllFiveFields(string extension)
    {
        var path = NewTrack(extension, out var directory);
        try
        {
            await RunWriterAsync(path, BuildTrack(), ["artistCountry", "artistCity", "artistRegion", "artistLanguage", "language"]);

            using (var verify = TagLib.File.Create(path))
            {
                Assert.Equal("Kenya", ReadOne(verify, extension, "ARTISTCOUNTRY"));
                Assert.Equal("Nairobi", ReadOne(verify, extension, "ARTISTCITY"));
                Assert.Equal("Nairobi County", ReadOne(verify, extension, "ARTISTREGION"));
                Assert.Equal("English", ReadOne(verify, extension, "ARTISTLANGUAGE"));
                Assert.Equal("sw", ReadOne(verify, extension, "LANGUAGE"));
                var method = typeof(LocalAutoTagRunner).GetMethod("VerifyPersistedTags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                var missing = (HashSet<SupportedTag>)method.Invoke(null, new object?[]
                { path, new LocalAutoTagRunner.AutoTagRunnerConfig { Separators = new LocalAutoTagRunner.AutoTagSeparators { Id3 = ";", Vorbis = ";", Mp4 = ";" } },
                    "test", BuildTrack(), null, new[] { SupportedTag.ArtistCountry, SupportedTag.ArtistCity, SupportedTag.ArtistRegion, SupportedTag.ArtistLanguage, SupportedTag.Language } })!;
                Assert.Empty(missing);
            }

            using (var file = TagLib.File.Create(path))
            {
                var otherTags = LocalLibraryScanner.ExtractOtherTags(file);
                foreach (var key in ArtistKeys)
                {
                    Assert.Contains(otherTags, t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                }
                Assert.Contains(otherTags, t => t.Key.Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public async Task DisabledFieldsAreNeverTouchedAndOtherTagsCannotFillThem(string extension)
    {
        var path = NewTrack(extension, out var directory);
        try
        {
            // Seed explicit file values.
            await RunWriterAsync(path, BuildTrack(), ["artistCountry", "language"]);

            // Now a run with no artist-field selections but a poisoned Other bag must not
            // replace or erase the existing first-class values.
            var track = BuildTrack();
            track.Other["ARTISTCOUNTRY"] = ["Poison"];
            track.Other["ARTISTCITY"] = ["Poison"];
            track.Other["ARTISTREGION"] = ["Poison"];
            track.Other["ARTISTLANGUAGE"] = ["Poison"];
            track.ArtistMetadata = new ArtistEnrichmentMetadata(
                7, "Artist", ArtistMetadataRole.Unknown, "manual", "ref", null,
                new ArtistEnrichmentLocation("Ghana", "Accra", "Greater Accra", "GH", "manual", null, null, null, null, null), []);
            await RunWriterAsync(path, track, ["otherTags"]);

            using var verify = TagLib.File.Create(path);
            Assert.Equal("Kenya", ReadOne(verify, extension, "ARTISTCOUNTRY"));
            Assert.Equal("sw", ReadOne(verify, extension, "LANGUAGE"));
            Assert.Null(ReadOne(verify, extension, "ARTISTCITY"));
            Assert.Null(ReadOne(verify, extension, "ARTISTREGION"));
            Assert.Null(ReadOne(verify, extension, "ARTISTLANGUAGE"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public async Task FillOnlyPreservesOverwriteReplacesMissingMetadataKeepsExisting(string extension)
    {
        var path = NewTrack(extension, out var directory);
        try
        {
            await RunWriterAsync(path, BuildTrack(), ["artistCountry", "artistCity"]);

            // Fill-only: overwrite off → existing value preserved.
            var newer = BuildTrack(country: "Uganda", city: "Kampala");
            await RunWriterAsync(path, newer, ["artistCountry", "artistCity"]);
            using (var verify = TagLib.File.Create(path))
            {
                Assert.Equal("Kenya", ReadOne(verify, extension, "ARTISTCOUNTRY"));
                Assert.Equal("Nairobi", ReadOne(verify, extension, "ARTISTCITY"));
            }

            // Overwrite on → replaced.
            await RunWriterAsync(path, newer, ["artistCountry", "artistCity"], overwriteAll: true);
            using (var verify = TagLib.File.Create(path))
            {
                Assert.Equal("Uganda", ReadOne(verify, extension, "ARTISTCOUNTRY"));
                Assert.Equal("Kampala", ReadOne(verify, extension, "ARTISTCITY"));
            }

            // Missing metadata → existing value is not erased.
            var noMetadata = BuildTrack();
            noMetadata.ArtistMetadata = null;
            await RunWriterAsync(path, noMetadata, ["artistCountry", "artistCity"], overwriteAll: true);
            using (var verify = TagLib.File.Create(path))
            {
                Assert.Equal("Uganda", ReadOne(verify, extension, "ARTISTCOUNTRY"));
                Assert.Equal("Kampala", ReadOne(verify, extension, "ARTISTCITY"));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TrackLanguageFallsBackToLegacyOtherValueWithoutQualifiedEvidence()
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            var track = new AutoTagTrack
            {
                Title = "Song",
                Artists = ["Artist"],
                AlbumArtists = ["Artist"],
                Album = "Album"
            };
            track.Other["language"] = ["fr"];
            await RunWriterAsync(path, track, ["language"]);

            using var verify = TagLib.File.Create(path);
            Assert.Equal("fr", ReadOne(verify, ".flac", "LANGUAGE"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UnknownScopeLanguageEvidenceIsNotWrittenAsTrackLanguage()
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            var track = BuildTrack();
            track.TrackLanguageEvidence =
            [
                new LanguageMetadataEvidence(["de"], LanguageMetadataScope.Unknown, "shazam", null, DateTimeOffset.UtcNow)
            ];
            await RunWriterAsync(path, track, ["language"]);

            using var verify = TagLib.File.Create(path);
            // No qualified Track evidence → legacy path; Other has no language here → nothing written.
            Assert.Null(ReadOne(verify, ".flac", "LANGUAGE"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UnknownLanguageCannotEscapeThroughLegacyDictionary()
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            var track = BuildTrack();
            track.TrackLanguageEvidence = [new LanguageMetadataEvidence(["de"], LanguageMetadataScope.Unknown, "shazam", null, DateTimeOffset.UtcNow)];
            track.Other["language"] = ["de"];
            await RunWriterAsync(path, track, ["language", "otherTags"], overwriteAll: true);
            using var verify = TagLib.File.Create(path);
            Assert.Null(ReadOne(verify, ".flac", "LANGUAGE"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MultiArtistLocationIsNotFlattenedIntoScalarTags()
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            var track = BuildTrack();
            track.Artists = ["Artist", "Other Artist"];
            await RunWriterAsync(path, track, ["artistCountry", "artistCity"], overwriteAll: true);
            using var verify = TagLib.File.Create(path);
            Assert.Null(ReadOne(verify, ".flac", "ARTISTCOUNTRY"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task VerificationRejectsPresentButDifferentArtistValue()
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            await RunWriterAsync(path, BuildTrack(), ["artistCountry"]);
            var method = typeof(LocalAutoTagRunner).GetMethod("VerifyPersistedTags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var missing = (HashSet<SupportedTag>)method.Invoke(null, new object?[]
            { path, new LocalAutoTagRunner.AutoTagRunnerConfig(), "test", BuildTrack(country: "Uganda"), null, new[] { SupportedTag.ArtistCountry } })!;
            Assert.Contains(SupportedTag.ArtistCountry, missing);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReportingIncludesQualifiedArtistFields()
    {
        var method = typeof(LocalAutoTagRunner).GetMethod("CollectAutoTagTags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var returned = (List<string>)method.Invoke(null, new object[] { BuildTrack() })!;
        Assert.Contains("artistCountry", returned);
        Assert.Contains("artistLanguage", returned);
        Assert.Contains("language", returned);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public async Task ExplicitFileLanguagesKeepSeparateScopes(string extension)
    {
        var path = NewTrack(extension, out var directory);
        try
        {
            await RunWriterAsync(path, BuildTrack(), ["artistLanguage", "language"]);
            var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
            collaborators.GetType().GetProperty("Logger")!.SetValue(collaborators, NullLogger<LocalAutoTagRunner>.Instance);
            var runner = new LocalAutoTagRunner(collaborators);
            var track = new AutoTagTrack { Artists = ["Artist"] };
            var method = typeof(LocalAutoTagRunner).GetMethod("PopulateArtistMetadataAsync")!;
            Assert.Equal(5, method.GetParameters().Length);
            await (Task)method.Invoke(runner, new object?[] { track, CancellationToken.None, "test", path, false })!;
            Assert.Equal(LanguageMetadataScope.Artist, Assert.Single(track.ArtistMetadata!.Languages).Scope);
            Assert.Equal("audio-file", Assert.Single(track.ArtistMetadata.Languages).Source);
            Assert.Equal(LanguageMetadataScope.Track, Assert.Single(track.TrackLanguageEvidence!).Scope);
            Assert.Null(track.ArtistMetadata.ArtistId);
        }
        finally { Directory.Delete(directory, true); }
    }

    public static IEnumerable<object[]> FieldContainers()
    {
        foreach (var extension in new[] { "mp3", "flac", "m4a" })
            foreach (var key in new[] { "artistCountry", "artistCity", "artistRegion", "artistLanguage", "language" })
                yield return new object[] { extension, key };
    }

    [Theory]
    [MemberData(nameof(FieldContainers))]
    public async Task EveryFieldHasIndependentOverwriteAndMissingValueProtection(string extension, string key)
    {
        var path = NewTrack(extension, out var directory);
        try
        {
            var original = BuildTrack();
            var raw = ArtistEnrichmentFields.RawNames[key];
            await RunWriterAsync(path, original, [key]);
            string? before;
            using (var file = TagLib.File.Create(path)) before = ReadOne(file, extension, raw);
            var newer = BuildTrack("Uganda", "Kampala");
            newer.ArtistMetadata = newer.ArtistMetadata! with
            {
                Location = newer.ArtistMetadata.Location! with { Region = "Central" },
                Languages = [new LanguageMetadataEvidence(["French"], LanguageMetadataScope.Artist, "test", "test-only", DateTimeOffset.UtcNow)]
            };
            newer.TrackLanguageEvidence = [new LanguageMetadataEvidence(["fr"], LanguageMetadataScope.Track, "test", "test-only", DateTimeOffset.UtcNow)];
            await RunWriterAsync(path, newer, [key]);
            using (var file = TagLib.File.Create(path)) Assert.Equal(before, ReadOne(file, extension, raw));
            await RunWriterAsync(path, newer, [key], overwriteTags: [key]);
            string? replaced;
            using (var file = TagLib.File.Create(path)) { replaced = ReadOne(file, extension, raw); Assert.NotEqual(before, replaced); }
            newer.ArtistMetadata = null;
            newer.TrackLanguageEvidence = [];
            await RunWriterAsync(path, newer, [key], overwriteTags: [key]);
            using (var file = TagLib.File.Create(path)) Assert.Equal(replaced, ReadOne(file, extension, raw));
            await RunWriterAsync(path, original, ["otherTags"], overwriteAll: true);
            using (var file = TagLib.File.Create(path)) Assert.Equal(replaced, ReadOne(file, extension, raw));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(LanguageMetadataScope.Artist)]
    [InlineData(LanguageMetadataScope.Lyrics)]
    [InlineData(LanguageMetadataScope.Unknown)]
    public async Task OtherLanguageScopesCannotBecomeTrackLanguage(LanguageMetadataScope scope)
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            var track = BuildTrack();
            track.TrackLanguageEvidence = [new LanguageMetadataEvidence(["fr"], scope, "test", "test-only", DateTimeOffset.UtcNow)];
            track.Other["language"] = ["fr"];
            await RunWriterAsync(path, track, ["language"], overwriteAll: true);
            using var file = TagLib.File.Create(path);
            Assert.Null(ReadOne(file, "flac", "LANGUAGE"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public async Task MultivalueLanguageRoundTripsWithExistingSeparator(string extension)
    {
        var path = NewTrack(extension, out var directory);
        try
        {
            var track = BuildTrack();
            track.ArtistMetadata = track.ArtistMetadata! with { Languages = [new LanguageMetadataEvidence(["English", "French"], LanguageMetadataScope.Artist, "audio-file", "test-only", DateTimeOffset.UtcNow)] };
            track.TrackLanguageEvidence = [new LanguageMetadataEvidence(["en", "fr"], LanguageMetadataScope.Track, "test", "test-only", DateTimeOffset.UtcNow)];
            await RunWriterAsync(path, track, ["artistLanguage", "language"]);
            using var file = TagLib.File.Create(path);
            var values = LocalLibraryScanner.ExtractOtherTags(file);
            Assert.Contains(values, value => value.Key == "ARTISTLANGUAGE" && value.Value == "English");
            Assert.Contains(values, value => value.Key == "ARTISTLANGUAGE" && value.Value == "French");
            Assert.Contains(values, value => value.Key == "LANGUAGE" && value.Value == "en");
            Assert.Contains(values, value => value.Key == "LANGUAGE" && value.Value == "fr");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ScannerPreservesPunctuationInScalarArtistCity()
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            await RunWriterAsync(path, BuildTrack(city: "Washington, D.C."), ["artistCity"]);
            using var file = TagLib.File.Create(path);
            var city = Assert.Single(LocalLibraryScanner.ExtractOtherTags(file).Where(value => value.Key == "ARTISTCITY"));
            Assert.Equal("Washington, D.C.", city.Value);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AmbiguousArtistsDoNotDiscardExplicitTrackLanguage()
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            await RunWriterAsync(path, BuildTrack(), ["language"]);
            var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
            collaborators.GetType().GetProperty("Logger")!.SetValue(collaborators, NullLogger<LocalAutoTagRunner>.Instance);
            var runner = new LocalAutoTagRunner(collaborators);
            var track = new AutoTagTrack { Artists = ["One", "Two"] };
            await runner.PopulateArtistMetadataAsync(track, filePath: path);
            Assert.Equal("sw", Assert.Single(track.TrackLanguageEvidence!).Values[0]);
            Assert.Null(track.ArtistMetadata);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("country")]
    [InlineData("artist-language")]
    [InlineData("track-language")]
    public async Task GeographyAndLanguageDoNotFillOtherSemanticFields(string supplied)
    {
        var path = NewTrack("flac", out var directory);
        try
        {
            var track = BuildTrack();
            track.TrackLanguageEvidence = supplied == "track-language" ? track.TrackLanguageEvidence : [];
            track.ArtistMetadata = track.ArtistMetadata! with
            {
                Location = supplied == "country" ? track.ArtistMetadata.Location : null,
                Languages = supplied == "artist-language" ? track.ArtistMetadata.Languages : []
            };
            await RunWriterAsync(path, track, ["artistCountry", "artistLanguage", "language"]);
            using var file = TagLib.File.Create(path);
            Assert.Equal(supplied == "country" ? "Kenya" : null, ReadOne(file, "flac", "ARTISTCOUNTRY"));
            Assert.Equal(supplied == "artist-language" ? "English" : null, ReadOne(file, "flac", "ARTISTLANGUAGE"));
            Assert.Equal(supplied == "track-language" ? "sw" : null, ReadOne(file, "flac", "LANGUAGE"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static AutoTagTrack BuildTrack(string country = "Kenya", string city = "Nairobi")
    {
        return new AutoTagTrack
        {
            Title = "Song",
            Artists = ["Artist"],
            AlbumArtists = ["Artist"],
            Album = "Album",
            ArtistMetadata = new ArtistEnrichmentMetadata(
                7, "Artist", ArtistMetadataRole.Unknown, "musicbrainz", "artist:7", null,
                new ArtistEnrichmentLocation(country, city, "Nairobi County", "KE", "musicbrainz", "artist:7", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "area", "origin"),
                [new LanguageMetadataEvidence(["English"], LanguageMetadataScope.Artist, "musicbrainz", null, DateTimeOffset.UtcNow)]),
            TrackLanguageEvidence =
            [
                new LanguageMetadataEvidence(["sw"], LanguageMetadataScope.Track, "boomplay", "track-1", DateTimeOffset.UtcNow)
            ]
        };
    }

    private static async Task RunWriterAsync(
        string path,
        AutoTagTrack track,
        List<string> tags,
        List<string>? overwriteTags = null,
        bool overwriteAll = false)
    {
        var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(
            typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
        collaborators.GetType().GetProperty("Logger")!.SetValue(collaborators, NullLogger<LocalAutoTagRunner>.Instance);
        var runner = new LocalAutoTagRunner(collaborators);
        var settings = new DeezSpoTagSettings();
        var config = new LocalAutoTagRunner.AutoTagRunnerConfig
        {
            Tags = tags,
            OverwriteTags = overwriteTags ?? new List<string>(),
            Overwrite = overwriteAll,
            Separators = new LocalAutoTagRunner.AutoTagSeparators { Id3 = ";", Vorbis = ";", Mp4 = ";" },
            Platforms = new List<string> { "test" }
        };
        var coreTrack = LocalAutoTagRunner.BuildCoreTrack(track, ";", false, settings);
        await runner.WriteTagsOnetaggerStyleAsync(
            new LocalAutoTagRunner.TagWriteRequest
            {
                FilePath = path,
                SourceTrack = track,
                CoreTrack = coreTrack,
                EffectiveTagSettings = new TagSettings(),
                Config = config,
                Settings = settings,
                PlatformId = "test",
                Separator = ";"
            },
            CancellationToken.None);
    }

    private static string? ReadOne(TagLib.File file, string extension, string rawName)
        => LocalAutoTagRunner.ReadRawTagValues(
            file,
            extension.StartsWith('.') ? extension : "." + extension,
            rawName).FirstOrDefault();

    private static string NewTrack(string extension, out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "artist-meta-roundtrip-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "track." + extension);
        CreateSilentAudio(path);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "Song";
            file.Tag.Performers = ["Artist"];
            file.Tag.Album = "Album";
            file.Save();
        }

        return path;
    }

    private static void CreateSilentAudio(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", path
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
