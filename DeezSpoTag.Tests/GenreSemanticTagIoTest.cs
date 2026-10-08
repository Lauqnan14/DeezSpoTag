using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Direct coverage of the one definition of the semantic-to-physical tag mapping.
/// </summary>
/// <remarks>
/// <para>
/// <c>GenreSemanticTagIo</c> is the only place that knows which physical tag holds
/// each semantic concept, and it is the reader and the writer for both AutoTag and
/// Genre Intelligence. Testing it through a reimplementation would test the
/// reimplementation, so every assertion here calls the production members directly.
/// <c>DeezSpoTag.Web</c> already declares
/// <c>InternalsVisibleTo("DeezSpoTag.Tests")</c>, so no visibility change was needed.
/// </para>
/// </remarks>
public sealed class GenreSemanticTagIoTest : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("deezspotag-semantic-io-").FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    public static TheoryData<string> SupportedContainers
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var extension in new[] { ".mp3", ".flac", ".m4a" }) data.Add(extension);
            return data;
        }
    }

    // ---------------------------------------------------------------- supported containers

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void Mp3FlacAndM4aAreSupported(string extension)
    {
        Assert.True(GenreSemanticTagIo.IsSupportedExtension(extension), extension);
        Assert.True(GenreSemanticTagIo.IsSupportedExtension(extension.ToUpperInvariant()), extension);
    }

    [Theory]
    [InlineData(".m4b")]
    [InlineData(".mp4")]
    public void TheRestOfTheMp4FamilyIsSupported(string extension)
    {
        Assert.True(GenreSemanticTagIo.IsSupportedExtension(extension), extension);
    }

    [Theory]
    [InlineData(".aac")]
    [InlineData(".m4p")]
    public void ExtensionsOutsideTheSupportedContainerSetAreRejected(string extension)
    {
        // IsMp4Family is .m4a/.mp4/.m4b only, so semantic write-back refuses these
        // rather than writing into a container it has no verified encoding for.
        // SplitComposite still knows how to split them, which is harmless precisely
        // because nothing downstream ever reaches this gate with one of them.
        Assert.False(GenreSemanticTagIo.IsSupportedExtension(extension), extension);
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".ogg")]
    [InlineData(".opus")]
    [InlineData("")]
    public void UnsupportedExtensionsAreRejectedRatherThanGuessed(string extension)
    {
        Assert.False(GenreSemanticTagIo.IsSupportedExtension(extension), extension);
    }

    // ---------------------------------------------------------------- field naming

    [Fact]
    public void StyleHonoursAConfiguredCustomFieldNameWhileOtherDimensionsAreFixed()
    {
        Assert.Equal("MYSTYLE", GenreSemanticTagIo.RawTagName(PersonalGenreTaxonKind.Style, "MYSTYLE"));
        Assert.Equal("TCON", GenreSemanticTagIo.RawTagName(PersonalGenreTaxonKind.Genre, "MYSTYLE"));
        Assert.Equal("LANGUAGE", GenreSemanticTagIo.RawTagName(PersonalGenreTaxonKind.Language, "MYSTYLE"));
        Assert.Equal("SUBSTYLE", GenreSemanticTagIo.RawTagName(PersonalGenreTaxonKind.Substyle, "MYSTYLE"));
        Assert.Equal("CONTEXT", GenreSemanticTagIo.RawTagName(PersonalGenreTaxonKind.Context, "MYSTYLE"));
        Assert.Equal("SCENE", GenreSemanticTagIo.RawTagName(PersonalGenreTaxonKind.Scene, "MYSTYLE"));
    }

    // ---------------------------------------------------------------- round trips

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void EverySemanticDimensionSurvivesAWriteAndAReadBack(string extension)
    {
        var path = CreateAudio(extension);
        var resolution = Resolution(
            genres: ["Hip-Hop"],
            styles: ["Kenyan Drill"],
            substyles: ["Swahili Pop"],
            contexts: ["Afrosounds"],
            scenes: ["Zilizopendwa"],
            languages: ["Swahili"]);

        WriteSemanticFields(path, resolution, WriteAll());

        var snapshot = ReadBack(path);
        Assert.Equal(new[] { "Hip-Hop" }, Values(snapshot, PersonalGenreTaxonKind.Genre));
        Assert.Equal(new[] { "Kenyan Drill" }, Values(snapshot, PersonalGenreTaxonKind.Style));
        Assert.Equal(new[] { "Swahili Pop" }, Values(snapshot, PersonalGenreTaxonKind.Substyle));
        Assert.Equal(new[] { "Afrosounds" }, Values(snapshot, PersonalGenreTaxonKind.Context));
        Assert.Equal(new[] { "Zilizopendwa" }, Values(snapshot, PersonalGenreTaxonKind.Scene));
        Assert.Equal(new[] { "Swahili" }, Values(snapshot, PersonalGenreTaxonKind.Language));
    }

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void MultipleValuesInOneDimensionRoundTripInFileOrder(string extension)
    {
        var path = CreateAudio(extension);
        WriteSemanticFields(path, Resolution(genres: ["Hip-Hop", "Rap", "Trap"]), WriteAll());

        var snapshot = ReadBack(path);
        Assert.Equal(new[] { "Hip-Hop", "Rap", "Trap" }, Values(snapshot, PersonalGenreTaxonKind.Genre));
        Assert.Equal(new[] { 0, 1, 2 }, snapshot.Observations
            .Where(item => item.InputField == PersonalGenreTaxonKind.Genre)
            .Select(item => item.Order)
            .ToArray());
    }

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void AConfiguredCustomStyleFieldNameIsReadBackFromWhereItWasWritten(string extension)
    {
        var path = CreateAudio(extension);
        // The writer resolves the configured name, so the reader must be told the same
        // one. Threading the name through both halves is what keeps them in step.
        const string custom = "GENRE_STYLE";
        WriteSemanticFields(path, Resolution(styles: ["Drill"]), WriteAll(), custom);

        var snapshot = ReadBack(path, custom);
        Assert.Equal(new[] { "Drill" }, Values(snapshot, PersonalGenreTaxonKind.Style));
        // Nothing leaked into the genre field.
        Assert.Empty(Values(snapshot, PersonalGenreTaxonKind.Genre));
    }

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void AnUnpopulatedDimensionIsLeftAloneRatherThanCleared(string extension)
    {
        var path = CreateAudio(extension);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Genres = ["Pre-existing"];
            file.Save();
        }

        WriteSemanticFields(path, Resolution(genres: ["Hip-Hop"]), WriteAll());

        var snapshot = ReadBack(path);
        Assert.Equal(new[] { "Hip-Hop" }, Values(snapshot, PersonalGenreTaxonKind.Genre));
        // Dimensions absent from the plan are never written and never demanded.
        Assert.Empty(Values(snapshot, PersonalGenreTaxonKind.Scene));
        Assert.Empty(Values(snapshot, PersonalGenreTaxonKind.Language));
    }

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void WritingTheSemanticFieldsLeavesUnrelatedTagsUntouched(string extension)
    {
        var path = CreateAudio(extension);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "Original Title";
            file.Tag.Performers = ["Original Artist"];
            file.Tag.Album = "Original Album";
            file.Tag.Year = 1999;
            file.Save();
        }

        WriteSemanticFields(path, Resolution(genres: ["Hip-Hop"], styles: ["Drill"]), WriteAll());

        using var verify = TagLib.File.Create(path);
        Assert.Equal("Original Title", verify.Tag.Title);
        Assert.Equal("Original Artist", Assert.Single(verify.Tag.Performers));
        Assert.Equal("Original Album", verify.Tag.Album);
        Assert.Equal(1999u, verify.Tag.Year);
    }

    // ---------------------------------------------------------------- composite splitting

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".m4a")]
    [InlineData(".mp4")]
    public void JoinedMultiValueTagsAreSplitForTheContainersThatJoinThem(string extension)
    {
        // An ID3v2 frame and an MP4 free-form atom both store several values as one
        // separator-joined string, so a reader that does not split sees a single value
        // that can never match the taxonomy.
        Assert.Equal(new[] { "Trap", "Drill" }, GenreSemanticTagIo.SplitComposite("Trap, Drill", extension).ToArray());
        Assert.Equal(new[] { "Trap", "Drill" }, GenreSemanticTagIo.SplitComposite("Trap; Drill", extension).ToArray());
        Assert.Equal(new[] { "Trap", "Drill" }, GenreSemanticTagIo.SplitComposite("Trap\0Drill", extension).ToArray());
    }

    [Fact]
    public void VorbisIsNotSplitBecauseACommaIsLegalInsideAQuotedField()
    {
        Assert.Equal(new[] { "R&B, Slow Jams" },
            GenreSemanticTagIo.SplitComposite("R&B, Slow Jams", ".flac").ToArray());
    }

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void AJoinedValueWrittenDirectlyComesBackAsSeparateValues(string extension)
    {
        var path = CreateAudio(extension);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Genres = ["Trap, Drill"];
            file.Save();
        }

        var values = Values(ReadBack(path), PersonalGenreTaxonKind.Genre);
        Assert.Equal(extension == ".flac" ? new[] { "Trap, Drill" } : new[] { "Trap", "Drill" }, values);
    }

    // ---------------------------------------------------------------- PlanFields

    [Fact]
    public void PreservedUnknownValuesAreWrittenBackVerbatimAndUnformatted()
    {
        var resolution = Resolution(genres: ["Hip-Hop"], preserved:
            [new PreservedTagValue("my own tag", PersonalGenreTaxonKind.Style, 0, GenreObservationOrigin.PostPlatform)]);
        var plan = GenreSemanticTagIo.PlanFields(resolution);

        Assert.Equal(new[] { "Hip-Hop" }, plan[PersonalGenreTaxonKind.Genre]);
        // Not formatted: cleanup was told this value means nothing to the taxonomy, so
        // it returns to the field it was read from and is not entitled to restyling.
        Assert.Equal(new[] { "my own tag" }, plan[PersonalGenreTaxonKind.Style]);
    }

    [Fact]
    public void PlanFieldsDeduplicatesCaseInsensitivelyAndKeepsTheFirstSpelling()
    {
        var plan = GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop", "hip-hop", "HIP-HOP"]));
        Assert.Equal(new[] { "Hip-Hop" }, plan[PersonalGenreTaxonKind.Genre]);
    }

    [Fact]
    public void PlanFieldsSkipsBlankValuesWithoutCreatingAnEmptyDimension()
    {
        var plan = GenreSemanticTagIo.PlanFields(Resolution(genres: ["   "], styles: ["Drill"]));
        Assert.False(plan.ContainsKey(PersonalGenreTaxonKind.Genre));
        Assert.Equal(new[] { "Drill" }, plan[PersonalGenreTaxonKind.Style]);
    }

    // ---------------------------------------------------------------- write ownership

    [Theory]
    [InlineData(PersonalGenreTaxonKind.Genre, true)]
    [InlineData(PersonalGenreTaxonKind.Style, true)]
    [InlineData(PersonalGenreTaxonKind.Language, true)]
    [InlineData(PersonalGenreTaxonKind.Substyle, false)]
    [InlineData(PersonalGenreTaxonKind.Context, false)]
    [InlineData(PersonalGenreTaxonKind.Scene, false)]
    public void WriteOwnershipFollowsTheSelectedTagsAndTheOptInFlags(PersonalGenreTaxonKind field, bool bySelection)
    {
        Assert.Equal(bySelection,
            GenreSemanticTagIo.IsWriteEnabled(field, new AutoTagGenreIntelligenceSettings(), SelectedTags()));
    }

    [Fact]
    public void GenreStyleAndLanguageFollowTheSelectedTagsWhileTheRestNeedTheirOwnFlag()
    {
        var selected = SelectedTags();
        var off = new AutoTagGenreIntelligenceSettings();
        Assert.False(GenreSemanticTagIo.IsWriteEnabled(PersonalGenreTaxonKind.Substyle, off, selected));
        Assert.False(GenreSemanticTagIo.IsWriteEnabled(PersonalGenreTaxonKind.Context, off, selected));
        Assert.False(GenreSemanticTagIo.IsWriteEnabled(PersonalGenreTaxonKind.Scene, off, selected));

        var on = new AutoTagGenreIntelligenceSettings { WriteSubstyle = true, WriteContext = true, WriteScene = true };
        Assert.True(GenreSemanticTagIo.IsWriteEnabled(PersonalGenreTaxonKind.Substyle, on, selected));
        Assert.True(GenreSemanticTagIo.IsWriteEnabled(PersonalGenreTaxonKind.Context, on, selected));
        Assert.True(GenreSemanticTagIo.IsWriteEnabled(PersonalGenreTaxonKind.Scene, on, selected));
    }

    [Theory]
    [InlineData(PersonalGenreTaxonKind.Genre)]
    [InlineData(PersonalGenreTaxonKind.Style)]
    [InlineData(PersonalGenreTaxonKind.Language)]
    public void ASelectedTagPassesUngatedWhenNoSelectedSetIsSupplied(PersonalGenreTaxonKind field)
    {
        // A null set means the caller already filtered the plan to the enabled
        // dimensions, so the verifier must not second-guess it.
        Assert.True(GenreSemanticTagIo.IsWriteEnabled(field, new AutoTagGenreIntelligenceSettings(), null));
    }

    [Theory]
    [InlineData(PersonalGenreTaxonKind.Substyle)]
    [InlineData(PersonalGenreTaxonKind.Context)]
    [InlineData(PersonalGenreTaxonKind.Scene)]
    public void TheOptInDimensionsAreGatedByTheirFlagEvenWithNoSelectedSet(PersonalGenreTaxonKind field)
    {
        // These three are never part of the AutoTag tag selection, so the flag is the
        // only thing that can authorise them and a null selection cannot stand in for it.
        Assert.False(GenreSemanticTagIo.IsWriteEnabled(field, new AutoTagGenreIntelligenceSettings(), null));

        var enabled = new AutoTagGenreIntelligenceSettings
        {
            WriteSubstyle = true,
            WriteContext = true,
            WriteScene = true
        };
        Assert.True(GenreSemanticTagIo.IsWriteEnabled(field, enabled, null));
    }

    // ---------------------------------------------------------------- DescribeFieldMoves

    [Fact]
    public void DescribeFieldMovesNamesAddedValues()
    {
        var observed = new List<GenreTagObservation>
        {
            new("Hip-Hop", PersonalGenreTaxonKind.Genre),
            new("Rap", PersonalGenreTaxonKind.Genre)
        };
        Assert.Equal(new[] { "Genre: added Trap" }, GenreSemanticTagIo.DescribeFieldMoves(observed,
            GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop", "Rap", "Trap"]))));
    }

    [Fact]
    public void DescribeFieldMovesNamesReplacedValues()
    {
        var observed = new List<GenreTagObservation>
        {
            new("Hip-Hop", PersonalGenreTaxonKind.Genre),
            new("Rap", PersonalGenreTaxonKind.Genre)
        };
        Assert.Equal(new[] { "Genre: Trap replaced Rap" }, GenreSemanticTagIo.DescribeFieldMoves(observed,
            GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop", "Trap"]))));
    }

    [Fact]
    public void DescribeFieldMovesNamesRemovedValues()
    {
        // A dimension emptied by the resolution is the one way a value disappears, so
        // the move report has to be able to say so.
        var plan = new Dictionary<PersonalGenreTaxonKind, List<string>>
        {
            [PersonalGenreTaxonKind.Style] = []
        };
        Assert.Equal(new[] { "Style: removed Drill" }, GenreSemanticTagIo.DescribeFieldMoves(
            [new("Drill", PersonalGenreTaxonKind.Style)], plan));
    }

    [Fact]
    public void DescribeFieldMovesIgnoresDimensionsTheResolutionDidNotMention()
    {
        var unchanged = new List<GenreTagObservation> { new("Hip-Hop", PersonalGenreTaxonKind.Genre) };
        Assert.Empty(GenreSemanticTagIo.DescribeFieldMoves(unchanged,
            GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop"]))));
    }

    [Fact]
    public void DescribeFieldMovesIgnoresADimensionThatWasNotObserved()
    {
        var plan = GenreSemanticTagIo.PlanFields(Resolution(styles: ["Drill"]));
        Assert.Empty(GenreSemanticTagIo.DescribeFieldMoves(
            [new("Hip-Hop", PersonalGenreTaxonKind.Genre)], plan));
    }

    // ---------------------------------------------------------------- VerifyWrittenFields

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void VerificationReportsNoProblemWhenEveryIntendedFieldLanded(string extension)
    {
        var path = CreateAudio(extension);
        var resolution = Resolution(genres: ["Hip-Hop"], styles: ["Kenyan Drill"]);
        var plan = GenreSemanticTagIo.PlanFields(resolution);
        WriteSemanticFields(path, resolution, WriteAll());

        Assert.Empty(GenreSemanticTagIo.VerifyWrittenFields(
            path, extension, "STYLE", plan, WriteAll(), SelectedTags()));
    }

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void VerificationReportsAMissingFieldRatherThanSilentSuccess(string extension)
    {
        var path = CreateAudio(extension);
        // Intend two dimensions, write only one. This is the dropped-frame case the
        // function exists to catch.
        var plan = GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop"], styles: ["Kenyan Drill"]));
        WriteSemanticFields(path, Resolution(genres: ["Hip-Hop"]), WriteAll());

        var problems = GenreSemanticTagIo.VerifyWrittenFields(
            path, extension, "STYLE", plan, WriteAll(), SelectedTags());

        Assert.Contains("Style is missing Kenyan Drill", problems);
        Assert.DoesNotContain(problems, problem => problem.Contains("Genre"));
    }

    [Theory]
    [MemberData(nameof(SupportedContainers))]
    public void VerificationRejectsAFieldWrittenWithTheWrongValue(string extension)
    {
        var path = CreateAudio(extension);
        var plan = GenreSemanticTagIo.PlanFields(Resolution(styles: ["Kenyan Drill"]));
        // The container accepted the write but stored something else entirely.
        WriteSemanticFields(path, Resolution(styles: ["Drill"]), WriteAll());

        Assert.Contains("Style is missing Kenyan Drill", GenreSemanticTagIo.VerifyWrittenFields(
            path, extension, "STYLE", plan, WriteAll(), SelectedTags()));
    }

    [Fact]
    public void VerificationIgnoresCasingAndPunctuationButNotTheTermItself()
    {
        var path = CreateAudio(".flac");
        var plan = GenreSemanticTagIo.PlanFields(Resolution(styles: ["UK Garage"]));
        WriteSemanticFields(path, Resolution(styles: ["uk garage"]), WriteAll());

        Assert.Empty(GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", plan, WriteAll(), SelectedTags()));

        var other = GenreSemanticTagIo.PlanFields(Resolution(styles: ["Drill"]));
        Assert.Contains("Style is missing Drill",
            GenreSemanticTagIo.VerifyWrittenFields(path, ".flac", "STYLE", other, WriteAll(), SelectedTags()));
    }

    [Fact]
    public void AnEmptyPlanNeverProducesAMissingFieldError()
    {
        var path = CreateAudio(".flac");
        Assert.Empty(GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", GenreSemanticTagIo.PlanFields(Resolution()), WriteAll(), SelectedTags()));
    }

    [Fact]
    public void ADimensionAbsentFromThePlanIsNeverDemanded()
    {
        var path = CreateAudio(".flac");
        // The plan covers Genre only, so Scene is not a requirement at all.
        var plan = GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop"]));
        WriteSemanticFields(path, Resolution(genres: ["Hip-Hop"]), WriteAll());

        Assert.Empty(GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", plan, WriteAll(), SelectedTags()));
    }

    [Fact]
    public void ADisabledDimensionIsNotVerifiedEvenWhenThePlanMentionsIt()
    {
        var path = CreateAudio(".flac");
        // The plan mentions Scene but the write deliberately leaves it out.
        var plan = GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop"], scenes: ["Zilizopendwa"]));
        WriteSemanticFields(path, Resolution(genres: ["Hip-Hop"]), WriteAll());

        var off = WriteAll();
        off.WriteScene = false;
        // Scene was never written, but it is switched off, so it is not required.
        Assert.Empty(GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", plan, off, SelectedTags()));

        var on = WriteAll();
        on.WriteScene = true;
        Assert.Contains("Scene is missing Zilizopendwa",
            GenreSemanticTagIo.VerifyWrittenFields(path, ".flac", "STYLE", plan, on, SelectedTags()));
    }

    [Fact]
    public void AnUnselectedTagIsNotVerifiedEvenWhenThePlanMentionsIt()
    {
        var path = CreateAudio(".flac");
        // The plan mentions Style, but only Genre is written.
        var plan = GenreSemanticTagIo.PlanFields(Resolution(genres: ["Hip-Hop"], styles: ["Drill"]));
        WriteSemanticFields(path, Resolution(genres: ["Hip-Hop"]), WriteAll());

        // With Style selected the dropped value is reported...
        Assert.Contains("Style is missing Drill", GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", plan, WriteAll(), SelectedTags()));

        // ...and with Style unselected the very same plan is silent about it, which is
        // what makes this a selection gate rather than an accident of the plan.
        Assert.Empty(GenreSemanticTagIo.VerifyWrittenFields(
            path, ".flac", "STYLE", plan, WriteAll(), new HashSet<string> { "genre" }));

        // Symmetrically, on a fresh file: a plan whose Genre was never written goes
        // quiet when Genre is unselected.
        var second = CreateAudio(".flac");
        var genrePlan = GenreSemanticTagIo.PlanFields(Resolution(genres: ["Trap"]));
        Assert.Contains("Genre is missing Trap", GenreSemanticTagIo.VerifyWrittenFields(
            second, ".flac", "STYLE", genrePlan, WriteAll(), SelectedTags()));
        Assert.Empty(GenreSemanticTagIo.VerifyWrittenFields(
            second, ".flac", "STYLE", genrePlan, WriteAll(), new HashSet<string> { "style" }));
    }

    // ---------------------------------------------------------------- helpers

    private static HashSet<string> SelectedTags() => new(StringComparer.Ordinal) { "genre", "style", "language" };

    private static AutoTagGenreIntelligenceSettings WriteAll() => new()
    {
        Enabled = true,
        MaxGenres = 3,
        PreserveUnmappedTags = true,
        IncludeParentGenres = false,
        WriteSubstyle = true,
        WriteContext = true,
        WriteScene = true
    };

    private static List<string> Values(GenreSemanticSnapshot snapshot, PersonalGenreTaxonKind field)
        => snapshot.Observations.Where(item => item.InputField == field).Select(item => item.RawValue).ToList();

    private static GenreSemanticSnapshot ReadBack(string path, string stylesTagName = "STYLE")
        => PersonalGenreService.ReadFileSnapshot(path, stylesTagName);

    private string CreateAudio(string extension)
    {
        var path = Path.Combine(_directory, $"track{Guid.NewGuid():N}{extension}");
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
        return path;
    }

    private static PersonalGenreResolution Resolution(
        IReadOnlyList<string>? genres = null,
        IReadOnlyList<string>? styles = null,
        IReadOnlyList<string>? substyles = null,
        IReadOnlyList<string>? contexts = null,
        IReadOnlyList<string>? scenes = null,
        IReadOnlyList<string>? languages = null,
        IReadOnlyList<PreservedTagValue>? preserved = null)
        => new(
            genres?.FirstOrDefault(),
            genres ?? [],
            styles ?? [],
            substyles ?? [],
            contexts ?? [],
            scenes ?? [],
            languages ?? [],
            preserved ?? [],
            [],
            [],
            [],
            [],
            PersonalGenreResolver.Version);

    /// <summary>
    /// Drives the production writer so the test never reimplements the encoder.
    /// </summary>
    private static void WriteSemanticFields(
        string path,
        PersonalGenreResolution resolution,
        AutoTagGenreIntelligenceSettings options,
        string stylesTagName = "STYLE")
    {
        var plan = GenreSemanticTagIo.PlanFields(resolution);
        var extension = Path.GetExtension(path);

        // The same private writer AutoTag uses, so a fixture is encoded exactly as a
        // real run encodes it and an encoding bug cannot hide behind a test helper.
        var configType = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)!;
        var config = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new
            {
                Tags = SelectedTags().ToArray(),
                StylesOptions = "customTag",
                StylesCustomTag = new { Id3 = stylesTagName, Vorbis = stylesTagName, Mp4 = stylesTagName },
                GenreIntelligence = options
            }),
            configType)!;

        var write = typeof(LocalAutoTagRunner).GetMethod(
            "WriteGenreIntelligenceTags", BindingFlags.NonPublic | BindingFlags.Static)!;
        write.Invoke(null, [path, config, resolution, plan]);
    }
}
