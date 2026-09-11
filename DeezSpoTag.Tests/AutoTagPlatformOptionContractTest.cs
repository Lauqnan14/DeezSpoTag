using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Contract between the UI's "Platform-specific Configuration" cards and the backend that the
/// autotag runner actually executes.
///
/// The UI renders one control per option an adapter declares in
/// <see cref="PlatformInfo.CustomOptions"/>, and persists the value under
/// <c>profile.autoTag.custom[platformId][optionId]</c>. The runner then deserializes that bag into
/// the typed config the matcher consumes. Nothing structurally forces those two halves to agree,
/// which is how options can be shown, saved, and silently ignored. These tests close that gap:
///
///   1. every declared option must bind to exactly one config property;
///   2. the backend's default for that property must equal the default the adapter declares,
///      because the backend is the source of truth for defaults;
///   3. a declared option must never be silently clamped away.
///
/// Deserialization deliberately mirrors the runner's own options instance
/// (LocalAutoTagRunner.CaseInsensitiveJsonOptions) so a test pass means the real path works.
/// </summary>
public sealed class AutoTagPlatformOptionContractTest
{
    private static readonly JsonSerializerOptions RunnerJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Platform adapter -> the typed config the runner hands to that platform's matcher.
    /// Mirrors the switch in LocalAutoTagRunner.PlatformMatching.cs.
    /// </summary>
    private static readonly (string PlatformId, Type AdapterType, Type ConfigType)[] Registry =
    [
        ("audiomack", typeof(AudiomackPlatform), typeof(AudiomackMatchConfig)),
        ("beatport", typeof(BeatportPlatform), typeof(BeatportMatchConfig)),
        ("boomplay", typeof(BoomplayPlatform), typeof(BoomplayConfig)),
        ("deezer", typeof(DeezerPlatform), typeof(DeezerConfig)),
        ("discogs", typeof(DiscogsPlatform), typeof(DiscogsConfig)),
        ("itunes", typeof(ItunesPlatform), typeof(ItunesMatchConfig)),
        ("lastfm", typeof(LastFmPlatform), typeof(LastFmConfig)),
        ("lrclib", typeof(LrclibPlatform), typeof(LrclibConfig)),
        ("musicbrainz", typeof(MusicBrainzPlatform), typeof(MusicBrainzMatchConfig)),
        ("shazam", typeof(ShazamPlatform), typeof(ShazamMatchConfig)),
    ];

    /// <summary>
    /// Config properties that intentionally have no UI control. Kept explicit so that adding a new
    /// unattached property is a deliberate, reviewed act rather than a silent drift.
    /// </summary>
    private static readonly HashSet<string> UnexposedByDesign =
    [
        // Artwork is out of scope; animated artwork keeps its existing Apple-integration channel.
        "ItunesMatchConfig.AnimatedArtwork",
    ];

    public static TheoryData<string> PlatformIds()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Registry)
        {
            data.Add(entry.PlatformId);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PlatformIds))]
    public void EveryDeclaredOption_BindsToExactlyOneConfigProperty(string platformId)
    {
        var (_, adapterType, configType) = Resolve(platformId);
        var options = DeclaredOptions(adapterType);
        Assert.NotEmpty(options);

        var failures = new List<string>();
        foreach (var option in options)
        {
            var matches = BindersFor(configType, option.Id).ToList();
            if (matches.Count == 0)
            {
                failures.Add($"{option.Id}: no config property binds this option id. The UI writes it, the runner never reads it.");
            }
            else if (matches.Count > 1)
            {
                failures.Add($"{option.Id}: binds to {matches.Count} properties ({string.Join(", ", matches.Select(p => p.Name))}); ambiguous.");
            }
        }

        Assert.True(failures.Count == 0,
            $"{configType.Name} has {failures.Count} option binding failure(s):{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", failures));
    }

    [Theory]
    [MemberData(nameof(PlatformIds))]
    public void EveryDeclaredOption_ValueSurvivesRoundTrip(string platformId)
    {
        var (_, adapterType, configType) = Resolve(platformId);
        var failures = new List<string>();

        foreach (var option in DeclaredOptions(adapterType))
        {
            var property = BindersFor(configType, option.Id).FirstOrDefault();
            if (property is null)
            {
                continue; // reported by EveryDeclaredOption_BindsToExactlyOneConfigProperty
            }

            var probe = ProbeValue(option);
            var json = "{" + JsonSerializer.Serialize(option.Id) + ":" + JsonSerializer.Serialize(probe) + "}";
            var bound = JsonSerializer.Deserialize(json, configType, RunnerJsonOptions);
            Assert.NotNull(bound);

            var actual = property.GetValue(bound);
            if (!ValuesEqual(probe, actual))
            {
                failures.Add($"{option.Id}: wrote {Format(probe)}, runner read {Format(actual)} (property {property.Name}).");
            }
        }

        Assert.True(failures.Count == 0,
            $"{configType.Name} silently drops values for:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", failures));
    }

    /// <summary>
    /// The backend is the source of truth. Whatever the runner uses when the profile carries no
    /// value must be exactly what the adapter advertises as the control's default, otherwise the UI
    /// shows a default the runner does not honour.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlatformIds))]
    public void AdapterDeclaredDefault_MatchesBackendDefault(string platformId)
    {
        var (_, adapterType, configType) = Resolve(platformId);
        var backendDefaults = DefaultsFor(configType);
        var failures = new List<string>();

        foreach (var option in DeclaredOptions(adapterType))
        {
            var property = BindersFor(configType, option.Id).FirstOrDefault();
            if (property is null || !backendDefaults.TryGetValue(property.Name, out var backendDefault))
            {
                continue; // reported elsewhere
            }

            var declared = DeclaredDefault(option);
            if (!ValuesEqual(declared, backendDefault))
            {
                failures.Add($"{option.Id}: UI default is {Format(declared)}, backend default is {Format(backendDefault)} (property {property.Name}).");
            }
        }

        Assert.True(failures.Count == 0,
            $"{configType.Name} advertises defaults the backend does not use:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", failures));
    }

    [Theory]
    [MemberData(nameof(PlatformIds))]
    public void ConfigProperties_AreAllReachableFromTheUi(string platformId)
    {
        var (_, adapterType, configType) = Resolve(platformId);
        var declaredIds = DeclaredOptions(adapterType).Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = new List<string>();

        foreach (var property in ReadableProperties(configType))
        {
            var key = property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), inherit: true)
                .OfType<JsonPropertyNameAttribute>()
                .Select(a => a.Name)
                .FirstOrDefault() ?? property.Name;

            if (declaredIds.Contains(key) || declaredIds.Contains(property.Name))
            {
                continue;
            }

            if (UnexposedByDesign.Contains($"{configType.Name}.{property.Name}"))
            {
                continue;
            }

            orphans.Add($"{property.Name} (json key '{key}')");
        }

        Assert.True(orphans.Count == 0,
            $"{configType.Name} carries config the UI cannot set (dead or hidden path):{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", orphans));
    }

    // ---------------------------------------------------------------- helpers

    private sealed class StubWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Web";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = "Production";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    }

    private static (string PlatformId, Type AdapterType, Type ConfigType) Resolve(string platformId)
    {
        foreach (var entry in Registry)
        {
            if (string.Equals(entry.PlatformId, platformId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"Unknown platform '{platformId}'.");
    }

    private static IAutoTagPlatform CreateAdapter(Type adapterType)
        => (IAutoTagPlatform)Activator.CreateInstance(adapterType, new StubWebHostEnvironment())!;

    private static List<PlatformCustomOption> DeclaredOptions(Type adapterType)
        => CreateAdapter(adapterType).Describe().Platform.CustomOptions.Options;

    private static IEnumerable<System.Reflection.PropertyInfo> ReadableProperties(Type configType)
        => configType.GetProperties()
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => p.PropertyType.IsPrimitive
                || p.PropertyType == typeof(string)
                || p.PropertyType.IsEnum
                || Nullable.GetUnderlyingType(p.PropertyType) is { IsPrimitive: true }
                || Nullable.GetUnderlyingType(p.PropertyType) == typeof(string));

    /// <summary>
    /// Properties that the runner would populate for <paramref name="optionId"/>.
    /// Matches on the explicit <see cref="JsonPropertyNameAttribute"/> when present, then falls back
    /// to a case-insensitive name match (the behaviour PropertyNameCaseInsensitive gives us).
    /// </summary>
    private static IEnumerable<System.Reflection.PropertyInfo> BindersFor(Type configType, string optionId)
    {
        foreach (var property in ReadableProperties(configType))
        {
            var jsonName = property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), inherit: true)
                .OfType<JsonPropertyNameAttribute>()
                .Select(a => a.Name)
                .FirstOrDefault();

            if (jsonName is not null)
            {
                if (string.Equals(jsonName, optionId, StringComparison.Ordinal)
                    || string.Equals(jsonName, optionId, StringComparison.OrdinalIgnoreCase))
                {
                    yield return property;
                }

                continue;
            }

            // No attribute: the key must match case-insensitively but must not need underscore
            // translation, because PropertyNameCaseInsensitive alone cannot do that.
            if (string.Equals(property.Name, optionId, StringComparison.OrdinalIgnoreCase))
            {
                yield return property;
            }
        }
    }

    private static Dictionary<string, object?> DefaultsFor(Type configType)
    {
        var instance = Activator.CreateInstance(configType)!;
        return ReadableProperties(configType).ToDictionary(p => p.Name, p => p.GetValue(instance));
    }

    private static object? DeclaredDefault(PlatformCustomOption option) => option.Value switch
    {
        PlatformCustomOptionNumber number => number.Value,
        PlatformCustomOptionBoolean boolean => boolean.Value,
        PlatformCustomOptionString text => text.Value,
        PlatformCustomOptionSelect select => select.Value,
        PlatformCustomOptionTag tag => tag.Value,
        _ => null,
    };

    /// <summary>
    /// A value guaranteed to differ from the declared default, so a binding failure cannot pass by
    /// coincidentally matching the default.
    /// </summary>
    private static object? ProbeValue(PlatformCustomOption option) => option.Value switch
    {
        PlatformCustomOptionNumber number => number.Value == number.Max ? number.Min : number.Max,
        PlatformCustomOptionBoolean boolean => !boolean.Value,
        PlatformCustomOptionString => "probe-value",
        PlatformCustomOptionSelect select => select.Values.LastOrDefault() ?? "probe-value",
        PlatformCustomOptionTag => "probe-value",
        _ => "probe-value",
    };

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return Normalize(left) == Normalize(right);
        }

        if (IsNumeric(left) && IsNumeric(right))
        {
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture)
                == Convert.ToDecimal(right, CultureInfo.InvariantCulture);
        }

        if (left is bool lb && right is bool rb)
        {
            return lb == rb;
        }

        return Normalize(left) == Normalize(right);
    }

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static string Normalize(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Format(object? value) => value switch
    {
        null => "<null>",
        string s => $"\"{s}\"",
        _ => Normalize(value),
    };
}
