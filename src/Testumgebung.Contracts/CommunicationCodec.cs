/**
 * @file
 * @brief Übersetzt Text, Register und Signalwerte in Protokolldaten; Byte-Reihenfolge und Adressbereiche sind hier zentralisiert.
 */

using System.Globalization;
using System.Text;

namespace Testumgebung.Contracts;

/// <summary>Darstellung von Kommunikationsdaten: Text mit Escapes oder Hex.</summary>
public static class CommText
{
    /// <summary>Latin-1: jedes Byte 0–255 ist genau ein Zeichen (Binärdaten bleiben verlustfrei).</summary>
    public static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>
    /// Text mit Escapes in Bytes: \r \n \t \0 \\ \xHH. Hex: Bytes in Hex, Leerzeichen/Doppelpunkte/Bindestriche erlaubt
    /// („01 03 00 00“, „0x0103“).
    /// </summary>
    public static byte[] Parse(string text, bool hex)
    {
        if (hex) return ParseHex(text);
        var sb = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character != '\\' || index + 1 >= text.Length)
            {
                sb.Append(character);
                continue;
            }
            AppendEscape(sb, text, ref index);
        }
        return Latin1.GetBytes(sb.ToString());
    }

    // Consumes one escape; unknown or incomplete escapes remain literal.
    private static void AppendEscape(StringBuilder sb, string text, ref int index)
    {
        var character = text[++index];
        switch (character)
        {
            case 'r': sb.Append('\r'); break;
            case 'n': sb.Append('\n'); break;
            case 't': sb.Append('\t'); break;
            case '0': sb.Append('\0'); break;
            case '\\': sb.Append('\\'); break;
            case 'x' when index + 2 < text.Length && byte.TryParse(text.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var dataByte):
                sb.Append((char)dataByte);
                index += 2;
                break;
            default: sb.Append('\\').Append(character); break;
        }
    }

    public static byte[] ParseHex(string text)
    {
        var clean = new StringBuilder();
        foreach (var part in text.Split([' ', ',', ';', ':', '-', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var startsWithPrefix = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            clean.Append(startsWithPrefix.Length % 2 == 1 ? "0" + startsWithPrefix : startsWithPrefix);
        }
        try
        {
            return Convert.FromHexString(clean.ToString());
        }
        catch (FormatException)
        {
            throw new FormatException($"„{text}“ ist keine gültige Hex-Folge.");
        }
    }

    public static string Format(ReadOnlySpan<byte> data, bool hex)
    {
        if (hex) return string.Join(" ", data.ToArray().Select(dataByte => dataByte.ToString("X2", CultureInfo.InvariantCulture)));
        var sb = new StringBuilder();
        foreach (var dataByte in data)
        {
            switch (dataByte)
            {
                case (byte)'\r': sb.Append("\\r"); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\t': sb.Append("\\t"); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case < 0x20 or >= 0x7F: sb.Append("\\x").Append(dataByte.ToString("X2", CultureInfo.InvariantCulture)); break;
                default: sb.Append((char)dataByte); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Für Protokolle: Text, wenn druckbar, sonst Hex.</summary>
    public static string Trace(ReadOnlySpan<byte> data, int max = 120)
    {
        var printable = true;
        foreach (var dataByte in data)
            if (dataByte is (< 0x20 and not (byte)'\r' and not (byte)'\n' and not (byte)'\t') or >= 0x7F) { printable = false; break; }
        var text = Format(data, !printable);
        return text.Length > max ? text[..max] + " …" : text;
    }

    /// <summary>Bytes als Text (Latin-1, ohne Escapes) – für Mustervergleiche.</summary>
    public static string Raw(ReadOnlySpan<byte> data) => Latin1.GetString(data);
}

/// <summary>Datentypen in Modbus-Registern.</summary>
public enum ModbusDataType
{
    Bool,
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32,
    Float64,
    Text,
}

/// <summary>Kodierung von Werten in 16-Bit-Register (Big-Endian je Register, Wortfolge wählbar).</summary>
public static class ModbusCodec
{
    public static int Registers(ModbusDataType type, int textLength = 0) => type switch
    {
        ModbusDataType.Bool or ModbusDataType.UInt16 or ModbusDataType.Int16 => 1,
        ModbusDataType.UInt32 or ModbusDataType.Int32 or ModbusDataType.Float32 => 2,
        ModbusDataType.Float64 => 4,
        _ => Math.Max(1, (textLength + 1) / 2),
    };

    private static readonly IReadOnlyDictionary<string, ModbusDataType> TypeAliases = new Dictionary<string, ModbusDataType>(StringComparer.Ordinal)
    {
        [""] = ModbusDataType.UInt16,
        ["uint16"] = ModbusDataType.UInt16,
        ["u16"] = ModbusDataType.UInt16,
        ["word"] = ModbusDataType.UInt16,
        ["int16"] = ModbusDataType.Int16,
        ["i16"] = ModbusDataType.Int16,
        ["s16"] = ModbusDataType.Int16,
        ["uint32"] = ModbusDataType.UInt32,
        ["u32"] = ModbusDataType.UInt32,
        ["dword"] = ModbusDataType.UInt32,
        ["int32"] = ModbusDataType.Int32,
        ["i32"] = ModbusDataType.Int32,
        ["s32"] = ModbusDataType.Int32,
        ["float32"] = ModbusDataType.Float32,
        ["f32"] = ModbusDataType.Float32,
        ["float"] = ModbusDataType.Float32,
        ["real"] = ModbusDataType.Float32,
        ["float64"] = ModbusDataType.Float64,
        ["f64"] = ModbusDataType.Float64,
        ["double"] = ModbusDataType.Float64,
        ["bool"] = ModbusDataType.Bool,
        ["bit"] = ModbusDataType.Bool,
        ["text"] = ModbusDataType.Text,
        ["string"] = ModbusDataType.Text,
        ["ascii"] = ModbusDataType.Text,
    };

    public static ModbusDataType ParseType(string? text)
    {
        var name = (text ?? "").Trim().ToLowerInvariant();
        return TypeAliases.TryGetValue(name, out var type) ? type
            : throw new FormatException($"Unbekannter Modbus-Datentyp „{name}“ (UInt16, Int16, UInt32, Int32, Float32, Float64, Bool, Text).");
    }

    /// <param name="lowWordFirst">true = niederwertiges Register zuerst („Wortfolge vertauscht“).</param>
    public static object Decode(IReadOnlyList<ushort> regs, ModbusDataType type, bool lowWordFirst = false)
    {
        var bytes = ToBytes(regs, lowWordFirst);
        return type switch
        {
            ModbusDataType.Bool => regs[0] != 0,
            ModbusDataType.UInt16 => (long)regs[0],
            ModbusDataType.Int16 => (long)(short)regs[0],
            ModbusDataType.UInt32 => (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes),
            ModbusDataType.Int32 => (long)System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes),
            ModbusDataType.Float32 => (double)System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(bytes),
            ModbusDataType.Float64 => System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(bytes),
            _ => Encoding.ASCII.GetString(regs.SelectMany(reg => new[] { (byte)(reg >> 8), (byte)reg }).ToArray()).TrimEnd('\0', ' '),
        };
    }

    public static ushort[] Encode(object value, ModbusDataType type, bool lowWordFirst = false, int textRegisters = 0)
    {
        var inv = CultureInfo.InvariantCulture;
        var bytes = new byte[Registers(type) * 2];
        switch (type)
        {
            case ModbusDataType.Bool:
                return [Convert.ToBoolean(value, inv) ? (ushort)1 : (ushort)0];
            case ModbusDataType.UInt16:
                return [checked((ushort)Convert.ToInt64(value, inv))];
            case ModbusDataType.Int16:
                return [unchecked((ushort)checked((short)Convert.ToInt64(value, inv)))];
            case ModbusDataType.UInt32:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)Convert.ToInt64(value, inv)));
                break;
            case ModbusDataType.Int32:
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)Convert.ToInt64(value, inv)));
                break;
            case ModbusDataType.Float32:
                System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(bytes, (float)Convert.ToDouble(value, inv));
                break;
            case ModbusDataType.Float64:
                System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(bytes, Convert.ToDouble(value, inv));
                break;
            default:
                {
                    var text = Encoding.ASCII.GetBytes(Convert.ToString(value, inv) ?? "");
                    var count = Math.Max(textRegisters, (text.Length + 1) / 2);
                    bytes = new byte[count * 2];
                    text.AsSpan(0, Math.Min(text.Length, bytes.Length)).CopyTo(bytes);
                    return Enumerable.Range(0, count).Select(index => (ushort)((bytes[2 * index] << 8) | bytes[2 * index + 1])).ToArray();
                }
        }
        var regs = Enumerable.Range(0, bytes.Length / 2).Select(index => (ushort)((bytes[2 * index] << 8) | bytes[2 * index + 1])).ToArray();
        if (lowWordFirst) Array.Reverse(regs);
        return regs;
    }

    private static byte[] ToBytes(IReadOnlyList<ushort> regs, bool lowWordFirst)
    {
        var ordered = lowWordFirst ? regs.Reverse().ToArray() : regs.ToArray();
        var bytes = new byte[ordered.Length * 2];
        for (var index = 0; index < ordered.Length; index++)
        {
            bytes[2 * index] = (byte)(ordered[index] >> 8);
            bytes[2 * index + 1] = (byte)ordered[index];
        }
        return bytes;
    }

    /// <summary>
    /// Adresse im Klartext: „HR100“, „IR5“, „C3“, „DI2“ (0-basiert) oder klassisch 40001…, 30001…, 10001…, 00001… (1-basiert).
    /// </summary>
    public static (ModbusArea Area, int Address) ParseAddress(string text, ModbusArea? defaultArea = null)
    {
        var text2 = text.Trim().ToUpperInvariant();
        (string Prefix, ModbusArea Area)[] prefixes = [("HR", ModbusArea.HoldingRegister), ("IR", ModbusArea.InputRegister), ("DI", ModbusArea.DiscreteInput), ("C", ModbusArea.Coil)];
        foreach (var (prefix, area) in prefixes)
            if (text2.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(text2[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var address) && address is >= 0 and <= 65535)
                return (area, address);
        if (!int.TryParse(text2.StartsWith("0X", StringComparison.Ordinal) ? text2[2..] : text2, text2.StartsWith("0X", StringComparison.Ordinal) ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
            throw new FormatException($"Modbus-Adresse „{text}“ ungültig (z. B. HR100, IR5, C3, DI2 oder 40101).");
        if (defaultArea is { } modbusArea) return (modbusArea, count);
        return ParseConventionalAddress(count, text);
    }

    private static (ModbusArea Area, int Address) ParseConventionalAddress(int address, string text)
    {
        return address switch
        {
            >= 400001 and <= 465536 => (ModbusArea.HoldingRegister, address - 400001),
            >= 300001 and <= 365536 => (ModbusArea.InputRegister, address - 300001),
            >= 40001 and <= 49999 => (ModbusArea.HoldingRegister, address - 40001),
            >= 30001 and <= 39999 => (ModbusArea.InputRegister, address - 30001),
            >= 10001 and <= 19999 => (ModbusArea.DiscreteInput, address - 10001),
            >= 1 and <= 9999 => (ModbusArea.Coil, address - 1),
            _ => throw new FormatException($"Modbus-Adresse „{text}“: Bereich nicht erkennbar – HR/IR/C/DI voranstellen."),
        };
    }

    public static string AreaPrefix(ModbusArea area) => area switch
    {
        ModbusArea.HoldingRegister => "HR",
        ModbusArea.InputRegister => "IR",
        ModbusArea.DiscreteInput => "DI",
        _ => "C",
    };
}

/// <summary>Signal in einem CAN-Rahmen (wie in DBC-Dateien): Startbit, Länge, Byteorder, Vorzeichen, Faktor, Offset.</summary>
public sealed record CanSignal(int StartBit, int Length, bool Motorola = false, bool Signed = false, double Factor = 1, double Offset = 0,
    bool IsFloat = false)
{
    /// <summary>Rohwert aus den Daten. Intel: Startbit = LSB. Motorola (DBC): Startbit = MSB, Bitnummerierung je Byte 7…0.</summary>
    public long Raw(ReadOnlySpan<byte> data)
    {
        var raw = Bits(data);
        if (Signed && !IsFloat && Length < 64 && (raw & (1UL << (Length - 1))) != 0) raw |= ulong.MaxValue << Length;
        return unchecked((long)raw);
    }

    private ulong Bits(ReadOnlySpan<byte> data)
    {
        if (Length is < 1 or > 64) throw new ArgumentException("Signallänge 1–64 Bit.");
        if (IsFloat && Length is not (32 or 64)) throw new ArgumentException("Gleitkommasignal: Länge 32 oder 64 Bit.");
        return Motorola ? MotorolaBits(data) : IntelBits(data);
    }

    private ulong IntelBits(ReadOnlySpan<byte> data)
    {
        ulong raw = 0;
        for (var index = 0; index < Length; index++)
        {
            var bit = StartBit + index;
            if (bit / 8 >= data.Length) throw new ArgumentException($"Signal reicht über die {data.Length} Datenbytes hinaus.");
            if ((data[bit / 8] >> (bit % 8) & 1) != 0) raw |= 1UL << index;
        }
        return raw;
    }

    private ulong MotorolaBits(ReadOnlySpan<byte> data)
    {
        ulong raw = 0;
        var bit = StartBit;
        for (var index = Length - 1; index >= 0; index--)
        {
            if (bit / 8 >= data.Length) throw new ArgumentException($"Signal reicht über die {data.Length} Datenbytes hinaus.");
            if ((data[bit / 8] >> (bit % 8) & 1) != 0) raw |= 1UL << index;
            bit = bit % 8 == 0 ? bit + 15 : bit - 1;
        }
        return raw;
    }

    /// <summary>Physikalischer Wert (Rohwert × Faktor + Offset; Gleitkommasignale nach IEEE 754).</summary>
    public double Value(ReadOnlySpan<byte> data)
    {
        if (!IsFloat) return Raw(data) * Factor + Offset;
        var bits = Bits(data);
        var frequency = Length == 32 ? BitConverter.Int32BitsToSingle(unchecked((int)(uint)bits)) : BitConverter.Int64BitsToDouble(unchecked((long)bits));
        return frequency * Factor + Offset;
    }

    /// <summary>Höchster bzw. niedrigster darstellbarer Rohwert.</summary>
    public (double Min, double Max) RawRange
        => IsFloat ? (double.MinValue, double.MaxValue)
            : Signed ? (-Math.Pow(2, Length - 1), Math.Pow(2, Length - 1) - 1) : (0, Math.Pow(2, Length) - 1);

    /// <summary>Physikalischen Wert in die Daten schreiben (für Simulation und Senden mit Signal).</summary>
    public void Write(Span<byte> data, double physical)
    {
        ulong raw;
        if (IsFloat)
        {
            var value = (physical - Offset) / Factor;
            raw = Length == 32 ? (uint)BitConverter.SingleToInt32Bits((float)value) : unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        }
        else
        {
            raw = unchecked((ulong)(long)Math.Round((physical - Offset) / Factor));
        }
        WriteRaw(data, raw);
    }

    /// <summary>Rohwert in die Daten schreiben (Multiplexer, Wertetabellen).</summary>
    public void WriteRaw(Span<byte> data, ulong raw)
    {
        if (!Motorola)
        {
            for (var index = 0; index < Length; index++)
            {
                var bit = StartBit + index;
                if (bit / 8 >= data.Length) throw new ArgumentException($"Signal reicht über die {data.Length} Datenbytes hinaus.");
                var mask = (byte)(1 << (bit % 8));
                data[bit / 8] = (raw >> index & 1) != 0 ? (byte)(data[bit / 8] | mask) : (byte)(data[bit / 8] & ~mask);
            }
        }
        else
        {
            var bit = StartBit;
            for (var index2 = Length - 1; index2 >= 0; index2--)
            {
                if (bit / 8 >= data.Length) throw new ArgumentException($"Signal reicht über die {data.Length} Datenbytes hinaus.");
                var mask = (byte)(1 << (bit % 8));
                data[bit / 8] = (raw >> index2 & 1) != 0 ? (byte)(data[bit / 8] | mask) : (byte)(data[bit / 8] & ~mask);
                bit = bit % 8 == 0 ? bit + 15 : bit - 1;
            }
        }
    }
}
