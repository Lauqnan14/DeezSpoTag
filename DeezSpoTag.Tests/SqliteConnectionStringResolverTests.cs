using System;
using System.Collections.Generic;
using System.IO;
using DeezSpoTag.Services.Utils;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SqliteConnectionStringResolverTests
{
    [Fact]
    public void Resolve_UsesFallback_WhenInputIsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var _ = new TestConfigRootScope(root);

        var resolved = SqliteConnectionStringResolver.Resolve(null, "library.db");
        var builder = new SqliteConnectionStringBuilder(resolved);
        var expectedPath = Path.GetFullPath(Path.Combine(root, "library.db"));

        Assert.Equal(expectedPath, builder.DataSource);
    }

    [Fact]
    public void Resolve_UsesFallback_WhenConnectionStringHasNoDataSource()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var _ = new TestConfigRootScope(root);

        var resolved = SqliteConnectionStringResolver.Resolve("Mode=ReadWriteCreate;Cache=Shared", "fallback.db");
        var builder = new SqliteConnectionStringBuilder(resolved);
        var expectedPath = Path.GetFullPath(Path.Combine(root, "fallback.db"));

        Assert.Equal(expectedPath, builder.DataSource);
    }

    [Fact]
    public void Resolve_MapsRelativeDataSource_ToConfiguredDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var _ = new TestConfigRootScope(root);

        var resolved = SqliteConnectionStringResolver.Resolve("Data Source=my.db;Mode=ReadWriteCreate", "fallback.db");
        var builder = new SqliteConnectionStringBuilder(resolved);
        var expectedPath = Path.GetFullPath(Path.Combine(root, "my.db"));

        Assert.Equal(expectedPath, builder.DataSource);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, builder.Mode);
    }

    [Fact]
    public void Resolve_KeepsSpecialMemoryDataSource()
    {
        var resolved = SqliteConnectionStringResolver.Resolve("Data Source=:memory:;Mode=Memory", "fallback.db");
        var builder = new SqliteConnectionStringBuilder(resolved);

        Assert.Equal(":memory:", builder.DataSource);
        Assert.Equal(SqliteOpenMode.Memory, builder.Mode);
    }

    [Fact]
    public void Resolve_KeepsFileUriDataSource()
    {
        var resolved = SqliteConnectionStringResolver.Resolve("Data Source=file:test.db?mode=memory&cache=shared", "fallback.db");
        var builder = new SqliteConnectionStringBuilder(resolved);

        Assert.StartsWith("file:", builder.DataSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_NormalizesBareFilePath_Input()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var _ = new TestConfigRootScope(root);

        var resolved = SqliteConnectionStringResolver.Resolve("custom.sqlite", "fallback.db");
        var builder = new SqliteConnectionStringBuilder(resolved);
        var expectedPath = Path.GetFullPath(Path.Combine(root, "custom.sqlite"));

        Assert.Equal(expectedPath, builder.DataSource);
    }
}
