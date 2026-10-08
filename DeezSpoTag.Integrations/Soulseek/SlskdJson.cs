using System.Globalization;
using System.Text.Json;

namespace DeezSpoTag.Integrations.Soulseek;

/// <summary>
///     Tolerant readers for slskd JSON payloads.
/// </summary>
/// <remarks>
///     <para>
///         slskd is an external process that DeezSpoTag does not control, and its serialisation has
///         changed across versions: property casing, and the representation of enums and booleans, are
///         not guaranteed stable. These helpers therefore accept camelCase or PascalCase keys and accept
///         numbers written as JSON numbers or strings.
///     </para>
///     <para>
///         This mirrors the tolerant reading already used by the shipped
///         <c>SoulseekConnectionService</c>, so both paths agree on what slskd means.
///     </para>
/// </remarks>
internal static class SlskdJson
{
    internal static JsonElement? Find(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var match in names
            .Select(name => element.TryGetProperty(name, out var value) ? (JsonElement?)value : null)
            .Where(match => match.HasValue))
        {
            return match;
        }

        // Fall back to a case-insensitive scan for casing variants we were not told about.
        foreach (var property in element.EnumerateObject()
            .Where(property => names.Any(name =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))))
        {
            return property.Value;
        }

        return null;
    }

    internal static string? ReadString(JsonElement element, params string[] names)
    {
        var found = Find(element, names);
        if (found is not { } value)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    internal static bool ReadBool(JsonElement element, bool fallback, params string[] names)
    {
        var found = Find(element, names);
        if (found is not { } value)
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed)
                ? parsed
                : double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var text) && text != 0,
            _ => fallback
        };
    }

    internal static int ReadInt(JsonElement element, int fallback, params string[] names)
    {
        var found = Find(element, names);
        if (found is not { } value)
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return (int)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
            {
                return (int)Math.Round(number, MidpointRounding.AwayFromZero);
            }
        }

        return fallback;
    }

    internal static int? ReadNullableInt(JsonElement element, params string[] names)
    {
        var found = Find(element, names);
        if (found is not { } value)
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : ReadInt(element, 0, names);
    }

    internal static long ReadLong(JsonElement element, long fallback, params string[] names)
    {
        var found = Find(element, names);
        if (found is not { } value)
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return (long)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    internal static long? ReadNullableLong(JsonElement element, params string[] names)
    {
        var found = Find(element, names);
        if (found is not { } value)
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : ReadLong(element, 0, names);
    }

    internal static double ReadDouble(JsonElement element, double fallback, params string[] names)
    {
        var found = Find(element, names);
        if (found is not { } value)
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    internal static DateTimeOffset? ReadDateTime(JsonElement element, params string[] names)
    {
        var text = ReadString(element, names);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    internal static IReadOnlyList<JsonElement> ReadArray(JsonElement element, params string[] names)
    {
        var found = Find(element, names);
        if (found is not { } value)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().ToList();
        }

        // slskd returns searches and shares either as an array or as an id-keyed object. Accept both.
        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.EnumerateObject().Select(property => property.Value).ToList();
        }

        return [];
    }

    internal static SlskdFile ReadFile(JsonElement element) => new()
    {
        Filename = ReadString(element, "filename", "fileName", "name") ?? string.Empty,
        Extension = ReadString(element, "extension"),
        Size = ReadLong(element, 0, "size"),
        BitRate = ReadNullableInt(element, "bitRate", "bitrate"),
        BitDepth = ReadNullableInt(element, "bitDepth"),
        SampleRate = ReadNullableInt(element, "sampleRate"),
        Length = ReadNullableInt(element, "length"),
        IsVariableBitRate = ReadBool(element, false, "isVariableBitRate", "variableBitRate"),
        IsLocked = ReadBool(element, false, "isLocked", "locked")
    };

    internal static SlskdSearchResponse ReadSearchResponse(JsonElement element) => new()
    {
        Username = ReadString(element, "username", "user") ?? string.Empty,
        QueueLength = ReadLong(element, 0, "queueLength"),
        HasFreeUploadSlot = ReadBool(element, false, "hasFreeUploadSlot", "freeUploadSlot"),
        UploadSpeed = ReadInt(element, 0, "uploadSpeed"),
        FileCount = ReadInt(element, 0, "fileCount"),
        Token = ReadInt(element, 0, "token"),
        Files = ReadArray(element, "files").Select(ReadFile).ToList(),
        LockedFiles = ReadArray(element, "lockedFiles").Select(ReadFile).ToList()
    };

    internal static SlskdSearch ReadSearch(JsonElement element)
    {
        var endedAt = ReadDateTime(element, "endedAt");
        return new SlskdSearch
        {
            Id = ReadGuid(element, "id") ?? Guid.Empty,
            SearchText = ReadString(element, "searchText", "searchQuery", "text"),
            State = ReadLong(element, 0, "state"),
            // slskd stamps endedAt on every terminal outcome (complete, timeout, cancelled, errored).
            // Deriving completion from it avoids depending on the numeric values of slskd's SearchStates.
            IsComplete = endedAt.HasValue,
            StartedAt = ReadDateTime(element, "startedAt"),
            EndedAt = endedAt,
            FileCount = ReadInt(element, 0, "fileCount"),
            ResponseCount = ReadInt(element, 0, "responseCount"),
            LockedFileCount = ReadInt(element, 0, "lockedFileCount"),
            Token = ReadInt(element, 0, "token"),
            Responses = ReadArray(element, "responses").Select(ReadSearchResponse).ToList()
        };
    }

    internal static SlskdTransfer ReadTransfer(JsonElement element)
    {
        var state = SlskdTransferStateInfo.ParseRaw(ReadString(element, "state"));
        return new SlskdTransfer
        {
            Id = ReadGuid(element, "id"),
            Username = ReadString(element, "username", "user"),
            Filename = ReadString(element, "filename", "fileName"),
            Size = ReadLong(element, 0, "size"),
            State = state,
            StateInfo = SlskdTransferStateInfo.Decode(state),
            BytesTransferred = ReadLong(element, 0, "bytesTransferred"),
            AverageSpeed = ReadDouble(element, 0, "averageSpeed"),
            PlaceInQueue = ReadNullableInt(element, "placeInQueue"),
            RequestedAt = ReadDateTime(element, "requestedAt"),
            EnqueuedAt = ReadDateTime(element, "enqueuedAt"),
            StartedAt = ReadDateTime(element, "startedAt"),
            EndedAt = ReadDateTime(element, "endedAt"),
            Exception = ReadString(element, "exception"),
            Attempts = ReadInt(element, 0, "attempts"),
            NextAttemptAt = ReadDateTime(element, "nextAttemptAt"),
            Removed = ReadBool(element, false, "removed"),
            BatchId = ReadGuid(element, "batchId")
        };
    }

    internal static SlskdDirectory ReadDirectory(JsonElement element) => new()
    {
        Directory = ReadString(element, "directory", "name", "path") ?? string.Empty,
        FileCount = ReadInt(element, 0, "fileCount"),
        Files = ReadArray(element, "files").Select(ReadFile).ToList()
    };

    internal static SlskdShare ReadShare(JsonElement element, string? key = null) => new()
    {
        Id = ReadString(element, "id") ?? key ?? string.Empty,
        Alias = ReadString(element, "alias"),
        IsExcluded = ReadBool(element, false, "isExcluded", "excluded"),
        LocalPath = ReadString(element, "localPath"),
        Raw = ReadString(element, "raw"),
        RemotePath = ReadString(element, "remotePath"),
        Directories = ReadNullableInt(element, "directories"),
        Files = ReadNullableInt(element, "files")
    };

    private static Guid? ReadGuid(JsonElement element, params string[] names)
    {
        var text = ReadString(element, names);
        return Guid.TryParse(text, out var parsed) ? parsed : null;
    }
}
