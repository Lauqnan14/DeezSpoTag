namespace DeezSpoTag.Services.Genre;

using System.Text.RegularExpressions;

/// <summary>
/// Rewrites one shared word inside a name. Kenyan hip hop and Hip-Hop stay
/// different terms. A convention for hiphop changes only that word.
/// </summary>
public static class GenreSpellingConventions
{
    public static string Apply(string value, IReadOnlyList<PersonalGenreSpellingConvention>? conventions)
    {
        if (string.IsNullOrWhiteSpace(value) || conventions is null || conventions.Count == 0)
        {
            return value;
        }

        var output = value;
        foreach (var convention in conventions)
        {
            var key = new string((convention.Key ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            var spelling = convention.Spelling?.Trim() ?? string.Empty;
            if (key.Length == 0 || spelling.Length == 0)
            {
                continue;
            }

            var pattern = string.Join(@"[\s\-]*", key.Select(character => Regex.Escape(character.ToString())));
            output = Regex.Replace(
                output,
                $@"(?<![0-9A-Za-z]){pattern}(?![0-9A-Za-z])",
                spelling,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return output;
    }
}
