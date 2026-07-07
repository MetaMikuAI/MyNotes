using System.Buffers;
using System.Text;

namespace MyNotes.Services;

public sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteString(int fieldNumber, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        WriteTag(fieldNumber, 2);
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint((ulong)bytes.Length);
        _stream.Write(bytes);
    }

    public void WriteInt64(int fieldNumber, long value)
    {
        if (value == 0)
            return;

        WriteTag(fieldNumber, 0);
        WriteVarint((ulong)value);
    }

    public void WriteInt32(int fieldNumber, int value)
    {
        if (value == 0)
            return;

        WriteTag(fieldNumber, 0);
        WriteVarint((ulong)value);
    }

    public void WriteBool(int fieldNumber, bool value)
    {
        if (!value)
            return;

        WriteTag(fieldNumber, 0);
        WriteVarint(1);
    }

    public void WriteBytes(int fieldNumber, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return;

        WriteTag(fieldNumber, 2);
        WriteVarint((ulong)value.Length);
        _stream.Write(value);
    }

    public void WriteMessage(int fieldNumber, byte[]? value)
    {
        if (value is not { Length: > 0 })
            return;

        WriteTag(fieldNumber, 2);
        WriteVarint((ulong)value.Length);
        _stream.Write(value);
    }

    public void WriteEmptyMessage(int fieldNumber)
    {
        WriteTag(fieldNumber, 2);
        WriteVarint(0);
    }

    public byte[] ToArray() => _stream.ToArray();

    private void WriteTag(int fieldNumber, int wireType) => WriteVarint((ulong)((fieldNumber << 3) | wireType));

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        _stream.WriteByte((byte)value);
    }
}

public ref struct ProtoReader(ReadOnlySpan<byte> buffer)
{
    private ReadOnlySpan<byte> _buffer = buffer;

    public bool TryReadField(out int fieldNumber, out int wireType, out ReadOnlySpan<byte> value)
    {
        fieldNumber = 0;
        wireType = 0;
        value = default;

        if (_buffer.IsEmpty)
            return false;

        var tag = ReadVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 7);

        switch (wireType)
        {
            case 0:
                var start = _buffer;
                ReadVarint();
                value = start[..(start.Length - _buffer.Length)];
                return true;
            case 2:
                var length = (int)ReadVarint();
                value = _buffer[..length];
                _buffer = _buffer[length..];
                return true;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
        }
    }

    private ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;

        while (true)
        {
            if (_buffer.IsEmpty)
                throw new InvalidDataException("Unexpected end of protobuf varint.");

            var b = _buffer[0];
            _buffer = _buffer[1..];
            result |= (ulong)(b & 0x7f) << shift;

            if ((b & 0x80) == 0)
                return result;

            shift += 7;
            if (shift > 63)
                throw new InvalidDataException("Invalid protobuf varint.");
        }
    }
}
