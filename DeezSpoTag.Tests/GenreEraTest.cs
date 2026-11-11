using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// An era is a year span a file can fall in. The research wording is not itself
/// a match, and a named period with no stated year matches no file.
/// </summary>
public sealed class GenreEraTest
{
    [Fact]
    public void KapukaIsAnAfricanStyleTheRegionFilterCanFind()
    {
        var catalog = PersonalGenreCatalog.Default;
        Assert.True(catalog.TryGetById("kapuka", out var term));
        Assert.Equal("Kapuka", term.Name);
        Assert.Equal(PersonalGenreTaxonKind.Style, term.Kind);
        Assert.Contains("africa", term.Regions ?? [], StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Boomba", term.Aliases ?? []);
    }

    [Fact]
    public void AFileYearSelectsOnlyTermsWhoseEraContainsIt()
    {
        var catalog = ResearchedGenreCatalog.Current;
        Assert.True(catalog.TryGetById("afrobeat", out var afrobeat));
        var lateSixties = Assert.Single(afrobeat.Research!.Era!.Ranges);
        Assert.Equal(1967, lateSixties.Start);
        Assert.Equal(1979, lateSixties.End);
        Assert.True(afrobeat.Research.Era.Contains(1968));
        Assert.False(afrobeat.Research.Era.Contains(1964));
        Assert.False(afrobeat.Research.Era.Contains(2024));

        Assert.Contains("afrobeat", catalog.TermsActiveIn(1968).Select(term => term.Id));
        Assert.DoesNotContain("afrobeat", catalog.TermsActiveIn(2024).Select(term => term.Id));

        // Colonial has no stated year, so no file year may be treated as inside it.
        Assert.True(catalog.TryGetById("aboio", out var aboio));
        Assert.Empty(aboio.Research!.Era!.Ranges);
        Assert.False(aboio.Research.Era.Contains(1900));
        Assert.DoesNotContain("aboio", catalog.TermsActiveIn(1900).Select(term => term.Id));

        Assert.True(catalog.TryGetById("kapuka", out var kapuka));
        Assert.Equal(1997, kapuka.Research!.Era!.Ranges[0].Start);
        Assert.Equal(2009, kapuka.Research.Era.Ranges[0].End);
        Assert.Contains("kapuka", catalog.TermsActiveIn(2001).Select(term => term.Id));
        Assert.DoesNotContain("kapuka", catalog.TermsActiveIn(2015).Select(term => term.Id));
    }

    [Fact]
    public void AnInvertedEraRangeFailsCatalogLoad()
    {
        const string document = """
            {
              "version": "candidate",
              "source": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
              "terms": [
                {
                  "id": "made-up",
                  "name": "Made Up",
                  "kind": "Genre",
                  "regions": [],
                  "aliases": [],
                  "parentIds": [],
                  "research": {
                    "status": "Verified",
                    "era": { "label": "backwards", "ranges": [{ "start": 1990, "end": 1980 }] }
                  }
                }
              ]
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson(document));
        Assert.Contains("era end", error.Message);
    }

    /// <summary>
    /// The regional merger imports <c>parse_era</c> from the tracked
    /// scripts/genre/build_genre_taxonomy.py. It used to import it from the ignored
    /// research scratch file, and it used to require openpyxl at module level;
    /// either dependency would break a clean import (CI has no openpyxl and no
    /// ignored research artifacts). This runs the real merger under Python with
    /// both modules made unimportable, so neither dependency can come back.
    /// </summary>
    [Fact]
    public void TheRegionalMergerImportsAndParsesErasWithoutOpenpyxlOrTheIgnoredResearchFile()
    {
        var root = ResolveRoot();
        var merger = Path.Combine(root, "scripts", "genre", "merge_regional_taxonomy.py");
        Assert.True(File.Exists(merger), $"{merger} is missing");

        // Static proof too: the merger must not mention the ignored research module.
        Assert.DoesNotContain("research_eras", File.ReadAllText(merger), StringComparison.Ordinal);

        var script = """
            import json, sys

            BLOCKED = ('research_eras', 'openpyxl')

            class Blocked:
                def find_spec(self, name, path=None, target=None):
                    if name in BLOCKED or name.startswith('openpyxl.'):
                        raise ImportError(
                            'the merger must not import ' + name + ' at import time')
                    return None

            sys.meta_path.insert(0, Blocked())
            sys.path.insert(0, sys.argv[1])
            import merge_regional_taxonomy
            for name in BLOCKED:
                assert name not in sys.modules, 'the merger imported ' + name
            assert 'build_genre_taxonomy' in sys.modules, 'the merger must use the tracked builder'

            parse_era = merge_regional_taxonomy.parse_era
            parsed = parse_era('Late 1960s–1970s')
            assert parsed['ranges'] == [{'start': 1967, 'end': 1979}], parsed
            parsed = parse_era('1990s–')
            assert parsed['ranges'] == [{'start': 1990, 'end': None}], parsed
            parsed = parse_era('colonial era–present')
            assert parsed['ranges'] == [], parsed
            print('era parser ok')
            """;
        var scriptPath = Path.Combine(Path.GetTempPath(), "merger-era-check-" + Guid.NewGuid().ToString("N") + ".py");
        try
        {
            File.WriteAllText(scriptPath, script);
            var start = new ProcessStartInfo("python3")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = root,
            };
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add(Path.Combine(root, "scripts", "genre"));
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"{output}{Environment.NewLine}{error}");
            Assert.Contains("era parser ok", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static string ResolveRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
