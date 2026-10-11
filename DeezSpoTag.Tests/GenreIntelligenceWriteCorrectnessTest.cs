using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Proves the file Genre Intelligence writes is the resolution it computed.
/// Fixtures are temporary audio files.
/// </summary>
public sealed class GenreIntelligenceWriteCorrectnessTest : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("deezspotag-gi-write-").FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".mp3")]
    public void GenreTrapMovesToStyleAndLeavesGenreEmpty(string extension)
    {
        var path = CreateAudio(extension);
        SetGenre(path, "Trap");
        var resolution = Clean(path);

        Assert.Empty(resolution.Genres);
        Assert.Equal(new[] { "Trap" }, resolution.Styles);
        AssertFields(path, genres: [], styles: ["Trap"]);
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".mp3")]
    public void StyleHipHopMovesToGenreAndLeavesStyleEmpty(string extension)
    {
        var path = CreateAudio(extension);
        SetRaw(path, "STYLE", "Hip-Hop");
        var resolution = Clean(path);

        Assert.Equal(new[] { "Hip-Hop" }, resolution.Genres);
        Assert.Empty(resolution.Styles);
        AssertFields(path, genres: ["Hip-Hop"], styles: []);
    }

    [Fact]
    public void SubgenreDeepHouseIsStyle()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "SUBGENRE", "Deep House");
        var resolution = Clean(path);

        Assert.Equal(new[] { "Deep House" }, resolution.Styles);
        Assert.Contains(resolution.Decisions, decision =>
            decision.Reason.Contains("SUBGENRE", StringComparison.Ordinal));
        AssertFields(path, genres: [], styles: ["Deep House"]);
        Assert.Empty(ReadRaw(path, "SUBGENRE"));
    }

    [Fact]
    public void SubgenreHipHopIsGenreBecauseTheTaxonomySaysSo()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "SUBGENRE", "Hip-Hop");
        var resolution = Clean(path);

        Assert.Equal(new[] { "Hip-Hop" }, resolution.Genres);
        Assert.Empty(resolution.Styles);
        AssertFields(path, genres: ["Hip-Hop"], styles: []);
        Assert.Empty(ReadRaw(path, "SUBGENRE"));
    }

    [Fact]
    public void TheSameStyleInTwoPhysicalFieldsIsOneStyle()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "STYLE", "Trap");
        SetRaw(path, "SUBGENRE", "Trap");
        var resolution = Clean(path);

        Assert.Equal(new[] { "Trap" }, resolution.Styles);
        AssertFields(path, genres: [], styles: ["Trap"]);
        Assert.Empty(ReadRaw(path, "SUBGENRE"));
    }

    [Fact]
    public void DifferentStyleSourcesAreBothKept()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "STYLE", "Trap");
        SetRaw(path, "SUBGENRE", "Boom Bap");
        var resolution = Clean(path);

        Assert.Equal(new[] { "Boom Bap", "Trap" }, resolution.Styles.OrderBy(value => value, StringComparer.Ordinal));
        AssertFields(path, genres: [], styles: ["Boom Bap", "Trap"]);
        Assert.Empty(ReadRaw(path, "SUBGENRE"));
    }

    [Fact]
    public void ABlockedOnlyGenreIsCleared()
    {
        var path = CreateAudio(".flac");
        SetGenre(path, "others");
        var resolution = Clean(path, blocked: ["others"]);

        Assert.Empty(resolution.Genres);
        Assert.Empty(resolution.Preserved);
        AssertFields(path, genres: [], styles: []);
    }

    [Fact]
    public void ABlockedValueDoesNotReturnThroughAnAlias()
    {
        var path = CreateAudio(".flac");
        SetGenre(path, "others");
        var normalization = GenreNormalizationSnapshot.Create(
            true,
            [new PersonalGenreAliasRule("others", "Hip-Hop")],
            ["others"]);
        var resolution = Clean(path, normalization: normalization, blocked: normalization.BlockList);

        Assert.DoesNotContain(resolution.Genres, value => value.Contains("Hip", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(resolution.Styles);
        AssertFields(path, genres: [], styles: []);
    }

    [Fact]
    public void ABlockedParentIsNotDerived()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "STYLE", "Trap");
        var resolution = Clean(
            path,
            settings: new PersonalGenreSettings(IncludeParentGenres: true),
            blocked: ["Hip-Hop"]);

        Assert.Equal(new[] { "Trap" }, resolution.Styles);
        Assert.DoesNotContain(resolution.Genres, value => value.Equals("Hip-Hop", StringComparison.OrdinalIgnoreCase));
        AssertFields(path, genres: [], styles: ["Trap"]);
    }

    [Fact]
    public void AnUnknownSubgenreIsPreservedInItsOriginalField()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "SUBGENRE", "My Personal Subgenre");
        var resolution = Clean(path, settings: new PersonalGenreSettings(PreserveUnmappedTags: true));

        var preserved = Assert.Single(resolution.Preserved);
        Assert.Equal("My Personal Subgenre", preserved.Value);
        Assert.Equal("SUBGENRE", preserved.PhysicalTag);
        Assert.Empty(resolution.Styles);
        Assert.Equal(new[] { "My Personal Subgenre" }, ReadRaw(path, "SUBGENRE"));
        Assert.Empty(ReadRaw(path, "STYLE"));
    }

    [Fact]
    public void AnUnknownSubgenreIsRemovedWhenPreservationIsOff()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "SUBGENRE", "My Personal Subgenre");
        var resolution = Clean(path, settings: new PersonalGenreSettings(PreserveUnmappedTags: false));

        Assert.Empty(resolution.Preserved);
        Assert.Empty(ReadRaw(path, "SUBGENRE"));
        Assert.Empty(ReadRaw(path, "STYLE"));
    }

    public static IEnumerable<object[]> CanonicalSeparatorCases()
    {
        var terms = new (string Input, string Field, string Canonical, string Written)[]
        {
            ("Singer/Songwriter", "genre", "singer-songwriter", "Singer-Songwriter"),
            ("alternative pop/rock", "style", "alternative pop/rock", "Alternative Pop/rock"),
            ("Folk, World, & Country", "genre", "Folk, World, & Country", "Folk, World, & Country"),
            ("Funk / Soul", "genre", "Funk / Soul", "Funk / Soul"),
            ("R&B", "genre", "R&B", "R&B"),
            ("Drum & Bass", "genre", "Drum and Bass", "Drum And Bass")
        };
        foreach (var extension in new[] { ".flac", ".mp3", ".m4a", ".mp4" })
        {
            foreach (var normalize in new[] { false, true })
            {
                foreach (var term in terms)
                {
                    yield return new object[] { extension, normalize, term.Input, term.Field, term.Canonical, term.Written };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalSeparatorCases))]
    public void CanonicalSeparatorTermsSurvive(
        string extension, bool normalize, string input, string field, string canonical, string written)
    {
        var path = CreateAudio(extension);
        SetGenre(path, input);
        var normalization = GenreNormalizationSnapshot.Create(normalize, null, ["other"]);
        var resolution = Clean(path, normalization: normalization, blocked: normalization.BlockList);
        var canonicalGenres = field == "genre" ? new[] { canonical } : Array.Empty<string>();
        var canonicalStyles = field == "style" ? new[] { canonical } : Array.Empty<string>();
        var writtenGenres = field == "genre" ? new[] { written } : Array.Empty<string>();
        var writtenStyles = field == "style" ? new[] { written } : Array.Empty<string>();
        Assert.Equal(canonicalGenres, resolution.Genres);
        Assert.Equal(canonicalStyles, resolution.Styles);
        Assert.Empty(resolution.Preserved);
        AssertFields(path, writtenGenres, writtenStyles);
    }

    public static IEnumerable<object[]> CompositeCases()
    {
        foreach (var extension in new[] { ".flac", ".mp3", ".m4a", ".mp4" })
        {
            foreach (var normalize in new[] { false, true })
            {
                yield return new object[] { extension, normalize };
            }
        }
    }

    [Theory]
    [MemberData(nameof(CompositeCases))]
    public void ARealCompositeStillSplits(string extension, bool normalize)
    {
        var path = CreateAudio(extension);
        SetGenre(path, "Hip-Hop; Trap");
        var normalization = GenreNormalizationSnapshot.Create(normalize, null, ["other"]);
        var resolution = Clean(path, normalization: normalization, blocked: normalization.BlockList);
        Assert.Equal(new[] { "Hip-Hop" }, resolution.Genres);
        Assert.Equal(new[] { "Trap" }, resolution.Styles);
        AssertFields(path, genres: ["Hip-Hop"], styles: ["Trap"]);
    }

    [Theory]
    [MemberData(nameof(CompositeCases))]
    public void RockAndPopStayTwoGenres(string extension, bool normalize)
    {
        var path = CreateAudio(extension);
        SetGenre(path, "Rock; Pop");
        var normalization = GenreNormalizationSnapshot.Create(normalize, null, ["other"]);
        var resolution = Clean(path, normalization: normalization, blocked: normalization.BlockList);

        Assert.Equal(new[] { "Rock", "Pop" }, resolution.Genres);
        Assert.Empty(resolution.Styles);
        AssertFields(path, genres: ["Rock", "Pop"], styles: []);
    }

    [Fact]
    public void FiveRecognizedGenresAllSurvive()
    {
        var path = CreateAudio(".flac");
        SetGenre(path, "Hip-Hop", "Rock", "Jazz", "Pop", "Reggae");
        var resolution = Clean(path, settings: new PersonalGenreSettings(MaxGenres: 3));

        Assert.Equal(new[] { "Hip-Hop", "Rock", "Jazz", "Pop", "Reggae" }, resolution.Genres);
        AssertFields(path, genres: ["Hip-Hop", "Jazz", "Pop", "Reggae", "Rock"], styles: []);
    }

    [Fact]
    public void VerificationDetectsAStaleGenre()
    {
        var path = CreateAudio(".flac");
        SetGenre(path, "Trap");
        SetRaw(path, "STYLE", "Trap");
        var plan = GenreSemanticTagIo.PlanFields(Resolution(styles: ["Trap"]));
        var problems = GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", plan, new AutoTagGenreIntelligenceSettings(), new HashSet<string> { "genre", "style" });

        var report = string.Join("\n", problems);
        Assert.Contains("expected: []", report);
        Assert.Contains("Trap", report);
        Assert.Contains("unexpected:", report);
    }

    public static IEnumerable<object[]> StyleFieldNames()
    {
        yield return new object[] { "STYLE" };
        yield return new object[] { "SUBGENRE" };
        yield return new object[] { "SUB-GENRE" };
        yield return new object[] { "SUB_GENRE" };
        yield return new object[] { "SUBSTYLE" };
        yield return new object[] { "PERSONAL_STYLE" };
    }

    [Theory]
    [MemberData(nameof(StyleFieldNames))]
    public void AKnownStyleInAnyStyleFieldBecomesStyle(string field)
    {
        var path = CreateAudio(".flac");
        var canonical = field == "PERSONAL_STYLE" ? "PERSONAL_STYLE" : "STYLE";
        SetRaw(path, field, "Deep House");
        var resolution = Clean(path, stylesTagName: canonical);

        Assert.Equal(new[] { "Deep House" }, resolution.Styles);
        Assert.Empty(resolution.Genres);
        Assert.Equal(new[] { "Deep House" }, ReadRaw(path, canonical));
        if (!field.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Empty(ReadRaw(path, field));
        }
    }

    [Theory]
    [MemberData(nameof(StyleFieldNames))]
    public void AKnownGenreInAnyStyleFieldBecomesGenre(string field)
    {
        var path = CreateAudio(".flac");
        var canonical = field == "PERSONAL_STYLE" ? "PERSONAL_STYLE" : "STYLE";
        SetRaw(path, field, "Hip-Hop");
        var resolution = Clean(path, stylesTagName: canonical);

        Assert.Equal(new[] { "Hip-Hop" }, resolution.Genres);
        Assert.Empty(resolution.Styles);
        Assert.Empty(ReadRaw(path, canonical));
        if (!field.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Empty(ReadRaw(path, field));
        }
    }

    [Fact]
    public void DuplicateStyleFieldsCollapseToOneStyleAndKeepProvenance()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "STYLE", "Trap");
        SetRaw(path, "SUBGENRE", "Trap");
        SetRaw(path, "SUBSTYLE", "Trap");
        var snapshot = PersonalGenreService.ReadFileSnapshot(path, "STYLE", PersonalGenreCatalog.Default);
        var tags = snapshot.Observations
            .Where(item => item.RawValue == "Trap")
            .Select(item => item.PhysicalTag)
            .ToArray();
        Assert.Equal(new[] { "STYLE", "SUBGENRE", "SUBSTYLE" }, tags);

        var resolution = Clean(path);
        Assert.Equal(new[] { "Trap" }, resolution.Styles);
        Assert.Equal(new[] { "Trap" }, ReadRaw(path, "STYLE"));
        Assert.Empty(ReadRaw(path, "SUBGENRE"));
        Assert.Empty(ReadRaw(path, "SUBSTYLE"));
    }

    [Fact]
    public void MixedStyleFieldsKeepEachLegitimateStyleOnce()
    {
        var path = CreateAudio(".flac");
        SetRaw(path, "STYLE", "Trap");
        SetRaw(path, "SUBGENRE", "Boom Bap");
        SetRaw(path, "SUBSTYLE", "Deep House");
        var resolution = Clean(path);

        Assert.Equal(
            new[] { "Boom Bap", "Deep House", "Trap" },
            resolution.Styles.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Assert.Empty(ReadRaw(path, "SUBGENRE"));
        Assert.Empty(ReadRaw(path, "SUBSTYLE"));
    }

    public static IEnumerable<object[]> AlternateStyleFields()
    {
        yield return new object[] { "SUBGENRE" };
        yield return new object[] { "SUB-GENRE" };
        yield return new object[] { "SUB_GENRE" };
        yield return new object[] { "SUBSTYLE" };
    }

    [Theory]
    [MemberData(nameof(AlternateStyleFields))]
    public void AnUnknownInAnAlternateStyleFieldStaysThereWhenPreservationIsOn(string field)
    {
        var path = CreateAudio(".flac");
        SetRaw(path, field, "My Personal Subgenre");
        var resolution = Clean(path, settings: new PersonalGenreSettings(PreserveUnmappedTags: true));

        var preserved = Assert.Single(resolution.Preserved);
        Assert.Equal("My Personal Subgenre", preserved.Value);
        Assert.Equal(field, preserved.PhysicalTag);
        Assert.Empty(resolution.Styles);
        Assert.Equal(new[] { "My Personal Subgenre" }, ReadRaw(path, field));
        Assert.Empty(ReadRaw(path, "STYLE"));
    }

    [Theory]
    [MemberData(nameof(AlternateStyleFields))]
    public void AnUnknownInAnAlternateStyleFieldIsRemovedWhenPreservationIsOff(string field)
    {
        var path = CreateAudio(".flac");
        SetRaw(path, field, "My Personal Subgenre");
        var resolution = Clean(path, settings: new PersonalGenreSettings(PreserveUnmappedTags: false));

        Assert.Empty(resolution.Preserved);
        Assert.Empty(resolution.Styles);
        Assert.Empty(ReadRaw(path, field));
        Assert.Empty(ReadRaw(path, "STYLE"));
    }

    [Fact]
    public void VerificationFailsWhenStyleRepeats()
    {
        var path = CreateAudio(".flac");
        SetRawValues(path, "STYLE", "Trap", "Trap");
        var plan = GenreSemanticTagIo.PlanFields(Resolution(styles: ["Trap"]));
        var problems = GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", plan, new AutoTagGenreIntelligenceSettings(), new HashSet<string> { "genre", "style" });

        var report = string.Join("\n", problems);
        Assert.Contains("expected: [Trap]", report);
        Assert.Contains("actual: [Trap, Trap]", report);
        Assert.Contains("unexpected: [Trap]", report);
    }

    [Fact]
    public void VerificationFailsWhenGenreRepeats()
    {
        var path = CreateAudio(".flac");
        SetGenre(path, "Hip-Hop", "Hip-Hop");
        var plan = GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop"]));
        var problems = GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", plan, new AutoTagGenreIntelligenceSettings(), new HashSet<string> { "genre", "style" });

        var report = string.Join("\n", problems);
        Assert.Contains("expected: [Hip-Hop]", report);
        Assert.Contains("actual: [Hip-Hop, Hip-Hop]", report);
        Assert.Contains("unexpected: [Hip-Hop]", report);
    }

    [Theory]
    [InlineData("Trap", "", "Trap")]
    [InlineData("", "Hip-Hop", "")]
    [InlineData("Deep House", "", "Deep House")]
    public void M4bWritesTheResolvedGenreAndStyle(string genre, string style, string expectedStyle)
    {
        var path = CreateAudio(".m4b");
        if (genre.Length > 0)
        {
            if (style.Length == 0 && expectedStyle == "Deep House")
            {
                SetRaw(path, "SUBGENRE", genre);
            }
            else
            {
                SetGenre(path, genre);
            }
        }

        if (style.Length > 0)
        {
            SetRaw(path, "STYLE", style);
        }

        var resolution = Clean(path);
        if (expectedStyle == "Trap")
        {
            Assert.Empty(resolution.Genres);
            Assert.Equal(new[] { "Trap" }, resolution.Styles);
            AssertFields(path, [], ["Trap"]);
        }
        else if (style == "Hip-Hop")
        {
            Assert.Equal(new[] { "Hip-Hop" }, resolution.Genres);
            Assert.Empty(resolution.Styles);
            AssertFields(path, ["Hip-Hop"], []);
        }
        else
        {
            Assert.Equal(new[] { "Deep House" }, resolution.Styles);
            Assert.Empty(ReadRaw(path, "SUBGENRE"));
            AssertFields(path, [], ["Deep House"]);
        }
    }

    [Fact]
    public void M4bKeepsAPunctuatedCanonicalAndSplitsARealComposite()
    {
        var punctuated = CreateAudio(".m4b");
        SetGenre(punctuated, "Folk, World, & Country");
        var punctuatedResolution = Clean(punctuated);
        Assert.Equal(new[] { "Folk, World, & Country" }, punctuatedResolution.Genres);
        AssertFields(punctuated, ["Folk, World, & Country"], []);

        var composite = CreateAudio(".m4b");
        SetGenre(composite, "Hip-Hop; Trap");
        var compositeResolution = Clean(composite);
        Assert.Equal(new[] { "Hip-Hop" }, compositeResolution.Genres);
        Assert.Equal(new[] { "Trap" }, compositeResolution.Styles);
        AssertFields(composite, ["Hip-Hop"], ["Trap"]);
    }

    [Theory]
    [InlineData("Drum and Bass", "drum-and-bass", PersonalGenreTaxonKind.Genre)]
    [InlineData("Singer/Songwriter", "singer-songwriter", PersonalGenreTaxonKind.Genre)]
    [InlineData("alternative pop/rock", "alternative-pop-rock", PersonalGenreTaxonKind.Style)]
    [InlineData("Folk, World, & Country", "folk-world-country", PersonalGenreTaxonKind.Genre)]
    public void CapitalizationRoundTripKeepsTheSameTaxon(string input, string taxonId, PersonalGenreTaxonKind kind)
    {
        var first = PersonalGenreResolver.Resolve([new GenreTagObservation(input, kind)]);
        var decision = Assert.Single(first.Decisions, item => item.TaxonId == taxonId);
        Assert.Equal(taxonId, decision.TaxonId);

        var path = CreateAudio(".flac");
        var plan = GenreSemanticTagIo.PlanFields(first, capitalizeGenres: true, canonicalStyleField: "STYLE");
        Write(path, first, plan);
        var snapshot = PersonalGenreService.ReadFileSnapshot(path, "STYLE", PersonalGenreCatalog.Default);
        var second = PersonalGenreResolver.Resolve(snapshot.Observations);
        var again = Assert.Single(second.Decisions, item => item.TaxonId == taxonId);
        Assert.Equal(decision.TaxonId, again.TaxonId);
        if (kind == PersonalGenreTaxonKind.Style)
        {
            Assert.Equal(first.Styles, second.Styles);
        }
        else
        {
            Assert.Equal(first.Genres, second.Genres);
        }
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".mp3")]
    public void ASecondCleanupIsIdempotent(string extension)
    {
        var path = CreateAudio(extension);
        SetGenre(path, "Trap");
        SetRaw(path, "SUBGENRE", "Boom Bap");
        Clean(path);
        var first = ReadState(path);
        var secondResolution = Clean(path);
        Assert.Equal(first, ReadState(path));
        Assert.Equal(new[] { "Boom Bap", "Trap" }, secondResolution.Styles.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Empty(secondResolution.Genres);
    }

    [Theory]
    [InlineData("GENRE", "Bollywood", "", "Bollywood", "")]
    [InlineData("STYLE", "Bollywood", "", "Bollywood", "")]
    [InlineData("SUBGENRE", "Bollywood", "", "Bollywood", "")]
    [InlineData("CONTEXT", "Bollywood", "", "", "Bollywood")]
    [InlineData("GENRE", "Swahili", "", "Swahili", "")]
    [InlineData("STYLE", "Swahili", "", "Swahili", "")]
    [InlineData("SUBGENRE", "Swahili", "", "Swahili", "")]
    [InlineData("LANGUAGE", "Swahili", "", "", "")]
    [InlineData("GENRE", "OPM", "OPM", "", "")]
    [InlineData("STYLE", "OPM", "OPM", "", "")]
    [InlineData("SUBGENRE", "OPM", "OPM", "", "")]
    [InlineData("CONTEXT", "OPM", "", "", "OPM")]
    public void OverloadedLabelsFollowTheInputFieldOnAFile(string field, string value, string genre, string style, string context)
    {
        var path = CreateAudio(".flac");
        if (field == "GENRE")
        {
            SetGenre(path, value);
        }
        else
        {
            SetRaw(path, field, value);
        }

        var resolution = Clean(path);
        Assert.Equal(genre.Length == 0 ? [] : [genre], resolution.Genres);
        Assert.Equal(style.Length == 0 ? [] : [style], resolution.Styles);
        Assert.Equal(context.Length == 0 ? [] : [context], resolution.Contexts);
        if (field == "LANGUAGE")
        {
            Assert.Equal(new[] { "Swahili" }, resolution.Languages);
            Assert.Equal(new[] { "Swahili" }, ReadRaw(path, "LANGUAGE"));
            Assert.Empty(ReadRaw(path, "STYLE"));
        }
        else
        {
            Assert.Empty(resolution.Languages);
        }

        Assert.Equal(genre.Length == 0 ? [] : [genre], ReadGenres(path));
        Assert.Equal(style.Length == 0 ? [] : [style], ReadRaw(path, "STYLE"));
        Assert.Equal(context.Length == 0 ? [] : [context], ReadRaw(path, "CONTEXT"));
        if (field is "SUBGENRE")
        {
            Assert.Empty(ReadRaw(path, "SUBGENRE"));
        }
    }

    private PersonalGenreResolution Clean(
        string path,
        PersonalGenreSettings? settings = null,
        GenreNormalizationSnapshot? normalization = null,
        IReadOnlyList<string>? blocked = null,
        string stylesTagName = "STYLE")
    {
        normalization ??= GenreNormalizationSnapshot.Create(false, null, blocked);
        var snapshot = PersonalGenreService.ReadFileSnapshot(path, stylesTagName, PersonalGenreCatalog.Default, normalization);
        var preprocessed = GenreNormalizationPreprocessor.Apply(snapshot, normalization, PersonalGenreCatalog.Default);
        var resolution = PersonalGenreResolver.Resolve(
            preprocessed.Observations,
            settings: settings ?? new PersonalGenreSettings(),
            removedByNormalization: preprocessed.Removed,
            blockedIdentities: blocked ?? normalization.BlockList);
        var plan = GenreSemanticTagIo.PlanFields(resolution, capitalizeGenres: true, canonicalStyleField: stylesTagName);
        Write(path, resolution, plan, stylesTagName);
        var problems = GenreSemanticTagIo.VerifyWrittenFields(
            path,
            Path.GetExtension(path),
            stylesTagName,
            plan,
            new AutoTagGenreIntelligenceSettings { WriteContext = true, WriteScene = true },
            new HashSet<string> { "genre", "style", "language" },
            resolution);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        return resolution;
    }

    private void AssertFields(string path, IReadOnlyList<string> genres, IReadOnlyList<string> styles)
    {
        Assert.Equal(
            genres.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
            ReadGenres(path).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            styles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
            ReadRaw(path, "STYLE").OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    }

    private static string ReadState(string path)
        => string.Join("|", ReadGenres(path)) + "||" + string.Join("|", ReadRaw(path, "STYLE"))
           + "||" + string.Join("|", ReadRaw(path, "SUBGENRE"));

    private static IReadOnlyList<string> ReadGenres(string path)
    {
        using var file = TagLib.File.Create(path);
        return GenreSemanticTagIo.ReadSnapshot(file, Path.GetExtension(path), "STYLE")
            .Observations.Where(item => item.InputField == PersonalGenreTaxonKind.Genre)
            .Select(item => item.RawValue)
            .ToList();
    }

    private static List<string> ReadRaw(string path, string name)
    {
        using var file = TagLib.File.Create(path);
        var read = typeof(LocalAutoTagRunner).GetMethod("ReadRawTagValues", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<string>)read.Invoke(null, [file, Path.GetExtension(path), name])!;
    }

    private static void SetGenre(string path, params string[] values)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Genres = values;
        file.Save();
    }

    private static void SetRaw(string path, string name, string value)
        => SetRawValues(path, name, value);

    private static void SetRawValues(string path, string name, params string[] values)
    {
        var extension = Path.GetExtension(path);
        using var file = TagLib.File.Create(path);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, true);
            frame.Text = values;
        }
        else if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);
            vorbis.SetField(name, values);
        }
        else
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, true);
            Assert.True(AppleDashBoxReflectionHelper.TrySetValues(apple, name, values));
        }

        file.Save();
    }

    private static void Write(
        string path,
        PersonalGenreResolution resolution,
        IReadOnlyDictionary<PersonalGenreTaxonKind, List<string>> plan,
        string stylesTagName = "STYLE")
    {
        var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)!;
        var configJson = stylesTagName.Equals("STYLE", StringComparison.OrdinalIgnoreCase)
            ? "{\"Tags\":[\"genre\",\"style\",\"language\"],\"GenreIntelligence\":{\"Enabled\":true,\"WriteContext\":true,\"WriteScene\":true}}"
            : "{\"Tags\":[\"genre\",\"style\",\"language\"],\"StylesOptions\":\"customTag\",\"StylesCustomTag\":{\"Vorbis\":\""
              + stylesTagName
              + "\"},\"GenreIntelligence\":{\"Enabled\":true,\"WriteContext\":true,\"WriteScene\":true}}";
        var config = JsonSerializer.Deserialize(configJson, type)!;
        var write = typeof(LocalAutoTagRunner).GetMethod("WriteGenreIntelligenceTags", BindingFlags.NonPublic | BindingFlags.Static)!;
        write.Invoke(null, [path, config, resolution, plan]);
    }

    private static PersonalGenreResolution Resolution(IReadOnlyList<string>? styles = null, IReadOnlyList<string>? genres = null)
        => new(null, genres ?? [], styles ?? [], [], [], [], [], [], [], [], [], [], PersonalGenreResolver.Version);

    private string CreateAudio(string extension)
    {
        var path = Path.Combine(_directory, $"track{Guid.NewGuid():N}{extension}");
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        var arguments = new List<string> { "-y", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1" };
        if (extension is ".m4a" or ".mp4" or ".m4b")
        {
            arguments.Add("-c:a");
            arguments.Add("aac");
        }

        arguments.Add(path);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return path;
    }
}
