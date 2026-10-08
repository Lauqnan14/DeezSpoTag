using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Append-only, write-once/read-once log of per-artist run outcomes, stored as newline-delimited
/// JSON beside the automation state file.
///
/// The outcome list grows monotonically during a run and is discarded when the run ends, so it is a
/// log rather than a document. Re-serialising the whole list after every artist was quadratic: at
/// 3768 artists that rewrote roughly 6 GiB per run. Appending one line per artist writes the same
/// data once.
///
/// Durability model: each record is flushed to the OS on append, and losing trailing records is
/// safe. A resumed run treats a missing record as "not finished" and simply reprocesses that artist,
/// which is at-least-once and still reconciles the reported totals. The first line is a header
/// carrying the run id, so a journal left over from an abandoned run is discarded rather than being
/// merged into an unrelated one.
/// </summary>
internal sealed class ArtistMetadataOutcomeJournal : IAsyncDisposable
{
    private const string HeaderKind = "header";
    private const string RecordKind = "record";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly string _runId;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private FileStream? _stream;

    private ArtistMetadataOutcomeJournal(string path, string runId, FileStream stream, int recoveredCount)
    {
        _path = path;
        _runId = runId;
        _stream = stream;
        RecoveredCount = recoveredCount;
    }

    public string RunId => _runId;

    /// <summary>Records recovered from the journal when it was opened, excluding any seed records.</summary>
    public int RecoveredCount { get; }

    /// <summary>
    /// Opens the journal for appending. When <paramref name="truncate"/> is set the file is reset and a
    /// fresh header written, which is what starting a new run requires.
    /// </summary>
    public static async Task<ArtistMetadataOutcomeJournal> OpenAsync(
        string path,
        string runId,
        bool truncate,
        IReadOnlyList<ArtistRunOutcomeRecord>? seed,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        if (truncate && existed)
        {
            File.Delete(path);
        }

        var recovered = !truncate && existed ? Read(path, runId).Count : 0;

        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        var seedCount = seed?.Count ?? 0;
        var journal = new ArtistMetadataOutcomeJournal(
            path,
            runId,
            stream,
            Math.Max(recovered, seedCount));

        // The header must exist before any record, otherwise the first record would be mistaken for
        // one. FileMode.Append creates the file, so existence has to be checked before opening it.
        if (truncate || !existed)
        {
            await journal.WriteLineAsync(
                $"{{\"kind\":\"{HeaderKind}\",\"runId\":{JsonSerializer.Serialize(runId, JsonOptions)}}}",
                cancellationToken);
        }

        if (seed is { Count: > 0 })
        {
            foreach (var record in seed)
            {
                await journal.AppendAsync(record, cancellationToken);
            }
        }

        return journal;
    }

    /// <summary>
    /// Appends one outcome. Ordered against other appends, so records land in the order the artists
    /// were processed.
    /// </summary>
    public async Task AppendAsync(ArtistRunOutcomeRecord record, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteLineAsync(SerializeRecord(record), cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Pushes any buffered bytes to the OS. Called on shutdown so the tail is not lost.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_stream is not null)
            {
                await _stream.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Reads every record belonging to <paramref name="runId"/>. A line that does not parse is skipped
    /// rather than failing the resume, which is what makes a torn final append harmless.
    /// </summary>
    public static IReadOnlyList<ArtistRunOutcomeRecord> Read(string path, string runId)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<ArtistRunOutcomeRecord>();
        }

        var records = new List<ArtistRunOutcomeRecord>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var headerSeen = false;
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!headerSeen)
                {
                    headerSeen = true;
                    if (!HeaderMatches(line, runId))
                    {
                        // A journal from a different run must not be merged into this one.
                        return Array.Empty<ArtistRunOutcomeRecord>();
                    }

                    continue;
                }

                if (TryDeserializeRecord(line, out var record))
                {
                    records.Add(record);
                }
            }
        }
        catch (IOException)
        {
            // A partially written journal still yields whatever records were readable.
        }
        catch (UnauthorizedAccessException)
        {
            return records;
        }

        return records;
    }

    public void Delete()
    {
        try
        {
            _stream?.Dispose();
            _stream = null;
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Best effort; a stale journal is discarded on the next run by the run id header.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            _stream?.Dispose();
            _stream = null;
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _stream.WriteAsync(bytes, cancellationToken);
        // Flushed per record so a hard stop loses at most the record being written.
        await _stream.FlushAsync(cancellationToken);
    }

    private static string SerializeRecord(ArtistRunOutcomeRecord record)
        => $"{{\"kind\":\"{RecordKind}\",\"record\":{JsonSerializer.Serialize(record, JsonOptions)}}}";

    private static bool HeaderMatches(string line, string runId)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.TryGetProperty("kind", out var kind)
                && kind.GetString() == HeaderKind
                && root.TryGetProperty("runId", out var id)
                && string.Equals(id.GetString(), runId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryDeserializeRecord(string line, out ArtistRunOutcomeRecord record)
    {
        record = null!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != RecordKind)
            {
                return false;
            }

            if (!root.TryGetProperty("record", out var payload))
            {
                return false;
            }

            record = payload.Deserialize<ArtistRunOutcomeRecord>(JsonOptions)
                ?? throw new JsonException("Empty outcome record.");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
