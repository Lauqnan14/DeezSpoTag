using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>Why a candidate artist was accepted or rejected.</summary>
public enum AudiomackArtistMatchOutcome
{
    /// <summary>The candidate's name matched and its catalogue contains an expected album.</summary>
    Accepted,

    /// <summary>
    /// The candidate's name matched but the album cross-check could not run: the
    /// library holds no albums for the artist, or Audiomack exposed no catalogue
    /// albums for the candidate. This is the explicit name-only fallback.
    /// </summary>
    AcceptedWithoutAlbumCrossCheck,

    /// <summary>The candidate's name does not match the artist name we hold.</summary>
    RejectedNameMismatch,

    /// <summary>The candidate's catalogue contains none of the albums we hold.</summary>
    RejectedAlbumMismatch
}

/// <summary>
/// The result of confirming an Audiomack artist-search candidate against the artist
/// name we hold and, when available, the albums we hold. <see cref="Reason"/> is a
/// log-friendly explanation, populated for both accept and reject decisions.
/// </summary>
public sealed record AudiomackArtistMatchDecision(AudiomackArtistMatchOutcome Outcome, string? Reason)
{
    public bool Accepted => Outcome
        is AudiomackArtistMatchOutcome.Accepted
        or AudiomackArtistMatchOutcome.AcceptedWithoutAlbumCrossCheck;

    public bool IsAlbumMismatch => Outcome == AudiomackArtistMatchOutcome.RejectedAlbumMismatch;
}

/// <summary>
/// Confirms that an Audiomack search hit is really the artist we hold. A text search
/// alone can land on a different artist with the same name, so a candidate is only
/// trusted when the artist name matches *and* — when we hold albums and Audiomack
/// exposes a catalogue for the candidate — the candidate's catalogue contains at
/// least one of those albums. Name-only matching is allowed only when there is
/// nothing to cross-check against, and that fallback is reported explicitly so it
/// is never silent.
/// </summary>
internal static class AudiomackArtistMatcher
{
    private const int MinimumNameLengthForPrefixMatch = 4;

    /// <summary>
    /// Case/punctuation-insensitive artist-name comparison. A short prefix match is
    /// accepted only when both names are at least four characters, so "Al" never
    /// matches "Alikiba".
    /// </summary>
    internal static bool IsSameArtist(string? candidateName, string? expectedName)
    {
        if (string.IsNullOrWhiteSpace(candidateName) || string.IsNullOrWhiteSpace(expectedName))
        {
            return false;
        }

        var expected = NormalizeName(expectedName);
        var actual = NormalizeName(candidateName);
        if (expected.Length == 0 || actual.Length == 0)
        {
            return false;
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        return expected.Length >= MinimumNameLengthForPrefixMatch
            && actual.Length >= MinimumNameLengthForPrefixMatch
            && (expected.StartsWith(actual, StringComparison.Ordinal)
                || actual.StartsWith(expected, StringComparison.Ordinal));
    }

    internal static AudiomackArtistMatchDecision Confirm(
        string? candidateName,
        string? expectedName,
        IReadOnlyList<string>? expectedAlbums,
        IReadOnlyList<string>? candidateAlbums)
    {
        if (!IsSameArtist(candidateName, expectedName))
        {
            return new AudiomackArtistMatchDecision(
                AudiomackArtistMatchOutcome.RejectedNameMismatch,
                $"candidate '{candidateName}' does not match artist '{expectedName}'");
        }

        var expected = CleanTitles(expectedAlbums);
        if (expected.Count == 0)
        {
            return new AudiomackArtistMatchDecision(
                AudiomackArtistMatchOutcome.AcceptedWithoutAlbumCrossCheck,
                "no library albums available for an album cross-check");
        }

        var catalogue = CleanTitles(candidateAlbums);
        if (catalogue.Count == 0)
        {
            return new AudiomackArtistMatchDecision(
                AudiomackArtistMatchOutcome.AcceptedWithoutAlbumCrossCheck,
                "Audiomack exposed no catalogue albums for the candidate; name-only match");
        }

        if (expected.Any(held => catalogue.Any(actual => AlbumTitlesMatch(held, actual))))
        {
            return new AudiomackArtistMatchDecision(
                AudiomackArtistMatchOutcome.Accepted,
                $"candidate catalogue contains a held album ({string.Join(", ", expected.Intersect(catalogue, StringComparer.OrdinalIgnoreCase))})");
        }

        return new AudiomackArtistMatchDecision(
            AudiomackArtistMatchOutcome.RejectedAlbumMismatch,
            $"candidate catalogue [{string.Join(", ", catalogue)}] contains none of the held albums [{string.Join(", ", expected)}]");
    }

    /// <summary>
    /// True when the search API's numeric id and the artist page's own id agree.
    /// When either side is missing the id cannot be confirmed and is not treated as
    /// a mismatch (the name/album checks still apply); a present-but-different id is
    /// a hard mismatch and the candidate must not be trusted.
    /// </summary>
    internal static bool ArtistIdMatches(long? candidateId, string? pageArtistId)
    {
        if (candidateId is not > 0 || string.IsNullOrWhiteSpace(pageArtistId))
        {
            return true;
        }

        return string.Equals(
            candidateId.Value.ToString(CultureInfo.InvariantCulture),
            pageArtistId.Trim(),
            StringComparison.Ordinal);
    }

    internal static string NormalizeName(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Normalizes an album title for comparison: lowercase, non-alphanumerics
    /// collapsed to single spaces. Exact equality or containment (both sides at
    /// least three characters) counts as the same album, so "Piano Season" matches
    /// "Piano Season (Deluxe)".
    /// </summary>
    internal static bool AlbumTitlesMatch(string left, string right)
    {
        var a = NormalizeAlbumTitle(left);
        var b = NormalizeAlbumTitle(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        return a.Length >= 3
            && b.Length >= 3
            && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    internal static string NormalizeAlbumTitle(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(ch);
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private static List<string> CleanTitles(IReadOnlyList<string>? titles)
    {
        if (titles is not { Count: > 0 })
        {
            return new List<string>();
        }

        var cleaned = new List<string>();
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var trimmed = title.Trim();
            if (!cleaned.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                cleaned.Add(trimmed);
            }
        }

        return cleaned;
    }
}
