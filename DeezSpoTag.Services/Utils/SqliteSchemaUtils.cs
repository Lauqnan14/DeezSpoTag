using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Utils;

internal static partial class SqliteSchemaUtils
{
    internal static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string type,
        CancellationToken cancellationToken)
    {
        var safeTableName = ValidateIdentifier(table, nameof(table));
        var safeTable = QuoteIdentifier(safeTableName);
        var safeColumn = QuoteIdentifier(ValidateIdentifier(column, nameof(column)));
        var safeType = ValidateTypeClause(type);

        if (await ColumnExistsAsync(connection, safeTableName, column, cancellationToken))
        {
            return;
        }

        var sql = BuildAddColumnSql(safeTable, safeColumn, safeType);
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        var safeTable = ValidateIdentifier(table, nameof(table));
        var safeColumn = ValidateIdentifier(column, nameof(column));
        const string Sql = "SELECT 1 FROM pragma_table_info(@tableName) WHERE name = @columnName LIMIT 1;";
        await using var command = new SqliteCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", safeTable);
        command.Parameters.AddWithValue("@columnName", safeColumn);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    /// <summary>
    /// The column names of an existing table, read through the parameterized
    /// table-valued <c>pragma_table_info</c> form so the table name is bound
    /// rather than spliced into the statement text.
    /// </summary>
    internal static async Task<HashSet<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var safeTable = ValidateIdentifier(table, nameof(table));
        const string Sql = "SELECT name FROM pragma_table_info(@tableName);";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqliteCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", safeTable);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                names.Add(reader.GetString(0));
            }
        }

        return names;
    }

    /// <summary>
    /// SQL for dropping an index. SQLite cannot bind an identifier, so the name
    /// is validated against the identifier shape and quoted.
    /// </summary>
    internal static string BuildDropIndexSql(string indexName)
        => $"DROP INDEX IF EXISTS {QuoteIdentifier(ValidateIdentifier(indexName, nameof(indexName)))};";

    /// <summary>
    /// SQL for dropping a column. As with indexes, the identifiers cannot be
    /// bound and are validated and quoted instead.
    /// </summary>
    internal static string BuildDropColumnSql(string table, string column)
        => $"ALTER TABLE {QuoteIdentifier(ValidateIdentifier(table, nameof(table)))} "
            + $"DROP COLUMN {QuoteIdentifier(ValidateIdentifier(column, nameof(column)))};";

    /// <summary>
    /// Provider-era columns on <c>personal_genre_classification_history</c> that
    /// the current history insert does not write. A NOT NULL flag with no default
    /// rejects that insert. The columns stay, so an old confidence is not discarded
    /// and a new one is not invented.
    /// </summary>
    internal static readonly string[] ObsoleteClassificationHistoryColumns =
    [
        "confidence",
        "sources_json",
        "evidence_state"
    ];

    internal static Task RelaxObsoleteClassificationHistoryConstraintsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
        => RelaxNotNullColumnsAsync(
            connection,
            "personal_genre_classification_history",
            ObsoleteClassificationHistoryColumns,
            cancellationToken);

    /// <summary>
    /// Drops NOT NULL from columns the caller names, without dropping the columns
    /// or writing a replacement value. SQLite cannot change a column constraint in
    /// place, so the table is rebuilt and every existing value is copied.
    /// </summary>
    internal static async Task RelaxNotNullColumnsAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyCollection<string> columns,
        CancellationToken cancellationToken)
    {
        var safeTable = ValidateIdentifier(table, nameof(table));
        var requested = new HashSet<string>(columns.Select(column => ValidateIdentifier(column, nameof(columns))), StringComparer.OrdinalIgnoreCase);
        var existing = await ReadColumnDefinitionsAsync(connection, safeTable, cancellationToken);
        if (existing.Count == 0
            || !existing.Any(column => requested.Contains(column.Name) && column.NotNull && column.PrimaryKey == 0))
        {
            return;
        }

        var definitions = existing.Select(column => BuildRelaxedColumnSql(column, requested)).ToList();
        var primaryKey = existing.Where(column => column.PrimaryKey > 0).OrderBy(column => column.PrimaryKey).ToList();
        if (primaryKey.Count > 0)
        {
            definitions.Add("PRIMARY KEY (" + string.Join(", ", primaryKey.Select(column => QuoteIdentifier(column.Name))) + ")");
        }

        definitions.AddRange(await ReadForeignKeyClausesAsync(connection, safeTable, cancellationToken));
        var indexes = await ReadIndexSqlAsync(connection, safeTable, cancellationToken);
        var foreignKeysWereEnabled = await ReadIntegerPragmaAsync(connection, "foreign_keys", cancellationToken) != 0;
        if (foreignKeysWereEnabled)
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken);
        }

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var rebuilt = safeTable + "__nullable";
            var quotedTable = QuoteIdentifier(safeTable);
            var quotedRebuilt = QuoteIdentifier(rebuilt);
            var columnList = string.Join(", ", existing.Select(column => QuoteIdentifier(column.Name)));
            var createSql = $"CREATE TABLE {quotedRebuilt} ({string.Join(", ", definitions)});";
            var copySql = $"INSERT INTO {quotedRebuilt} ({columnList}) SELECT {columnList} FROM {quotedTable};";

            await ExecuteAsync(connection, transaction, $"DROP TABLE IF EXISTS {quotedRebuilt};", cancellationToken);
            await ExecuteAsync(connection, transaction, createSql, cancellationToken);
            await ExecuteAsync(connection, transaction, copySql, cancellationToken);
            await ExecuteAsync(connection, transaction, $"DROP TABLE {quotedTable};", cancellationToken);
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {quotedRebuilt} RENAME TO {quotedTable};", cancellationToken);
            foreach (var indexSql in indexes)
            {
                await ExecuteAsync(connection, transaction, indexSql, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (foreignKeysWereEnabled)
            {
                await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            }
        }
    }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
        }

        if (!SqliteIdentifierRegex().IsMatch(value))
        {
            throw new ArgumentException($"Unsafe SQLite identifier: '{value}'.", parameterName);
        }

        return value;
    }

    private static string ValidateTypeClause(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("SQLite type clause cannot be null or whitespace.", nameof(value));
        }

        var normalized = value.Trim();
        if (!SqliteTypeClauseRegex().IsMatch(normalized))
        {
            throw new ArgumentException($"Unsafe SQLite type clause: '{value}'.", nameof(value));
        }

        return normalized;
    }

    private static string BuildAddColumnSql(string table, string column, string type)
    {
        var builder = new StringBuilder(96);
        builder.Append("ALTER TABLE ");
        builder.Append(table);
        builder.Append(" ADD COLUMN ");
        builder.Append(column);
        builder.Append(' ');
        builder.Append(type);
        builder.Append(';');
        return builder.ToString();
    }

    private readonly record struct ColumnDefinition(string Name, string Type, bool NotNull, string? DefaultSql, int PrimaryKey);

    private static async Task<List<ColumnDefinition>> ReadColumnDefinitionsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT name, type, "notnull", dflt_value, pk
            FROM pragma_table_info(@tableName)
            ORDER BY cid;
            """;
        var columns = new List<ColumnDefinition>();
        await using var command = new SqliteCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            ValidateIdentifier(name, nameof(name));
            var type = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1))
                ? "TEXT"
                : reader.GetString(1).Trim();
            if (!SqliteTypeNameRegex().IsMatch(type))
            {
                throw new InvalidOperationException($"Unsafe SQLite column type '{type}' on {table}.{name}.");
            }

            columns.Add(new ColumnDefinition(
                name,
                type,
                !reader.IsDBNull(2) && reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4)));
        }

        return columns;
    }

    private static string BuildRelaxedColumnSql(ColumnDefinition column, HashSet<string> requested)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteIdentifier(column.Name));
        builder.Append(' ');
        builder.Append(column.Type);
        var relax = requested.Contains(column.Name) && column.PrimaryKey == 0;
        if (column.NotNull && !relax)
        {
            builder.Append(" NOT NULL");
        }

        if (!string.IsNullOrWhiteSpace(column.DefaultSql) && SqliteDefaultLiteralRegex().IsMatch(column.DefaultSql))
        {
            builder.Append(" DEFAULT ");
            builder.Append(column.DefaultSql);
        }

        return builder.ToString();
    }

    private static async Task<List<string>> ReadForeignKeyClausesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT id, seq, "table", "from", "to", on_delete
            FROM pragma_foreign_key_list(@tableName)
            ORDER BY id, seq;
            """;
        var grouped = new SortedDictionary<long, List<(int Sequence, string Table, string From, string To, string OnDelete)>>();
        await using var command = new SqliteCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            if (!grouped.TryGetValue(id, out var parts))
            {
                parts = [];
                grouped[id] = parts;
            }

            parts.Add((
                reader.GetInt32(1),
                ValidateIdentifier(reader.GetString(2), "referencedTable"),
                ValidateIdentifier(reader.GetString(3), "fromColumn"),
                ValidateIdentifier(reader.GetString(4), "toColumn"),
                reader.IsDBNull(5) ? "NO ACTION" : reader.GetString(5)));
        }

        var clauses = new List<string>();
        foreach (var parts in grouped.Values)
        {
            var ordered = parts.OrderBy(part => part.Sequence).ToList();
            var onDelete = ordered[0].OnDelete;
            var clause = "FOREIGN KEY ("
                + string.Join(", ", ordered.Select(part => QuoteIdentifier(part.From)))
                + ") REFERENCES "
                + QuoteIdentifier(ordered[0].Table)
                + " ("
                + string.Join(", ", ordered.Select(part => QuoteIdentifier(part.To)))
                + ")";
            if (SqliteReferentialActionRegex().IsMatch(onDelete)
                && !string.Equals(onDelete, "NO ACTION", StringComparison.OrdinalIgnoreCase))
            {
                clause += " ON DELETE " + onDelete;
            }

            clauses.Add(clause);
        }

        return clauses;
    }

    private static async Task<List<string>> ReadIndexSqlAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'index' AND tbl_name = @tableName AND sql IS NOT NULL;
            """;
        var indexes = new List<string>();
        await using var command = new SqliteCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sql = reader.GetString(0).Trim();
            if (!sql.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to replay an unexpected index statement on {table}.");
            }

            indexes.Add(sql.EndsWith(';') ? sql : sql + ";");
        }

        return indexes;
    }

    private static async Task<long> ReadIntegerPragmaAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        var sql = "PRAGMA " + ValidateIdentifier(pragma, nameof(pragma)) + ";";
        await using var command = new SqliteCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    private static readonly Regex s_sqliteIdentifierRegex = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex s_sqliteTypeClauseRegex = new(
        "^(TEXT|INTEGER|REAL|BIGINT)(?:\\s+NOT\\s+NULL)?(?:\\s+DEFAULT\\s+(?:-?\\d+|'(?:''|[^'])*'|CURRENT_TIMESTAMP))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex s_sqliteTypeNameRegex = new(
        "^(TEXT|INTEGER|REAL|BIGINT)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex s_sqliteDefaultLiteralRegex = new(
        "^(?:-?\\d+(?:\\.\\d+)?|NULL|CURRENT_TIMESTAMP|'(?:[^']|'')*')$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex s_sqliteReferentialActionRegex = new(
        "^(NO ACTION|RESTRICT|CASCADE|SET NULL|SET DEFAULT)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    private static Regex SqliteIdentifierRegex() => s_sqliteIdentifierRegex;

    private static Regex SqliteTypeClauseRegex() => s_sqliteTypeClauseRegex;

    private static Regex SqliteTypeNameRegex() => s_sqliteTypeNameRegex;

    private static Regex SqliteDefaultLiteralRegex() => s_sqliteDefaultLiteralRegex;

    private static Regex SqliteReferentialActionRegex() => s_sqliteReferentialActionRegex;
}
