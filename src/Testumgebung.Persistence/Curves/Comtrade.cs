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
        return Directory.GetFiles(dir).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase)
                                                        && Path.GetExtension(f).Equals(".dat", StringComparison.OrdinalIgnoreCase))
               ?? throw new CurveImportException($"Zur COMTRADE-Konfiguration fehlt die Datendatei {stem}.dat.");
    }

    public static Config ParseConfig(byte[] cfgBytes)
    {
        var lines = Encoding.Latin1.GetString(cfgBytes).Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
        var pos = 0;
        string[] Next() => pos < lines.Count ? lines[pos++].Split(',').Select(x => x.Trim()).ToArray() : throw new CurveImportException("COMTRADE-Konfiguration unvollständig.");
        double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new CurveImportException($"COMTRADE: Zahl '{s}' ungültig.");
        var head = Next();
        var rev = head.Length > 2 ? head[2] : "1991";
        var counts = Next();
        var analogCount = int.Parse(counts[1].TrimEnd('A', 'a'), CultureInfo.InvariantCulture);
        var digitalCount = int.Parse(counts[2].TrimEnd('D', 'd'), CultureInfo.InvariantCulture);
        var analog = new List<AnalogChannel>();
        for (var i = 0; i < analogCount; i++)
        {
            var a = Next();
            if (a.Length < 7) throw new CurveImportException($"COMTRADE: Analogkanal {i + 1} unvollständig.");
            analog.Add(new AnalogChannel(int.Parse(a[0], CultureInfo.InvariantCulture), a[1], a[2], a[4], D(a[5]), D(a[6])));
        }
        for (var i = 0; i < digitalCount; i++) Next();
        Next(); // Netzfrequenz
        var nrates = (int)D(Next()[0]);
        var rates = new List<(double, long)>();
        for (var i = 0; i < Math.Max(1, nrates); i++)
        {
            var r = Next();
            rates.Add((D(r[0]), (long)D(r[1])));
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
        var n = cfg.Analog.Count;
        var times = new List<double>();
        var values = Enumerable.Range(0, n).Select(_ => new List<double>()).ToList();
        if (cfg.FileType == "ASCII") ReadAscii(cfg, datBytes, times, values);
        else ReadBinary(cfg, datBytes, times, values);

        // Zeitbasis: genau eine Abtastrate > 0 → fest; sonst Zeitstempel (µs × timemult)
        double? rate = cfg.Rates.Count == 1 && cfg.Rates[0].Rate > 0 ? cfg.Rates[0].Rate : null;
        var timeSeconds = times.Select(t => t * cfg.TimeMult * 1e-6).ToArray();
        var columns = new List<ImportColumn> { new(0, "Zeit", "s", true, timeSeconds.DefaultIfEmpty().Min(), timeSeconds.DefaultIfEmpty().Max()) };
        var data = new List<double[]> { timeSeconds };
        for (var i = 0; i < n; i++)
        {
            var arr = values[i].ToArray();
            var finite = arr.Where(double.IsFinite).ToArray();
            var id = cfg.Analog[i].Id.Length > 0 ? cfg.Analog[i].Id : $"A{cfg.Analog[i].Number}";
            if (columns.Any(c => c.Name == id)) id = $"{id}_{i + 1}";
            columns.Add(new ImportColumn(i + 1, id, cfg.Analog[i].Unit, true, finite.DefaultIfEmpty().Min(), finite.DefaultIfEmpty().Max()));
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
                .Select(r => data.Select(d => d[r].ToString("0.######", CultureInfo.InvariantCulture)).ToArray()).ToList(),
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
        var n = cfg.Analog.Count;
        foreach (var line in Encoding.Latin1.GetString(datBytes).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var p = line.Split(',');
            if (p.Length < 2 + n) throw new CurveImportException($"COMTRADE-Daten: Zeile '{line}' unvollständig.");
            times.Add(double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) ? ts : double.NaN);
            for (var i = 0; i < n; i++)
            {
                var raw = p[2 + i].Trim();
                values[i].Add(raw.Length == 0 || raw == "99999" ? double.NaN
                    : cfg.Analog[i].A * double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) + cfg.Analog[i].B);
            }
        }
    }

    private static void ReadBinary(Config cfg, byte[] datBytes, List<double> times, List<List<double>> values)
    {
        var n = cfg.Analog.Count;
        var width = cfg.FileType switch
        {
            "BINARY" => 2,
            "BINARY32" or "FLOAT32" => 4,
            _ => throw new CurveImportException($"COMTRADE-Dateityp '{cfg.FileType}' wird nicht unterstützt."),
        };
        var record = 8 + n * width + 2 * ((cfg.Digital + 15) / 16);
        if (datBytes.Length % record != 0) throw new CurveImportException("COMTRADE-Binärdaten passen nicht zur Konfiguration.");
        for (var off = 0; off < datBytes.Length; off += record)
        {
            var ts = BinaryPrimitives.ReadUInt32LittleEndian(datBytes.AsSpan(off + 4));
            times.Add(ts == 0xFFFFFFFF ? double.NaN : ts);
            for (var i = 0; i < n; i++)
            {
                var at = off + 8 + i * width;
                double raw = cfg.FileType switch
                {
                    "BINARY" => BinaryPrimitives.ReadInt16LittleEndian(datBytes.AsSpan(at)) is var s16 && s16 == short.MinValue ? double.NaN : s16,
                    "BINARY32" => BinaryPrimitives.ReadInt32LittleEndian(datBytes.AsSpan(at)) is var s32 && s32 == int.MinValue ? double.NaN : s32,
                    _ => BinaryPrimitives.ReadSingleLittleEndian(datBytes.AsSpan(at)),
                };
                values[i].Add(double.IsNaN(raw) ? double.NaN : cfg.Analog[i].A * raw + cfg.Analog[i].B);
            }
        }
    }

}
