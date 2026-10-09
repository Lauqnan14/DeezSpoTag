using System;
using System.Globalization;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// The identity of one scheduled generation occasion, and the only input to Random DJ.
/// </summary>
/// <remarks>
/// <para>Random DJ has to be unpredictable week to week and reproducible within a
/// week. A clock-based or unseeded random source gives the first property and loses
/// the second: a run that fails part-way is retried, and an unseeded pick would hand
/// the retry a different DJ, so a transient publish failure would visibly change the
/// playlist. Deriving the pick from this key instead means the retry reproduces the
/// original choice exactly, while next Tuesday — a different key — is free to differ.
/// </para>
///
/// <para>The key deliberately omits the mode. "Both" is two runs of one time occasion,
/// and a DJ is a personality applied to a listening context, not to a selection
/// algorithm: resolving Direct and Sonic separately would confound the mode
/// comparison with a second random variable. It is also why a separately scheduled
/// Direct run and a Sonic run of the same slot still agree.</para>
///
/// <para>It carries no DJ either. The resolved DJ is not part of playlist identity
/// (see <c>MelodayScheduleSlots.SlotIdForMix</c>), so it must not be part of the
/// identity of the occasion either — otherwise the run-state key would reset whenever
/// Random landed somewhere new.</para>
/// </remarks>
public static class DjOccurrenceKey
{
    /// <summary>
    /// Version tag mixed into every key.
    ///
    /// <para>Bump this only if the mapping from key to DJ must change retroactively.
    /// Changing how the key is built changes which DJ a given day resolves to, which
    /// is a deliberate act rather than a refactor.</para>
    /// </summary>
    public const string AlgorithmVersion = "v1";

    private const string Separator = "|";

    /// <summary>One key per library, per time slot, per local calendar day.</summary>
    public static string ForOccurrence(long libraryId, string? slotId, DateOnly localDate)
    {
        return string.Join(
            Separator,
            "meloday-dj",
            libraryId.ToString(CultureInfo.InvariantCulture),
            NormalizeSlotId(slotId),
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AlgorithmVersion);
    }

    private static string NormalizeSlotId(string? slotId)
        => (slotId ?? string.Empty).Trim().ToLowerInvariant();
}
