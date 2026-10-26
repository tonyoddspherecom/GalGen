using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace UniverseGen.Service.Protocol;

/// <summary>Writes the protocol's primitives: little-endian fixed sizes, LEB128 varints, length-prefixed UTF-8.</summary>
internal sealed class WireWriter
{
    private readonly ArrayBufferWriter<byte> _buffer;

    public WireWriter(int capacity = 256) => _buffer = new ArrayBufferWriter<byte>(capacity);

    public int Length => _buffer.WrittenCount;
    public ReadOnlySpan<byte> Written => _buffer.WrittenSpan;
    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    private Span<byte> Take(int size)
    {
        Span<byte> span = _buffer.GetSpan(size)[..size];
        _buffer.Advance(size);
        return span;
    }

    public void U8(byte v) => Take(1)[0] = v;
    public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
    public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
    public void F64(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), v);

    public void VarU(ulong v)
    {
        while (v >= 0x80)
        {
            U8((byte)(v | 0x80));
            v >>= 7;
        }
        U8((byte)v);
    }

    /// Zigzag, so small negative numbers stay small.
    public void VarI(long v) => VarU((ulong)((v << 1) ^ (v >> 63)));

    public void Str(string v)
    {
        int count = Encoding.UTF8.GetByteCount(v);
        VarU((ulong)count);
        Encoding.UTF8.GetBytes(v, Take(count));
    }

    public void Bytes(ReadOnlySpan<byte> v)
    {
        VarU((ulong)v.Length);
        v.CopyTo(Take(v.Length));
    }

    public void Raw(ReadOnlySpan<byte> v) => v.CopyTo(Take(v.Length));

    public void Vec3(Vector3 v)
    {
        F32(v.X);
        F32(v.Y);
        F32(v.Z);
    }
}

/// <summary>
/// Reads what <see cref="WireWriter"/> wrote. Anything cut short or impossible (a count bigger than the
/// bytes left, an unknown enum value) is a <see cref="GalaxyError.BadRequest"/>, never a crash or a huge
/// allocation, so a server can take frames from anyone.
/// </summary>
internal sealed class WireReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public WireReader(ReadOnlyMemory<byte> data, int start = 0)
    {
        _data = data;
        _position = start;
    }

    public int Remaining => _data.Length - _position;
    public bool AtEnd => _position == _data.Length;

    private static GalaxyServiceException Malformed(string what) => new(GalaxyError.BadRequest, $"Malformed message: {what}.");

    private ReadOnlySpan<byte> Take(int size)
    {
        if (size < 0 || size > Remaining) throw Malformed("it ends too soon");
        ReadOnlySpan<byte> span = _data.Span.Slice(_position, size);
        _position += size;
        return span;
    }

    public byte U8() => Take(1)[0];

    public bool Bool() => U8() switch
    {
        0 => false,
        1 => true,
        _ => throw Malformed("a flag isn't 0 or 1"),
    };

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public ulong VarU()
    {
        ulong v = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte b = U8();
            v |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return v;
        }
        throw Malformed("a number is too long");
    }

    public long VarI()
    {
        ulong v = VarU();
        return (long)(v >> 1) ^ -(long)(v & 1);
    }

    /// A varint that must fit an int in [min, max].
    public int Int(int min = int.MinValue, int max = int.MaxValue)
    {
        long v = VarI();
        if (v < min || v > max) throw Malformed($"{v} is out of range");
        return (int)v;
    }

    /// A count of items each at least <paramref name="minItemBytes"/> long: never more than the bytes left could hold.
    public int Count(int minItemBytes = 1)
    {
        ulong n = VarU();
        if (n > (ulong)(Remaining / Math.Max(minItemBytes, 1))) throw Malformed("a count is bigger than the message");
        return (int)n;
    }

    public string Str()
    {
        int length = Count();
        return Encoding.UTF8.GetString(Take(length));
    }

    public byte[] Bytes() => Take(Count()).ToArray();

    public Vector3 Vec3() => new(F32(), F32(), F32());

    public T Enum<T>() where T : struct, Enum
    {
        byte raw = U8();
        var value = (T)System.Enum.ToObject(typeof(T), raw);
        if (!System.Enum.IsDefined(value)) throw Malformed($"{raw} isn't a {typeof(T).Name}");
        return value;
    }

    /// A [Flags] enum: any combination of defined bits.
    public T Flags<T>(byte allBits) where T : struct, Enum
    {
        byte raw = U8();
        if ((raw & ~allBits) != 0) throw Malformed($"{raw} has bits no {typeof(T).Name} has");
        return (T)System.Enum.ToObject(typeof(T), raw);
    }

    public void End()
    {
        if (!AtEnd) throw Malformed("there's more after the end");
    }
}
