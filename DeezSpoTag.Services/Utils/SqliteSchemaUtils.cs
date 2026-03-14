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

    private static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    [GeneratedRegex(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex SqliteIdentifierRegex();

    [GeneratedRegex(
        "^(TEXT|INTEGER|REAL|BIGINT)(?:\\s+NOT\\s+NULL)?(?:\\s+DEFAULT\\s+(?:-?\\d+|'(?:''|[^'])*'))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex SqliteTypeClauseRegex();
}
