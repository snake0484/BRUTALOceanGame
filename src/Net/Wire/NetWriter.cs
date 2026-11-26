using System.Buffers.Binary;

namespace BrutalGame.Net.Wire;

/// <summary>Little-endian writer over a caller's buffer. Overflow sets <see cref="Overflow"/> and drops the write; it never throws.</summary>
public ref struct NetWriter(Span<byte> buffer)
{
    private readonly Span<byte> _buffer = buffer;
    private int _position;

    public readonly int Position => _position;

    public readonly int Remaining => _buffer.Length - _position;

    public bool Overflow { get; private set; }

    public readonly ReadOnlySpan<byte> Written => _buffer[.._position];

    public void Byte(byte value)
    {
        if (Reserve(1, out var at))
            _buffer[at] = value;
    }

    public void UShort(ushort value)
    {
        if (Reserve(2, out var at))
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer[at..], value);
    }

    public void UInt(uint value)
    {
        if (Reserve(4, out var at))
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer[at..], value);
    }

    public void Long(long value)
    {
        if (Reserve(8, out var at))
            BinaryPrimitives.WriteInt64LittleEndian(_buffer[at..], value);
    }

    public void Float(float value)
    {
        if (Reserve(4, out var at))
            BinaryPrimitives.WriteSingleLittleEndian(_buffer[at..], value);
    }

    public void VarUInt(ulong value)
    {
        while (value >= 0x80)
        {
            Byte((byte)(value | 0x80));
            value >>= 7;
        }

        Byte((byte)value);
    }

    /// <summary>Zigzag varint: small magnitudes of either sign take one byte.</summary>
    public void VarInt(long value) => VarUInt((ulong)((value << 1) ^ (value >> 63)));

    public void Bytes(scoped ReadOnlySpan<byte> data)
    {
        if (Reserve(data.Length, out var at))
            data.CopyTo(_buffer[at..]);
    }

    /// <summary>Reserves room for a later <see cref="PatchUShort"/> and returns where it is.</summary>
    public int ReserveUShort()
    {
        var at = _position;
        UShort(0);
        return at;
    }

    public readonly void PatchUShort(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(_buffer[at..], value);

    /// <summary>Rolls back to <paramref name="position"/>, clearing an overflow past it.</summary>
    public void Rewind(int position)
    {
        _position = position;
        Overflow = false;
    }

    private bool Reserve(int count, out int at)
    {
        at = _position;

        if (Overflow || _position + count > _buffer.Length)
        {
            Overflow = true;
            return false;
        }

        _position += count;
        return true;
    }
}

/// <summary>Reader matching <see cref="NetWriter"/>. Reading past the end sets <see cref="Failed"/> and returns zeros.</summary>
public ref struct NetReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position;

    public readonly int Position => _position;

    public readonly int Remaining => _buffer.Length - _position;

    public bool Failed { get; private set; }

    public byte Byte() => Take(1, out var at) ? _buffer[at] : (byte)0;

    public ushort UShort() => Take(2, out var at) ? BinaryPrimitives.ReadUInt16LittleEndian(_buffer[at..]) : (ushort)0;

    public uint UInt() => Take(4, out var at) ? BinaryPrimitives.ReadUInt32LittleEndian(_buffer[at..]) : 0u;

    public long Long() => Take(8, out var at) ? BinaryPrimitives.ReadInt64LittleEndian(_buffer[at..]) : 0L;

    public float Float() => Take(4, out var at) ? BinaryPrimitives.ReadSingleLittleEndian(_buffer[at..]) : 0f;

    public ulong VarUInt()
    {
        ulong value = 0;

        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = Byte();
            value |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return value;
        }

        Failed = true;
        return 0;
    }

    public long VarInt()
    {
        var raw = VarUInt();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    public ReadOnlySpan<byte> Bytes(int count) => Take(count, out var at) ? _buffer.Slice(at, count) : default;

    private bool Take(int count, out int at)
    {
        at = _position;

        if (Failed || count < 0 || _position + count > _buffer.Length)
        {
            Failed = true;
            return false;
        }

        _position += count;
        return true;
    }
}
