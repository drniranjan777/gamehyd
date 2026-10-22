using System;
using System.Buffers.Binary;
using System.Text;

namespace RWBR.Shared.Protocol
{
    public sealed class PacketFormatException : Exception
    {
        public PacketFormatException(string message) : base(message) { }
    }

    /// <summary>Little-endian packet writer. First byte is always the <see cref="PacketType"/>.</summary>
    public sealed class PacketWriter
    {
        private byte[] _buf = new byte[128];
        private int _pos;

        public PacketWriter(PacketType type) => U8((byte)type);

        private Span<byte> Take(int n)
        {
            if (_pos + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));
            Span<byte> s = _buf.AsSpan(_pos, n);
            _pos += n;
            return s;
        }

        public void U8(byte v) => Take(1)[0] = v;
        public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
        public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
        public void F32(float v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), BitConverter.SingleToInt32Bits(v));
        public void Guid(Guid v) => v.TryWriteBytes(Take(16));
        public void I16(short v) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), v);
        public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
        public void Bytes(byte[] v) => v.AsSpan().CopyTo(Take(v.Length));
        public int Length => _pos;

        public void Str(string v, int maxBytes)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(v ?? "");
            int n = Math.Min(bytes.Length, Math.Min(maxBytes, 255));
            U8((byte)n);
            bytes.AsSpan(0, n).CopyTo(Take(n));
        }

        public byte[] ToArray() => _buf.AsSpan(0, _pos).ToArray();
    }

    /// <summary>Bounds-checked reader; any overrun throws <see cref="PacketFormatException"/>.</summary>
    public sealed class PacketReader
    {
        private readonly byte[] _buf;
        private readonly int _len;
        private int _pos;

        public PacketReader(byte[] buffer, int length)
        {
            _buf = buffer;
            _len = length;
            if (length < 1) throw new PacketFormatException("empty packet");
            Type = (PacketType)U8();
        }

        public PacketType Type { get; }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (_pos + n > _len) throw new PacketFormatException("truncated packet");
            ReadOnlySpan<byte> s = _buf.AsSpan(_pos, n);
            _pos += n;
            return s;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public float F32() => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Take(4)));
        public Guid Guid() => new Guid(Take(16));
        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public byte[] Bytes(int n) => Take(n).ToArray();
        public int Position => _pos;
        public int Remaining => _len - _pos;

        public string Str(int maxBytes)
        {
            int n = U8();
            if (n > maxBytes) throw new PacketFormatException("string too long");
            return Encoding.UTF8.GetString(Take(n));
        }
    }
}
