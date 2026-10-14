/**
 * @file
 * @brief Kodiert die verwendete MessagePack-Teilmenge und Prüfsummen des Ergebnisdateiformats.
 */

using System.Buffers.Binary;
using System.Text;

namespace Testumgebung.Persistence.Results;

/// <summary>
/// Minimaler MessagePack-Kodierer (Teilmenge der Spezifikation: nil, bool, int, float64, str, bin, array, map).
/// Die erzeugten Bytes sind mit jeder MessagePack-Bibliothek lesbar – das Dateiformat hängt nicht an dieser Implementierung.
/// </summary>
public sealed class MsgPackWriter
{
    private readonly MemoryStream _out = new();

    public byte[] ToArray() => _out.ToArray();

    public MsgPackWriter Nil()
    {
        _out.WriteByte(0xc0);
        return this;
    }

    public MsgPackWriter Bool(bool v)
    {
        _out.WriteByte(v ? (byte)0xc3 : (byte)0xc2);
        return this;
    }

    public MsgPackWriter Int(long v)
    {
        Span<byte> b = stackalloc byte[9];
        if (v >= 0 && v <= 0x7f) _out.WriteByte((byte)v);
        else if (v < 0 && v >= -32) _out.WriteByte(unchecked((byte)(sbyte)v));
        else if (v >= int.MinValue && v <= int.MaxValue)
        {
            b[0] = 0xd2;
            BinaryPrimitives.WriteInt32BigEndian(b[1..], (int)v);
            _out.Write(b[..5]);
        }
        else
        {
            b[0] = 0xd3;
            BinaryPrimitives.WriteInt64BigEndian(b[1..], v);
            _out.Write(b);
        }
        return this;
    }

    public MsgPackWriter Double(double v)
    {
        Span<byte> b = stackalloc byte[9];
        b[0] = 0xcb;
        BinaryPrimitives.WriteDoubleBigEndian(b[1..], v);
        _out.Write(b);
        return this;
    }

    public MsgPackWriter Str(string? s)
    {
        if (s is null) return Nil();
        var bytes = Encoding.UTF8.GetBytes(s);
        Header(bytes.Length, 0xa0, 31, 0xd9, 0xda, 0xdb);
        _out.Write(bytes);
        return this;
    }

    public MsgPackWriter Bin(ReadOnlySpan<byte> data)
    {
        Span<byte> b = stackalloc byte[5];
        if (data.Length <= byte.MaxValue)
        {
            b[0] = 0xc4;
            b[1] = (byte)data.Length;
            _out.Write(b[..2]);
        }
        else if (data.Length <= ushort.MaxValue)
        {
            b[0] = 0xc5;
            BinaryPrimitives.WriteUInt16BigEndian(b[1..], (ushort)data.Length);
            _out.Write(b[..3]);
        }
        else
        {
            b[0] = 0xc6;
            BinaryPrimitives.WriteUInt32BigEndian(b[1..], (uint)data.Length);
            _out.Write(b);
        }
        _out.Write(data);
        return this;
    }

    public MsgPackWriter Array(int count)
    {
        Header(count, 0x90, 15, null, 0xdc, 0xdd);
        return this;
    }

    public MsgPackWriter Map(int count)
    {
        Header(count, 0x80, 15, null, 0xde, 0xdf);
        return this;
    }

    public MsgPackWriter Key(string key) => Str(key);

    public MsgPackWriter OptDouble(double? v) => v is { } d ? Double(d) : Nil();

    public MsgPackWriter OptInt(long? v) => v is { } i ? Int(i) : Nil();

    /// <summary>Beliebigen Wert aus dem Lesemodell schreiben (Dictionary, Liste, Primitive).</summary>
    public MsgPackWriter Value(object? v)
    {
        switch (v)
        {
            case null: return Nil();
            case bool b: return Bool(b);
            case int i: return Int(i);
            case long l: return Int(l);
            case double d: return Double(d);
            case string s: return Str(s);
            case byte[] bytes: return Bin(bytes);
            case IDictionary<string, object?> map:
                Map(map.Count);
                foreach (var (k, val) in map) Str(k).Value(val);
                return this;
            case System.Collections.IList list:
                Array(list.Count);
                foreach (var item in list) Value(item);
                return this;
            default: throw new ArgumentException($"Typ {v.GetType().Name} ist nicht kodierbar.");
        }
    }

    private void Header(int length, byte fixBase, int fixMax, byte? b8, byte b16, byte b32)
    {
        Span<byte> b = stackalloc byte[5];
        if (length <= fixMax) _out.WriteByte((byte)(fixBase | length));
        else if (b8 is { } t8 && length <= byte.MaxValue)
        {
            b[0] = t8;
            b[1] = (byte)length;
            _out.Write(b[..2]);
        }
        else if (length <= ushort.MaxValue)
        {
            b[0] = b16;
            BinaryPrimitives.WriteUInt16BigEndian(b[1..], (ushort)length);
            _out.Write(b[..3]);
        }
        else
        {
            b[0] = b32;
            BinaryPrimitives.WriteUInt32BigEndian(b[1..], (uint)length);
            _out.Write(b);
        }
    }
}

/// <summary>Liest MessagePack in ein generisches Modell: Dictionary&lt;string,object?&gt;, object?[], long, double, string, byte[], bool, null.</summary>
public ref struct MsgPackReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public MsgPackReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public static object? Parse(ReadOnlySpan<byte> data)
    {
        var r = new MsgPackReader(data);
        var v = r.Read();
        if (r._pos != data.Length) throw new FormatException("Überzählige Bytes nach MessagePack-Wert.");
        return v;
    }

    public object? Read()
    {
        var t = Byte();
        if (t <= 0x7f) return (long)t;
        if (t >= 0xe0) return (long)(sbyte)t;
        if ((t & 0xf0) == 0x80) return ReadMap(t & 0x0f);
        if ((t & 0xf0) == 0x90) return ReadArray(t & 0x0f);
        if ((t & 0xe0) == 0xa0) return ReadStr(t & 0x1f);
        return ReadTypedValue(t);
    }

    private object? ReadTypedValue(byte t)
    {
        return t switch
        {
            0xc0 => null,
            0xc2 => false,
            0xc3 => true,
            0xc4 => Take(Byte()).ToArray(),
            0xc5 => Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))).ToArray(),
            0xc6 => Take(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))).ToArray(),
            0xca => (double)BinaryPrimitives.ReadSingleBigEndian(Take(4)),
            0xcb => BinaryPrimitives.ReadDoubleBigEndian(Take(8)),
            >= 0xd9 and <= 0xdf => ReadCollection(t),
            >= 0xcc and <= 0xd3 => ReadInteger(t),
            _ => throw new FormatException($"MessagePack-Typ 0x{t:x2} wird nicht unterstützt."),
        };
    }

    private object ReadCollection(byte t) => t switch
    {
        0xd9 => ReadStr(Byte()),
        0xda => ReadStr(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
        0xdb => ReadStr(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
        0xdc => ReadArray(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
        0xdd => ReadArray(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
        0xde => ReadMap(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
        0xdf => ReadMap(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
        _ => throw new FormatException($"MessagePack-Sammlungstyp 0x{t:x2} wird nicht unterstützt."),
    };

    private long ReadInteger(byte t) => t switch
    {
        0xcc => (long)Byte(),
        0xcd => (long)BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
        0xce => (long)BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
        0xcf => checked((long)BinaryPrimitives.ReadUInt64BigEndian(Take(8))),
        0xd0 => (long)(sbyte)Byte(),
        0xd1 => (long)BinaryPrimitives.ReadInt16BigEndian(Take(2)),
        0xd2 => (long)BinaryPrimitives.ReadInt32BigEndian(Take(4)),
        0xd3 => BinaryPrimitives.ReadInt64BigEndian(Take(8)),
        _ => throw new FormatException($"MessagePack-Ganzzahltyp 0x{t:x2} wird nicht unterstützt."),
    };

    private byte Byte()
    {
        if (_pos >= _data.Length) throw new FormatException("Unerwartetes Ende der MessagePack-Daten.");
        return _data[_pos++];
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new FormatException("Unerwartetes Ende der MessagePack-Daten.");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    private string ReadStr(int n) => Encoding.UTF8.GetString(Take(n));

    private object?[] ReadArray(int n)
    {
        if (n > _data.Length - _pos) throw new FormatException("Array-Länge unplausibel.");
        var a = new object?[n];
        for (var i = 0; i < n; i++) a[i] = Read();
        return a;
    }

    private Dictionary<string, object?> ReadMap(int n)
    {
        if (n > _data.Length - _pos) throw new FormatException("Map-Länge unplausibel.");
        var m = new Dictionary<string, object?>(n, StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var key = Read() as string ?? throw new FormatException("Map-Schlüssel muss Text sein.");
            m[key] = Read();
        }
        return m;
    }
}

/// <summary>Bequemer Zugriff auf gelesene Maps.</summary>
public static class MsgPackMap
{
    public static string? S(this IReadOnlyDictionary<string, object?> m, string key) => m.TryGetValue(key, out var v) ? v as string : null;

    public static string Req(this IReadOnlyDictionary<string, object?> m, string key)
        => m.S(key) ?? throw new FormatException($"Feld '{key}' fehlt.");

    public static long? L(this IReadOnlyDictionary<string, object?> m, string key)
        => m.TryGetValue(key, out var v) ? v switch { long l => l, double d => (long)d, _ => null } : null;

    public static double? D(this IReadOnlyDictionary<string, object?> m, string key)
        => m.TryGetValue(key, out var v) ? v switch { double d => d, long l => l, _ => null } : null;

    public static bool B(this IReadOnlyDictionary<string, object?> m, string key) => m.TryGetValue(key, out var v) && v is true;

    public static object?[] A(this IReadOnlyDictionary<string, object?> m, string key) => m.TryGetValue(key, out var v) && v is object?[] a ? a : [];

    public static byte[]? Bin(this IReadOnlyDictionary<string, object?> m, string key) => m.TryGetValue(key, out var v) ? v as byte[] : null;

    public static Dictionary<string, object?> M(this IReadOnlyDictionary<string, object?> m, string key)
        => m.TryGetValue(key, out var v) && v is Dictionary<string, object?> d ? d : new Dictionary<string, object?>();
}

/// <summary>CRC-32 (IEEE 802.3, wie ZIP/PNG).</summary>
public static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xff] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
