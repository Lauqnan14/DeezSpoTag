using System.Buffers.Binary;

namespace DeezSpoTag.Services.Library;

/// <summary>
/// Reads the true duration of fragmented MP4 (fMP4/CMAF) files by walking all
/// moof/traf/trun boxes. FFmpeg 6.x and TagLib fail to read beyond the first
/// fragment when mvhd/mdhd declare duration=0, which is per-spec for fMP4.
/// </summary>
public static class FragmentedMp4DurationReader
{
    public sealed record FragmentedMp4Info(double DurationSeconds, int SampleRateHz);

    /// <summary>
    /// Checks whether the given file is a fragmented MP4 by looking for a
    /// moov box containing mvex. Lightweight — reads only the first few KB.
    /// </summary>
    public static bool IsFragmentedMp4(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            if (length < 16)
            {
                return false;
            }

            while (stream.Position < length)
            {
                var boxStart = stream.Position;
                if (!TryReadBoxHeader(stream, length, out var boxSize, out var boxType))
                {
                    break;
                }

                var boxEnd = boxSize > 0 ? boxStart + boxSize : length;

                if (boxType == "moov")
                {
                    var (_, hasMvex) = ParseMoov(stream, boxEnd);
                    return hasMvex;
                }

                if (boxType == "moof")
                {
                    return true;
                }

                stream.Position = boxEnd;
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the concatenated mdat payloads of a fragmented MP4 to the given
    /// output stream. This produces a clean raw bitstream (e.g. raw EAC3)
    /// without MP4 box framing, suitable for piping to FFmpeg with -f eac3.
    /// </summary>
    public static async Task ExtractMdatPayloadsAsync(string filePath, Stream output, CancellationToken cancellationToken = default)
    {
        const int bufferSize = 64 * 1024;
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        var length = fileStream.Length;
        var buffer = new byte[bufferSize];

        while (fileStream.Position < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var boxStart = fileStream.Position;
            if (!TryReadBoxHeader(fileStream, length, out var boxSize, out var boxType))
            {
                break;
            }

            var boxEnd = boxSize > 0 ? boxStart + boxSize : length;

            if (boxType == "mdat")
            {
                var payloadBytes = boxEnd - fileStream.Position;
                while (payloadBytes > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var toRead = (int)Math.Min(payloadBytes, bufferSize);
                    var read = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    payloadBytes -= read;
                }
            }

            fileStream.Position = boxEnd;
        }
    }

    /// <summary>
    /// Attempts to parse a fragmented MP4 file and return the true duration.
    /// Returns null if the file is not a fragmented MP4, cannot be read, or
    /// on any parse error (callers should fall back to existing logic).
    /// </summary>
    public static FragmentedMp4Info? TryRead(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadCore(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static FragmentedMp4Info? TryReadCore(Stream stream)
    {
        var length = stream.Length;
        if (length < 16)
        {
            return null;
        }

        int timescale = 0;
        bool hasMvex = false;
        long totalDurationTicks = 0;
        bool foundMoof = false;

        stream.Position = 0;
        while (stream.Position < length)
        {
            var boxStart = stream.Position;
            if (!TryReadBoxHeader(stream, length, out var boxSize, out var boxType))
            {
                break;
            }

            var boxEnd = boxSize > 0 ? boxStart + boxSize : length;

            switch (boxType)
            {
                case "moov":
                    (timescale, hasMvex) = ParseMoov(stream, boxEnd);
                    break;

                case "moof":
                    foundMoof = true;
                    totalDurationTicks += ParseMoof(stream, boxEnd);
                    break;
            }

            stream.Position = boxEnd;
        }

        if (!foundMoof || !hasMvex || timescale <= 0 || totalDurationTicks <= 0)
        {
            return null;
        }

        var durationSeconds = totalDurationTicks / (double)timescale;
        return new FragmentedMp4Info(durationSeconds, timescale);
    }

    private static (int Timescale, bool HasMvex) ParseMoov(Stream stream, long moovEnd)
    {
        int timescale = 0;
        bool hasMvex = false;

        while (stream.Position < moovEnd)
        {
            var boxStart = stream.Position;
            if (!TryReadBoxHeader(stream, moovEnd, out var size, out var type))
            {
                break;
            }

            var boxEnd = size > 0 ? boxStart + size : moovEnd;

            switch (type)
            {
                case "mvhd":
                    timescale = ParseMvhdTimescale(stream);
                    break;

                case "trak":
                    var trakTimescale = ParseTrakForTimescale(stream, boxEnd);
                    if (trakTimescale > 0 && timescale <= 0)
                    {
                        timescale = trakTimescale;
                    }
                    break;

                case "mvex":
                    hasMvex = true;
                    break;
            }

            stream.Position = boxEnd;
        }

        return (timescale, hasMvex);
    }

    private static int ParseMvhdTimescale(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4];
        if (stream.Read(buf) < 4)
        {
            return 0;
        }

        // buf[0]=version, buf[1..3]=flags (already consumed)
        var version = buf[0];
        // Skip creation_time + modification_time to reach timescale
        stream.Position += version == 0 ? 4 + 4 : 8 + 8;

        if (stream.Read(buf) < 4)
        {
            return 0;
        }

        return (int)BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    private static int ParseTrakForTimescale(Stream stream, long trakEnd)
    {
        while (stream.Position < trakEnd)
        {
            var boxStart = stream.Position;
            if (!TryReadBoxHeader(stream, trakEnd, out var size, out var type))
            {
                break;
            }

            var boxEnd = size > 0 ? boxStart + size : trakEnd;

            if (type == "mdia")
            {
                return ParseMdiaForTimescale(stream, boxEnd);
            }

            stream.Position = boxEnd;
        }

        return 0;
    }

    private static int ParseMdiaForTimescale(Stream stream, long mdiaEnd)
    {
        while (stream.Position < mdiaEnd)
        {
            var boxStart = stream.Position;
            if (!TryReadBoxHeader(stream, mdiaEnd, out var size, out var type))
            {
                break;
            }

            var boxEnd = size > 0 ? boxStart + size : mdiaEnd;

            if (type == "mdhd")
            {
                return ParseMdhdTimescale(stream);
            }

            stream.Position = boxEnd;
        }

        return 0;
    }

    private static int ParseMdhdTimescale(Stream stream)
    {
        return ParseMvhdTimescale(stream);
    }

    private static long ParseMoof(Stream stream, long moofEnd)
    {
        long durationTicks = 0;

        while (stream.Position < moofEnd)
        {
            var boxStart = stream.Position;
            if (!TryReadBoxHeader(stream, moofEnd, out var size, out var type))
            {
                break;
            }

            var boxEnd = size > 0 ? boxStart + size : moofEnd;

            if (type == "traf")
            {
                durationTicks += ParseTraf(stream, boxEnd);
            }

            stream.Position = boxEnd;
        }

        return durationTicks;
    }

    private static long ParseTraf(Stream stream, long trafEnd)
    {
        long durationTicks = 0;
        int defaultSampleDuration = 0;

        // First find tfhd to get default sample duration
        var trafDataStart = stream.Position;

        while (stream.Position < trafEnd)
        {
            var boxStart = stream.Position;
            if (!TryReadBoxHeader(stream, trafEnd, out var size, out var type))
            {
                break;
            }

            var boxEnd = size > 0 ? boxStart + size : trafEnd;

            if (type == "tfhd")
            {
                defaultSampleDuration = ParseTfhdDefaultDuration(stream);
                break;
            }

            stream.Position = boxEnd;
        }

        // Now scan all trun boxes
        stream.Position = trafDataStart;
        while (stream.Position < trafEnd)
        {
            var boxStart = stream.Position;
            if (!TryReadBoxHeader(stream, trafEnd, out var size, out var type))
            {
                break;
            }

            var boxEnd = size > 0 ? boxStart + size : trafEnd;

            if (type == "trun")
            {
                durationTicks += ParseTrun(stream, defaultSampleDuration);
            }

            stream.Position = boxEnd;
        }

        return durationTicks;
    }

    private static int ParseTfhdDefaultDuration(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4];

        // version (1 byte) + flags (3 bytes)
        if (stream.Read(buf) < 4)
        {
            return 0;
        }

        int flags = (buf[1] << 16) | (buf[2] << 8) | buf[3];

        // track_id (4 bytes)
        stream.Position += 4;

        if ((flags & 0x000001) != 0) // base-data-offset-present
        {
            stream.Position += 8;
        }

        if ((flags & 0x000002) != 0) // sample-description-index-present
        {
            stream.Position += 4;
        }

        if ((flags & 0x000008) != 0) // default-sample-duration-present
        {
            if (stream.Read(buf) < 4)
            {
                return 0;
            }

            return (int)BinaryPrimitives.ReadUInt32BigEndian(buf);
        }

        return 0;
    }

    private static long ParseTrun(Stream stream, int defaultSampleDuration)
    {
        Span<byte> buf = stackalloc byte[4];

        // version (1 byte) + flags (3 bytes)
        if (stream.Read(buf) < 4)
        {
            return 0;
        }

        int flags = (buf[1] << 16) | (buf[2] << 8) | buf[3];

        // sample_count
        if (stream.Read(buf) < 4)
        {
            return 0;
        }

        int sampleCount = (int)BinaryPrimitives.ReadUInt32BigEndian(buf);
        if (sampleCount <= 0)
        {
            return 0;
        }

        bool hasDataOffset = (flags & 0x000001) != 0;
        bool hasFirstSampleFlags = (flags & 0x000004) != 0;
        bool hasSampleDuration = (flags & 0x000100) != 0;
        bool hasSampleSize = (flags & 0x000200) != 0;
        bool hasSampleFlags = (flags & 0x000400) != 0;
        bool hasSampleCompositionOffset = (flags & 0x000800) != 0;

        if (hasDataOffset)
        {
            stream.Position += 4;
        }

        if (hasFirstSampleFlags)
        {
            stream.Position += 4;
        }

        if (!hasSampleDuration)
        {
            // All samples use default duration
            return (long)sampleCount * defaultSampleDuration;
        }

        // Read per-sample durations
        long totalDuration = 0;
        int bytesPerSample = 4;
        if (hasSampleSize) bytesPerSample += 4;
        if (hasSampleFlags) bytesPerSample += 4;
        if (hasSampleCompositionOffset) bytesPerSample += 4;

        for (int i = 0; i < sampleCount; i++)
        {
            // Sample duration is the first field when present
            if (stream.Read(buf) < 4)
            {
                break;
            }

            totalDuration += BinaryPrimitives.ReadUInt32BigEndian(buf);

            // Skip remaining per-sample fields
            var skip = bytesPerSample - 4;
            if (skip > 0)
            {
                stream.Position += skip;
            }
        }

        return totalDuration;
    }

    private static bool TryReadBoxHeader(Stream stream, long limit, out long size, out string type)
    {
        size = 0;
        type = string.Empty;

        if (stream.Position + 8 > limit)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) < 8)
        {
            return false;
        }

        var size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
        type = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));

        if (size32 == 1)
        {
            // 64-bit extended size
            if (stream.Position + 8 > limit)
            {
                return false;
            }

            Span<byte> ext = stackalloc byte[8];
            if (stream.Read(ext) < 8)
            {
                return false;
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
        }
        else if (size32 == 0)
        {
            // Box extends to end of file/container
            size = 0;
        }
        else
        {
            size = size32;
        }

        return true;
    }
}
