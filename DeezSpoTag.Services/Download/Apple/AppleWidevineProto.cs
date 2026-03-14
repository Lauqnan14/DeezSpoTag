using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace DeezSpoTag.Services.Download.Apple;

public static class AppleWidevineProto
{
    public static byte[] BuildWidevineCencHeader(byte[] kidBytes, byte[] contentIdBytes)
    {
        var writer = new ProtoWriter();
        writer.WriteVarintField(1, 1); // algorithm AESCTR
        writer.WriteBytesField(2, kidBytes);
        if (contentIdBytes.Length > 0)
        {
            writer.WriteBytesField(4, contentIdBytes);
        }
        return writer.ToArray();
    }

    public static byte[] BuildLicenseRequest(byte[] clientIdBytes, byte[] widevineHeaderBytes, byte[] requestId)
    {
        var cenc = new ProtoWriter();
        cenc.WriteBytesField(1, widevineHeaderBytes);
        cenc.WriteVarintField(2, 1); // LicenseType DEFAULT
        cenc.WriteBytesField(3, requestId);

        var content = new ProtoWriter();
        content.WriteMessageField(1, cenc.ToArray());

        var request = new ProtoWriter();
        request.WriteBytesField(1, clientIdBytes);
        request.WriteMessageField(2, content.ToArray());
        request.WriteVarintField(3, 1); // RequestType NEW
        request.WriteVarintField(4, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        request.WriteVarintField(6, 21); // ProtocolVersion CURRENT
        request.WriteVarintField(7, RandomUInt32());

        return request.ToArray();
    }

    public static byte[] BuildSignedLicenseRequest(byte[] licenseRequestMsg, byte[] signature)
    {
        var writer = new ProtoWriter();
        writer.WriteVarintField(1, 1); // LICENSE_REQUEST
        writer.WriteMessageField(2, licenseRequestMsg);
        writer.WriteBytesField(3, signature);
        return writer.ToArray();
    }

    public static AppleWidevineLicense ParseSignedLicense(byte[] data)
    {
        var reader = new ProtoReader(data);
        var license = new AppleWidevineLicense();

        while (reader.TryReadTag(out var fieldNumber, out var wireType))
        {
            switch (fieldNumber)
            {
                case 2:
                    license.LicenseMessage = reader.ReadBytes();
                    break;
                case 4:
                    license.SessionKey = reader.ReadBytes();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        return license;
    }

    public static List<AppleWidevineKeyContainer> ParseLicenseKeys(byte[] data)
    {
        var reader = new ProtoReader(data);
        var keys = new List<AppleWidevineKeyContainer>();

        while (reader.TryReadTag(out var fieldNumber, out var wireType))
        {
            if (fieldNumber == 3 && wireType == ProtoWireType.LengthDelimited)
            {
                var payload = reader.ReadBytes();
                keys.Add(ParseKeyContainer(payload));
            }
            else
            {
                reader.SkipField(wireType);
            }
        }

        return keys;
    }

    private static AppleWidevineKeyContainer ParseKeyContainer(byte[] data)
    {
        var reader = new ProtoReader(data);
        var container = new AppleWidevineKeyContainer();

        while (reader.TryReadTag(out var fieldNumber, out var wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    container.Id = reader.ReadBytes();
                    break;
                case 2:
                    container.Iv = reader.ReadBytes();
                    break;
                case 3:
                    container.Key = reader.ReadBytes();
                    break;
                case 4:
                    container.Type = (int)reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        return container;
    }

    private static uint RandomUInt32()
    {
        Span<byte> buffer = stackalloc byte[4];
        RandomNumberGenerator.Fill(buffer);
        return BitConverter.ToUInt32(buffer);
    }
}

public sealed class AppleWidevineLicense
{
    public byte[] LicenseMessage { get; set; } = Array.Empty<byte>();
    public byte[] SessionKey { get; set; } = Array.Empty<byte>();
}

public sealed class AppleWidevineKeyContainer
{
    public byte[] Id { get; set; } = Array.Empty<byte>();
    public byte[] Iv { get; set; } = Array.Empty<byte>();
    public byte[] Key { get; set; } = Array.Empty<byte>();
    public int Type { get; set; }
}

internal enum ProtoWireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    Fixed32 = 5
}

internal sealed class ProtoWriter
{
    private readonly ArrayBufferWriter<byte> _writer = new();

    public void WriteVarintField(int fieldNumber, uint value)
    {
        WriteTag(fieldNumber, ProtoWireType.Varint);
        WriteVarint(value);
    }

    public void WriteBytesField(int fieldNumber, byte[] value)
    {
        WriteTag(fieldNumber, ProtoWireType.LengthDelimited);
        WriteVarint((uint)value.Length);
        _writer.Write(value);
    }

    public void WriteMessageField(int fieldNumber, byte[] value)
    {
        WriteBytesField(fieldNumber, value);
    }

    private void WriteTag(int fieldNumber, ProtoWireType wireType)
    {
        var tag = (uint)((fieldNumber << 3) | (int)wireType);
        WriteVarint(tag);
    }

    private void WriteVarint(uint value)
    {
        while (value > 0x7F)
        {
            _writer.GetSpan(1)[0] = (byte)((value & 0x7F) | 0x80);
            _writer.Advance(1);
            value >>= 7;
        }
        _writer.GetSpan(1)[0] = (byte)value;
        _writer.Advance(1);
    }

    public byte[] ToArray() => _writer.WrittenSpan.ToArray();
}

internal sealed class ProtoReader
{
    private readonly byte[] _data;
    private int _offset;

    public ProtoReader(byte[] data)
    {
        _data = data;
        _offset = 0;
    }

    public bool TryReadTag(out int fieldNumber, out ProtoWireType wireType)
    {
        if (_offset >= _data.Length)
        {
            fieldNumber = 0;
            wireType = ProtoWireType.Varint;
            return false;
        }

        var tag = ReadVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (ProtoWireType)(tag & 0x7);
        return true;
    }

    public uint ReadVarint()
    {
        uint result = 0;
        var shift = 0;
        while (_offset < _data.Length)
        {
            var b = _data[_offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }
            shift += 7;
        }
        return result;
    }

    public byte[] ReadBytes()
    {
        var length = (int)ReadVarint();
        if (_offset + length > _data.Length)
        {
            length = Math.Max(0, _data.Length - _offset);
        }

        var bytes = new byte[length];
        Array.Copy(_data, _offset, bytes, 0, length);
        _offset += length;
        return bytes;
    }

    public void SkipField(ProtoWireType wireType)
    {
        switch (wireType)
        {
            case ProtoWireType.Varint:
                ReadVarint();
                break;
            case ProtoWireType.Fixed64:
                _offset += 8;
                break;
            case ProtoWireType.Fixed32:
                _offset += 4;
                break;
            case ProtoWireType.LengthDelimited:
                var length = (int)ReadVarint();
                _offset += length;
                break;
            default:
                break;
        }
    }
}
