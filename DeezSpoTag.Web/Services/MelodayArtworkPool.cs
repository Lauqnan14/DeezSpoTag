using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

/// <summary>One persistent artwork assignment: a playlist instance owns one pool image.</summary>
public sealed record MelodayArtworkAssignment(
    long LibraryId,
    string SlotId,
    string Mode,
    string ImageId);

/// <summary>
/// Discovers every valid image in the Meloday source pool (no hard-coded limit) and
/// provides the deterministic shuffled deck used for allocation.
/// </summary>
public sealed class MelodayArtworkPool
{
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private const int DeckSeed = 0x4D454C4F; // "MELO"

    private readonly string _sourceDirectory;
    private readonly ILogger<MelodayArtworkPool> _logger;

    public MelodayArtworkPool(IWebHostEnvironment env, ILogger<MelodayArtworkPool> logger)
    {
        _logger = logger;
        _sourceDirectory = Path.Join(env.WebRootPath, "images", "meloday", "source");
    }

    public string SourceDirectory => _sourceDirectory;

    public static bool IsAllowedExtension(string fileName)
        => AllowedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Pool images sorted by file name; empty when the directory is missing.</summary>
    public IReadOnlyList<string> ListImages()
    {
        if (!Directory.Exists(_sourceDirectory))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(_sourceDirectory)
                .Where(path => IsAllowedExtension(path))
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to enumerate Meloday artwork pool at {Path}.", _sourceDirectory);
            return Array.Empty<string>();
        }
    }

    /// <summary>Deterministic shuffled deck: the same pool always yields the same order, so
    /// allocations stay stable across restarts and pool growth only ever adds unused images.</summary>
    public IReadOnlyList<string> ShuffledDeck()
        => BuildDeck(ListImages());

    internal static IReadOnlyList<string> BuildDeck(IReadOnlyList<string> images)
    {
        var deck = images.ToList();
        if (deck.Count < 2)
        {
            return deck;
        }

        var random = new Random(DeckSeed);
        for (var index = deck.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (deck[index], deck[swap]) = (deck[swap], deck[index]);
        }

        return deck;
    }

    public string ResolveSourcePath(string imageId)
        => Path.Join(_sourceDirectory, Path.GetFileName(imageId));
}

/// <summary>
/// Pure allocation rules. A pool image is picked for a playlist instance in this order:
/// keep an existing valid assignment; otherwise take the first globally unused image in
/// deck order; otherwise the first image unused within this library; otherwise start a
/// new cycle from the earliest deck position this library already holds.
/// </summary>
public static class MelodayArtworkAllocator
{
    public static string? Allocate(
        IReadOnlyList<string> deck,
        IReadOnlyList<MelodayArtworkAssignment> assignments,
        long libraryId,
        string slotId,
        string mode)
    {
        if (deck.Count == 0)
        {
            return null;
        }

        var slotKey = MelodayScheduleSlots.NormalizeSlotId(slotId);
        var modeKey = MelodayModes.Normalize(mode);
        var own = assignments.FirstOrDefault(assignment =>
            assignment.LibraryId == libraryId
            && string.Equals(assignment.SlotId, slotKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(assignment.Mode, modeKey, StringComparison.OrdinalIgnoreCase)
            && deck.Contains(assignment.ImageId, StringComparer.OrdinalIgnoreCase));
        if (own is not null)
        {
            return own.ImageId;
        }

        var assignedGlobal = assignments
            .Where(assignment => deck.Contains(assignment.ImageId, StringComparer.OrdinalIgnoreCase))
            .Select(assignment => assignment.ImageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unusedGlobal = deck
            .Where(image => !assignedGlobal.Contains(image))
            .ToList();
        if (unusedGlobal.Count > 0)
        {
            return unusedGlobal[0];
        }

        var assignedLibrary = assignments
            .Where(assignment => assignment.LibraryId == libraryId
                && deck.Contains(assignment.ImageId, StringComparer.OrdinalIgnoreCase))
            .Select(assignment => assignment.ImageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unusedWithinLibrary = deck
            .Where(image => !assignedLibrary.Contains(image))
            .ToList();
        if (unusedWithinLibrary.Count > 0)
        {
            return unusedWithinLibrary[0];
        }

        // Every image is in use globally; restart a cycle inside this library from the
        // earliest deck position the library already holds.
        var deckPosition = deck
            .Select((image, index) => (Image: image, Index: index))
            .GroupBy(entry => entry.Image, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Min(entry => entry.Index), StringComparer.OrdinalIgnoreCase);
        return assignedLibrary
            .OrderBy(image => deckPosition.TryGetValue(image, out var position) ? position : int.MaxValue)
            .FirstOrDefault()
            ?? deck[0];
    }
}

/// <summary>Persistent artwork assignment store backing the allocator (meloday/artwork-assignments.json).</summary>
public sealed class MelodayArtworkAssignments
{
    private sealed record AssignmentFile(
        [property: JsonPropertyName("assignments")] List<MelodayArtworkAssignment>? Assignments);

    private readonly string _storePath;
    private readonly MelodayArtworkPool _pool;
    private readonly ILogger<MelodayArtworkAssignments> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public MelodayArtworkAssignments(IWebHostEnvironment env, MelodayArtworkPool pool, ILogger<MelodayArtworkAssignments> logger)
    {
        _logger = logger;
        _pool = pool;
        var dataDir = Path.Join(AppDataPaths.GetDataRoot(env), "meloday");
        Directory.CreateDirectory(dataDir);
        _storePath = Path.Join(dataDir, "artwork-assignments.json");
    }

    public async Task<IReadOnlyList<MelodayArtworkAssignment>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken)).ToList();
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Returns the pool image assigned to this playlist instance, allocating and persisting one when needed.</summary>
    public async Task<string?> AssignAsync(long libraryId, string slotId, string mode, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var deck = _pool.ShuffledDeck();
            if (deck.Count == 0)
            {
                return null;
            }

            var assignments = await LoadAsync(cancellationToken);
            // Assignments pointing at deleted images are dropped; those playlists reallocate.
            assignments.RemoveAll(assignment => !deck.Contains(assignment.ImageId, StringComparer.OrdinalIgnoreCase));

            var imageId = MelodayArtworkAllocator.Allocate(deck, assignments, libraryId, slotId, mode);
            if (imageId is null)
            {
                return null;
            }

            if (!assignments.Any(assignment => assignment.LibraryId == libraryId
                && string.Equals(assignment.SlotId, MelodayScheduleSlots.NormalizeSlotId(slotId), StringComparison.OrdinalIgnoreCase)
                && string.Equals(assignment.Mode, MelodayModes.Normalize(mode), StringComparison.OrdinalIgnoreCase)))
            {
                assignments.Add(new MelodayArtworkAssignment(libraryId, MelodayScheduleSlots.NormalizeSlotId(slotId), MelodayModes.Normalize(mode), imageId));
                var json = JsonSerializer.Serialize(new AssignmentFile(assignments), _jsonOptions);
                await File.WriteAllTextAsync(_storePath, json, cancellationToken);
            }

            return imageId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist Meloday artwork assignment to {Path}.", _storePath);
            return _pool.ShuffledDeck().FirstOrDefault();
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<List<MelodayArtworkAssignment>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return new List<MelodayArtworkAssignment>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_storePath, cancellationToken);
            return JsonSerializer.Deserialize<AssignmentFile>(json, _jsonOptions)?.Assignments
                   ?? new List<MelodayArtworkAssignment>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read Meloday artwork assignments from {Path}.", _storePath);
            return new List<MelodayArtworkAssignment>();
        }
    }
}
