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
        Span<byte> block = stackalloc byte[9];
        if (v >= 0 && v <= 0x7f) _out.WriteByte((byte)v);
        else if (v < 0 && v >= -32) _out.WriteByte(unchecked((byte)(sbyte)v));
        else if (v >= int.MinValue && v <= int.MaxValue)
        {
            block[0] = 0xd2;
            BinaryPrimitives.WriteInt32BigEndian(block[1..], (int)v);
            _out.Write(block[..5]);
        }
        else
        {
            block[0] = 0xd3;
            BinaryPrimitives.WriteInt64BigEndian(block[1..], v);
            _out.Write(block);
        }
        return this;
    }

    public MsgPackWriter Double(double v)
    {
        Span<byte> block = stackalloc byte[9];
        block[0] = 0xcb;
        BinaryPrimitives.WriteDoubleBigEndian(block[1..], v);
        _out.Write(block);
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
        Span<byte> block = stackalloc byte[5];
        if (data.Length <= byte.MaxValue)
        {
            block[0] = 0xc4;
            block[1] = (byte)data.Length;
            _out.Write(block[..2]);
        }
        else if (data.Length <= ushort.MaxValue)
        {
            block[0] = 0xc5;
            BinaryPrimitives.WriteUInt16BigEndian(block[1..], (ushort)data.Length);
            _out.Write(block[..3]);
        }
        else
        {
            block[0] = 0xc6;
            BinaryPrimitives.WriteUInt32BigEndian(block[1..], (uint)data.Length);
            _out.Write(block);
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

    public MsgPackWriter OptDouble(double? v) => v is { } number ? Double(number) : Nil();

    public MsgPackWriter OptInt(long? v) => v is { } index ? Int(index) : Nil();

    /// <summary>Beliebigen Wert aus dem Lesemodell schreiben (Dictionary, Liste, Primitive).</summary>
    public MsgPackWriter Value(object? v)
    {
        switch (v)
        {
            case null: return Nil();
            case bool flag: return Bool(flag);
            case int index: return Int(index);
            case long integerValue: return Int(integerValue);
            case double number: return Double(number);
            case string text: return Str(text);
            case byte[] bytes: return Bin(bytes);
            case IDictionary<string, object?> map:
                Map(map.Count);
                foreach (var (key, val) in map) Str(key).Value(val);
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
        Span<byte> block = stackalloc byte[5];
        if (length <= fixMax) _out.WriteByte((byte)(fixBase | length));
        else if (b8 is { } t8 && length <= byte.MaxValue)
        {
            block[0] = t8;
            block[1] = (byte)length;
            _out.Write(block[..2]);
        }
        else if (length <= ushort.MaxValue)
        {
            block[0] = b16;
            BinaryPrimitives.WriteUInt16BigEndian(block[1..], (ushort)length);
            _out.Write(block[..3]);
        }
        else
        {
            block[0] = b32;
            BinaryPrimitives.WriteUInt32BigEndian(block[1..], (uint)length);
            _out.Write(block);
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
        var msgPackReader = new MsgPackReader(data);
        var value = msgPackReader.Read();
        if (msgPackReader._pos != data.Length) throw new FormatException("Überzählige Bytes nach MessagePack-Wert.");
        return value;
    }

    public object? Read()
    {
        var dataByte = Byte();
        if (dataByte <= 0x7f) return (long)dataByte;
        if (dataByte >= 0xe0) return (long)(sbyte)dataByte;
        if ((dataByte & 0xf0) == 0x80) return ReadMap(dataByte & 0x0f);
        if ((dataByte & 0xf0) == 0x90) return ReadArray(dataByte & 0x0f);
        if ((dataByte & 0xe0) == 0xa0) return ReadStr(dataByte & 0x1f);
        return ReadTypedValue(dataByte);
    }

    private object? ReadTypedValue(byte dataByte)
    {
        return dataByte switch
        {
            0xc0 => null,
            0xc2 => false,
            0xc3 => true,
            0xc4 => Take(Byte()).ToArray(),
            0xc5 => Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))).ToArray(),
            0xc6 => Take(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))).ToArray(),
            0xca => (double)BinaryPrimitives.ReadSingleBigEndian(Take(4)),
            0xcb => BinaryPrimitives.ReadDoubleBigEndian(Take(8)),
            >= 0xd9 and <= 0xdf => ReadCollection(dataByte),
            >= 0xcc and <= 0xd3 => ReadInteger(dataByte),
            _ => throw new FormatException($"MessagePack-Typ 0x{dataByte:x2} wird nicht unterstützt."),
        };
    }

    private object ReadCollection(byte dataByte) => dataByte switch
    {
        0xd9 => ReadStr(Byte()),
        0xda => ReadStr(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
        0xdb => ReadStr(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
        0xdc => ReadArray(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
        0xdd => ReadArray(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
        0xde => ReadMap(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
        0xdf => ReadMap(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
        _ => throw new FormatException($"MessagePack-Sammlungstyp 0x{dataByte:x2} wird nicht unterstützt."),
    };

    private long ReadInteger(byte dataByte) => dataByte switch
    {
        0xcc => (long)Byte(),
        0xcd => (long)BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
        0xce => (long)BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
        0xcf => checked((long)BinaryPrimitives.ReadUInt64BigEndian(Take(8))),
        0xd0 => (long)(sbyte)Byte(),
        0xd1 => (long)BinaryPrimitives.ReadInt16BigEndian(Take(2)),
        0xd2 => (long)BinaryPrimitives.ReadInt32BigEndian(Take(4)),
        0xd3 => BinaryPrimitives.ReadInt64BigEndian(Take(8)),
        _ => throw new FormatException($"MessagePack-Ganzzahltyp 0x{dataByte:x2} wird nicht unterstützt."),
    };

    private byte Byte()
    {
        if (_pos >= _data.Length) throw new FormatException("Unerwartetes Ende der MessagePack-Daten.");
        return _data[_pos++];
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _pos + count > _data.Length) throw new FormatException("Unerwartetes Ende der MessagePack-Daten.");
        var source = _data.Slice(_pos, count);
        _pos += count;
        return source;
    }

    private string ReadStr(int count) => Encoding.UTF8.GetString(Take(count));

    private object?[] ReadArray(int index2)
    {
        if (index2 > _data.Length - _pos) throw new FormatException("Array-Länge unplausibel.");
        var items = new object?[index2];
        for (var index = 0; index < index2; index++) items[index] = Read();
        return items;
    }

    private Dictionary<string, object?> ReadMap(int count)
    {
        if (count > _data.Length - _pos) throw new FormatException("Map-Länge unplausibel.");
        var entries = new Dictionary<string, object?>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = Read() as string ?? throw new FormatException("Map-Schlüssel muss Text sein.");
            entries[key] = Read();
        }
        return entries;
    }
}

/// <summary>Bequemer Zugriff auf gelesene Maps.</summary>
public static class MsgPackMap
{
    public static string? S(this IReadOnlyDictionary<string, object?> m, string key) => m.TryGetValue(key, out var value) ? value as string : null;

    public static string Req(this IReadOnlyDictionary<string, object?> m, string key)
        => m.S(key) ?? throw new FormatException($"Feld '{key}' fehlt.");

    public static long? L(this IReadOnlyDictionary<string, object?> m, string key)
        => m.TryGetValue(key, out var value) ? value switch { long integerValue => integerValue, double number => (long)number, _ => null } : null;

    public static double? D(this IReadOnlyDictionary<string, object?> m, string key)
        => m.TryGetValue(key, out var value) ? value switch { double number => number, long integerValue => integerValue, _ => null } : null;

    public static bool B(this IReadOnlyDictionary<string, object?> m, string key) => m.TryGetValue(key, out var value) && value is true;

    public static object?[] A(this IReadOnlyDictionary<string, object?> m, string key) => m.TryGetValue(key, out var value) && value is object?[] items ? items : [];

    public static byte[]? Bin(this IReadOnlyDictionary<string, object?> m, string key) => m.TryGetValue(key, out var value) ? value as byte[] : null;

    public static Dictionary<string, object?> M(this IReadOnlyDictionary<string, object?> m, string key)
        => m.TryGetValue(key, out var value) && value is Dictionary<string, object?> entries ? entries : new Dictionary<string, object?>();
}

/// <summary>CRC-32 (IEEE 802.3, wie ZIP/PNG).</summary>
public static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var uintItems = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            var count = index;
            for (var previousIndex = 0; previousIndex < 8; previousIndex++) count = (count & 1) != 0 ? 0xEDB88320u ^ (count >> 1) : count >> 1;
            uintItems[index] = count;
        }
        return uintItems;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var columnIndex = 0xFFFFFFFFu;
        foreach (var dataByte in data) columnIndex = Table[(columnIndex ^ dataByte) & 0xff] ^ (columnIndex >> 8);
        return columnIndex ^ 0xFFFFFFFFu;
    }
}
