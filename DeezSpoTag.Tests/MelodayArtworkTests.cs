using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MelodayArtworkTests
{
    private static readonly IReadOnlyList<string> Deck = Enumerable.Range(1, 18)
        .Select(index => $"{index:00}.jpg")
        .ToList();

    private static MelodayArtworkAssignment Assignment(long libraryId, string slotId, string mode, string imageId, string weekday = "monday")
        => new(libraryId, slotId, mode, imageId, weekday);

    [Fact]
    public void Deck_Is_Deterministic_For_The_Same_Pool()
    {
        var first = MelodayArtworkPool.BuildDeck(Deck);
        var second = MelodayArtworkPool.BuildDeck(Deck);

        Assert.Equal(first, second);
        // A full shuffle actually happened.
        Assert.NotEqual(Deck, first);
        Assert.Equal(Deck.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase), first.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Allocate_Keeps_An_Existing_Valid_Assignment()
    {
        var assignments = new[] { Assignment(12, "morning", "sonic", "07.jpg") };

        var imageId = MelodayArtworkAllocator.Allocate(Deck, assignments, 12, "morning", "sonic", "monday");

        Assert.Equal("07.jpg", imageId);
    }

    [Fact]
    public void Allocate_Picks_Globally_Unused_Images_First()
    {
        var deck = MelodayArtworkPool.BuildDeck(Deck);
        var assignments = deck.Take(10)
            .Select((imageId, index) => Assignment(30 + index, "morning", "sonic", imageId))
            .ToList();

        var allocated = new List<MelodayArtworkAssignment>();
        foreach (var (libraryId, slotId) in new[] { (11L, "evening"), (11L, "noon"), (12L, "morning") })
        {
            var imageId = MelodayArtworkAllocator.Allocate(deck, assignments.Concat(allocated).ToList(), libraryId, slotId, "sonic", "monday");
            allocated.Add(Assignment(libraryId, slotId, "sonic", imageId!));
        }

        // Three new playlists received three different covers from the unused deck tail,
        // in deck order.
        var covers = allocated.Select(assignment => assignment.ImageId).ToList();
        Assert.Equal(deck.Skip(10).Take(3), covers);
    }

    [Fact]
    public void Allocate_Protects_Library_Uniqueness_After_Global_Exhaustion()
    {
        // Pool of 5, every image already used globally by other libraries.
        var smallDeck = Deck.Take(5).ToList();
        var assignments = smallDeck
            .Select((imageId, index) => Assignment(20 + index, "morning", "sonic", imageId))
            .ToList();

        // Music has none yet: first allocation must give an image unused by Music (all of them are).
        var first = MelodayArtworkAllocator.Allocate(smallDeck, assignments, 12, "morning", "sonic", "monday");
        // Second Music playlist: every image is now globally used, but must not duplicate
        // Music's own cover while an image unused by Music exists.
        var second = MelodayArtworkAllocator.Allocate(
            smallDeck,
            assignments.Append(Assignment(12, "morning", "sonic", first!)).ToList(),
            12,
            "evening",
            "sonic",
            "monday");

        Assert.NotEqual(first, second);
        Assert.Contains(first, smallDeck);
        Assert.Contains(second, smallDeck);
    }

    [Fact]
    public void Allocate_Starts_A_New_Cycle_When_The_Library_Exhausts_The_Pool()
    {
        var smallDeck = Deck.Take(3).ToList();
        var assignments = new List<MelodayArtworkAssignment>
        {
            Assignment(12, "morning", "sonic", smallDeck[0]),
            Assignment(12, "evening", "sonic", smallDeck[1])
        };

        var third = MelodayArtworkAllocator.Allocate(smallDeck, assignments, 12, "late-evening", "sonic", "monday");
        assignments.Add(Assignment(12, "late-evening", "sonic", third!));
        var fourth = MelodayArtworkAllocator.Allocate(smallDeck, assignments, 12, "noon", "sonic", "monday");

        // Pool exhausted for this library: the cycle restarts from the earliest deck position.
        Assert.Equal(smallDeck[2], third);
        Assert.Equal(smallDeck[0], fourth);
    }

    [Fact]
    public void Allocate_Returns_Null_For_An_Empty_Pool()
    {
        Assert.Null(MelodayArtworkAllocator.Allocate(Array.Empty<string>(), Array.Empty<MelodayArtworkAssignment>(), 12, "morning", "sonic", "monday"));
    }

    [Fact]
    public void Allocate_Ignores_Assignments_Pointing_At_Deleted_Images()
    {
        var assignments = new[] { Assignment(12, "morning", "sonic", "deleted.jpg") };

        var imageId = MelodayArtworkAllocator.Allocate(Deck, assignments, 12, "morning", "sonic", "monday");

        Assert.Contains(imageId, Deck);
    }

    [Fact]
    public void TryResolveExistingCoverWebPath_Finds_The_Generated_Cover_For_A_Mix()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-meloday-cover-url-" + Path.GetRandomFileName());
        var generatedDir = Path.Join(root, "images", "meloday", "generated");
        Directory.CreateDirectory(generatedDir);
        try
        {
            var coverPath = Path.Join(generatedDir, "meloday-7-noon-sonic-tuesday-e1683dc8.jpg");
            File.WriteAllBytes(coverPath, [0xFF, 0xD8, 0xFF]);
            var composer = new MelodayCoverComposer(
                new StubWebHostEnvironment(root),
                NullLogger<MelodayCoverComposer>.Instance);

            var url = composer.TryResolveExistingCoverWebPath("meloday-7-noon-sonic-tuesday");

            Assert.Equal("/images/meloday/generated/meloday-7-noon-sonic-tuesday-e1683dc8.jpg", url);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Compose_Keeps_The_Source_Image_Visible_Under_The_Text()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-meloday-cover-" + Path.GetRandomFileName());
        var webRoot = Path.Join(root, "wwwroot");
        var fontDir = Path.Join(webRoot, "fonts", "onetagger");
        var generatedDir = Path.Join(webRoot, "images", "meloday", "generated");
        Directory.CreateDirectory(fontDir);
        Directory.CreateDirectory(generatedDir);
        try
        {
            File.Copy(ResolveDosisFont(), Path.Join(fontDir, "Dosis-Bold.ttf"));
            var sourcePath = Path.Join(root, "source.jpg");
            using (var source = new Image<Rgba32>(200, 200, new Rgba32(220, 40, 40)))
            {
                source.SaveAsJpeg(sourcePath);
            }

            var composer = new MelodayCoverComposer(
                new StubWebHostEnvironment(webRoot),
                NullLogger<MelodayCoverComposer>.Instance);
            var result = composer.Compose(sourcePath, "Noon", "Aggressive · Rap", 7, "noon", "sonic", "tuesday", null);

            Assert.NotNull(result);
            Assert.True(File.Exists(result!.FilePath));
            using var composed = Image.Load<Rgba32>(result.FilePath);
            var sample = composed[500, 200];
            Assert.True(sample.R > 150, $"Top of the cover should keep the source art, but the pixel was {sample}.");
            Assert.True(sample.G < 90, $"Top of the cover was not the source red; pixel was {sample}.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string ResolveDosisFont()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Join(directory.FullName, "DeezSpoTag.Web", "wwwroot", "fonts", "onetagger", "Dosis-Bold.ttf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Dosis-Bold.ttf was not found.");
    }

    private sealed class StubWebHostEnvironment(string webRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = webRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = webRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
