using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Web.Services.Audiomack;

public sealed record AudiomackLocationResult(
    string RawLocation,
    string? City,
    string? Country,
    string? CountryCode,
    string Source = "audiomack");

/// <summary>
/// Normalizes raw Audiomack artist location values (for example "Lagos, Nigeria",
/// "Nairobi", "Thika") into city / country / ISO 3166-1 alpha-2 country code.
/// Pure and side-effect free; the raw value is always preserved on the result.
/// </summary>
public static class AudiomackLocationNormalizer
{
    private static readonly Dictionary<string, string> CountryNameToCode = BuildCountryNameToCode();

    public static AudiomackLocationResult? Normalize(string? rawLocation)
    {
        if (string.IsNullOrWhiteSpace(rawLocation))
        {
            return null;
        }

        var raw = rawLocation.Trim();
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        if (parts.Length == 1)
        {
            var single = parts[0];
            var singleCode = ResolveCountryCode(single);
            return singleCode != null
                ? new AudiomackLocationResult(raw, null, single, singleCode)
                : new AudiomackLocationResult(raw, single, null, null);
        }

        var city = parts[0];
        var countryCandidate = parts[^1];
        var countryCode = ResolveCountryCode(countryCandidate);
        if (countryCode != null)
        {
            return new AudiomackLocationResult(raw, city, countryCandidate, countryCode);
        }

        // Last segment is not a recognized country (for example "Austin, TX").
        // Keep the value as city-level text instead of inventing a country.
        return new AudiomackLocationResult(raw, string.Join(", ", parts), null, null);
    }

    public static string? ResolveCountryCode(string? countryName)
    {
        if (string.IsNullOrWhiteSpace(countryName))
        {
            return null;
        }

        var normalized = NormalizeCountryName(countryName);
        return normalized.Length > 0 && CountryNameToCode.TryGetValue(normalized, out var code) ? code : null;
    }

    private static string NormalizeCountryName(string value)
    {
        var cleaned = value.Trim().TrimEnd('.');
        if (cleaned.Length > 4 && cleaned.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[4..];
        }

        return cleaned.ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildCountryNameToCode()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo? region;
            try
            {
                region = new RegionInfo(culture.Name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var code = region.TwoLetterISORegionName;
            if (string.IsNullOrWhiteSpace(code) || code.Length != 2 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
            {
                continue;
            }

            AddName(map, region.EnglishName, code);
            AddName(map, region.DisplayName, code);
            AddName(map, code, code);
        }

        foreach (var (name, code) in BuildAliasCountryCodes())
        {
            AddName(map, name, code);
        }

        return map;
    }

    private static void AddName(Dictionary<string, string> map, string? name, string code)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = NormalizeCountryName(name);
        if (key.Length > 0)
        {
            map.TryAdd(key, code);
        }
    }

    // Common spellings that ICU region names do not cover or spell differently.
    // A method (not a field) so initialization order cannot race the country map above.
    private static IReadOnlyDictionary<string, string> BuildAliasCountryCodes() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["usa"] = "US",
        ["us"] = "US",
        ["u.s.a"] = "US",
        ["america"] = "US",
        ["united states of america"] = "US",
        ["uk"] = "GB",
        ["u.k"] = "GB",
        ["britain"] = "GB",
        ["great britain"] = "GB",
        ["england"] = "GB",
        ["scotland"] = "GB",
        ["wales"] = "GB",
        ["northern ireland"] = "GB",
        ["south korea"] = "KR",
        ["korea"] = "KR",
        ["republic of korea"] = "KR",
        ["north korea"] = "KP",
        ["dprk"] = "KP",
        ["russia"] = "RU",
        ["russian federation"] = "RU",
        ["ivory coast"] = "CI",
        ["cote d'ivoire"] = "CI",
        ["cote divoire"] = "CI",
        ["dr congo"] = "CD",
        ["drc"] = "CD",
        ["democratic republic of congo"] = "CD",
        ["congo kinshasa"] = "CD",
        ["congo-kinshasa"] = "CD",
        ["zaire"] = "CD",
        ["republic of the congo"] = "CG",
        ["congo brazzaville"] = "CG",
        ["congo-brazzaville"] = "CG",
        ["cape verde"] = "CV",
        ["cabo verde"] = "CV",
        ["tanzania"] = "TZ",
        ["united republic of tanzania"] = "TZ",
        ["czech republic"] = "CZ",
        ["czechia"] = "CZ",
        ["swaziland"] = "SZ",
        ["eswatini"] = "SZ",
        ["burma"] = "MM",
        ["myanmar"] = "MM",
        ["vietnam"] = "VN",
        ["viet nam"] = "VN",
        ["holland"] = "NL",
        ["uae"] = "AE",
        ["u.a.e"] = "AE",
        ["turkey"] = "TR",
        ["turkiye"] = "TR",
        ["palestine"] = "PS",
        ["palestinian territories"] = "PS",
        ["hong kong"] = "HK",
        ["macau"] = "MO",
        ["macao"] = "MO",
        ["gambia"] = "GM",
        ["bahamas"] = "BS",
        ["sao tome and principe"] = "ST",
        ["guinea bissau"] = "GW",
        ["south sudan"] = "SS",
        ["macedonia"] = "MK",
        ["north macedonia"] = "MK",
        ["kosovo"] = "XK",
        ["vatican"] = "VA",
        ["vatican city"] = "VA",
        ["taiwan"] = "TW",
        ["trinidad"] = "TT",
        ["republic of ireland"] = "IE"
    };
}
