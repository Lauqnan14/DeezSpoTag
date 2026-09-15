using DeezSpoTag.Services.Download.Shared.Utils;
using TagLib;
using IOFile = System.IO.File;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// The persistence mechanism that actually owns a file's provider identity tags. It is
/// selected from the detected tag/container type (never from the run type), and the
/// same backend performs write, cleanup, presence checks, and read-back verification.
/// </summary>
internal enum ProviderIdentityPersistenceBackend
{
    Id3,
    Xiph,
    Mp4,
    AtlNative
}

public sealed partial class LocalAutoTagRunner : IAutoTagRunner
{
    private sealed record ProviderIdentityWriteResult(
        HashSet<SupportedTag> AttemptedTags,
        IReadOnlyList<ProviderIdentityPersistenceFailure> Failures);

    private sealed record ProviderIdentityPersistenceFailure(
        string Format,
        string Provider,
        ProviderIdentityField Field,
        string Reason);

    /// <summary>The generic compatibility fields no provider AutoTag pass may write,
    /// clear, or overwrite. They are also excluded from every cleanup family.</summary>
    internal static readonly string[] GenericIdentityCompatibilityFields =
        ["ALBUMID", "ARTISTID", "ALBUMARTISTID", "RECORDINGID", "URL", "WWWAUDIOFILE"];

    private static readonly ProviderIdentityField[] ProviderIdentityFieldOrder =
    [
        ProviderIdentityField.TrackId,
        ProviderIdentityField.AlbumId,
        ProviderIdentityField.ReleaseId,
        ProviderIdentityField.ArtistId,
        ProviderIdentityField.AlbumArtistId,
        ProviderIdentityField.Url
    ];

    /// <summary>
    /// Resolves the one backend that owns this file's provider identity. The detected
    /// tag type on disk wins; the extension is used only to create a missing native tag
    /// of a known family, and every remaining accepted format routes to ATL native
    /// additional fields.
    /// </summary>
    private static ProviderIdentityPersistenceBackend ResolveProviderIdentityBackend(string path, TagLib.File? file)
    {
        if (file is not null)
        {
            var types = file.TagTypesOnDisk;
            if ((types & TagTypes.Id3v2) != 0)
            {
                return ProviderIdentityPersistenceBackend.Id3;
            }

            if ((types & TagTypes.Xiph) != 0)
            {
                return ProviderIdentityPersistenceBackend.Xiph;
            }

            if ((types & TagTypes.Apple) != 0)
            {
                return ProviderIdentityPersistenceBackend.Mp4;
            }
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" or ".mp2" or ".mp1" or ".wav" or ".aiff" or ".aif" => ProviderIdentityPersistenceBackend.Id3,
            ".flac" or ".ogg" or ".oga" or ".opus" => ProviderIdentityPersistenceBackend.Xiph,
            ".m4a" or ".m4b" or ".mp4" => ProviderIdentityPersistenceBackend.Mp4,
            _ => ProviderIdentityPersistenceBackend.AtlNative
        };
    }

    internal static bool TryOpenTagLibFile(string filePath, out TagLib.File? file)
    {
        try
        {
            file = TagLib.File.Create(filePath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            file = null;
            return false;
        }
    }

    /// <summary>
    /// Reads one raw identity value through the backend that owns the file. Returns null
    /// when the value is absent, blank, or the container cannot be read.
    /// </summary>
    internal static string? ReadRawIdentityValue(string filePath, string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        TryOpenTagLibFile(filePath, out var file);
        try
        {
            var backend = ResolveProviderIdentityBackend(filePath, file);
            using var session = ProviderIdentityTagSession.Open(filePath, backend, file);
            return session.Read(rawName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            file?.Dispose();
        }
    }

    internal static bool HasRawIdentityValue(string filePath, string rawName)
        => !string.IsNullOrWhiteSpace(ReadRawIdentityValue(filePath, rawName));

    /// <summary>Snapshots the six generic compatibility fields so a provider pass can
    /// prove it neither created nor changed them.</summary>
    internal static Dictionary<string, string?> CaptureGenericIdentityCompatibilityFields(string filePath)
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in GenericIdentityCompatibilityFields)
        {
            snapshot[name] = ReadRawIdentityValue(filePath, name);
        }

        return snapshot;
    }

    /// <summary>
    /// The single provider identity writer. For every authoritative non-empty field it
    /// resolves the exact provider+field family, cleans only that family when overwrite
    /// is enabled for its supported tag, writes the canonical names, and verifies the
    /// value reads back. Missing values and non-native payloads do nothing.
    /// </summary>
    private static Task<ProviderIdentityWriteResult> WriteProviderIdentityAsync(
        string filePath,
        ProviderIdentityPayload? payload,
        AutoTagRunnerConfig config,
        IReadOnlySet<ProviderIdentityField> enabledFields,
        CancellationToken token)
    {
        var attempted = new HashSet<SupportedTag>();
        var failures = new List<ProviderIdentityPersistenceFailure>();
        token.ThrowIfCancellationRequested();

        if (payload is null
            || !payload.IsNativeProviderResult
            || !payload.HasAnyValue
            || enabledFields.Count == 0
            || string.IsNullOrWhiteSpace(filePath)
            || !IOFile.Exists(filePath))
        {
            return Task.FromResult(new ProviderIdentityWriteResult(attempted, failures));
        }

        var format = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var genericBefore = CaptureGenericIdentityCompatibilityFields(filePath);

        TryOpenTagLibFile(filePath, out var file);
        var backend = ResolveProviderIdentityBackend(filePath, file);
        var written = new List<(ProviderIdentityField Field, string Value)>();

        try
        {
            using var session = ProviderIdentityTagSession.Open(filePath, backend, file);
            foreach (var field in ProviderIdentityFieldOrder)
            {
                if (!enabledFields.Contains(field))
                {
                    continue;
                }

                var value = payload.ValueFor(field);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var family = AutoTagIdentityTags.ResolveFamily(payload.ProviderId, field);
                try
                {
                    if (ShouldOverwriteTag(config, family.SupportedTag))
                    {
                        foreach (var alias in family.CleanupNames)
                        {
                            session.Remove(alias);
                        }
                    }
                    else if (family.CleanupNames.Any(session.Has))
                    {
                        // A missing authoritative value (or a retained existing one) leaves
                        // the aliases untouched — never delete what we are not overwriting.
                        continue;
                    }

                    var persisted = true;
                    foreach (var writeName in family.WriteNames)
                    {
                        persisted &= session.Write(writeName, value.Trim());
                    }

                    if (!persisted)
                    {
                        failures.Add(new ProviderIdentityPersistenceFailure(
                            format,
                            payload.ProviderId,
                            field,
                            "the container cannot persist the provider identity field"));
                        continue;
                    }

                    written.Add((field, value.Trim()));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(new ProviderIdentityPersistenceFailure(
                        format,
                        payload.ProviderId,
                        field,
                        ex.GetType().Name + ": " + ex.Message));
                }
            }

            session.Save();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(new ProviderIdentityPersistenceFailure(
                format,
                payload.ProviderId,
                ProviderIdentityField.TrackId,
                ex.GetType().Name + ": " + ex.Message));
            written.Clear();
        }
        finally
        {
            file?.Dispose();
        }

        foreach (var (field, value) in written)
        {
            var family = AutoTagIdentityTags.ResolveFamily(payload.ProviderId, field);
            var readBack = family.WriteNames
                .Select(name => ReadRawIdentityValue(filePath, name))
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            if (!string.Equals(readBack, value, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(new ProviderIdentityPersistenceFailure(
                    format,
                    payload.ProviderId,
                    field,
                    "the written provider identity could not be read back from the container"));
                continue;
            }

            attempted.Add(family.SupportedTag);
        }

        var genericAfter = CaptureGenericIdentityCompatibilityFields(filePath);
        foreach (var name in GenericIdentityCompatibilityFields)
        {
            if (!string.Equals(genericBefore.GetValueOrDefault(name), genericAfter.GetValueOrDefault(name), StringComparison.Ordinal))
            {
                failures.Add(new ProviderIdentityPersistenceFailure(
                    format,
                    payload.ProviderId,
                    ProviderIdentityField.Url,
                    $"generic compatibility field {name} changed during the provider identity pass"));
            }
        }

        return Task.FromResult(new ProviderIdentityWriteResult(attempted, failures));
    }

    /// <summary>
    /// One open tag handle for a provider identity pass. Every backend implements the
    /// same presence/read/write/remove/save operations so they cannot disagree.
    /// </summary>
    private sealed class ProviderIdentityTagSession : IDisposable
    {
        private readonly ProviderIdentityPersistenceBackend _backend;
        private readonly TagLib.File? _file;
        private readonly ATL.Track? _atl;

        private ProviderIdentityTagSession(
            ProviderIdentityPersistenceBackend backend,
            TagLib.File? file,
            ATL.Track? atl)
        {
            _backend = backend;
            _file = file;
            _atl = atl;
        }

        public static ProviderIdentityTagSession Open(
            string filePath,
            ProviderIdentityPersistenceBackend backend,
            TagLib.File? file)
        {
            if (backend == ProviderIdentityPersistenceBackend.AtlNative || file is null)
            {
                return new ProviderIdentityTagSession(
                    ProviderIdentityPersistenceBackend.AtlNative,
                    null,
                    AtlTagHelper.OpenNativeTrack(filePath));
            }

            return new ProviderIdentityTagSession(backend, file, null);
        }

        public bool Has(string name) => Read(name) is not null;

        public string? Read(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            switch (_backend)
            {
                case ProviderIdentityPersistenceBackend.Id3:
                {
                    var id3 = Id3Tag(create: false);
                    if (id3 is null)
                    {
                        return null;
                    }

                    var values = name.Length == 4
                        ? TagLib.Id3v2.TextInformationFrame.Get(id3, name, false)?.Text
                        : TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, false)?.Text;
                    return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
                }
                case ProviderIdentityPersistenceBackend.Xiph:
                {
                    var xiph = XiphTag(create: false);
                    return xiph?.GetField(name).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
                }
                case ProviderIdentityPersistenceBackend.Mp4:
                {
                    var apple = AppleTag(create: false);
                    return apple is null
                        ? null
                        : AppleDashBoxReflectionHelper
                            .ReadValues(apple, Mp4RawTagNameNormalizer.Normalize(name))
                            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
                }
                default:
                    return AtlTagHelper.ReadNativeField(_atl!, name);
            }
        }

        public bool Write(string name, string value)
        {
            switch (_backend)
            {
                case ProviderIdentityPersistenceBackend.Id3:
                {
                    var id3 = Id3Tag(create: true)!;
                    if (name.Length == 4)
                    {
                        TagLib.Id3v2.TextInformationFrame.Get(id3, name, true).Text = [value];
                    }
                    else
                    {
                        TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, true).Text = [value];
                    }

                    return true;
                }
                case ProviderIdentityPersistenceBackend.Xiph:
                {
                    var xiph = XiphTag(create: true)!;
                    xiph.SetField(name, [value]);
                    return true;
                }
                case ProviderIdentityPersistenceBackend.Mp4:
                {
                    var apple = AppleTag(create: true);
                    return apple is not null
                        && AppleDashBoxReflectionHelper.TrySetValues(
                            apple,
                            Mp4RawTagNameNormalizer.Normalize(name),
                            [value]);
                }
                default:
                    return AtlTagHelper.SetNativeField(_atl!, name, value);
            }
        }

        public void Remove(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            switch (_backend)
            {
                case ProviderIdentityPersistenceBackend.Id3:
                {
                    var id3 = Id3Tag(create: false);
                    if (id3 is null)
                    {
                        return;
                    }

                    if (name.Length == 4)
                    {
                        id3.RemoveFrames(name);
                        return;
                    }

                    foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>("TXXX")
                                 .Where(frame => string.Equals(frame.Description, name, StringComparison.OrdinalIgnoreCase))
                                 .ToList())
                    {
                        id3.RemoveFrame(frame);
                    }

                    return;
                }
                case ProviderIdentityPersistenceBackend.Xiph:
                    XiphTag(create: false)?.RemoveField(name);
                    return;
                case ProviderIdentityPersistenceBackend.Mp4:
                    AppleDashBoxReflectionHelper.TryClearValues(
                        AppleTag(create: false),
                        Mp4RawTagNameNormalizer.Normalize(name));
                    return;
                default:
                    AtlTagHelper.RemoveNativeField(_atl!, name);
                    return;
            }
        }

        public void Save()
        {
            if (_backend == ProviderIdentityPersistenceBackend.AtlNative)
            {
                AtlTagHelper.SaveNative(_atl!);
                return;
            }

            _file!.Save();
        }

        public void Dispose() => _file?.Dispose();

        private TagLib.Id3v2.Tag? Id3Tag(bool create)
            => (TagLib.Id3v2.Tag?)_file!.GetTag(TagTypes.Id3v2, create);

        private TagLib.Ogg.XiphComment? XiphTag(bool create)
            => (TagLib.Ogg.XiphComment?)_file!.GetTag(TagTypes.Xiph, create);

        private TagLib.Mpeg4.AppleTag? AppleTag(bool create)
            => (TagLib.Mpeg4.AppleTag?)_file!.GetTag(TagTypes.Apple, create);
    }
}