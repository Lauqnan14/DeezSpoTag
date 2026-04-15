using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Collections.Concurrent;
using System.IO.Compression;

namespace DeezSpoTag.Services.Download.Shared.Advanced;

/// <summary>
/// Enhanced queue persistence service with compression, backup, and recovery
/// Ported from: Advanced queue persistence logic in deezspotag with reliability improvements
/// </summary>
public class EnhancedQueuePersistenceService : IDisposable
{
    private readonly ILogger<EnhancedQueuePersistenceService> _logger;
    private readonly string _queueDirectory;
    private readonly string _backupDirectory;
    private readonly string _orderFilePath;
    private readonly string _metadataFilePath;
    private readonly Timer _backupTimer;
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _persistenceSemaphore = new(1, 1);
    private bool _disposed;

    private readonly ConcurrentDictionary<string, QueueItemMetadata> _itemMetadata = new();
    private readonly JsonSerializerOptions _jsonOptions;

    private const int MaxBackupFiles = 10;
    private const int MaxRetries = 3;
    private const long MaxRestoreExtractBytes = 256L * 1024 * 1024;
    private readonly TimeSpan _backupInterval = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public EnhancedQueuePersistenceService(
        ILogger<EnhancedQueuePersistenceService> logger,
        string configFolder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _queueDirectory = Path.Join(configFolder, "queue");
        _backupDirectory = Path.Join(configFolder, "queue_backups");
        _orderFilePath = Path.Join(_queueDirectory, "order.json");
        _metadataFilePath = Path.Join(_queueDirectory, "metadata.json");

        // Ensure directories exist
        Directory.CreateDirectory(_queueDirectory);
        Directory.CreateDirectory(_backupDirectory);

        // Configure JSON serialization
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Start background timers
        _backupTimer = new Timer(CreateBackup, null, _backupInterval, _backupInterval);
        _cleanupTimer = new Timer(CleanupOldFiles, null, _cleanupInterval, _cleanupInterval);

        // Load existing metadata
        LoadMetadata();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Enhanced queue persistence service initialized with directory: {QueueDirectory}", _queueDirectory);        }
    }

    /// <summary>
    /// Save queue order with atomic write and backup
    /// </summary>
    public async Task SaveQueueOrderAsync(List<string> queueOrder)
    {
        await _persistenceSemaphore.WaitAsync();
        try
        {
            await SaveWithRetriesAsync(_orderFilePath, queueOrder, "queue order");
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Saved queue order with {Count} items", queueOrder.Count);            }
        }
        finally
        {
            _persistenceSemaphore.Release();
        }
    }

    /// <summary>
    /// Load queue order with corruption recovery
    /// </summary>
    public async Task<List<string>> LoadQueueOrderAsync()
    {
        try
        {
            if (!System.IO.File.Exists(_orderFilePath))
            {
                _logger.LogDebug("Queue order file not found, returning empty list");
                return new List<string>();
            }

            var content = await File.ReadAllTextAsync(_orderFilePath);
            var queueOrder = JsonSerializer.Deserialize<List<string>>(content, _jsonOptions) ?? new List<string>();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Loaded queue order with {Count} items", queueOrder.Count);            }
            return queueOrder;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error loading queue order, attempting recovery");
            return await RecoverQueueOrderAsync();
        }
    }

    /// <summary>
    /// Save queue item with compression and metadata
    /// </summary>
    public async Task SaveQueueItemAsync(DeezSpoTagDownloadObject downloadObject, string status = "inQueue")
    {
        await _persistenceSemaphore.WaitAsync();
        try
        {
            var itemPath = Path.Join(_queueDirectory, $"{downloadObject.UUID}.json");
            var compressedPath = Path.Join(_queueDirectory, $"{downloadObject.UUID}.json.gz");

            // Create item data with status
            var itemData = downloadObject.ToDict();
            itemData["status"] = status;
            itemData["savedAt"] = DateTime.UtcNow;

            // Save both compressed and uncompressed versions
            await SaveWithRetriesAsync(itemPath, itemData, $"queue item {downloadObject.UUID}");
            await SaveCompressedAsync(compressedPath, itemData);

            // Update metadata
            _itemMetadata[downloadObject.UUID] = new QueueItemMetadata
            {
                UUID = downloadObject.UUID,
                Type = downloadObject.Type,
                Title = downloadObject.Title,
                Status = status,
                CreatedAt = DateTime.UtcNow,
                LastModified = DateTime.UtcNow,
                Size = downloadObject.Size,
                FileSize = new FileInfo(itemPath).Length
            };

            await SaveMetadataAsync();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Saved queue item: {UUID} - {Title} ({Status})",
                    downloadObject.UUID, downloadObject.Title, status);            }
        }
        finally
        {
            _persistenceSemaphore.Release();
        }
    }

    /// <summary>
    /// Load queue item with fallback to compressed version
    /// </summary>
    public async Task<Dictionary<string, object>?> LoadQueueItemAsync(string uuid)
    {
        try
        {
            var itemPath = Path.Join(_queueDirectory, $"{uuid}.json");
            var compressedPath = Path.Join(_queueDirectory, $"{uuid}.json.gz");

            var uncompressedItem = await TryLoadUncompressedQueueItemAsync(uuid, itemPath);
            if (uncompressedItem != null)
            {
                return uncompressedItem;
            }

            var compressedItem = await TryLoadCompressedQueueItemAsync(uuid, compressedPath);
            if (compressedItem != null)
            {
                return compressedItem;
            }

            _logger.LogWarning("Queue item not found: {UUID}", uuid);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error loading queue item: {UUID}", uuid);
            return null;
        }
    }

    private async Task<Dictionary<string, object>?> TryLoadUncompressedQueueItemAsync(string uuid, string itemPath)
    {
        if (!System.IO.File.Exists(itemPath))
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(itemPath);
            var item = JsonSerializer.Deserialize<Dictionary<string, object>>(content, _jsonOptions);
            if (item == null)
            {
                return null;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Loaded queue item from uncompressed file: {UUID}", uuid);
            }

            return item;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load uncompressed queue item {UUID}, trying compressed", uuid);
            return null;
        }
    }

    private async Task<Dictionary<string, object>?> TryLoadCompressedQueueItemAsync(string uuid, string compressedPath)
    {
        if (!System.IO.File.Exists(compressedPath))
        {
            return null;
        }

        try
        {
            var item = await LoadCompressedAsync<Dictionary<string, object>>(compressedPath);
            if (item == null)
            {
                return null;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Loaded queue item from compressed file: {UUID}", uuid);
            }

            return item;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load compressed queue item {UUID}", uuid);
            return null;
        }
    }

    /// <summary>
    /// Delete queue item and its metadata
    /// </summary>
    public async Task DeleteQueueItemAsync(string uuid)
    {
        await _persistenceSemaphore.WaitAsync();
        try
        {
            var itemPath = Path.Join(_queueDirectory, $"{uuid}.json");
            var compressedPath = Path.Join(_queueDirectory, $"{uuid}.json.gz");

            // Delete both versions
            if (System.IO.File.Exists(itemPath))
            {
                File.Delete(itemPath);
            }

            if (System.IO.File.Exists(compressedPath))
            {
                File.Delete(compressedPath);
            }

            // Remove from metadata
            _itemMetadata.TryRemove(uuid, out _);
            await SaveMetadataAsync();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Deleted queue item: {UUID}", uuid);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error deleting queue item: {UUID}", uuid);
        }
        finally
        {
            _persistenceSemaphore.Release();
        }
    }

    /// <summary>
    /// Get queue statistics
    /// </summary>
    public QueueStatistics GetStatistics()
    {
        try
        {
            var stats = new QueueStatistics();

            // Count files in queue directory
            var files = Directory.GetFiles(_queueDirectory, "*.json");
            var compressedFiles = Directory.GetFiles(_queueDirectory, "*.json.gz");

            stats.TotalItems = _itemMetadata.Count;
            stats.UncompressedFiles = files.Length;
            stats.CompressedFiles = compressedFiles.Length;

            // Calculate sizes
            stats.TotalUncompressedSize = files.Sum(f => new FileInfo(f).Length);
            stats.TotalCompressedSize = compressedFiles.Sum(f => new FileInfo(f).Length);

            // Status breakdown
            stats.ItemsByStatus = _itemMetadata.Values
                .GroupBy(m => m.Status)
                .ToDictionary(g => g.Key, g => g.Count());

            // Type breakdown
            stats.ItemsByType = _itemMetadata.Values
                .GroupBy(m => m.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            // Age analysis
            stats.OldestItem = !_itemMetadata.IsEmpty ?
                _itemMetadata.Values.Min(m => m.CreatedAt) :
                DateTime.MinValue;
            stats.NewestItem = !_itemMetadata.IsEmpty ?
                _itemMetadata.Values.Max(m => m.CreatedAt) :
                DateTime.MinValue;

            return stats;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error calculating queue statistics");
            return new QueueStatistics();
        }
    }

    /// <summary>
    /// Restore queue from backup
    /// </summary>
    public Task<bool> RestoreFromBackupAsync(string? backupName = null)
    {
        try
        {
            var backupFiles = Directory.GetFiles(_backupDirectory, "queue_backup_*.zip")
                .OrderByDescending(f => File.GetCreationTime(f))
                .ToList();

            if (backupFiles.Count == 0)
            {
                _logger.LogWarning("No backup files found for restoration");
                return Task.FromResult(false);
            }

            var backupFile = backupName != null ?
                backupFiles.FirstOrDefault(f => Path.GetFileName(f).Contains(backupName)) :
                backupFiles[0];

            if (backupFile == null)
            {
                _logger.LogWarning("Specified backup file not found: {BackupName}", backupName);
                return Task.FromResult(false);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Restoring queue from backup: {BackupFile}", Path.GetFileName(backupFile));            }

            // Create temporary directory for extraction
            var tempDir = Path.Join(Path.GetTempPath(), $"deezspotag_restore_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Extract backup with path and size guards.
                ExtractBackupSafely(backupFile, tempDir, MaxRestoreExtractBytes);

                // Copy files back to queue directory
                var extractedFiles = Directory.GetFiles(tempDir);
                foreach (var file in extractedFiles)
                {
                    var fileName = Path.GetFileName(file);
                    var destPath = Path.Join(_queueDirectory, fileName);
                    File.Copy(file, destPath, true);
                }

                // Reload metadata
                LoadMetadata();

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully restored queue from backup with {FileCount} files", extractedFiles.Length);                }
                return Task.FromResult(true);
            }
            finally
            {
                // Clean up temporary directory
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error restoring queue from backup");
            return Task.FromResult(false);
        }
    }

    private static void ExtractBackupSafely(string backupFile, string destinationDirectory, long maxExtractBytes)
    {
        using var archive = ZipFile.OpenRead(backupFile);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        if (!destinationRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            destinationRoot += Path.DirectorySeparatorChar;
        }

        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var entryName = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(entryName))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Join(destinationRoot, entryName));
            if (!destinationPath.StartsWith(destinationRoot, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsafe zip entry path: {entry.FullName}");
            }

            if (entryName.EndsWith('/'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            extractedBytes += entry.Length;
            if (extractedBytes > maxExtractBytes)
            {
                throw new InvalidDataException("Backup archive extraction exceeds size limit.");
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var source = entry.Open();
            using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
        }
    }

    /// <summary>
    /// Create backup of current queue
    /// </summary>
    private void CreateBackup(object? state)
    {
        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"queue_backup_{timestamp}.zip";
            var backupPath = Path.Join(_backupDirectory, backupFileName);

            using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);

            // Add all queue files to backup
            var files = Directory.GetFiles(_queueDirectory);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                archive.CreateEntryFromFile(file, fileName);
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Created queue backup: {BackupFile} with {FileCount} files", backupFileName, files.Length);            }

            // Clean up old backups
            CleanupOldBackups();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error creating queue backup");
        }
    }

    /// <summary>
    /// Clean up old files and backups
    /// </summary>
    private void CleanupOldFiles(object? state)
    {
        try
        {
            CleanupOldBackups();
            CleanupOrphanedFiles();
            _ = Task.Run(async () => await CompressOldFiles());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during cleanup");
        }
    }

    /// <summary>
    /// Save data with retry logic
    /// </summary>
    private async Task SaveWithRetriesAsync<T>(string filePath, T data, string description)
    {
        var tempPath = filePath + ".tmp";

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Write to temporary file first
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                await System.IO.File.WriteAllTextAsync(tempPath, json);
                PersistTempFile(tempPath, filePath);

                return; // Success
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} failed to save {Description}",
                    attempt, MaxRetries, description);

                if (attempt == MaxRetries)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100d * attempt));
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }
        }
    }

    private static void PersistTempFile(string tempPath, string filePath)
    {
        if (System.IO.File.Exists(filePath))
        {
            File.Replace(tempPath, filePath, null);
            return;
        }

        File.Move(tempPath, filePath);
    }

    private void TryDeleteTempFile(string tempPath)
    {
        if (!System.IO.File.Exists(tempPath))
        {
            return;
        }

        try
        {
            File.Delete(tempPath);
        }
        catch (IOException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Temporary queue file cleanup failed for {Path}", tempPath);            }
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Temporary queue file cleanup denied for {Path}", tempPath);            }
        }
    }

    /// <summary>
    /// Save compressed data
    /// </summary>
    private async Task SaveCompressedAsync<T>(string filePath, T data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);

            using var fileStream = new FileStream(filePath, FileMode.Create);
            using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
            await gzipStream.WriteAsync(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save compressed file: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Load compressed data
    /// </summary>
    private async Task<T?> LoadCompressedAsync<T>(string filePath)
    {
        using var fileStream = new FileStream(filePath, FileMode.Open);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);

        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    /// <summary>
    /// Recover queue order from backup or metadata
    /// </summary>
    private async Task<List<string>> RecoverQueueOrderAsync()
    {
        try
        {
            _logger.LogInformation("Attempting to recover queue order");

            // Try to restore from latest backup
            var backupFiles = Directory.GetFiles(_backupDirectory, "queue_backup_*.zip")
                .OrderByDescending(f => File.GetCreationTime(f))
                .ToList();

            if (backupFiles.Count > 0)
            {
                var success = await RestoreFromBackupAsync();
                if (success)
                {
                    return await LoadQueueOrderAsync();
                }
            }

            // Fallback: reconstruct from metadata
            var queueOrder = _itemMetadata.Values
                .Where(m => m.Status == "inQueue")
                .OrderBy(m => m.CreatedAt)
                .Select(m => m.UUID)
                .ToList();

            if (queueOrder.Count > 0)
            {
                await SaveQueueOrderAsync(queueOrder);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Recovered queue order from metadata with {Count} items", queueOrder.Count);                }
            }

            return queueOrder;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to recover queue order");
            return new List<string>();
        }
    }

    /// <summary>
    /// Load metadata from disk
    /// </summary>
    private void LoadMetadata()
    {
        try
        {
            if (System.IO.File.Exists(_metadataFilePath))
            {
                var json = File.ReadAllText(_metadataFilePath);
                var metadata = JsonSerializer.Deserialize<Dictionary<string, QueueItemMetadata>>(json, _jsonOptions);

                if (metadata != null)
                {
                    _itemMetadata.Clear();
                    foreach (var kvp in metadata)
                    {
                        _itemMetadata[kvp.Key] = kvp.Value;
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Loaded metadata for {Count} queue items", _itemMetadata.Count);                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error loading queue metadata");
        }
    }

    /// <summary>
    /// Save metadata to disk
    /// </summary>
    private async Task SaveMetadataAsync()
    {
        try
        {
            var metadata = _itemMetadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            await SaveWithRetriesAsync(_metadataFilePath, metadata, "queue metadata");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error saving queue metadata");
        }
    }

    /// <summary>
    /// Clean up old backup files
    /// </summary>
    private void CleanupOldBackups()
    {
        try
        {
            var backupFiles = Directory.GetFiles(_backupDirectory, "queue_backup_*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            if (backupFiles.Count > MaxBackupFiles)
            {
                var filesToDelete = backupFiles.Skip(MaxBackupFiles);
                foreach (var file in filesToDelete)
                {
                    file.Delete();
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Cleaned up {Count} old backup files", backupFiles.Count - MaxBackupFiles);                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error cleaning up old backups");
        }
    }

    /// <summary>
    /// Clean up orphaned files
    /// </summary>
    private void CleanupOrphanedFiles()
    {
        try
        {
            var files = Directory.GetFiles(_queueDirectory, "*.json")
                .Where(f => !Path.GetFileName(f).Equals("order.json") && !Path.GetFileName(f).Equals("metadata.json"))
                .ToList();

            var orphanedFiles = files.Where(f =>
            {
                var uuid = Path.GetFileNameWithoutExtension(f);
                return !_itemMetadata.ContainsKey(uuid);
            }).ToList();

            foreach (var file in orphanedFiles)
            {
                File.Delete(file);
            }

            if (orphanedFiles.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Cleaned up {Count} orphaned queue files", orphanedFiles.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error cleaning up orphaned files");
        }
    }

    /// <summary>
    /// Compress old uncompressed files
    /// </summary>
    private async Task CompressOldFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            var files = Directory.GetFiles(_queueDirectory, "*.json")
                .Where(f => !Path.GetFileName(f).Equals("order.json") && !Path.GetFileName(f).Equals("metadata.json"))
                .Where(f => File.GetLastWriteTime(f) < cutoff)
                .ToList();

            foreach (var file in files)
            {
                var compressedPath = file + ".gz";
                if (!System.IO.File.Exists(compressedPath))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file);
                        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(content, _jsonOptions);
                        await SaveCompressedAsync(compressedPath, data);
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to compress file: {File}", file);
                    }
                }
            }

            if (files.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Compressed {Count} old queue files", files.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error compressing old files");
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!disposing)
        {
            return;
        }

        _backupTimer.Dispose();
        _cleanupTimer.Dispose();
        _persistenceSemaphore.Dispose();
    }
}

/// <summary>
/// Queue item metadata
/// </summary>
public class QueueItemMetadata
{
    public string UUID { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastModified { get; set; }
    public int Size { get; set; }
    public long FileSize { get; set; }
}

/// <summary>
/// Queue statistics
/// </summary>
public class QueueStatistics
{
    public int TotalItems { get; set; }
    public int UncompressedFiles { get; set; }
    public int CompressedFiles { get; set; }
    public long TotalUncompressedSize { get; set; }
    public long TotalCompressedSize { get; set; }
    public Dictionary<string, int> ItemsByStatus { get; set; } = new();
    public Dictionary<string, int> ItemsByType { get; set; } = new();
    public DateTime OldestItem { get; set; }
    public DateTime NewestItem { get; set; }

    public double CompressionRatio => TotalUncompressedSize > 0 ?
        (double)TotalCompressedSize / TotalUncompressedSize : 0;

    public long SpaceSaved => TotalUncompressedSize - TotalCompressedSize;
}
