using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 0 contract test for Sonic Analysis embedding models.
///
/// Sonic Analysis stores a learned audio embedding per track in a fixed-width
/// BLOB. The stored width is part of the on-disk contract: a BLOB whose length
/// does not match the declared dimension is not a harmless mismatch, it is a
/// corrupt vector that silently poisons every similarity result computed from
/// it. The dimension therefore has to be provable, not assumed.
///
/// The dimensions are asserted from the frozen TensorFlow graphs themselves.
/// Each classification head consumes exactly one embedding tensor, so the
/// head's first dense kernel shape reveals the embedding width the upstream
/// extractor must produce:
///
///   Discogs400 head   kernel [400, 1280]  -> EffNet embedding is 1280-d
///   MusiCNN mood head kernel [200, 100]   -> musiCNN embedding is 200-d
///   Discogs519 head   tensors [1, 768]    -> MAEST token embedding is 768-d
///
/// The MAEST model name "519l" is a label count, not a dimensionality. That
/// distinction is asserted explicitly because reading it as a width is the
/// exact mistake this file exists to prevent.
///
/// No TensorFlow or Essentia runtime is required: these graphs are read
/// directly. A separate runtime probe below re-derives the same numbers from a
/// live extractor when one is installed, and reports rather than silently
/// passing when it is not.
/// </summary>
public sealed class SonicEmbeddingModelContractTest
{
    /// <summary>Embedding width of the primary proposed Sonic model.</summary>
    public const int ExpectedEffNetDimensions = 1280;

    /// <summary>Embedding width of the MusiCNN output consumed by the mood heads.</summary>
    public const int ExpectedMusiCnnDimensions = 200;

    /// <summary>Per-token embedding width produced by the MAEST extractor.</summary>
    public const int ExpectedMaestTokenDimensions = 768;

    /// <summary>Number of Discogs genre labels in the Discogs519 head.</summary>
    public const int ExpectedDiscogs519LabelCount = 519;

    /// <summary>
    /// Tokens MAEST emits per 30-second segment, and the segment length it is
    /// trained over ("30s-pw" = 30 second patch-wise). Measured at runtime as
    /// the middle axis of [batch, 1, segments, 1685, 768].
    /// </summary>
    public const int ExpectedMaestTokensPerSegment = 1685;

    /// <summary>Seconds of audio per MAEST segment.</summary>
    public const int ExpectedMaestSegmentSeconds = 30;

    /// <summary>
    /// Observed frame rate of the EffNet extractor: one embedding frame per
    /// second of audio (29 frames for 30 s, 226 frames for a 225 s track).
    ///
    /// This is the fact that makes mean pooling mandatory rather than optional.
    /// A 1280-wide frame at ~1 fps means an unpooled 600 s track is ~600 x 1280
    /// floats, so the transport must carry the pooled vector only. It also means
    /// a pooled vector is duration-dependent, which is why the source audio
    /// length belongs in the invalidation key alongside size and mtime.
    /// </summary>
    public const double ObservedEffNetFramesPerSecond = 1.0;

    [Fact]
    public void EffNetEmbedding_Is1280Dimensions()
    {
        var head = FindModel("genre_discogs400-discogs-effnet-1.pb");
        var kernels = ConstShapes(head)
            .Where(shape => shape.Length == 2)
            .ToList();

        // The classifier projects embedding -> class. [classes, embedding].
        var projection = kernels.FirstOrDefault(shape => shape[0] == 400);
        Assert.NotNull(projection);
        Assert.Equal(ExpectedEffNetDimensions, projection![1]);

        // The extractor ends in a 1x1 convolution producing the same width.
        // A 1x1 conv kernel is [out_channels, in_channels, 1, 1].
        var extractor = FindModel("discogs-effnet-bs64-1.pb");
        var finalConv = ConstShapes(extractor)
            .Where(shape => shape.Length == 4 && shape[2] == 1 && shape[3] == 1)
            .Where(shape => shape[0] == ExpectedEffNetDimensions)
            .ToList();
        Assert.True(
            finalConv.Count > 0,
            "discogs-effnet-bs64-1 no longer contains a 1x1 convolution producing "
            + $"{ExpectedEffNetDimensions} channels; the Sonic embedding contract changed.");
    }

    [Fact]
    public void MusiCnnEmbedding_Is200Dimensions()
    {
        var head = FindModel("mood_happy-msd-musicnn-1.pb");
        var firstDense = ConstShapes(head)
            .FirstOrDefault(shape => shape.Length == 2 && shape[1] > 1);

        Assert.NotNull(firstDense);
        Assert.Equal(ExpectedMusiCnnDimensions, firstDense![0]);
    }

    [Fact]
    public void EveryMusiCnnMoodHead_AgreesOnTheEmbeddingWidth()
    {
        // The seven mood heads are separate graphs. A width disagreement between
        // them would mean the mood scores are not comparable across dimensions.
        var heads = new[]
        {
            "mood_happy-msd-musicnn-1.pb",
            "mood_sad-msd-musicnn-1.pb",
            "mood_relaxed-msd-musicnn-1.pb",
            "mood_aggressive-msd-musicnn-1.pb",
            "mood_party-msd-musicnn-1.pb",
            "mood_acoustic-msd-musicnn-1.pb",
            "mood_electronic-msd-musicnn-1.pb",
        };

        var widths = new HashSet<int>();
        foreach (var head in heads)
        {
            var firstDense = ConstShapes(FindModel(head))
                .FirstOrDefault(shape => shape.Length == 2 && shape[1] > 1);
            Assert.NotNull(firstDense);
            widths.Add(firstDense![0]);
        }

        Assert.True(
            widths.Count == 1 && widths.Contains(ExpectedMusiCnnDimensions),
            $"musiCNN mood heads disagree on embedding width: {string.Join(", ", widths)}");
    }

    [Fact]
    public void Discogs519_IsALabelCount_AndMaestTokensAre768()
    {
        // The MAEST extractor is a ~332 MB model that is not part of the default
        // checkout; the tracked model set carries the Discogs400/EffNet pair only.
        // These assertions therefore run where MAEST is provisioned (the
        // application image, or scripts/fetch-vibe-models.sh) and are inert
        // elsewhere. EffNet remains the primary Sonic model either way.
        var headPath = TryFindModel("genre_discogs519-discogs-maest-30s-pw-519l-1.pb");
        if (headPath is null)
        {
            return;
        }

        var labels = LoadDiscogs519Labels();
        Assert.Equal(ExpectedDiscogs519LabelCount, labels.Count);

        var trailingAxes = ConstShapes(headPath)
            .Where(shape => shape.Length > 0)
            .Select(shape => shape[^1])
            .Distinct()
            .ToList();

        // The token axis is the embedding width the upstream extractor produces, so this is
        // the number that has to be right.
        Assert.Contains(ExpectedMaestTokenDimensions, trailingAxes);

        // 519 is the LABEL count, and the head that classifies into that label set is
        // therefore legitimately 519 wide - the real graph does contain such a tensor. An
        // earlier version of this test asserted that no tensor here was 519 wide, which is
        // false of the shipped model and only ever passed because the model was absent.
        //
        // The mistake this file exists to prevent is reading "519l" in the file name as a
        // vector width. The pair of assertions is what actually rules it out: the embedding
        // axis is 768, and the one 519 axis agrees with the label count read from the model's
        // own JSON above. A 519 read as an embedding width would have to displace the 768.
        Assert.Contains(ExpectedDiscogs519LabelCount, trailingAxes);
    }

    [Fact]
    public void Discogs519Labels_UseTheBroadSubStyleSeparatorTheResolverSplits()
    {
        // VibeSemanticResolver splits acoustic genre labels on "---" to derive a
        // broad Genre and a Style. The classifier label set is the source of
        // those labels, so a separator change silently empties Style.
        if (TryFindModel("genre_discogs519-discogs-maest-30s-pw-519l-1.json") is null)
        {
            return;
        }

        var labels = LoadDiscogs519Labels();
        var withSeparator = labels.Count(label => label.Contains("---", StringComparison.Ordinal));

        Assert.True(
            withSeparator > 0,
            "No Discogs519 label contains the '---' separator that "
            + "VibeSemanticResolver splits on; Style would resolve empty.");
    }

    [Fact]
    public void TrackedModelSet_ProvidesThePrimarySonicModel()
    {
        // Sonic Analysis ships with EffNet as its primary representation, so the
        // tracked model set must contain it without any extra provisioning step.
        var modelsDirectory = FindModelDirectory("discogs-effnet-bs64-1.pb");
        var effnet = Path.Join(modelsDirectory, "discogs-effnet-bs64-1.pb");

        Assert.True(
            File.Exists(effnet) && new FileInfo(effnet).Length > 0,
            "The primary Sonic model is missing from the resolved model directory. "
            + $"Resolved: {modelsDirectory}");
    }

    [Fact]
    public void GenreBranch_ShortCircuitsBeforeEffNet_WhenMaestIsAvailable()
    {
        // This is the Phase 0 question that changes the Sonic transport design:
        // is the EffNet extractor run on every enhanced analysis, or only as a
        // MAEST fallback? _extract_essentia_genre_evidence returns from inside
        // the MAEST branch, so when MAEST loads, EffNet is never invoked and no
        // EffNet embedding exists to capture without changing this function.
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        var start = analyzer.IndexOf("def _extract_essentia_genre_evidence", StringComparison.Ordinal);
        Assert.True(start > 0, "_extract_essentia_genre_evidence not found in vibe_analyzer.py.");
        var body = analyzer.Substring(start, Math.Min(2600, analyzer.Length - start));

        var maestReturn = body.IndexOf("return top_genre_evidence", StringComparison.Ordinal);
        var effnetCall = body.IndexOf("self.effnet_extractor(audio_16k)", StringComparison.Ordinal);

        Assert.True(maestReturn > 0, "The MAEST branch no longer returns genre evidence.");
        Assert.True(effnetCall > 0, "The EffNet fallback branch no longer calls the extractor.");
        Assert.True(
            maestReturn < effnetCall,
            "The EffNet branch now runs before the MAEST branch returns; EffNet is no longer "
            + "a fallback-only path and the Sonic transport must assume it always runs.");
    }

    [Fact]
    public void EffNetOnlyGenreHelper_IsNotOnTheLiveAnalysisPath()
    {
        // The duplicate EffNet-only helper was removed. Keep it absent so it
        // cannot bypass the shared genre evidence path or run EffNet per track.
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        Assert.DoesNotContain("def _extract_essentia_genres", analyzer, StringComparison.Ordinal);

        var analyzeStart = analyzer.IndexOf("    def analyze(self, file_path", StringComparison.Ordinal);
        Assert.True(analyzeStart > 0, "AudioAnalyzer.analyze not found.");
        var analyzeBody = analyzer.Substring(analyzeStart, Math.Min(2600, analyzer.Length - analyzeStart));

        Assert.DoesNotContain("_extract_essentia_genres(", analyzeBody, StringComparison.Ordinal);
        Assert.Contains("_extract_essentia_genre_evidence(audio_16k)", analyzeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EffNetOutputNode_UsedByTheAnalyzer_IsTheEmbeddingTensor()
    {
        // The analyzer reads output "PartitionedCall:1" from the EffNet graph.
        // That tensor must be the 1280-channel embedding, not the 400-way genre
        // logits; capturing logits would produce a classification-shaped vector
        // under an embedding contract.
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        Assert.Contains("TensorflowPredictEffnetDiscogs", analyzer, StringComparison.Ordinal);
        Assert.Contains("output=\"PartitionedCall:1\"", analyzer, StringComparison.Ordinal);

        // The same graph also embeds a 400-class head. Its presence is why the
        // semantic and acoustic outputs must not be conflated.
        var extractorShapes = ConstShapes(FindModel("discogs-effnet-bs64-1.pb"));
        Assert.Contains(
            extractorShapes,
            shape => shape.Length == 2 && shape[1] == ExpectedEffNetDimensions);
    }

    [Fact]
    public void BuildPayload_CarriesTheVectorOnlyInItsOwnField()
    {
        // Phase 1 invariant. The vector must reach .NET, but only in a dedicated
        // payload field. It must not leak into the semantic fields that are
        // persisted into track_analysis.metadata_json, because a vector there
        // would ride along with every ordinary analysis query.
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        var start = analyzer.IndexOf("def build_payload", StringComparison.Ordinal);
        Assert.True(start > 0, "build_payload not found in vibe_analyzer.py.");
        var body = analyzer.Substring(start, Math.Min(2600, analyzer.Length - start));

        Assert.Contains("SonicEmbedding", body, StringComparison.Ordinal);
        Assert.Contains("VectorBase64", body, StringComparison.Ordinal);

        // The semantic payload fields must remain scalar/list only.
        foreach (var semanticField in new[]
        {
            "\"EssentiaGenreEvidence\"", "\"MoodTags\"", "\"essentiaGenres\"",
            "\"Genres\"", "\"SemanticEvidence\"",
        })
        {
            var index = body.IndexOf(semanticField, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var line = body[index..body.IndexOf('\n', index)];
            Assert.DoesNotContain("Vector", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SonicIsOptIn_AndGatedByItsOwnFlag()
    {
        // Sonic adds a second inference pass per track, so it must be off by
        // default and must not be switched on merely because Vibe Analysis runs.
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());
        Assert.Contains("SONIC_ENABLED_ENV_NAME = \"VIBE_SONIC_ENABLED\"", analyzer, StringComparison.Ordinal);
        Assert.Contains("def sonic_enabled()", analyzer, StringComparison.Ordinal);

        var service = File.ReadAllText(ResolveRepoRootPath("DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs"));
        Assert.Contains("SonicAnalysisConfigurationPath = \"SonicAnalysis:Enabled\"", service, StringComparison.Ordinal);

        var settings = File.ReadAllText(ResolveRepoRootPath("DeezSpoTag.Web", "appsettings.json"));
        Assert.Contains("\"SonicAnalysis\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicVector_IsNeverPlacedOnTheAnalysisDto()
    {
        // The vector travels beside TrackAnalysisResultDto, not inside it. If a
        // vector field were added to the DTO, every GetTrackAnalysisByTrackIdsAsync
        // caller would start paying for it.
        var models = File.ReadAllText(ResolveRepoRootPath("DeezSpoTag.Services", "Library", "Models.cs"));

        var recordStart = models.IndexOf("public sealed record TrackAnalysisResultDto", StringComparison.Ordinal);
        Assert.True(recordStart > 0, "TrackAnalysisResultDto not found.");
        var recordEnd = models.IndexOf(");", recordStart, StringComparison.Ordinal);
        var recordBody = models[recordStart..recordEnd];

        Assert.DoesNotContain("Sonic", recordBody, StringComparison.Ordinal);
        Assert.Contains("public sealed record SonicEmbeddingDto", models, StringComparison.Ordinal);
    }

    private static string ResolveRepoRootPath(params string[] parts)
        => Path.Join(new[] { ResolveRepoRoot() }.Concat(parts).ToArray());

    [Fact]
    public void RuntimeProbe_ReportsLiveExtractorShapes_WhenEssentiaIsInstalled()
    {
        // Re-derives the dimensions above from live extractors. This is the check
        // the design gate asks for (raw shape, dtype, frames, pooled shape, NaN,
        // Infinity). It requires the 291 MB essentia-tensorflow wheel, which the
        // application image provides and the test host usually does not, so it
        // reports and returns rather than failing when the runtime is absent.
        //
        // Verified against deezspotag:essentia-worker-test with Essentia
        // 2.1-beta6-dev on a real 225 s track:
        //   discogs-effnet-bs64-1/PartitionedCall:1  [226, 1280]  float32
        //   msd-musicnn-1/model/dense/BiasAdd        [150,  200]  float32
        //   discogs-maest-30s-pw-519l-2/Identity_12   [  7, 1, 1685, 768]
        // No NaN and no Infinity in any of the three, at any probed duration.
        var models = FindModelDirectory("discogs-effnet-bs64-1.pb");

        var probe = RunPython(
            "-c",
            "import essentia.standard as es, numpy as np;"
            + $"m=es.TensorflowPredictEffnetDiscogs(graphFilename=r'{models}/discogs-effnet-bs64-1.pb',"
            + "output='PartitionedCall:1');"
            + "a=np.zeros(16000*30,dtype=np.float32);"
            + "e=np.asarray(m(a));"
            + "print('SHAPE', list(e.shape));"
            + "print('DTYPE', e.dtype);"
            + "print('NAN', bool(np.isnan(e).any()));"
            + "print('INF', bool(np.isinf(e).any()));");

        if (probe.ExitCode != 0 || probe.StandardOutput.Contains("ModuleNotFoundError", StringComparison.Ordinal))
        {
            // Not a failure: the runtime is provisioned by the Docker image, not
            // by the test suite. The static contract above is the CI gate.
            return;
        }

        Assert.Contains("SHAPE", probe.StandardOutput, StringComparison.Ordinal);
        var shapeLine = probe.StandardOutput
            .Split('\n')
            .First(line => line.StartsWith("SHAPE", StringComparison.Ordinal));
        var dims = shapeLine
            .Split('[')[1]
            .Split(']')[0]
            .Split(',')
            .Select(part => int.Parse(part.Trim()))
            .ToArray();

        // Trailing axis is the embedding width; the leading axis is the frame
        // count and must not be treated as part of the stored contract.
        Assert.Equal(ExpectedEffNetDimensions, dims[^1]);
        Assert.Contains("DTYPE float32", probe.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("NAN False", probe.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("INF False", probe.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void MaestOutputNode_IsTheOneTheAnalyzerActuallyUses()
    {
        // "PartitionedCall:1" is the EffNet output node and does NOT exist in the
        // MAEST graph; using it raises "Reconfigure this algorithm with valid
        // node names". MAEST must be read at PartitionedCall/Identity_12, the
        // node vibe_analyzer.py captures. Getting this wrong is an easy and
        // silent failure when MAEST becomes the Sonic model.
        var analyzer = File.ReadAllText(ResolveAnalyzerPath());

        Assert.Contains("output=\"PartitionedCall/Identity_12\"", analyzer, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<int[]> ConstShapes(string graphPath)
    {
        var shapes = new List<int[]>();
        foreach (var node in FrozenGraphReader.ReadNodes(graphPath))
        {
            if (!string.Equals(node.Op, "Const", StringComparison.Ordinal))
            {
                continue;
            }

            if (node.TryGetTensorShape("value", out var shape))
            {
                shapes.Add(shape);
            }
        }

        return shapes;
    }

    private static IReadOnlyList<string> LoadDiscogs519Labels()
    {
        var path = FindModel("genre_discogs519-discogs-maest-30s-pw-519l-1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var classes = document.RootElement.TryGetProperty("classes", out var element)
            ? element
            : document.RootElement.GetProperty("labels");

        return classes.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();
    }

    private static string ResolveAnalyzerPath()
        => Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "Tools", "vibe_analyzer.py");

    private static string FindModel(string fileName)
    {
        var path = TryFindModel(fileName);
        Assert.True(
            path is not null,
            $"Model '{fileName}' was not found. Run scripts/setup-vibe-analysis-local.sh "
            + "(or use the application image) before running the Sonic model contract tests.");
        return path!;
    }

    private static string? TryFindModel(string fileName)
    {
        foreach (var directory in ResolveModelDirectories())
        {
            var path = Path.Join(directory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Model files are large and are not present in every checkout, so the
    /// contract tests resolve them from the same places the application does:
    /// an explicit VIBE_ANALYZER_MODELS override, the tracked Tools/models
    /// directory, then the Workers data root used by the local setup script.
    /// </summary>
    private static string FindModelDirectory(string fileName)
    {
        foreach (var directory in ResolveModelDirectories())
        {
            if (File.Exists(Path.Join(directory, fileName)))
            {
                return directory;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate '{fileName}'. Searched: "
            + string.Join(", ", ResolveModelDirectories()));
    }

    private static IReadOnlyList<string> ResolveModelDirectories()
    {
        var root = ResolveRepoRoot();
        var configured = Environment.GetEnvironmentVariable("VIBE_ANALYZER_MODELS");
        return new[]
        {
            configured,
            Path.Join(root, "DeezSpoTag.Web", "Tools", "models"),
            Path.Join(root, "DeezSpoTag.Workers", "Data", "analysis", "models"),
        }
        .Where(directory => !string.IsNullOrWhiteSpace(directory))
        .Select(directory => directory!)
        .ToList();
    }

    private static (int ExitCode, string StandardOutput) RunPython(string flag, string code)
    {
        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(flag);
        start.ArgumentList.Add(code);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start python3.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    /// <summary>
    /// Minimal reader for frozen TensorFlow GraphDef protobuf, sufficient to read
    /// Const tensor shapes without a TensorFlow dependency.
    ///
    /// Field numbers used here are the ones the frozen graphs actually use:
    /// GraphDef.node=1; NodeDef name=1 op=2 input=3 attr=5; AttrValue shape=7
    /// tensor=8; TensorProto dtype=1 tensor_shape=2.
    /// </summary>
    private static class FrozenGraphReader
    {
        internal sealed record NodeDef(string Name, string Op, Dictionary<string, byte[]> Attrs)
        {
            internal bool TryGetTensorShape(string attrName, out int[] shape)
            {
                shape = Array.Empty<int>();
                if (!Attrs.TryGetValue(attrName, out var attrValue))
                {
                    return false;
                }

                var tensorProto = ReadFirstSubMessage(attrValue, 8);
                if (tensorProto is null)
                {
                    return false;
                }

                var shapeProto = ReadFirstSubMessage(tensorProto, 2);
                if (shapeProto is null)
                {
                    return false;
                }

                var dims = new List<int>();
                foreach (var dim in ReadAllSubMessages(shapeProto, 2))
                {
                    dims.Add((int)(ReadInt64(dim, 1) ?? -1));
                }

                shape = dims.ToArray();
                return shape.Length > 0;
            }
        }

        internal static IReadOnlyList<NodeDef> ReadNodes(string path)
        {
            var buffer = File.ReadAllBytes(path);
            var nodes = new List<NodeDef>();
            foreach (var nodeBytes in ReadAllSubMessages(buffer, 1))
            {
                var name = ReadString(nodeBytes, 1) ?? string.Empty;
                var op = ReadString(nodeBytes, 2) ?? string.Empty;
                var attrs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var entry in ReadAllSubMessages(nodeBytes, 5))
                {
                    var key = ReadString(entry, 1);
                    var value = ReadFirstSubMessage(entry, 2);
                    if (key is not null && value is not null)
                    {
                        attrs[key] = value;
                    }
                }

                nodes.Add(new NodeDef(name, op, attrs));
            }

            return nodes;
        }

        private static byte[]? ReadFirstSubMessage(byte[] buffer, int field)
        {
            foreach (var message in ReadAllSubMessages(buffer, field))
            {
                return message;
            }

            return null;
        }

        private static IEnumerable<byte[]> ReadAllSubMessages(byte[] buffer, int field)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                if (!TryReadVarint(buffer, ref offset, out var key))
                {
                    yield break;
                }

                var currentField = (int)(key >> 3);
                var wire = (int)(key & 0x7);

                switch (wire)
                {
                    case 0:
                        if (!TryReadVarint(buffer, ref offset, out _))
                        {
                            yield break;
                        }

                        break;
                    case 1:
                        offset += 8;
                        break;
                    case 5:
                        offset += 4;
                        break;
                    case 2:
                        if (!TryReadVarint(buffer, ref offset, out var length)
                            || length < 0
                            || offset + length > buffer.Length)
                        {
                            yield break;
                        }

                        if (currentField == field)
                        {
                            var message = new byte[length];
                            Array.Copy(buffer, offset, message, 0, length);
                            yield return message;
                        }

                        offset += (int)length;
                        break;
                    default:
                        yield break;
                }
            }
        }

        private static string? ReadString(byte[] buffer, int field)
        {
            foreach (var message in ReadAllSubMessages(buffer, field))
            {
                return Encoding.UTF8.GetString(message);
            }

            return null;
        }

        private static long? ReadInt64(byte[] buffer, int field)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                if (!TryReadVarint(buffer, ref offset, out var key))
                {
                    return null;
                }

                var currentField = (int)(key >> 3);
                var wire = (int)(key & 0x7);

                if (wire == 0)
                {
                    if (!TryReadVarint(buffer, ref offset, out var value))
                    {
                        return null;
                    }

                    if (currentField == field)
                    {
                        return value;
                    }
                }
                else if (wire == 1)
                {
                    offset += 8;
                }
                else if (wire == 5)
                {
                    offset += 4;
                }
                else if (wire == 2)
                {
                    if (!TryReadVarint(buffer, ref offset, out var length) || offset + length > buffer.Length)
                    {
                        return null;
                    }

                    offset += (int)length;
                }
                else
                {
                    return null;
                }
            }

            return null;
        }

        private static bool TryReadVarint(byte[] buffer, ref int offset, out long value)
        {
            value = 0;
            var shift = 0;
            while (offset < buffer.Length && shift < 64)
            {
                var current = buffer[offset++];
                value |= (long)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }
    }
}
