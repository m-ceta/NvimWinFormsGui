using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NvimGuiCommon.Nvim;

/// <summary>
/// Minimal MessagePack encoder/streaming decoder sufficient for Neovim msgpack-rpc.
/// Supports: nil/bool/int/uint/float/double/str/bin/array/map/ext.
/// </summary>
internal static class MsgPack
{
    public static byte[] Pack(object? value)
    {
        using var ms = new MemoryStream();
        PackTo(ms, value);
        return ms.ToArray();
    }

    public static void PackTo(Stream s, object? value)
    {
        switch (value)
        {
            case null:
                s.WriteByte(0xC0);
                return;
            case bool b:
                s.WriteByte(b ? (byte)0xC3 : (byte)0xC2);
                return;

            case byte ub:
                WriteUInt(s, ub);
                return;
            case sbyte sb:
                WriteInt(s, sb);
                return;
            case short i16:
                WriteInt(s, i16);
                return;
            case ushort u16:
                WriteUInt(s, u16);
                return;
            case int i32:
                WriteInt(s, i32);
                return;
            case uint u32:
                WriteUInt(s, u32);
                return;
            case long i64:
                WriteInt(s, i64);
                return;
            case ulong u64:
                WriteUInt(s, u64);
                return;

            case float f:
                s.WriteByte(0xCA);
                WriteBE(s, BitConverter.GetBytes(f));
                return;
            case double d:
                s.WriteByte(0xCB);
                WriteBE(s, BitConverter.GetBytes(d));
                return;

            case string str:
                WriteStr(s, str);
                return;

            case byte[] bin:
                WriteBin(s, bin);
                return;
            case object?[] arr2:
                WriteArrayHeader(s, arr2.Length);
                for (int i = 0; i < arr2.Length; i++) PackTo(s, arr2[i]);
                return;

            case IList<object?> arr:
                WriteArrayHeader(s, arr.Count);
                for (int i = 0; i < arr.Count; i++) PackTo(s, arr[i]);
                return;


            case Dictionary<string, object?> mapStr:
                WriteMapHeader(s, mapStr.Count);
                foreach (var kv in mapStr)
                {
                    WriteStr(s, kv.Key);
                    PackTo(s, kv.Value);
                }
                return;

            case IDictionary<object, object?> mapObj:
                WriteMapHeader(s, mapObj.Count);
                foreach (var kv in mapObj)
                {
                    PackTo(s, kv.Key);
                    PackTo(s, kv.Value);
                }
                return;

            case Ext ext:
                WriteExt(s, ext.Type, ext.Data);
                return;

            default:
                // Try common generic collections
                if (value is System.Collections.IList list)
                {
                    WriteArrayHeader(s, list.Count);
                    for (int i = 0; i < list.Count; i++) PackTo(s, list[i]);
                    return;
                }
                if (value is System.Collections.IDictionary dict)
                {
                    WriteMapHeader(s, dict.Count);
                    foreach (System.Collections.DictionaryEntry de in dict)
                    {
                        PackTo(s, de.Key);
                        PackTo(s, de.Value);
                    }
                    return;
                }
                // Fallback to string
                WriteStr(s, value.ToString() ?? "");
                return;
        }
    }

    private static void WriteStr(Stream s, string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        int n = bytes.Length;
        if (n <= 31)
        {
            s.WriteByte((byte)(0xA0 | n));
        }
        else if (n <= 255)
        {
            s.WriteByte(0xD9);
            s.WriteByte((byte)n);
        }
        else if (n <= 65535)
        {
            s.WriteByte(0xDA);
            WriteBE16(s, (ushort)n);
        }
        else
        {
            s.WriteByte(0xDB);
            WriteBE32(s, (uint)n);
        }
        s.Write(bytes, 0, n);
    }

    private static void WriteBin(Stream s, byte[] bin)
    {
        int n = bin.Length;
        if (n <= 255)
        {
            s.WriteByte(0xC4);
            s.WriteByte((byte)n);
        }
        else if (n <= 65535)
        {
            s.WriteByte(0xC5);
            WriteBE16(s, (ushort)n);
        }
        else
        {
            s.WriteByte(0xC6);
            WriteBE32(s, (uint)n);
        }
        s.Write(bin, 0, n);
    }

    private static void WriteArrayHeader(Stream s, int n)
    {
        if (n <= 15)
        {
            s.WriteByte((byte)(0x90 | n));
        }
        else if (n <= 65535)
        {
            s.WriteByte(0xDC);
            WriteBE16(s, (ushort)n);
        }
        else
        {
            s.WriteByte(0xDD);
            WriteBE32(s, (uint)n);
        }
    }

    private static void WriteMapHeader(Stream s, int n)
    {
        if (n <= 15)
        {
            s.WriteByte((byte)(0x80 | n));
        }
        else if (n <= 65535)
        {
            s.WriteByte(0xDE);
            WriteBE16(s, (ushort)n);
        }
        else
        {
            s.WriteByte(0xDF);
            WriteBE32(s, (uint)n);
        }
    }

    private static void WriteExt(Stream s, sbyte type, byte[] data)
    {
        int n = data.Length;
        if (n == 1) s.WriteByte(0xD4);
        else if (n == 2) s.WriteByte(0xD5);
        else if (n == 4) s.WriteByte(0xD6);
        else if (n == 8) s.WriteByte(0xD7);
        else if (n == 16) s.WriteByte(0xD8);
        else if (n <= 255) { s.WriteByte(0xC7); s.WriteByte((byte)n); }
        else if (n <= 65535) { s.WriteByte(0xC8); WriteBE16(s, (ushort)n); }
        else { s.WriteByte(0xC9); WriteBE32(s, (uint)n); }

        s.WriteByte(unchecked((byte)type));
        s.Write(data, 0, n);
    }

    private static void WriteUInt(Stream s, ulong v)
    {
        if (v <= 0x7F) { s.WriteByte((byte)v); }
        else if (v <= byte.MaxValue) { s.WriteByte(0xCC); s.WriteByte((byte)v); }
        else if (v <= ushort.MaxValue) { s.WriteByte(0xCD); WriteBE16(s, (ushort)v); }
        else if (v <= uint.MaxValue) { s.WriteByte(0xCE); WriteBE32(s, (uint)v); }
        else { s.WriteByte(0xCF); WriteBE64(s, v); }
    }

    private static void WriteUInt(Stream s, uint v) => WriteUInt(s, (ulong)v);
    private static void WriteUInt(Stream s, ushort v) => WriteUInt(s, (ulong)v);
    private static void WriteUInt(Stream s, byte v) => WriteUInt(s, (ulong)v);

    private static void WriteInt(Stream s, long v)
    {
        if (v >= 0 && v <= 0x7F) { s.WriteByte((byte)v); return; }
        if (v < 0 && v >= -32) { s.WriteByte(unchecked((byte)v)); return; }

        if (v >= sbyte.MinValue && v <= sbyte.MaxValue) { s.WriteByte(0xD0); s.WriteByte(unchecked((byte)(sbyte)v)); }
        else if (v >= short.MinValue && v <= short.MaxValue) { s.WriteByte(0xD1); WriteBE16(s, unchecked((ushort)(short)v)); }
        else if (v >= int.MinValue && v <= int.MaxValue) { s.WriteByte(0xD2); WriteBE32(s, unchecked((uint)(int)v)); }
        else { s.WriteByte(0xD3); WriteBE64(s, unchecked((ulong)v)); }
    }

    private static void WriteInt(Stream s, int v) => WriteInt(s, (long)v);
    private static void WriteInt(Stream s, short v) => WriteInt(s, (long)v);
    private static void WriteInt(Stream s, sbyte v) => WriteInt(s, (long)v);

    private static void WriteBE(Stream s, byte[] bytes)
    {
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteBE16(Stream s, ushort v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v));
    }

    private static void WriteBE32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v));
    }

    private static void WriteBE64(Stream s, ulong v)
    {
        s.WriteByte((byte)(v >> 56));
        s.WriteByte((byte)(v >> 48));
        s.WriteByte((byte)(v >> 40));
        s.WriteByte((byte)(v >> 32));
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v));
    }

    public sealed class Ext
    {
        public sbyte Type { get; }
        public byte[] Data { get; }
        public Ext(sbyte type, byte[] data) { Type = type; Data = data; }
        public override string ToString() => $"Ext(type={Type}, len={Data.Length})";
    }
}

internal sealed class MsgPackStreamReader
{
    private readonly Stream _stream;
    private readonly byte[] _readBuf = new byte[64 * 1024];
    private byte[] _buf = new byte[128 * 1024];
    private int _len;

    public MsgPackStreamReader(Stream stream) { _stream = stream; }

    public async System.Threading.Tasks.Task<object?> ReadNextAsync(System.Threading.CancellationToken ct)
    {
        while (true)
        {
            if (TryParse(out var value, out int consumed))
            {
                Consume(consumed);
                return value;
            }

            int n = await _stream.ReadAsync(_readBuf, 0, _readBuf.Length, ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();

            EnsureCapacity(_len + n);
            Buffer.BlockCopy(_readBuf, 0, _buf, _len, n);
            _len += n;
        }
    }

    private void Consume(int n)
    {
        if (n <= 0) return;
        Buffer.BlockCopy(_buf, n, _buf, 0, _len - n);
        _len -= n;
    }

    private void EnsureCapacity(int needed)
    {
        if (_buf.Length >= needed) return;
        int cap = _buf.Length;
        while (cap < needed) cap *= 2;
        Array.Resize(ref _buf, cap);
    }

    private bool TryParse(out object? value, out int consumed)
    {
        value = null;
        consumed = 0;
        var span = new ReadOnlySpan<byte>(_buf, 0, _len);
        return MsgPackParser.TryRead(span, out value, out consumed);
    }
}

internal static class MsgPackParser
{
    public static bool TryRead(ReadOnlySpan<byte> s, out object? value, out int consumed)
    {
        value = null;
        consumed = 0;
        if (s.Length < 1) return false;

        byte b = s[0];

        // positive fixint
        if (b <= 0x7F) { value = (long)b; consumed = 1; return true; }
        // fixmap
        if ((b & 0xF0) == 0x80) return ReadMapFix(s, b & 0x0F, out value, out consumed);
        // fixarray
        if ((b & 0xF0) == 0x90) return ReadArrayFix(s, b & 0x0F, out value, out consumed);
        // fixstr
        if ((b & 0xE0) == 0xA0) return ReadStrFix(s, b & 0x1F, out value, out consumed);
        // negative fixint
        if (b >= 0xE0) { value = (long)unchecked((sbyte)b); consumed = 1; return true; }

        switch (b)
        {
            case 0xC0: value = null; consumed = 1; return true;
            case 0xC2: value = false; consumed = 1; return true;
            case 0xC3: value = true; consumed = 1; return true;

            case 0xCC: return ReadUInt(s, 1, out value, out consumed);
            case 0xCD: return ReadUInt(s, 2, out value, out consumed);
            case 0xCE: return ReadUInt(s, 4, out value, out consumed);
            case 0xCF: return ReadUInt(s, 8, out value, out consumed);

            case 0xD0: return ReadInt(s, 1, out value, out consumed);
            case 0xD1: return ReadInt(s, 2, out value, out consumed);
            case 0xD2: return ReadInt(s, 4, out value, out consumed);
            case 0xD3: return ReadInt(s, 8, out value, out consumed);

            case 0xCA: return ReadFloat32(s, out value, out consumed);
            case 0xCB: return ReadFloat64(s, out value, out consumed);

            case 0xD9: return ReadStrX(s, 1, out value, out consumed);
            case 0xDA: return ReadStrX(s, 2, out value, out consumed);
            case 0xDB: return ReadStrX(s, 4, out value, out consumed);

            case 0xC4: return ReadBinX(s, 1, out value, out consumed);
            case 0xC5: return ReadBinX(s, 2, out value, out consumed);
            case 0xC6: return ReadBinX(s, 4, out value, out consumed);

            case 0xDC: return ReadArrayX(s, 2, out value, out consumed);
            case 0xDD: return ReadArrayX(s, 4, out value, out consumed);

            case 0xDE: return ReadMapX(s, 2, out value, out consumed);
            case 0xDF: return ReadMapX(s, 4, out value, out consumed);

            // ext
            case 0xD4: return ReadExtFix(s, 1, out value, out consumed);
            case 0xD5: return ReadExtFix(s, 2, out value, out consumed);
            case 0xD6: return ReadExtFix(s, 4, out value, out consumed);
            case 0xD7: return ReadExtFix(s, 8, out value, out consumed);
            case 0xD8: return ReadExtFix(s, 16, out value, out consumed);
            case 0xC7: return ReadExtX(s, 1, out value, out consumed);
            case 0xC8: return ReadExtX(s, 2, out value, out consumed);
            case 0xC9: return ReadExtX(s, 4, out value, out consumed);

            default:
                return false;
        }
    }

    private static bool ReadUInt(ReadOnlySpan<byte> s, int n, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 1 + n) return false;
        ulong v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | s[1 + i];
        value = (long)v;
        consumed = 1 + n;
        return true;
    }

    private static bool ReadInt(ReadOnlySpan<byte> s, int n, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 1 + n) return false;
        long v;
        if (n == 1) v = (sbyte)s[1];
        else if (n == 2) v = (short)((s[1] << 8) | s[2]);
        else if (n == 4) v = (int)((s[1] << 24) | (s[2] << 16) | (s[3] << 8) | s[4]);
        else
        {
            ulong u = 0;
            for (int i = 0; i < 8; i++) u = (u << 8) | s[1 + i];
            v = unchecked((long)u);
        }
        value = v;
        consumed = 1 + n;
        return true;
    }

    private static bool ReadFloat32(ReadOnlySpan<byte> s, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 5) return false;
        Span<byte> tmp = stackalloc byte[4];
        s.Slice(1, 4).CopyTo(tmp);
        if (BitConverter.IsLittleEndian) tmp.Reverse();
        value = BitConverter.ToSingle(tmp);
        consumed = 5;
        return true;
    }

    private static bool ReadFloat64(ReadOnlySpan<byte> s, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 9) return false;
        Span<byte> tmp = stackalloc byte[8];
        s.Slice(1, 8).CopyTo(tmp);
        if (BitConverter.IsLittleEndian) tmp.Reverse();
        value = BitConverter.ToDouble(tmp);
        consumed = 9;
        return true;
    }

    private static bool ReadStrFix(ReadOnlySpan<byte> s, int n, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 1 + n) return false;
        value = Encoding.UTF8.GetString(s.Slice(1, n));
        consumed = 1 + n;
        return true;
    }

    private static bool ReadStrX(ReadOnlySpan<byte> s, int lenBytes, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 1 + lenBytes) return false;
        int n = ReadLen(s.Slice(1, lenBytes));
        if (s.Length < 1 + lenBytes + n) return false;
        value = Encoding.UTF8.GetString(s.Slice(1 + lenBytes, n));
        consumed = 1 + lenBytes + n;
        return true;
    }

    private static bool ReadBinX(ReadOnlySpan<byte> s, int lenBytes, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 1 + lenBytes) return false;
        int n = ReadLen(s.Slice(1, lenBytes));
        if (s.Length < 1 + lenBytes + n) return false;
        value = s.Slice(1 + lenBytes, n).ToArray();
        consumed = 1 + lenBytes + n;
        return true;
    }

    private static bool ReadArrayFix(ReadOnlySpan<byte> s, int n, out object? value, out int consumed)
        => ReadArrayAt(s, 1, n, out value, out consumed);

    private static bool ReadArrayX(ReadOnlySpan<byte> s, int lenBytes, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 1 + lenBytes) return false;
        int n = ReadLen(s.Slice(1, lenBytes));
        int offset = 1 + lenBytes;
        return ReadArrayAt(s, offset, n, out value, out consumed);
    }

    private static bool ReadArrayAt(ReadOnlySpan<byte> s, int offset, int n, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (offset > s.Length) return false;

        var list = new List<object?>(n);
        int pos = offset;
        for (int i = 0; i < n; i++)
        {
            if (!TryRead(s.Slice(pos), out var v, out int c)) return false;
            list.Add(v);
            pos += c;
            if (pos > s.Length) return false;
        }
        value = list;
        consumed = pos;
        return true;
    }

    private static bool ReadMapFix(ReadOnlySpan<byte> s, int n, out object? value, out int consumed)
        => ReadMapAt(s, 1, n, out value, out consumed);

    private static bool ReadMapX(ReadOnlySpan<byte> s, int lenBytes, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 1 + lenBytes) return false;
        int n = ReadLen(s.Slice(1, lenBytes));
        int offset = 1 + lenBytes;
        return ReadMapAt(s, offset, n, out value, out consumed);
    }

    private static bool ReadMapAt(ReadOnlySpan<byte> s, int offset, int n, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (offset > s.Length) return false;

        var dict = new Dictionary<object, object?>(n);
        int pos = offset;
        for (int i = 0; i < n; i++)
        {
            if (!TryRead(s.Slice(pos), out var k, out int ck)) return false;
            pos += ck;
            if (!TryRead(s.Slice(pos), out var v, out int cv)) return false;
            pos += cv;
            dict[k ?? ""] = v;
            if (pos > s.Length) return false;
        }
        value = dict;
        consumed = pos;
        return true;
    }

    private static bool ReadExtFix(ReadOnlySpan<byte> s, int n, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 2 + n) return false;
        sbyte type = unchecked((sbyte)s[1]);
        var data = s.Slice(2, n).ToArray();
        value = new MsgPack.Ext(type, data);
        consumed = 2 + n;
        return true;
    }

    private static bool ReadExtX(ReadOnlySpan<byte> s, int lenBytes, out object? value, out int consumed)
    {
        value = null; consumed = 0;
        if (s.Length < 1 + lenBytes + 1) return false;
        int n = ReadLen(s.Slice(1, lenBytes));
        if (s.Length < 1 + lenBytes + 1 + n) return false;
        sbyte type = unchecked((sbyte)s[1 + lenBytes]);
        var data = s.Slice(1 + lenBytes + 1, n).ToArray();
        value = new MsgPack.Ext(type, data);
        consumed = 1 + lenBytes + 1 + n;
        return true;
    }

    private static int ReadLen(ReadOnlySpan<byte> b)
    {
        int n = 0;
        for (int i = 0; i < b.Length; i++) n = (n << 8) | b[i];
        return n;
    }
}
