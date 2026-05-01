using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Utils;

public abstract class SqlitePersistentCacheStoreBase
{
    protected const string LastUsedAtFormat = "O";
    protected static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);
    protected static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private volatile bool _schemaEnsured;
    private DateTimeOffset _nextCleanupUtc = DateTimeOffset.MinValue;

    protected SqlitePersistentCacheStoreBase(IConfiguration configuration, string fallbackDatabaseFileName)
    {
        var rawConnection = Environment.GetEnvironmentVariable("LIBRARY_DB")
            ?? configuration.GetConnectionString("Library");
        ConnectionString = SqliteConnectionStringResolver.Resolve(rawConnection, fallbackDatabaseFileName);
    }

    protected string? ConnectionString { get; }

    protected static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString(LastUsedAtFormat, CultureInfo.InvariantCulture);
    }

    protected static bool TryParseTimestamp(string? raw, out DateTimeOffset value)
    {
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out value);
    }

    protected static bool IsStale(DateTimeOffset lastUsedUtc, DateTimeOffset nowUtc)
    {
        return nowUtc - lastUsedUtc > StaleAfter;
    }

    protected async Task EnsureSchemaOnceAsync(
        Func<SqliteConnection, CancellationToken, Task> ensureSchemaCoreAsync,
        ILogger logger,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (_schemaEnsured || string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await ensureSchemaCoreAsync(connection, cancellationToken);
            _schemaEnsured = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{FailureMessage}", failureMessage);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    protected async Task CleanupStaleEntriesIfDueAsync(
        Func<SqliteConnection, string, CancellationToken, Task> cleanupCoreAsync,
        ILogger logger,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextCleanupUtc)
        {
            return;
        }

        var hasLock = await _cleanupGate.WaitAsync(0, cancellationToken);
        if (!hasLock)
        {
            return;
        }

        try
        {
            now = DateTimeOffset.UtcNow;
            if (now < _nextCleanupUtc)
            {
                return;
            }

            var cutoff = FormatTimestamp(now.Subtract(StaleAfter));
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await cleanupCoreAsync(connection, cutoff, cancellationToken);
            _nextCleanupUtc = now.Add(CleanupInterval);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{FailureMessage}", failureMessage);
            _nextCleanupUtc = DateTimeOffset.UtcNow.Add(CleanupInterval);
        }
        finally
        {
            _cleanupGate.Release();
        }
    }
}
