using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 1 contract tests for Sonic Analysis pooling and persistence.
///
/// The pooling rules are executed against the real vibe_analyzer.py so the
/// Python and the C# side cannot drift: the same function pools the frames in
/// production and in this test. It skips when python3 or numpy is unavailable
/// rather than failing, because the application image provisions the runtime and
/// the test host usually does not.
/// </summary>
public sealed class SonicEmbeddingPoolingTest
{
    [Fact]
    public void Pooling_ProducesAL2NormalizedVectorOfTheDeclaredWidth()
    {
        if (!RunPython("import numpy", out _))
        {
            return;
        }

        var result = RunPooling("29, 1280", seed: 1);
        Assert.Equal("1280", result["dimensions"]);
        Assert.Equal("29", result["frameCount"]);
        Assert.Equal("discogs-effnet-bs64-1", result["modelId"]);
        Assert.Equal("embedding-v1", result["embeddingVersion"]);
        Assert.Equal("mean-v1", result["pooling"]);
        Assert.Equal("l2-v1", result["normalization"]);
        Assert.Equal("cosine", result["distanceMetric"]);

        // L2 normalization is what makes cosine similarity a plain dot product.
        Assert.Equal(1.0d, double.Parse(result["norm"]!), 5);
        Assert.Equal("True", result["finite"]);
    }

    [Fact]
    public void Pooling_IsDeterministic()
    {
        if (!RunPython("import numpy", out _))
        {
            return;
        }

        var first = RunPooling("12, 1280", seed: 7);
        var second = RunPooling("12, 1280", seed: 7);
        Assert.Equal(first["norm"], second["norm"]);
        Assert.Equal(first["checksum"], second["checksum"]);

        // A different frame set must produce a different vector, otherwise the
        // embedding is not actually derived from the audio.
        var other = RunPooling("12, 1280", seed: 99);
        Assert.NotEqual(first["checksum"], other["checksum"]);
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("inf")]
    [InlineData("zeros")]
    [InlineData("empty")]
    public void Pooling_RejectsUnusableInput(string kind)
    {
        if (!RunPython("import numpy", out _))
        {
            return;
        }

        var script = $@"
import numpy as np, sys
sys.path.insert(0, {ToPythonLiteral(ToolsDirectory())})
import vibe_analyzer as va
rs = np.random.RandomState(3)
base = rs.rand(10, 1280).astype(np.float32)
frames = {{
    'nan': None,
    'inf': None,
    'zeros': np.zeros((10, 1280), dtype=np.float32),
    'empty': None,
}}['{kind}']
if '{kind}' == 'nan':
    frames = base.copy(); frames[0, 3] = np.nan
elif '{kind}' == 'inf':
    frames = base.copy(); frames[2, 7] = np.inf
elif '{kind}' == 'empty':
    frames = np.zeros((0, 1280), dtype=np.float32)
out = va.pool_sonic_embedding(frames)
print('REJECTED', out is None)";

        var (exitCode, stdout) = RunScript(script);
        Assert.Equal(0, exitCode);
        Assert.Contains("REJECTED True", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_EncodesExactlyOneVectorOfTheDeclaredWidth()
    {
        if (!RunPython("import numpy", out _))
        {
            return;
        }

        var script = $@"
import base64, numpy as np, sys
sys.path.insert(0, {ToPythonLiteral(ToolsDirectory())})
import vibe_analyzer as va
rs = np.random.RandomState(5)
sonic = va.pool_sonic_embedding(rs.rand(29, 1280).astype(np.float32))
payload = va.build_payload({{'sonicEmbedding': sonic}})
enc = payload['SonicEmbedding']
raw = base64.b64decode(enc['VectorBase64'])
arr = np.frombuffer(raw, dtype='<f4')
print('BYTES', len(raw))
print('DIMS', len(arr))
print('DECLARED', enc['Dimensions'])
print('NORM', round(float(np.linalg.norm(arr)), 5))
print('FINITE', bool(np.isfinite(arr).all()))";

        var (exitCode, stdout) = RunScript(script);
        Assert.Equal(0, exitCode);

        // 1280 float32 values. If this ever changes, the BLOB width in the
        // schema and the C# dimension check must change with it.
        Assert.Contains("BYTES 5120", stdout, StringComparison.Ordinal);
        Assert.Contains("DIMS 1280", stdout, StringComparison.Ordinal);
        Assert.Contains("DECLARED 1280", stdout, StringComparison.Ordinal);
        Assert.Contains("NORM 1.0", stdout, StringComparison.Ordinal);
        Assert.Contains("FINITE True", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableSonic_ReportsWithoutFailingTheTrack()
    {
        if (!RunPython("import numpy", out _))
        {
            return;
        }

        var script = $@"
import sys
sys.path.insert(0, {ToPythonLiteral(ToolsDirectory())})
import vibe_analyzer as va
payload = va.build_payload({{'sonicUnavailable': True}})
print('FLAG', payload.get('SonicUnavailable'))
print('OK', payload.get('ok'))
print('NOEMBED', 'SonicEmbedding' not in payload)";

        var (exitCode, stdout) = RunScript(script);
        Assert.Equal(0, exitCode);

        // The analysis is still reported as successful; only the vector is absent.
        Assert.Contains("FLAG True", stdout, StringComparison.Ordinal);
        Assert.Contains("OK True", stdout, StringComparison.Ordinal);
        Assert.Contains("NOEMBED True", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicExtractor_IsLoadedIndependentlyOfTheGenreBranch()
    {
        // The genre branch only reaches EffNet when MAEST is unavailable, which
        // is every application image. A Sonic extractor that piggybacks on that
        // branch would produce no vectors at all in production.
        var analyzer = File.ReadAllText(Path.Join(ToolsDirectory(), "vibe_analyzer.py"));

        Assert.Contains("def _load_sonic_model", analyzer, StringComparison.Ordinal);
        Assert.Contains("self._load_sonic_model()", analyzer, StringComparison.Ordinal);
        Assert.Contains("self.sonic_extractor = TensorflowPredictEffnetDiscogs", analyzer, StringComparison.Ordinal);

        var loadStart = analyzer.IndexOf("def _load_sonic_model", StringComparison.Ordinal);
        var loadBody = analyzer.Substring(loadStart, Math.Min(1400, analyzer.Length - loadStart));
        Assert.DoesNotContain("self.maest_genre_extractor", loadBody, StringComparison.Ordinal);
        Assert.DoesNotContain("self.effnet_extractor", loadBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicExtraction_IsIsolatedFromTheSemanticFailurePath()
    {
        var analyzer = File.ReadAllText(Path.Join(ToolsDirectory(), "vibe_analyzer.py"));

        // Sonic must not be able to raise into the block that sets _error.
        var analyzeStart = analyzer.IndexOf("    def analyze(self, file_path", StringComparison.Ordinal);
        Assert.True(analyzeStart > 0, "AudioAnalyzer.analyze not found.");
        var body = analyzer.Substring(analyzeStart, Math.Min(3200, analyzer.Length - analyzeStart));

        var sonicStart = body.IndexOf("if self.sonic_enabled:", StringComparison.Ordinal);
        Assert.True(sonicStart > 0, "Sonic is not invoked from analyze().");

        var before = body[..sonicStart];
        Assert.DoesNotContain("_extract_sonic_embedding", before, StringComparison.Ordinal);

        var sonicBlock = body[sonicStart..];
        Assert.Contains("result[\"sonicUnavailable\"] = True", sonicBlock, StringComparison.Ordinal);
        Assert.Contains("except Exception", sonicBlock, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static Dictionary<string, string> RunPooling(string shape, int seed)
    {
        var script = $@"
import numpy as np, sys
sys.path.insert(0, {ToPythonLiteral(ToolsDirectory())})
import vibe_analyzer as va
frames, width = '{shape}'.split(',')
rs = np.random.RandomState({seed})
sonic = va.pool_sonic_embedding(rs.rand(int(frames), int(width)).astype(np.float32))
v = np.asarray(sonic['values'])
print('DIMENSIONS', sonic['dimensions'])
print('FRAMECOUNT', sonic['frameCount'])
print('NORM', repr(float(np.linalg.norm(v))))
print('FINITE', bool(np.isfinite(v).all()))
print('CHECKSUM', float(np.abs(v).sum()))
print('MODELID', sonic['modelId'])
print('EMBEDDINGVERSION', sonic['embeddingVersion'])
print('POOLING', sonic['pooling'])
print('NORMALIZATION', sonic['normalization'])
print('DISTANCEMETRIC', sonic['distanceMetric'])";

        var (exitCode, stdout) = RunScript(script);
        Assert.Equal(0, exitCode);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2)
            {
                values[parts[0]] = parts[1].Trim();
            }
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dimensions"] = values["DIMENSIONS"],
            ["frameCount"] = values["FRAMECOUNT"],
            ["norm"] = values["NORM"],
            ["finite"] = values["FINITE"],
            ["checksum"] = values["CHECKSUM"],
            ["modelId"] = values["MODELID"],
            ["embeddingVersion"] = values["EMBEDDINGVERSION"],
            ["pooling"] = values["POOLING"],
            ["normalization"] = values["NORMALIZATION"],
            ["distanceMetric"] = values["DISTANCEMETRIC"],
        };
    }

    private static (int ExitCode, string StdOut) RunScript(string code)
        => RunPythonRaw("-c", code);

    private static bool RunPython(string code, out string stdout)
    {
        var (exitCode, output) = RunPythonRaw("-c", code);
        stdout = output;
        return exitCode == 0;
    }

    private static (int ExitCode, string StdOut) RunPythonRaw(string flag, string code)
    {
        var start = new System.Diagnostics.ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(flag);
        start.ArgumentList.Add(code);

        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start python3.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout);
    }

    private static string ToPythonLiteral(string value)
        => "\"" + value.Replace("\\", "\\\\") + "\"";

    private static string ToolsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        return Path.Join(
            directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."),
            "DeezSpoTag.Web",
            "Tools");
    }
}
