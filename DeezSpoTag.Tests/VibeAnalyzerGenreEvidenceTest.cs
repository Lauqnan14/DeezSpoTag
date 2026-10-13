using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Locks the acoustic genre evidence contract in vibe_analyzer.py.
///
/// The regression these guard against: the Discogs519 branch referenced a bare
/// <c>Pool()</c> that was never imported, and the resulting NameError was swallowed
/// by a bare <c>except Exception: return []</c>. Docker ships the Discogs519/MAEST
/// models, so that path was taken on every track and enhanced mode produced zero
/// genre evidence while still reporting Discogs519 provenance.
/// </summary>
public sealed class VibeAnalyzerGenreEvidenceTest
{
    [Fact]
    public void DeamLoading_UsesTheClassificationHeadInputNode()
    {
        var result = RunPythonScript(@"
from types import SimpleNamespace
module.os.path.exists = lambda path: True
module.TensorflowPredict2D = lambda **kwargs: kwargs
analyzer = SimpleNamespace(_model_path=lambda name: name)
print(json.dumps(module.AudioAnalyzer._load_deam_model(analyzer)))
");
        Assert.Equal("model/Placeholder", result.GetProperty("input").GetString());
        Assert.Equal("model/Identity", result.GetProperty("output").GetString());
    }

    [Fact]
    public void DeamPrediction_ReceivesMusicnnEmbeddingsAndReportsNormalizedScores()
    {
        var result = RunPythonScript(@"
from types import SimpleNamespace
embeddings = module.np.ones((2, 200))
audio = module.np.zeros(16000)
def predict(values):
    assert values is embeddings, 'DEAM must consume MusicNN embeddings'
    return [[1.0, 5.0], [9.0, 9.0]]
analyzer = SimpleNamespace(
    musicnn_model=lambda values: embeddings,
    deam_predictor=predict,
    _collect_raw_moods=lambda values, mapping: {},
    _normalize_core_moods=lambda values: None,
    _populate_ml_summary_scores=lambda result: None,
    _populate_optional_ml_scores=lambda result, values: None)
analyzer._predict_deam = lambda values: module.AudioAnalyzer._predict_deam(analyzer, values)
print(json.dumps(module.AudioAnalyzer._extract_ml_features(analyzer, audio)))
");
        Assert.Equal(0.5, result.GetProperty("valence").GetDouble());
        Assert.Equal(0.75, result.GetProperty("arousal").GetDouble());
        Assert.Equal("deam-msd-musicnn-2", result.GetProperty("valenceSource").GetString());
        Assert.Equal("deam-msd-musicnn-2", result.GetProperty("arousalSource").GetString());
    }

    [Theory]
    [InlineData("discogs519-maest-30s-pw-519l", "discogs519-maest-30s-pw-519l")]
    [InlineData(null, "discogs400-discogs-effnet")]
    public void EffnetLoading_PreservesLoadedMaestProvenance(string? loadedModel, string expected)
    {
        var result = RunPythonScript($@"
from types import SimpleNamespace
module.os.path.exists = lambda path: True
module.TensorflowPredictEffnetDiscogs = lambda **kwargs: object()
module.TensorflowPredict2D = lambda **kwargs: object()
analyzer = SimpleNamespace(
    genre_model_name={ToPythonLiteral(loadedModel ?? "")},
    effnet_extractor=None, genre_predictor=None,
    _model_path=lambda name: name, _load_genre_labels=lambda: ['Rock'])
if not analyzer.genre_model_name:
    analyzer.genre_model_name = None
module.AudioAnalyzer._load_effnet_genre_models(analyzer)
print(json.dumps(analyzer.genre_model_name))
");
        Assert.Equal(expected, result.GetString());
    }

    [Fact]
    public void Discogs519Branch_UsesEssentiaPoolInsteadOfAnUnboundName()
    {
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        // essentia.Pool must be resolved and used; a bare Pool() is the bug.
        Assert.Contains("getattr(essentia, \"Pool\", None)", analyzer, StringComparison.Ordinal);
        Assert.Contains("pool = EssentiaPool()", analyzer, StringComparison.Ordinal);
        Assert.DoesNotContain("pool = Pool()", analyzer, StringComparison.Ordinal);
    }

    [Fact]
    public void GenreEvidenceFailure_DegradesToTheNextBranchInsteadOfReturningEmpty()
    {
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        // The 519 branch must not swallow its own failure and yield nothing; it
        // falls through to the EffNet/Discogs400 branch instead.
        Assert.DoesNotContain("except Exception:\n                return []", analyzer, StringComparison.Ordinal);
        Assert.Contains("_warn_once(\n                    \"discogs519\"", analyzer, StringComparison.Ordinal);
    }

    [Fact]
    public void GenreModelProvenance_IsOnlyReportedWhenTheHeadActuallyLoaded()
    {
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        // No unconditional "claim 519 even though nothing loaded" assignment.
        Assert.DoesNotContain(
            "        if TensorflowPredictMAEST is None or TensorflowPredict is None:\n            self.genre_model_name",
            analyzer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "self.genre_model_name = GENRE_MODEL_DISCogs519\n        except Exception:",
            analyzer,
            StringComparison.Ordinal);

        // analyze() must report the loaded model verbatim, with no dead fallback.
        Assert.Contains("result[\"genreModel\"] = self.genre_model_name", analyzer, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegDecode_IsDurationCappedAndCleansUpOnFailure()
    {
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        Assert.Contains("MAX_ANALYSIS_SECONDS_ENV_NAME = \"VIBE_ANALYZER_MAX_SECONDS\"", analyzer, StringComparison.Ordinal);
        Assert.Contains("\"-t\",", analyzer, StringComparison.Ordinal);
        Assert.Contains("self._discard_temp_file(temp_path)", analyzer, StringComparison.Ordinal);
    }

    [Theory]
    // A 519-way head produces a diffuse distribution: the old absolute 0.15 floor
    // returned zero labels for both of these. The relative rule keeps the top-K.
    [InlineData(0.11, 0.90, 8)]
    [InlineData(0.04, 0.90, 8)]
    // A peaked head: a tail at or below 25% of top-1 is dropped.
    [InlineData(0.62, 0.10, 1)]
    [InlineData(0.62, 0.20, 1)]
    // A tail at 30% of top-1 clears the ratio and is retained.
    [InlineData(0.62, 0.30, 8)]
    [InlineData(0.90, 0.90, 8)]
    public void TopGenreEvidence_UsesThresholdRelativeToTheTopScore(
        double topScore,
        double tailRatio,
        int expectedCount)
    {
        var scores = new List<double>();
        for (var i = 0; i < 519; i++)
        {
            scores.Add(i == 0 ? topScore : topScore * tailRatio);
        }

        var result = RunPython("top_genre_evidence", BuildScoreLiteral(scores), BuildLabelLiteral(519));

        Assert.Equal(expectedCount, result.GetArrayLength());
        var first = result[0];
        Assert.Equal("L0", first.GetProperty("label").GetString());
        Assert.True(first.GetProperty("score").GetDouble() > 0);
        Assert.Equal("discogs519-maest-30s-pw-519l", first.GetProperty("model").GetString());
    }

    [Fact]
    public void TopGenreEvidence_AlwaysKeepsTheTopLabelWhenTheHeadIsDiffuse()
    {
        // 519-way diffuse head: every score is below the old 0.15 absolute floor,
        // so the previous implementation returned an empty list here.
        var scores = new List<double>();
        for (var i = 0; i < 519; i++)
        {
            scores.Add(i == 0 ? 0.11 : 0.099);
        }

        var result = RunPython("top_genre_evidence", BuildScoreLiteral(scores), BuildLabelLiteral(519));

        Assert.NotEmpty(result.EnumerateArray());
        Assert.All(
            result.EnumerateArray(),
            entry => Assert.True(entry.GetProperty("score").GetDouble() < 0.15));
    }

    [Fact]
    public void TopGenreEvidence_ReturnsNothingForDegenerateScores()
    {
        Assert.Equal(0, RunPython("top_genre_evidence", "[]", "[]").GetArrayLength());
        Assert.Equal(
            0,
            RunPython("top_genre_evidence", "[0.0, 0.0, 0.0]", "[\"a\", \"b\", \"c\"]").GetArrayLength());
    }

    [Fact]
    public void TopGenreEvidence_StaysWithinTopK()
    {
        var scores = new List<double> { 1.0 };
        for (var i = 1; i < 519; i++)
        {
            scores.Add(0.99 - (i * 0.0001));
        }

        var result = RunPython("top_genre_evidence", BuildScoreLiteral(scores), BuildLabelLiteral(519));

        Assert.Equal(8, result.GetArrayLength());
    }

    [Theory]
    [InlineData(519, "(519,)")]
    [InlineData(519, "(2, 519)")]
    [InlineData(519, "(2, 1, 3, 519)")]
    [InlineData(400, "(2, 1, 3, 400)")]
    public void MeanScores_AveragesFramesWithoutFlatteningClasses(int classes, string shape)
    {
        var result = RunPythonScript($@"
import numpy as np
values = np.arange(np.prod({shape}), dtype=float).reshape({shape})
actual = module._mean_scores(values, {classes})
expected = values.reshape(-1, {classes}).mean(axis=0)
assert len(actual) == {classes}
assert np.allclose(actual, expected)
labels = ['Label ' + str(i) for i in range({classes})]
evidence = module.top_genre_evidence(actual, labels, 'test')
assert all(item['label'] in labels for item in evidence)
print(json.dumps(actual))
");
        Assert.Equal(classes, result.GetArrayLength());
    }

    [Theory]
    [InlineData("module._mean_scores(np.ones((2, 520)), 519)")]
    [InlineData("module._mean_scores(np.array(1.0), 519)")]
    [InlineData("module.top_genre_evidence([0.8, 0.9], ['Rock'], 'test')")]
    [InlineData("module.top_genre_evidence([0.8], [], 'test')")]
    [InlineData("module.top_genre_evidence([0.8], ['  '], 'test')")]
    public void GenreEvidence_RejectsUnalignedPredictions(string expression)
    {
        var result = RunPythonScript($@"
import numpy as np
try:
    {expression}
except ValueError:
    print('true')
else:
    raise AssertionError('Expected invalid genre predictions to be rejected')
");
        Assert.True(result.GetBoolean());
    }

    [Fact]
    public void MeanScores_EmptyInputProducesNoEvidence()
    {
        var result = RunPythonScript(@"
import numpy as np
assert module._mean_scores(None, 519) == []
assert module._mean_scores(np.array([]), 519) == []
assert module._mean_scores(np.empty((0, 519)), 519) == []
print('true')
");
        Assert.True(result.GetBoolean());
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(91)]
    [InlineData(137)]
    [InlineData(600)]
    public void MaestWindows_BoundAudioAndPreserveOriginal(int seconds)
    {
        var result = RunPythonScript($@"
import numpy as np
audio = np.arange({seconds} * 16000, dtype=np.float32)
before = audio.copy()
windows = module.select_maest_windows(audio)
assert np.array_equal(audio, before)
if {seconds} <= 90:
    assert len(windows) == 1 and windows[0] is audio
else:
    starts = [0, (len(audio) - 30 * 16000) // 2, len(audio) - 30 * 16000]
    assert len(windows) == 3
    for window, start in zip(windows, starts):
        assert len(window) == 30 * 16000
        assert np.array_equal(window, audio[start:start + 30 * 16000])
assert sum(len(w) for w in windows) <= 90 * 16000
print('true')");
        Assert.True(result.GetBoolean());
    }

    [Fact]
    public void MaestWindows_AverageClassScoresBeforeSelectingLabels()
    {
        var result = RunPythonScript(@"
from types import SimpleNamespace
import numpy as np
class Pool(dict):
    def set(self, key, value): self[key] = value
module.EssentiaPool = Pool
calls = []
def extract(audio):
    calls.append(float(audio[0]))
    assert len(audio) == 30 * 16000
    return len(calls) - 1
vectors = [[0.9, 0.0], [0.0, 0.9], [0.0, 0.9]]
def predict(pool): return {'PartitionedCall/Identity_1': np.array([vectors[pool['embeddings']]])}
analyzer = SimpleNamespace(maest_genre_extractor=extract, genre519_predictor=predict,
    genre519_labels=['Rock', 'Pop'], genre_model_name='discogs519-maest-30s-pw-519l')
evidence = module.AudioAnalyzer._extract_essentia_genre_evidence(analyzer, np.arange(137 * 16000, dtype=np.float32))
assert len(calls) == 3
assert evidence[0]['label'] == 'Pop' and abs(evidence[0]['score'] - 0.6) < 0.0001
assert evidence[1]['label'] == 'Rock' and abs(evidence[1]['score'] - 0.3) < 0.0001
assert all(e['model'] == 'discogs519-maest-30s-pw-519l' for e in evidence)
print('true')");
        Assert.True(result.GetBoolean());
    }

    [Fact]
    public void SamplingLeavesMoodCoreAndSonicAudioUnchangedAndReportsItsPolicy()
    {
        var result = RunPythonScript(@"
import numpy as np
module.ESSENTIA_AVAILABLE = True
analyzer = object.__new__(module.AudioAnalyzer)
analyzer.enhanced_mode = True
analyzer.sonic_enabled = True
analyzer.genre_model_name = 'discogs519-maest-30s-pw-519l'
audio44 = np.zeros(137 * 44100, dtype=np.float32)
audio16 = np.zeros(137 * 16000, dtype=np.float32)
analyzer.load_audio_pair = lambda path: (audio44, audio16, None)
def core(audio):
    assert audio is audio44
    return {'bpm': 120, 'keyScale': 'major'}
def mood(audio):
    assert audio is audio16
    return {'valence': 0.8}
def sonic(audio):
    assert audio is audio16
    return None
def genre(audio):
    assert audio is audio16
    windows = module.select_maest_windows(audio)
    analyzer.genre_sampling = {'genreSamplingPolicy': 'three-30s-v1',
        'genreAnalyzedSeconds': sum(len(w) for w in windows) / 16000,
        'genreWindowStartSeconds': [0, 53.5, 107]}
    return [{'label': 'Hip-Hop', 'score': 0.8, 'model': analyzer.genre_model_name}]
analyzer._extract_core_audio_metrics = core
analyzer._extract_ml_features = mood
analyzer._extract_essentia_genre_evidence = genre
analyzer._extract_sonic_embedding = sonic
analyzer._generate_mood_tags = lambda result: ['Happy']
actual = analyzer.analyze('unused')
assert actual['analysisMode'] == 'enhanced'
payload = module.build_payload(actual)
assert payload['GenreSamplingPolicy'] == 'three-30s-v1'
assert payload['GenreAnalyzedSeconds'] == 90
assert payload['GenreWindowStartSeconds'] == [0, 53.5, 107]
assert payload['ValenceMl'] == 0.8
print('true')");
        Assert.True(result.GetBoolean());
    }

    private static string BuildScoreLiteral(List<double> scores)
        => JsonSerializer.Serialize(scores);

    private static string BuildLabelLiteral(int count)
        => JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => $"L{i}").ToList());

    private static JsonElement RunPython(string functionName, string scoresJson, string labelsJson)
    {
        return RunPythonScript($@"
print(json.dumps(module.{functionName}(
    json.loads(base64.b64decode('{ToBase64(scoresJson)}').decode('utf-8')),
    json.loads(base64.b64decode('{ToBase64(labelsJson)}').decode('utf-8')),
    'discogs519-maest-30s-pw-519l')))
");
    }

    private static JsonElement RunPythonScript(string body)
    {
        var scriptPath = ResolveAnalyzerPath();
        var script = $@"
import base64, importlib.util, json
spec = importlib.util.spec_from_file_location('vibe_analyzer', {ToPythonLiteral(scriptPath)})
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
{body}
";
        var tempScript = Path.Combine(Path.GetTempPath(), $"vibe-genre-{Guid.NewGuid():N}.py");
        File.WriteAllText(tempScript, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("VIBE_ANALYZER_PYTHON") ?? "python3",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-B");
            startInfo.ArgumentList.Add(tempScript);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var stdout = process!.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, stderr);
            using var document = JsonDocument.Parse(stdout);
            return document.RootElement.Clone();
        }
        finally
        {
            File.Delete(tempScript);
        }
    }

    private static string ToPythonLiteral(string value) => "\"" + value.Replace("\\", "\\\\") + "\"";

    private static string ToBase64(string value)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string ResolveAnalyzerPath()
        => Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "Tools", "vibe_analyzer.py");
}
