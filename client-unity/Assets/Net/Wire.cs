// Little-endian binary reader/writer for the wire protocol (docs/PROTOCOL.md
// "Transport": binary messages only, all integers little-endian, no padding,
// no alignment).
//
// NO UnityEngine. Net.asmdef is declared "noEngineReferences" and is compiled
// headless by headless/Net/Net.csproj, because C41 (codec parity against the
// Go and Node implementations) and C44 (Sim and Net test with no Editor) both
// have to run in CI.
//
// BitConverter is deliberately not used to move multi-byte values. Its
// GetBytes follows the HOST's endianness, so the same call is correct on x64
// and silently wrong elsewhere; every integer here is written out byte by
// byte, which is endian-independent by construction and is what the Go and
// TypeScript codecs both do. Reinterpreting a float as its bits is a
// different question and is handled by F32Bits below.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SpaceAdventure.Net
{
    /// <summary>
    /// Sequential little-endian reader over a frame payload. Every read is
    /// bounds-checked: a truncated or hostile frame must produce a clean
    /// <see cref="WireException"/>, never a partially-decoded message or an
    /// out-of-range crash inside the render loop.
    /// </summary>
    public struct WireReader
    {
        private readonly byte[] _buf;
        private readonly int _end;
        private int _pos;

        public WireReader(byte[] buf) : this(buf, 0, buf?.Length ?? 0) { }

        public WireReader(byte[] buf, int offset, int count)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if (offset < 0 || count < 0 || offset + count > buf.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "reader window outside the buffer");
            }
            _buf = buf;
            _pos = offset;
            _end = offset + count;
        }

        /// <summary>Bytes not yet consumed.</summary>
        public int Remaining => _end - _pos;

        /// <summary>Position within the underlying buffer.</summary>
        public int Position => _pos;

        private void Need(int n, string what)
        {
            if (Remaining < n)
            {
                throw new WireException($"{what}: need {n} bytes, {Remaining} left");
            }
        }

        public byte ReadU8(string what = "u8")
        {
            Need(1, what);
            return _buf[_pos++];
        }

        public sbyte ReadI8(string what = "i8") => unchecked((sbyte)ReadU8(what));

        public ushort ReadU16(string what = "u16")
        {
            Need(2, what);
            ushort v = (ushort)(_buf[_pos] | (_buf[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public uint ReadU32(string what = "u32")
        {
            Need(4, what);
            uint v = (uint)(_buf[_pos]
                            | (_buf[_pos + 1] << 8)
                            | (_buf[_pos + 2] << 16)
                            | (_buf[_pos + 3] << 24));
            _pos += 4;
            return v;
        }

        /// <summary>IEEE-754 single, little-endian.</summary>
        public float ReadF32(string what = "f32") => BitsToFloat(ReadU32(what));

        /// <summary>Reads <paramref name="n"/> raw bytes as a new array.</summary>
        public byte[] ReadBytes(int n, string what = "bytes")
        {
            if (n < 0) throw new WireException($"{what}: negative length {n}");
            Need(n, what);
            var outBuf = new byte[n];
            Array.Copy(_buf, _pos, outBuf, 0, n);
            _pos += n;
            return outBuf;
        }

        /// <summary>Reads the rest of the window as raw bytes.</summary>
        public byte[] ReadRest(string what = "rest") => ReadBytes(Remaining, what);

        /// <summary>
        /// Reads a u32 length followed by that many UTF-8 bytes — the shape
        /// every variable-length field on this wire uses (`hello` name and
        /// token, `spawn` data, `cmd` body).
        /// </summary>
        /// <summary>
        /// A u16-length-prefixed UTF-8 string, as `props` uses for an asset id.
        /// Bounded before the allocation for the same reason the u32 form is:
        /// a length field is attacker-controlled input, not a promise.
        /// </summary>
        public string ReadU16Utf8(string what = "string")
        {
            ushort n = ReadU16(what + " length");
            if (n > Remaining)
            {
                throw new WireException($"{what}: length {n} exceeds the {Remaining} bytes remaining");
            }
            return Utf8.GetString(ReadBytes(n, what));
        }

        public string ReadLengthPrefixedUtf8(string what = "string")
        {
            uint n = ReadU32(what + " length");
            // The cast is safe only after the bound: a hostile u32 length is
            // otherwise a multi-gigabyte allocation from one 4-byte field.
            if (n > (uint)Remaining)
            {
                throw new WireException($"{what}: length {n} exceeds the {Remaining} bytes remaining");
            }
            return Utf8.GetString(ReadBytes((int)n, what));
        }

        /// <summary>UTF-8 over the next <paramref name="n"/> bytes.</summary>
        public string ReadUtf8(int n, string what = "string") => Utf8.GetString(ReadBytes(n, what));

        /// <summary>Throws unless the window is fully consumed.</summary>
        public void ExpectEnd(string what)
        {
            if (Remaining != 0)
            {
                throw new WireException($"{what}: {Remaining} trailing bytes");
            }
        }

        /// <summary>The wire's text encoding. Strict: invalid UTF-8 is data, not an exception.</summary>
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        internal static float BitsToFloat(uint bits) => new F32Bits { U = bits }.F;
    }

    /// <summary>
    /// Reinterprets a float as its IEEE-754 bits and back.
    ///
    /// The bit pattern is what the wire carries, so the conversion has to be
    /// exact for every value including NaN payloads, negative zero and
    /// subnormals — which is why this overlays the two rather than taking a
    /// float apart with arithmetic. Byte ORDER is still handled explicitly by
    /// the reader and writer, so nothing here depends on the host's
    /// endianness.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct F32Bits
    {
        [FieldOffset(0)] public float F;
        [FieldOffset(0)] public uint U;
    }

    /// <summary>
    /// Growable little-endian writer. Frames are small (the largest is a
    /// 64 KiB terrain message the client never sends), so this keeps one
    /// array and doubles it rather than pooling.
    /// </summary>
    public sealed class WireWriter
    {
        private byte[] _buf;
        private int _len;

        public WireWriter(int capacity = 64)
        {
            _buf = new byte[Math.Max(4, capacity)];
        }

        public int Length => _len;

        private void Need(int n)
        {
            if (_len + n <= _buf.Length) return;
            int cap = _buf.Length;
            while (cap < _len + n) cap *= 2;
            Array.Resize(ref _buf, cap);
        }

        public WireWriter U8(byte v)
        {
            Need(1);
            _buf[_len++] = v;
            return this;
        }

        public WireWriter I8(sbyte v) => U8(unchecked((byte)v));

        public WireWriter U16(ushort v)
        {
            Need(2);
            _buf[_len++] = (byte)(v & 0xFF);
            _buf[_len++] = (byte)((v >> 8) & 0xFF);
            return this;
        }

        public WireWriter U32(uint v)
        {
            Need(4);
            _buf[_len++] = (byte)(v & 0xFF);
            _buf[_len++] = (byte)((v >> 8) & 0xFF);
            _buf[_len++] = (byte)((v >> 16) & 0xFF);
            _buf[_len++] = (byte)((v >> 24) & 0xFF);
            return this;
        }

        public WireWriter F32(float v) => U32(FloatToBits(v));

        public WireWriter Bytes(byte[] v)
        {
            if (v == null || v.Length == 0) return this;
            Need(v.Length);
            Array.Copy(v, 0, _buf, _len, v.Length);
            _len += v.Length;
            return this;
        }

        /// <summary>u32 length followed by the UTF-8 bytes.</summary>
        public WireWriter LengthPrefixedUtf8(string s)
        {
            byte[] b = s == null ? Array.Empty<byte>() : WireReader.Utf8.GetBytes(s);
            U32((uint)b.Length);
            return Bytes(b);
        }

        /// <summary>The frame built so far, as a fresh array.</summary>
        public byte[] ToArray()
        {
            var outBuf = new byte[_len];
            Array.Copy(_buf, outBuf, _len);
            return outBuf;
        }

        internal static uint FloatToBits(float value) => new F32Bits { F = value }.U;
    }

    /// <summary>
    /// A malformed frame. Thrown by the codec, caught by the transport, which
    /// closes the connection — PROTOCOL.md's rule that a bad payload is a
    /// protocol error, not something to decode halfway and carry on with.
    /// </summary>
    public sealed class WireException : Exception
    {
        public WireException(string message) : base(message) { }
    }
}
