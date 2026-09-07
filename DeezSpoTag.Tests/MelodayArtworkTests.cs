using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MelodayArtworkTests
{
    private static readonly IReadOnlyList<string> Deck = Enumerable.Range(1, 18)
        .Select(index => $"{index:00}.jpg")
        .ToList();

    private static MelodayArtworkAssignment Assignment(long libraryId, string slotId, string mode, string imageId)
        => new(libraryId, slotId, mode, imageId);

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

        var imageId = MelodayArtworkAllocator.Allocate(Deck, assignments, 12, "morning", "sonic");

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
            var imageId = MelodayArtworkAllocator.Allocate(deck, assignments.Concat(allocated).ToList(), libraryId, slotId, "sonic");
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
        var first = MelodayArtworkAllocator.Allocate(smallDeck, assignments, 12, "morning", "sonic");
        // Second Music playlist: every image is now globally used, but must not duplicate
        // Music's own cover while an image unused by Music exists.
        var second = MelodayArtworkAllocator.Allocate(
            smallDeck,
            assignments.Append(Assignment(12, "morning", "sonic", first!)).ToList(),
            12,
            "evening",
            "sonic");

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

        var third = MelodayArtworkAllocator.Allocate(smallDeck, assignments, 12, "late-evening", "sonic");
        assignments.Add(Assignment(12, "late-evening", "sonic", third!));
        var fourth = MelodayArtworkAllocator.Allocate(smallDeck, assignments, 12, "noon", "sonic");

        // Pool exhausted for this library: the cycle restarts from the earliest deck position.
        Assert.Equal(smallDeck[2], third);
        Assert.Equal(smallDeck[0], fourth);
    }

    [Fact]
    public void Allocate_Returns_Null_For_An_Empty_Pool()
    {
        Assert.Null(MelodayArtworkAllocator.Allocate(Array.Empty<string>(), Array.Empty<MelodayArtworkAssignment>(), 12, "morning", "sonic"));
    }

    [Fact]
    public void Allocate_Ignores_Assignments_Pointing_At_Deleted_Images()
    {
        var assignments = new[] { Assignment(12, "morning", "sonic", "deleted.jpg") };

        var imageId = MelodayArtworkAllocator.Allocate(Deck, assignments, 12, "morning", "sonic");

        Assert.Contains(imageId, Deck);
    }
}
