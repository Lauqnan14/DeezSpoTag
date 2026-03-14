using Microsoft.Data.Sqlite;

namespace DeezSpoTag.Services.Utils;

public static class SqliteConnectionStringResolver
{
    public static string? Resolve(string? rawConnection, string defaultFileName)
    {
        var normalized = NormalizeInput(rawConnection);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return BuildFallback(defaultFileName);
        }

        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(normalized);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return BuildFallback(defaultFileName);
        }

        var dataSource = builder.DataSource?.Trim();
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return BuildFallback(defaultFileName);
        }

        if (IsSpecialDataSource(dataSource))
        {
            return builder.ToString();
        }

        var resolvedDataSource = ResolveDataSourcePath(dataSource, defaultFileName);
        builder.DataSource = resolvedDataSource;
        return builder.ToString();
    }

    private static string? NormalizeInput(string? rawConnection)
    {
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            return null;
        }

        var trimmed = rawConnection.Trim();
        if (trimmed.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (!trimmed.Contains(';'))
        {
            return $"Data Source={trimmed}";
        }

        return trimmed;
    }

    private static bool IsSpecialDataSource(string dataSource)
    {
        if (string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDataSourcePath(string dataSource, string defaultFileName)
    {
        if (Path.IsPathRooted(dataSource))
        {
            return Path.GetFullPath(dataSource);
        }

        var fileName = Path.GetFileName(dataSource);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = defaultFileName;
        }

        var dataRoot = ResolveDataRoot();
        Directory.CreateDirectory(dataRoot);
        return Path.GetFullPath(Path.Join(dataRoot, fileName));
    }

    private static string BuildFallback(string defaultFileName)
    {
        var dataRoot = ResolveDataRoot();
        Directory.CreateDirectory(dataRoot);
        var dbPath = Path.Join(dataRoot, defaultFileName);
        return $"Data Source={Path.GetFullPath(dbPath)}";
    }

    private static string ResolveDataRoot()
    {
        var configDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return configDir.Trim();
        }

        var dataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return dataDir.Trim();
        }

        return Path.Join(AppContext.BaseDirectory, "Data");
    }
}
