using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ClearSkies.Engine.Serialization;

/// <summary>Writes little-endian values into a growable buffer, for commands, events, descriptions and packets. Reuse
/// one with <see cref="Clear"/> rather than allocating per message.</summary>
public sealed class NetWriter
{
    private byte[] _buffer;
    private int _length;

    public NetWriter(int capacity = 256) => _buffer = new byte[capacity];

    public int Length => _length;
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);
    public byte[] ToArray() => Written.ToArray();
    public void Clear() => _length = 0;

    private Span<byte> Take(int count)
    {
        if (_length + count > _buffer.Length)
            Array.Resize(ref _buffer, System.Math.Max(_buffer.Length * 2, _length + count));
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public void WriteByte(byte v) => Take(1)[0] = v;
    public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);
    public void WriteUInt16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
    public void WriteInt16(short v) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), v);
    public void WriteUInt32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    public void WriteInt32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
    public void WriteUInt64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
    public void WriteInt64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
    public void WriteSingle(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
    public void WriteDouble(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), v);
    public void WriteHalf(float v) => BinaryPrimitives.WriteHalfLittleEndian(Take(2), (Half)v);

    public void WriteVector3(Vector3 v) { WriteSingle(v.X); WriteSingle(v.Y); WriteSingle(v.Z); }

    public void WriteQuaternion(Quaternion q) { WriteSingle(q.X); WriteSingle(q.Y); WriteSingle(q.Z); WriteSingle(q.W); }

    public void WriteGuid(Guid g) => g.TryWriteBytes(Take(16));

    /// <summary>A length (up to 2^32) in 1-5 bytes.</summary>
    public void WriteVarUInt(uint v)
    {
        while (v >= 0x80) { WriteByte((byte)(v | 0x80)); v >>= 7; }
        WriteByte((byte)v);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        WriteVarUInt((uint)bytes.Length);
        bytes.CopyTo(Take(bytes.Length));
    }

    /// <summary>Raw bytes with no length in front.</summary>
    public void WriteRaw(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Take(bytes.Length));

    public void WriteString(string s)
    {
        int count = Encoding.UTF8.GetByteCount(s);
        WriteVarUInt((uint)count);
        Encoding.UTF8.GetBytes(s, Take(count));
    }
}

/// <summary>Reads what a <see cref="NetWriter"/> wrote. Throws <see cref="EndOfStreamException"/> past the end, so a
/// truncated or corrupt message fails loudly instead of reading garbage.</summary>
public ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public NetReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Position => _position;
    public readonly int Remaining => _data.Length - _position;
    public readonly bool AtEnd => _position >= _data.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _position + count > _data.Length) throw new EndOfStreamException("Read past the end of a message.");
        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];
    public bool ReadBool() => ReadByte() != 0;
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public float ReadHalf() => (float)BinaryPrimitives.ReadHalfLittleEndian(Take(2));
    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
    public Quaternion ReadQuaternion() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
    public Guid ReadGuid() => new(Take(16));

    public uint ReadVarUInt()
    {
        uint value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = ReadByte();
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Malformed length.");
    }

    public ReadOnlySpan<byte> ReadBytes() => Take((int)ReadVarUInt());

    public ReadOnlySpan<byte> ReadRaw(int count) => Take(count);

    public string ReadString() => Encoding.UTF8.GetString(Take((int)ReadVarUInt()));
}
