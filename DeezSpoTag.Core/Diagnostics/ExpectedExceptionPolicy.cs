using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;

namespace DeezSpoTag.Core.Diagnostics;

public static class ExpectedExceptionPolicy
{
    public static bool IsRecoverable(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex is IOException
            or UnauthorizedAccessException
            or SecurityException
            or PathTooLongException
            or DirectoryNotFoundException
            or FileNotFoundException
            or JsonException
            or XmlException
            or HttpRequestException
            or WebException
            or TimeoutException
            or TaskCanceledException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or FormatException
            or CryptographicException
            or Win32Exception
            or TargetInvocationException
            or ReflectionTypeLoadException
            or TypeLoadException
            or MissingMethodException
            or AmbiguousMatchException
            || IsKnownOperationalException(ex);
    }

    private static bool IsKnownOperationalException(Exception ex)
    {
        var name = ex.GetType().FullName;
        return name is "Microsoft.Data.Sqlite.SqliteException"
            or "Microsoft.EntityFrameworkCore.DbUpdateException"
            or "Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"
            or "SQLitePCL.SQLiteException";
    }
}
