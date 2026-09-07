using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// A named Meloday daypart slot. The name does not imply fixed hours; each slot carries a
/// user-configured exact local generation time (HH:mm). A slot generates a playlist for
/// every library that selects it.
/// </summary>
public sealed record MelodayScheduleSlot(
    string Id,
    string Name,
    string GenerateAt,
    int Order);

/// <summary>
/// Per-library Meloday configuration: the library's playlist mode, how many slots it may
/// select, and which scheduled slots it wants. A library is targeted when it has slots.
/// </summary>
public sealed record MelodayLibrarySchedule(
    long LibraryId,
    int MaxActivePlaylists,
    string Mode,
    List<string> SlotIds)
{
    [JsonIgnore]
    public bool IsTargeted => (SlotIds ?? new List<string>()).Count > 0;

    [JsonIgnore]
    public bool ProducesBothPlaylists => MelodayModes.Normalize(Mode) == MelodayModes.Both;
}

/// <summary>Canonical slot definitions, time parsing, naming and limit arithmetic.</summary>
public static class MelodayScheduleSlots
{
    public const string EarlyMorningId = "early-morning";
    public const string MorningId = "morning";
    public const string MiddayId = "midday";
    public const string NoonId = "noon";
    public const string AfternoonId = "afternoon";
    public const string EveningId = "evening";
    public const string LateEveningId = "late-evening";

    public const int DefaultMaxActivePlaylists = 4;
    public const int MaxAllowedPlaylistsPerLibrary = 7;

    public static IReadOnlyList<MelodayScheduleSlot> Defaults { get; } = new[]
    {
        new MelodayScheduleSlot(EarlyMorningId, "Early Morning", "05:30", 0),
        new MelodayScheduleSlot(MorningId, "Morning", "08:30", 1),
        new MelodayScheduleSlot(MiddayId, "Midday", "11:00", 2),
        new MelodayScheduleSlot(NoonId, "Noon", "13:00", 3),
        new MelodayScheduleSlot(AfternoonId, "Afternoon", "16:00", 4),
        new MelodayScheduleSlot(EveningId, "Evening", "19:00", 5),
        new MelodayScheduleSlot(LateEveningId, "Late Evening", "22:30", 6)
    };

    private static readonly Dictionary<string, string> PhrasesById = new(StringComparer.OrdinalIgnoreCase)
    {
        [EarlyMorningId] = "in the early morning",
        [MorningId] = "in the morning",
        [MiddayId] = "at midday",
        [NoonId] = "around noon",
        [AfternoonId] = "during the afternoon",
        [EveningId] = "in the evening",
        [LateEveningId] = "late in the evening"
    };

    public static string SlotName(string? slotId)
        => Defaults.FirstOrDefault(slot => string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase))?.Name
           ?? (string.IsNullOrWhiteSpace(slotId) ? "Meloday" : slotId.Trim());

    public static string SlotPhrase(string? slotId)
        => PhrasesById.TryGetValue(slotId?.Trim().ToLowerInvariant() ?? string.Empty, out var phrase)
            ? phrase
            : "today";

    public static int SlotOrder(string? slotId)
        => Defaults.FirstOrDefault(slot => string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase))?.Order
           ?? int.MaxValue;

    public static bool IsKnownSlot(string? slotId)
        => !string.IsNullOrWhiteSpace(slotId)
           && Defaults.Any(slot => string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase));

    public static string NormalizeSlotId(string? slotId)
    {
        var normalized = (slotId ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '-');
        return IsKnownSlot(normalized) ? normalized : string.Empty;
    }

    /// <summary>Parses "HH:mm" (24h) into minutes since local midnight; null when invalid.</summary>
    public static int? TryParseMinutes(string? generateAt)
    {
        var trimmed = (generateAt ?? string.Empty).Trim();
        var parts = trimmed.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minute)
            || hour is < 0 or > 23
            || minute is < 0 or > 59)
        {
            return null;
        }

        return (hour * 60) + minute;
    }

    public static string FormatMinutes(int minutes)
        => $"{minutes / 60:D2}:{minutes % 60:D2}";

    public static string NormalizeTime(string? generateAt, string defaultTime)
        => TryParseMinutes(generateAt) is { } minutes ? FormatMinutes(minutes) : defaultTime;

    /// <summary>
    /// Rule-based playlist naming. Direct playlists drop the mode word; Sonic keeps it;
    /// Both produces the Direct name and the Sonic name.
    /// </summary>
    public static string PlaylistName(string libraryName, string slotName, string mode)
    {
        var library = string.IsNullOrWhiteSpace(libraryName) ? "Library" : libraryName.Trim();
        var slot = string.IsNullOrWhiteSpace(slotName) ? "Meloday" : slotName.Trim();
        return MelodayModes.Normalize(mode) == MelodayModes.Sonic
            ? $"{slot} Sonic Playlist for {library}"
            : $"{slot} Playlist for {library}";
    }

    public static IReadOnlyList<string> PlaylistNamesForMode(string libraryName, string slotName, string mode)
        => MelodayModes.Normalize(mode) == MelodayModes.Both
            ? new[]
            {
                PlaylistName(libraryName, slotName, MelodayModes.Direct),
                PlaylistName(libraryName, slotName, MelodayModes.Sonic)
            }
            : new[] { PlaylistName(libraryName, slotName, mode) };

    /// <summary>Normalized canonical slot list: every known slot present exactly once, in canonical order.</summary>
    public static List<MelodayScheduleSlot> Normalize(IEnumerable<MelodayScheduleSlot>? slots)
    {
        var stored = (slots ?? Array.Empty<MelodayScheduleSlot>())
            .Where(slot => IsKnownSlot(slot.Id))
            .GroupBy(slot => NormalizeSlotId(slot.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return Defaults
            .Select(defaultSlot => stored.TryGetValue(defaultSlot.Id, out var storedSlot)
                ? new MelodayScheduleSlot(
                    defaultSlot.Id,
                    defaultSlot.Name,
                    NormalizeTime(storedSlot.GenerateAt, defaultSlot.GenerateAt),
                    defaultSlot.Order)
                : defaultSlot)
            .ToList();
    }

    /// <summary>Normalized per-library schedule list ordered by library id. Last slot selection wins per slot.</summary>
    public static List<MelodayLibrarySchedule> NormalizeLibraries(IEnumerable<MelodayLibrarySchedule>? libraries)
    {
        var normalized = new List<MelodayLibrarySchedule>();
        var seenLibraryIds = new HashSet<long>();
        foreach (var library in libraries ?? Array.Empty<MelodayLibrarySchedule>())
        {
            if (library.LibraryId <= 0 || !seenLibraryIds.Add(library.LibraryId))
            {
                continue;
            }

            var slotIds = new List<string>();
            foreach (var slotId in library.SlotIds ?? new List<string>())
            {
                var normalizedSlotId = NormalizeSlotId(slotId);
                if (normalizedSlotId.Length == 0)
                {
                    continue;
                }

                slotIds.RemoveAll(existing => string.Equals(existing, normalizedSlotId, StringComparison.OrdinalIgnoreCase));
                slotIds.Add(normalizedSlotId);
            }

            normalized.Add(new MelodayLibrarySchedule(
                library.LibraryId,
                MelodayClamp.PositiveOrDefault(library.MaxActivePlaylists, DefaultMaxActivePlaylists, 1, MaxAllowedPlaylistsPerLibrary),
                MelodayModes.Normalize(string.IsNullOrWhiteSpace(library.Mode) ? MelodayModes.Sonic : library.Mode),
                slotIds.OrderBy(SlotOrder).ToList()));
        }

        return normalized.OrderBy(library => library.LibraryId).ToList();
    }

    /// <summary>Actual playlist count for a library: "both" mode doubles every selected slot.</summary>
    public static int CountPlaylists(MelodayLibrarySchedule library)
        => (library.SlotIds ?? new List<string>()).Count * (library.ProducesBothPlaylists ? 2 : 1);

    public static string SlotIdForMix(long libraryId, string slotId, string mode)
        => $"meloday-{libraryId}-{NormalizeSlotId(slotId)}-{MelodayModes.Normalize(mode)}";
}
