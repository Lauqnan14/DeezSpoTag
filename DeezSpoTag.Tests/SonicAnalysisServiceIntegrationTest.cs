using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 1 integration contract for Sonic Analysis inside the analysis service.
///
/// <para>The vector is produced by a subprocess and arrives as base64 text, so
/// the decode is a trust boundary: whatever the analyzer emits must be validated
/// before it can reach storage. These tests pin that boundary, the opt-in
/// switch, and the guarantee that a Sonic problem cannot fail a track that
/// analysed perfectly well without it.</para>
/// </summary>
public sealed class SonicAnalysisServiceIntegrationTest
{
    private static readonly string ServicePath = Path.Join(
        ResolveRepoRoot(), "DeezSpoTag.Web", "Services", "TrackAnalysisBackgroundService.cs");

    [Fact]
    public void DecodeSonicVector_RejectsAPayloadWhoseWidthDisagreesWithTheDeclaredDimension()
    {
        // 1280 declared but 640 float32 values actually sent. Accepting this
        // would store a vector that no reader can interpret.
        var payload = InvokeDecode(1280, Base64(640));
        Assert.Null(payload);
    }

    [Fact]
    public void DecodeSonicVector_RejectsANonNumericPayload()
    {
        Assert.Null(InvokeDecode(1280, Convert.ToBase64String(Encoding.UTF8.GetBytes("not-a-vector"))));
    }

    [Fact]
    public void DecodeSonicVector_RejectsAnEmptyPayload()
    {
        Assert.Null(InvokeDecode(1280, string.Empty));
        Assert.Null(InvokeDecode(0, Base64(1280)));
    }

    [Fact]
    public void DecodeSonicVector_RejectsNaNAndInfinity()
    {
        // A vector containing a non-finite value is silently unusable: cosine
        // against it produces NaN, which would poison every ranking it touched.
        var withNaN = new float[1280];
        withNaN[7] = float.NaN;
        Assert.Null(InvokeDecode(1280, Base64Of(withNaN)));

        var withInfinity = new float[1280];
        withInfinity[11] = float.PositiveInfinity;
        Assert.Null(InvokeDecode(1280, Base64Of(withInfinity)));
    }

    [Fact]
    public void DecodeSonicVector_AcceptsAWellFormedLittleEndianFloat32Vector()
    {
        var vector = new float[1280];
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(index % 17) / 1280f;
        }

        var decoded = InvokeDecode(1280, Base64Of(vector));
        Assert.NotNull(decoded);
        Assert.Equal(1280, decoded!.Count);
        for (var index = 0; index < vector.Length; index++)
        {
            Assert.Equal(vector[index], decoded[index], 6);
        }
    }

    [Fact]
    public void SonicIsOptInAndIndependentOfVibeAnalysis()
    {
        var service = File.ReadAllText(ServicePath);

        // Sonic must not be switched on merely because Vibe Analysis runs: it
        // adds a second inference pass per track.
        Assert.Contains("SonicAnalysisConfigurationPath = \"SonicAnalysis:Enabled\"", service, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsSonicAnalysisEnabled()\n            && await IsAnalysisEnabledAsync",
            service,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SonicFlagIsAlwaysSetExplicitlyOnAnalyzerProcesses()
    {
        // ConfigurePythonEnvironment is the single choke point for the batch
        // process, the persistent worker and the probe. Setting the flag there
        // means an inherited shell value cannot silently enable a second
        // inference pass, and a disabled run actively clears it.
        var service = File.ReadAllText(ServicePath);

        Assert.Contains("startInfo.Environment[SonicAnalysisEnabledEnvironmentVariable]", service, StringComparison.Ordinal);
        Assert.Contains("sonicEnabled ? \"1\" : \"0\"", service, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicPersistenceIsIsolatedFromTheSemanticResult()
    {
        // The order matters: the semantic row is committed first, and the Sonic
        // write is a separate, non-throwing step after it.
        var service = File.ReadAllText(ServicePath);

        var upsert = service.IndexOf("await _repository.UpsertTrackAnalysisAsync(result, cancellationToken);", StringComparison.Ordinal);
        var persist = service.IndexOf("await PersistSonicEmbeddingAsync(track, completion.Sonic, cancellationToken);", StringComparison.Ordinal);

        Assert.True(upsert > 0, "Sonic integration lost its semantic upsert anchor.");
        Assert.True(persist > upsert, "Sonic persistence must follow the semantic upsert, never precede it.");
    }

    [Fact]
    public void PersistSonicEmbedding_SwallowsFailuresRatherThanFailingTheTrack()
    {
        var service = File.ReadAllText(ServicePath);

        var start = service.IndexOf("private async Task PersistSonicEmbeddingAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "PersistSonicEmbeddingAsync not found.");
        var body = service.Substring(start, Math.Min(3000, service.Length - start));

        Assert.Contains("catch (Exception ex) when", body, StringComparison.Ordinal);
        Assert.Contains("IsRecoverable(ex)", body, StringComparison.Ordinal);
        Assert.Contains("Failed to persist Sonic embedding", body, StringComparison.Ordinal);

        // It must not declare a result whose failure the caller has to handle.
        Assert.Contains("private async Task PersistSonicEmbeddingAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private async Task<bool> PersistSonicEmbeddingAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFileRevisionIsRecordedForStalenessDetection()
    {
        // Size and mtime are what let a later run decide a vector is stale
        // without re-hashing the audio. The service captures them from disk; the
        // DTO is what carries them to storage.
        var service = File.ReadAllText(ServicePath);
        Assert.Contains("TryReadSourceRevision", service, StringComparison.Ordinal);
        Assert.Contains("info.Length", service, StringComparison.Ordinal);
        Assert.Contains("info.LastWriteTimeUtc", service, StringComparison.Ordinal);

        var models = File.ReadAllText(Path.Join(
            ResolveRepoRoot(), "DeezSpoTag.Services", "Library", "Models.cs"));
        Assert.Contains("public sealed record SonicEmbeddingDto", models, StringComparison.Ordinal);
        Assert.Contains("long? SourceFileSize", models, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset? SourceFileMtimeUtc", models, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVectorIsNeverPlacedOnTheAnalysisDto()
    {
        // Carrying the vector on TrackAnalysisResultDto would put it in every
        // GetTrackAnalysisByTrackIdsAsync result. It travels beside the DTO.
        var service = File.ReadAllText(ServicePath);

        Assert.Contains("private sealed record TrackAnalysisCompletion(", service, StringComparison.Ordinal);
        Assert.Contains("TrackAnalysisResultDto Result, SonicPayload? Sonic", service, StringComparison.Ordinal);
    }

    [Fact]
    public void SonicModelIdentityIsDeclaredOnce()
    {
        // The stored identity and the analyzer's identity must agree. Both are
        // pinned by name so a change in one place is a visible test failure.
        var service = File.ReadAllText(ServicePath);
        Assert.Contains("SonicModelId = \"discogs-effnet-bs64-1\"", service, StringComparison.Ordinal);
        Assert.Contains("SonicModelVersion = \"1\"", service, StringComparison.Ordinal);
        Assert.Contains("SonicEmbeddingVersion = \"embedding-v1\"", service, StringComparison.Ordinal);

        var identity = File.ReadAllText(Path.Join(
            ResolveRepoRoot(), "DeezSpoTag.Services", "Library", "Sonic", "SonicSimilarityIndex.cs"));
        Assert.Contains("\"discogs-effnet-bs64-1\"", identity, StringComparison.Ordinal);
        Assert.Contains("\"embedding-v1\"", identity, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Exercises the real decode through reflection, because the method is a
    /// private static on a 3900-line service. Asserting on its behaviour is the
    /// point; asserting on its source text would not catch a bad cast.
    /// </summary>
    private static IReadOnlyList<float>? InvokeDecode(int dimensions, string base64)
    {
        var serviceType = Type.GetType(
            "DeezSpoTag.Web.Services.TrackAnalysisBackgroundService, DeezSpoTag.Web")
            ?? throw new InvalidOperationException("TrackAnalysisBackgroundService type not found.");

        var payloadType = serviceType.GetNestedType(
            "SonicPayload",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SonicPayload type not found.");

        var payload = Activator.CreateInstance(
            payloadType,
            "discogs-effnet-bs64-1",
            "1",
            "embedding-v1",
            dimensions,
            "mean-v1",
            "l2-v1",
            "cosine",
            29,
            base64);

        var method = serviceType.GetMethod(
            "DecodeSonicVector",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DecodeSonicVector not found.");

        return method.Invoke(null, new[] { payload }) as IReadOnlyList<float>;
    }

    private static string Base64(int floatCount)
        => Base64Of(new float[floatCount]);

    private static string Base64Of(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
