using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MelodayScheduleTests
{
    [Fact]
    public void Defaults_Contain_The_Seven_Schedule_Slots_With_Default_Times()
    {
        var defaults = MelodayScheduleSlots.Defaults;

        Assert.Equal(7, defaults.Count);
        Assert.Equal(new[]
        {
            ("early-morning", "Early Morning", "05:30"),
            ("morning", "Morning", "08:30"),
            ("midday", "Midday", "11:00"),
            ("noon", "Noon", "13:00"),
            ("afternoon", "Afternoon", "16:00"),
            ("evening", "Evening", "19:00"),
            ("late-evening", "Late Evening", "22:30")
        }, defaults.Select(slot => (slot.Id, slot.Name, slot.GenerateAt)).ToArray());
        Assert.Equal(Enumerable.Range(0, 7), defaults.Select(slot => slot.Order));
    }

    [Fact]
    public void DaypartHours_With_Default_Times_Bound_Morning_Between_Morning_And_Midday()
    {
        var hours = MelodayScheduleMath.DaypartHours(MelodayScheduleSlots.Defaults, "morning");

        Assert.Equal(new[] { 8, 9, 10 }, hours);
    }

    [Fact]
    public void DaypartHours_Wraps_Midnight_For_Late_Evening()
    {
        var hours = MelodayScheduleMath.DaypartHours(MelodayScheduleSlots.Defaults, "late-evening");

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 22, 23 }, hours);
    }

    [Fact]
    public void DaypartHours_Uses_User_Configured_Times_Not_Fixed_Hours()
    {
        var slots = new[]
        {
            new MelodayScheduleSlot("morning", "Morning", true, "07:15", 1),
            new MelodayScheduleSlot("evening", "Evening", true, "20:00", 5)
        };

        Assert.Equal(new[] { 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 }, MelodayScheduleMath.DaypartHours(slots, "morning"));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 20, 21, 22, 23 }, MelodayScheduleMath.DaypartHours(slots, "evening"));
    }

    [Fact]
    public void DaypartHours_Partition_The_Whole_Day_Across_All_Seven_Slots()
    {
        var slots = MelodayScheduleSlots.Normalize(null);

        var covered = slots
            .Select(slot => MelodayScheduleMath.DaypartHours(slots, slot.Id))
            .SelectMany(static hours => hours)
            .Distinct()
            .OrderBy(hour => hour)
            .ToList();

        Assert.Equal(Enumerable.Range(0, 24), covered);
    }

    [Fact]
    public void IsDue_Is_True_Only_Within_The_Grace_Window_When_Not_Generated_Today()
    {
        var slot = new MelodayScheduleSlot("morning", "Morning", true, "08:30", 1);
        var today = new DateOnly(2031, 4, 2);

        // Before the scheduled time.
        Assert.False(MelodayScheduleMath.IsDue(slot, today, new TimeOnly(8, 29), null, 60));
        // Inside the grace window.
        Assert.True(MelodayScheduleMath.IsDue(slot, today, new TimeOnly(8, 42), null, 60));
        // Past the grace window: stale, skip for the day.
        Assert.False(MelodayScheduleMath.IsDue(slot, today, new TimeOnly(14, 0), null, 60));
        // Already generated today: do nothing.
        Assert.False(MelodayScheduleMath.IsDue(slot, today, new TimeOnly(8, 42), new MelodayRunStateEntry(today.ToString("o"), DateTimeOffset.UtcNow, "complete"), 60));
        // Generated yesterday: due again.
        Assert.True(MelodayScheduleMath.IsDue(slot, today, new TimeOnly(8, 42), new MelodayRunStateEntry(today.AddDays(-1).ToString("o"), DateTimeOffset.UtcNow, "complete"), 60));
        // Disabled slot is never due.
        Assert.False(MelodayScheduleMath.IsDue(slot with { Enabled = false }, today, new TimeOnly(8, 42), null, 60));
    }

    [Fact]
    public void CountPlaylists_Treats_Both_As_Two_Playlists()
    {
        var library = new MelodayLibrarySchedule(12, true, 4, new List<MelodayLibrarySlotAssignment>
        {
            new("morning", "sonic"),
            new("evening", "sonic"),
            new("late-evening", "both")
        });

        Assert.Equal(4, MelodayScheduleSlots.CountPlaylists(library));
    }

    [Fact]
    public void Normalize_Limits_MaxActivePlaylists_To_1_Through_7()
    {
        var normalized = MelodayScheduleSlots.NormalizeLibraries(new[]
        {
            new MelodayLibrarySchedule(12, true, 99, new List<MelodayLibrarySlotAssignment>()),
            new MelodayLibrarySchedule(13, true, 0, new List<MelodayLibrarySlotAssignment>())
        });

        Assert.Equal(7, normalized[0].MaxActivePlaylists);
        Assert.Equal(MelodayScheduleSlots.DefaultMaxActivePlaylists, normalized[1].MaxActivePlaylists);
    }

    [Fact]
    public void NormalizeLibraries_Deduplicates_Slots_And_Keeps_Canonical_Order()
    {
        var normalized = MelodayScheduleSlots.NormalizeLibraries(new[]
        {
            new MelodayLibrarySchedule(12, true, 4, new List<MelodayLibrarySlotAssignment>
            {
                new("evening", "sonic"),
                new("morning", "both"),
                new("morning", "direct"),
                new("unknown-slot", "sonic")
            })
        });

        var slots = normalized[0].Slots;
        Assert.Equal(new[] { "morning", "evening" }, slots.Select(assignment => assignment.SlotId));
        Assert.Equal("direct", slots[0].Mode);
    }

    [Fact]
    public void NormalizeSlots_Keeps_The_Canonical_List_And_Repairs_Invalid_Times()
    {
        var normalized = MelodayScheduleSlots.Normalize(new[]
        {
            new MelodayScheduleSlot("morning", "Ignored Name", true, "9:75", 99),
            new MelodayScheduleSlot("made-up", "Ghost", true, "10:00", 100)
        });

        Assert.Equal(7, normalized.Count);
        var morning = normalized.Single(slot => slot.Id == "morning");
        Assert.Equal("Morning", morning.Name);
        Assert.Equal("08:30", morning.GenerateAt);
        Assert.Equal(1, morning.Order);
        Assert.True(morning.Enabled);
        // Slots absent from storage stay at their defaults.
        Assert.True(normalized.Single(slot => slot.Id == "afternoon").Enabled);
        Assert.Equal("16:00", normalized.Single(slot => slot.Id == "afternoon").GenerateAt);
    }

    [Fact]
    public void DescribeNextSlot_Reports_The_Next_Enabled_Occurrence()
    {
        var slots = new[]
        {
            new MelodayScheduleSlot("morning", "Morning", true, "08:30", 1),
            new MelodayScheduleSlot("evening", "Evening", true, "19:00", 5),
            new MelodayScheduleSlot("midday", "Midday", false, "11:00", 2)
        };
        var now = new DateTimeOffset(2031, 4, 2, 12, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal("Evening at 7:00 PM", MelodayScheduleMath.DescribeNextSlot(slots, now));
        Assert.Null(MelodayScheduleMath.DescribeNextSlot(
            slots.Where(slot => slot.Id == "midday").ToList(), now));
    }

    [Fact]
    public void MixIdentity_Encodes_Library_Slot_And_Mode()
    {
        Assert.Equal("meloday-12-morning-sonic", MelodayScheduleSlots.SlotIdForMix(12, "Morning", "sonic"));
        Assert.Equal("meloday-12-evening-sonic", MelodayScheduleSlots.SlotIdForMix(12, "Evening", "SONIC"));
        Assert.Equal("meloday-18-evening-sonic", MelodayScheduleSlots.SlotIdForMix(18, "evening", "sonic"));
    }
}
