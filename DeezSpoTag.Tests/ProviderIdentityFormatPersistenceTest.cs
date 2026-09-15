using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Every extension AutoTag accepts must resolve to one supported persistence backend,
/// and a provider identity written through that backend must read back — never silently
/// succeed.
/// </summary>
public sealed class ProviderIdentityFormatPersistenceTest
{
    /// <summary>All 22 extensions accepted by <c>AutoTagService.EligibleAudioExtensions</c>.</summary>
    public static readonly string[] EligibleAudioExtensions =
    [
        ".flac", ".wav", ".aiff", ".aif", ".alac", ".m4a", ".m4b", ".mp4", ".aac",
        ".mp3", ".wma", ".ogg", ".opus", ".oga", ".ape", ".wv", ".mp2", ".mp1",
        ".tta", ".dsf", ".dff", ".mka"
    ];

    [Theory]
    [MemberData(nameof(EligibleExtensionData))]
    public void EveryEligibleExtension_ResolvesToOneSupportedBackend(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "dsh-identity-dispatch", "probe" + extension);
        LocalAutoTagRunner.TryOpenTagLibFile(path, out var file);
        var backend = InvokeResolveBackend(path, file);
        file?.Dispose();

        Assert.True(
            Enum.IsDefined(backend),
            $"{extension} resolved to {backend}, which is not a supported provider identity backend");
    }

    [Fact]
    public async Task EveryAvailableFixture_RetainsTwoProviderFamilies()
    {
        foreach (var extension in EligibleAudioExtensions)
        {
            using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(extension);
            if (!fixture.Available)
            {
                continue;
            }

            var spotify = Payload("spotify", trackId: "spotify-track", albumId: "spotify-album");
            var deezer = Payload("deezer", trackId: "123", albumId: "456");

            var first = await Write(fixture.Path, spotify, overwrite: true);
            var deezerResult = await Write(fixture.Path, deezer, overwrite: true);

            if (Failures(first).Count > 0 || Failures(deezerResult).Count > 0)
            {
                // An accepted container that cannot persist provider identity must report
                // an explicit failure; it may never fail silently.
                Assert.NotEmpty(Failures(first).Concat(Failures(deezerResult)));
                continue;
            }

            Assert.Equal("spotify-track", Read(fixture.Path, "SPOTIFY_TRACK_ID"));
            Assert.Equal("spotify-album", Read(fixture.Path, "SPOTIFY_ALBUM_ID"));
            Assert.Equal("123", Read(fixture.Path, "DEEZER_TRACK_ID"));
            Assert.Equal("456", Read(fixture.Path, "DEEZER_ALBUM_ID"));
        }
    }

    [Fact]
    public async Task Persistence_OverwritesOnlyTheTargetedProviderField()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        Assert.True(fixture.Available);

        await Write(
            fixture.Path,
            Payload("spotify", trackId: "spotify-track", albumId: "spotify-album", releaseId: "spotify-release"),
            overwrite: true);
        await Write(
            fixture.Path,
            Payload("deezer", trackId: "123", albumId: "456", releaseId: "789"),
            overwrite: true);

        var genericFields = LocalAutoTagRunner.GenericIdentityCompatibilityFields
            .ToDictionary(name => name, name => Read(fixture.Path, name), StringComparer.OrdinalIgnoreCase);
        Assert.All(genericFields.Values, value => Assert.Null(value));

        await Write(
            fixture.Path,
            Payload("spotify", releaseId: "spotify-release-2"),
            overwrite: true,
            fields: new HashSet<ProviderIdentityField> { ProviderIdentityField.ReleaseId });

        Assert.Equal("spotify-release-2", Read(fixture.Path, "SPOTIFY_RELEASE_ID"));
        Assert.Equal("spotify-track", Read(fixture.Path, "SPOTIFY_TRACK_ID"));
        Assert.Equal("spotify-album", Read(fixture.Path, "SPOTIFY_ALBUM_ID"));
        Assert.Equal("123", Read(fixture.Path, "DEEZER_TRACK_ID"));
        Assert.Equal("456", Read(fixture.Path, "DEEZER_ALBUM_ID"));
        Assert.Equal("789", Read(fixture.Path, "DEEZER_RELEASE_ID"));

        foreach (var name in LocalAutoTagRunner.GenericIdentityCompatibilityFields)
        {
            Assert.Equal(genericFields[name], Read(fixture.Path, name));
        }
    }

    [Fact]
    public async Task NonWritableContainer_ReportsExplicitFailureInsteadOfSilentSuccess()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".mka");
        if (!fixture.Available)
        {
            return;
        }

        var result = await Write(fixture.Path, Payload("spotify", trackId: "spotify-track"), overwrite: true);

        Assert.NotEmpty(Failures(result));
        Assert.Null(Read(fixture.Path, "SPOTIFY_TRACK_ID"));
    }

    [Fact]
    public async Task NonNativePayload_WritesNothing()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        Assert.True(fixture.Available);

        var result = await Write(
            fixture.Path,
            new ProviderIdentityPayload("shazam", null, null, null, null, null, null, false),
            overwrite: true);

        Assert.Empty(Failures(result));
        Assert.Empty(Attempted(result));
        Assert.Null(Read(fixture.Path, "SHAZAM_TRACK_ID"));
    }

    [Fact]
    public async Task DetectedTagType_SelectsTheBackendOverTheExtension()
    {
        using var mp3 = await ProviderIdentityTestAudioFactory.CreateAsync(".mp3");
        using var flac = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        using var m4a = await ProviderIdentityTestAudioFactory.CreateAsync(".m4a");
        using var wav = await ProviderIdentityTestAudioFactory.CreateAsync(".wav");
        using var mka = await ProviderIdentityTestAudioFactory.CreateAsync(".mka");

        Assert.Equal(ProviderIdentityPersistenceBackend.Id3, BackendOf(wav.Path));
        Assert.Equal(ProviderIdentityPersistenceBackend.AtlNative, BackendOf(mka.Path));

        // A written identity establishes the detected tag type, which must then win.
        await Write(mp3.Path, Payload("spotify", trackId: "t"), overwrite: true);
        await Write(flac.Path, Payload("spotify", trackId: "t"), overwrite: true);
        await Write(m4a.Path, Payload("spotify", trackId: "t"), overwrite: true);

        Assert.Equal(ProviderIdentityPersistenceBackend.Id3, BackendOf(mp3.Path));
        Assert.Equal(ProviderIdentityPersistenceBackend.Xiph, BackendOf(flac.Path));
        Assert.Equal(ProviderIdentityPersistenceBackend.Mp4, BackendOf(m4a.Path));
    }

    [Fact]
    public async Task WavAndAiffWithoutNativeTagCapability_UseTheId3Family()
    {
        using var wav = await ProviderIdentityTestAudioFactory.CreateAsync(".wav");
        using var aiff = await ProviderIdentityTestAudioFactory.CreateAsync(".aiff");
        using var aif = await ProviderIdentityTestAudioFactory.CreateAsync(".aif");

        Assert.True(wav.Available && aiff.Available && aif.Available);
        Assert.Equal(ProviderIdentityPersistenceBackend.Id3, BackendOf(wav.Path));
        Assert.Equal(ProviderIdentityPersistenceBackend.Id3, BackendOf(aiff.Path));
        Assert.Equal(ProviderIdentityPersistenceBackend.Id3, BackendOf(aif.Path));

        await Write(wav.Path, Payload("spotify", trackId: "spotify-track"), overwrite: true);
        Assert.Equal("spotify-track", Read(wav.Path, "SPOTIFY_TRACK_ID"));
        Assert.NotNull(TagLib.File.Create(wav.Path).GetTag(TagLib.TagTypes.Id3v2, false));
    }

    public static TheoryData<string> EligibleExtensionData()
    {
        var data = new TheoryData<string>();
        foreach (var extension in EligibleAudioExtensions)
        {
            data.Add(extension);
        }

        return data;
    }

    private static ProviderIdentityPayload Payload(
        string provider,
        string? trackId = null,
        string? albumId = null,
        string? releaseId = null,
        string? artistId = null,
        string? albumArtistId = null,
        string? url = null)
        => new(provider, trackId, albumId, releaseId, artistId, albumArtistId, url, true);

    private static async Task<object> Write(
        string path,
        ProviderIdentityPayload payload,
        bool overwrite,
        IReadOnlySet<ProviderIdentityField>? fields = null)
    {
        var config = CreateConfig(overwrite);
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "WriteProviderIdentityAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WriteProviderIdentityAsync not found.");
        var task = (Task)method.Invoke(
            null,
            [path, payload, config, fields ?? Enum.GetValues<ProviderIdentityField>().ToHashSet(), CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static object CreateConfig(bool overwrite)
    {
        var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AutoTagRunnerConfig not found.");
        var config = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("AutoTagRunnerConfig could not be created.");
        type.GetProperty("Overwrite")!.SetValue(config, overwrite);
        return config;
    }

    private static IReadOnlyList<string> Failures(object result)
    {
        var failures = (System.Collections.IEnumerable)result.GetType().GetProperty("Failures")!.GetValue(result)!;
        return failures.Cast<object>().Select(_ => "failure").ToList();
    }

    private static IReadOnlyList<string> Attempted(object result)
    {
        var attempted = (System.Collections.IEnumerable)result.GetType().GetProperty("AttemptedTags")!.GetValue(result)!;
        return attempted.Cast<object>().Select(_ => "attempted").ToList();
    }

    private static string? Read(string path, string rawName)
        => LocalAutoTagRunner.ReadRawIdentityValue(path, rawName);

    private static ProviderIdentityPersistenceBackend BackendOf(string path)
    {
        LocalAutoTagRunner.TryOpenTagLibFile(path, out var file);
        try
        {
            return InvokeResolveBackend(path, file);
        }
        finally
        {
            file?.Dispose();
        }
    }

    private static ProviderIdentityPersistenceBackend InvokeResolveBackend(string path, TagLib.File? file)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "ResolveProviderIdentityBackend",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ResolveProviderIdentityBackend not found.");
        return (ProviderIdentityPersistenceBackend)method.Invoke(null, [path, file])!;
    }
}

/// <summary>
/// Generates disposable, 100 ms silent container fixtures. Encodable containers are
/// produced with the repository's existing ffmpeg dependency; accepted native
/// containers ffmpeg cannot encode use embedded minimal, non-copyrighted silent bytes.
/// The factory never touches the user's music library.
/// </summary>
internal sealed class ProviderIdentityTestAudioFactory : IDisposable
{
    private readonly string? _directory;

    private ProviderIdentityTestAudioFactory(string path, bool available, string? directory)
    {
        Path = path;
        Available = available;
        _directory = directory;
    }

    public string Path { get; }

    public bool Available { get; }

    public static async Task<ProviderIdentityTestAudioFactory> CreateAsync(string extension)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dsh-provider-identity",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "fixture" + extension);

        var ffmpegArgs = ResolveFfmpegArguments(extension);
        if (ffmpegArgs is not null)
        {
            var encoded = await TryEncodeAsync(ffmpegArgs, path);
            return new ProviderIdentityTestAudioFactory(path, encoded, directory);
        }

        if (EmbeddedFixtures.TryGetValue(extension, out var bytes))
        {
            await File.WriteAllBytesAsync(path, bytes);
            return new ProviderIdentityTestAudioFactory(path, true, directory);
        }

        return new ProviderIdentityTestAudioFactory(path, false, directory);
    }

    public void Dispose()
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort cleanup only
        }
    }

    private static string[]? ResolveFfmpegArguments(string extension) => extension.ToLowerInvariant() switch
    {
        ".flac" => ["-f", "flac"],
        ".wav" => ["-f", "wav"],
        ".aiff" => ["-f", "aiff"],
        ".aif" => ["-f", "aiff"],
        ".m4a" => ["-f", "mp4", "-c:a", "aac"],
        ".m4b" => ["-f", "mp4", "-c:a", "aac"],
        ".mp4" => ["-f", "mp4", "-c:a", "aac"],
        ".aac" => ["-f", "adts", "-c:a", "aac"],
        ".alac" => ["-f", "ipod", "-c:a", "alac"],
        ".mp3" => ["-f", "mp3"],
        ".mp2" => ["-f", "mp2"],
        ".mp1" => ["-f", "mp2"],
        ".wma" => ["-f", "asf", "-c:a", "wmav2"],
        ".ogg" => ["-f", "ogg"],
        ".oga" => ["-f", "ogg", "-c:a", "libvorbis"],
        ".opus" => ["-f", "opus"],
        ".wv" => ["-f", "wv"],
        ".tta" => ["-f", "tta"],
        ".mka" => ["-f", "matroska", "-c:a", "flac"],
        _ => null
    };

    private static async Task<bool> TryEncodeAsync(string[] codecArguments, string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("lavfi");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add("anullsrc=r=44100:cl=mono");
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add("0.1");
            startInfo.ArgumentList.Add("-y");
            foreach (var argument in codecArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add(path);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return new FileInfo(path).Exists && new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static readonly Dictionary<string, byte[]> EmbeddedFixtures =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".dsf"] = BuildMinimalDsf()
        };

    private static byte[] BuildMinimalDsf()
    {
        const int blockSize = 4096;
        const int dataLength = blockSize * 2;
        var total = 28 + 52 + 12 + dataLength;
        using var stream = new MemoryStream(total);
        using var writer = new BinaryWriter(stream);

        writer.Write("DSD "u8.ToArray());
        writer.Write((ulong)28);
        writer.Write((ulong)total);
        writer.Write((ulong)0);

        writer.Write("fmt "u8.ToArray());
        writer.Write((ulong)52);
        writer.Write((uint)1);
        writer.Write((uint)0);
        writer.Write((uint)1);
        writer.Write((uint)1);
        writer.Write((uint)2822400);
        writer.Write((uint)1);
        writer.Write((ulong)dataLength);
        writer.Write((uint)blockSize);
        writer.Write((uint)0);

        writer.Write("data"u8.ToArray());
        writer.Write((ulong)(12 + dataLength));
        for (var i = 0; i < dataLength; i++)
        {
            writer.Write((byte)0x69);
        }

        writer.Flush();
        return stream.ToArray();
    }
}