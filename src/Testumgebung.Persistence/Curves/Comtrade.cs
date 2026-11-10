/**
 * @file
 * @brief Liest COMTRADE-Konfigurationen und Samples mit ihren Kanal- und Zeitbasisinformationen.
 */

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Testumgebung.Persistence.Curves;

/// <summary>
/// COMTRADE (IEEE C37.111, 1991/1999/2013): .cfg + .dat in ASCII, BINARY, BINARY32 oder FLOAT32.
/// Analogwerte = a·x + b; Zeit aus Abtastrate (eine Rate) oder Zeitstempeln (µs × timemult). Digitalkanäle werden ignoriert.
/// </summary>
public static class Comtrade
{
    public sealed record AnalogChannel(int Number, string Id, string Phase, string Unit, double A, double B);

    public sealed record Config(string Station, string Revision, IReadOnlyList<AnalogChannel> Analog, int Digital,
        IReadOnlyList<(double Rate, long EndSample)> Rates, string FileType, double TimeMult);

    public static ImportTable Read(string cfgPath)
    {
        var cfgBytes = File.ReadAllBytes(cfgPath);
        var datPath = FindDat(cfgPath);
        var datBytes = File.ReadAllBytes(datPath);
        return Parse(cfgBytes, datBytes, Path.GetFileName(cfgPath));
    }

    private static string FindDat(string cfgPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(cfgPath))!;
        var stem = Path.GetFileNameWithoutExtension(cfgPath);
        return Directory.GetFiles(dir).FirstOrDefault(fileName => Path.GetFileNameWithoutExtension(fileName).Equals(stem, StringComparison.OrdinalIgnoreCase)
                                                        && Path.GetExtension(fileName).Equals(".dat", StringComparison.OrdinalIgnoreCase))
               ?? throw new CurveImportException($"Zur COMTRADE-Konfiguration fehlt die Datendatei {stem}.dat.");
    }

    public static Config ParseConfig(byte[] cfgBytes)
    {
        var lines = Encoding.Latin1.GetString(cfgBytes).Split('\n').Select(line => line.TrimEnd('\r').Trim()).Where(line => line.Length > 0).ToList();
        var pos = 0;
        string[] Next() => pos < lines.Count ? lines[pos++].Split(',').Select(entry => entry.Trim()).ToArray() : throw new CurveImportException("COMTRADE-Konfiguration unvollständig.");
        double D(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new CurveImportException($"COMTRADE: Zahl '{text}' ungültig.");
        var head = Next();
        var rev = head.Length > 2 ? head[2] : "1991";
        var counts = Next();
        var analogCount = int.Parse(counts[1].TrimEnd('A', 'a'), CultureInfo.InvariantCulture);
        var digitalCount = int.Parse(counts[2].TrimEnd('D', 'd'), CultureInfo.InvariantCulture);
        var analog = new List<AnalogChannel>();
        for (var index = 0; index < analogCount; index++)
        {
            var parts = Next();
            if (parts.Length < 7) throw new CurveImportException($"COMTRADE: Analogkanal {index + 1} unvollständig.");
            analog.Add(new AnalogChannel(int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], parts[2], parts[4], D(parts[5]), D(parts[6])));
        }
        for (var index2 = 0; index2 < digitalCount; index2++) Next();
        Next(); // Netzfrequenz
        var nrates = (int)D(Next()[0]);
        var rates = new List<(double, long)>();
        for (var index3 = 0; index3 < Math.Max(1, nrates); index3++)
        {
            var parts2 = Next();
            rates.Add((D(parts2[0]), (long)D(parts2[1])));
        }
        Next(); // Startzeit
        Next(); // Triggerzeit
        var ft = Next()[0].ToUpperInvariant();
        var timemult = pos < lines.Count && double.TryParse(lines[pos].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var tm) ? tm : 1;
        return new Config(head[0], rev, analog, digitalCount, nrates == 0 ? [] : rates, ft, timemult);
    }

    public static ImportTable Parse(byte[] cfgBytes, byte[] datBytes, string name)
    {
        var cfg = ParseConfig(cfgBytes);
        var count = cfg.Analog.Count;
        var times = new List<double>();
        var values = Enumerable.Range(0, count).Select(_ => new List<double>()).ToList();
        if (cfg.FileType == "ASCII") ReadAscii(cfg, datBytes, times, values);
        else ReadBinary(cfg, datBytes, times, values);

        // Zeitbasis: genau eine Abtastrate > 0 → fest; sonst Zeitstempel (µs × timemult)
        double? rate = cfg.Rates.Count == 1 && cfg.Rates[0].Rate > 0 ? cfg.Rates[0].Rate : null;
        var timeSeconds = times.Select(time => time * cfg.TimeMult * 1e-6).ToArray();
        var columns = new List<ImportColumn> { new(0, "Zeit", "s", true, timeSeconds.DefaultIfEmpty().Min(), timeSeconds.DefaultIfEmpty().Max()) };
        var data = new List<double[]> { timeSeconds };
        for (var index = 0; index < count; index++)
        {
            var arr = values[index].ToArray();
            var finite = arr.Where(double.IsFinite).ToArray();
            var id = cfg.Analog[index].Id.Length > 0 ? cfg.Analog[index].Id : $"A{cfg.Analog[index].Number}";
            if (columns.Any(importColumn => importColumn.Name == id)) id = $"{id}_{index + 1}";
            columns.Add(new ImportColumn(index + 1, id, cfg.Analog[index].Unit, true, finite.DefaultIfEmpty().Min(), finite.DefaultIfEmpty().Max()));
            data.Add(arr);
        }
        var table = new ImportTable
        {
            Format = $"COMTRADE {cfg.Revision} ({cfg.FileType})",
            SourceName = name,
            Sha256 = CurveImport.Hash([.. cfgBytes, .. datBytes]),
            Columns = columns,
            Data = data,
            PreviewRows = Enumerable.Range(0, Math.Min(10, timeSeconds.Length))
                .Select(sampleIndex => data.Select(samples => samples[sampleIndex].ToString("0.######", CultureInfo.InvariantCulture)).ToArray()).ToList(),
            SampleRate = rate,
            TimeColumn = "Zeit",
            TimeFactor = 1,
        };
        if (cfg.Rates.Count > 1) table.Warnings.Add($"{cfg.Rates.Count} Abtastraten in der Datei – explizite Zeitpunkte verwenden.");
        if (cfg.Digital > 0) table.Warnings.Add($"{cfg.Digital} Digitalkanäle nicht übernommen.");
        return table;
    }
    private static void ReadAscii(Config cfg, byte[] datBytes, List<double> times, List<List<double>> values)
    {
        var count = cfg.Analog.Count;
        foreach (var line in Encoding.Latin1.GetString(datBytes).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
        {
            var parts = line.Split(',');
            if (parts.Length < 2 + count) throw new CurveImportException($"COMTRADE-Daten: Zeile '{line}' unvollständig.");
            times.Add(double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) ? ts : double.NaN);
            for (var index = 0; index < count; index++)
            {
                var raw = parts[2 + index].Trim();
                values[index].Add(raw.Length == 0 || raw == "99999" ? double.NaN
                    : cfg.Analog[index].A * double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) + cfg.Analog[index].B);
            }
        }
    }

    private static void ReadBinary(Config cfg, byte[] datBytes, List<double> times, List<List<double>> values)
    {
        var count = cfg.Analog.Count;
        var width = cfg.FileType switch
        {
            "BINARY" => 2,
            "BINARY32" or "FLOAT32" => 4,
            _ => throw new CurveImportException($"COMTRADE-Dateityp '{cfg.FileType}' wird nicht unterstützt."),
        };
        var record = 8 + count * width + 2 * ((cfg.Digital + 15) / 16);
        if (datBytes.Length % record != 0) throw new CurveImportException("COMTRADE-Binärdaten passen nicht zur Konfiguration.");
        for (var off = 0; off < datBytes.Length; off += record)
        {
            var ts = BinaryPrimitives.ReadUInt32LittleEndian(datBytes.AsSpan(off + 4));
            times.Add(ts == 0xFFFFFFFF ? double.NaN : ts);
            for (var index = 0; index < count; index++)
            {
                var at = off + 8 + index * width;
                double raw = cfg.FileType switch
                {
                    "BINARY" => BinaryPrimitives.ReadInt16LittleEndian(datBytes.AsSpan(at)) is var s16 && s16 == short.MinValue ? double.NaN : s16,
                    "BINARY32" => BinaryPrimitives.ReadInt32LittleEndian(datBytes.AsSpan(at)) is var s32 && s32 == int.MinValue ? double.NaN : s32,
                    _ => BinaryPrimitives.ReadSingleLittleEndian(datBytes.AsSpan(at)),
                };
                values[index].Add(double.IsNaN(raw) ? double.NaN : cfg.Analog[index].A * raw + cfg.Analog[index].B);
            }
        }
    }

}
