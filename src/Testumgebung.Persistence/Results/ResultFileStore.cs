/**
 * @file
 * @brief Verwaltet Ergebnisdateien und alternative Sicherungspfade bei Speicherfehlern.
 */

using System.Text;

namespace Testumgebung.Persistence.Results;

/// <summary>Ergebnis einer Dateispeicherung.</summary>
/// <param name="Path">Gespeicherte Datei (bei Wiederherstellung im Wiederherstellungsordner); null = gar nicht gespeichert.</param>
/// <param name="Error">Fehler beim regulären Speichern (auch wenn die Wiederherstellung gelang).</param>
public sealed record FileSaveResult(string? Path, string? Error, bool Recovered)
{
    public bool Success => Error is null && Path is not null;
}

public sealed record ResultFileInfo(string Path, string FileName, DateTime Modified, long Size);

/// <summary>
/// Speichert je Prüflingstest eine eigene Ergebnisdatei (Abschnitt 9.2): temporär schreiben, flushen, schließen,
/// atomar umbenennen. Run-ID im Dateinamen verhindert Kollisionen. Scheitert das Speichern im Zielordner,
/// wird in den lokalen Wiederherstellungsordner geschrieben und der Fehler gemeldet.
/// </summary>
public sealed class ResultFileStore(string folder, string? recoveryFolder = null)
{
    public string Folder { get; } = folder;
    public string? RecoveryFolder { get; } = recoveryFolder;

    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Testumgebung", "Ergebnisse");

    public static string DefaultRecoveryFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "Wiederherstellung");

    public static string FileNameFor(ResultRun run)
    {
        var dut = Sanitize(string.IsNullOrWhiteSpace(run.DutId) ? "ohne-Kennung" : run.DutId);
        return $"{run.Started.LocalDateTime:yyyyMMdd_HHmmss}_{dut}_{Sanitize(run.RunId)}{ResultFile.Extension}";
    }

    public FileSaveResult Save(ResultRun run)
    {
        try
        {
            return new FileSaveResult(WriteAtomic(Folder, FileNameFor(run), stream => ResultFile.Write(stream, run)), null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var error = $"Ergebnisdatei konnte nicht in '{Folder}' gespeichert werden: {ex.Message}";
            if (RecoveryFolder is null) return new FileSaveResult(null, error, false);
            try
            {
                return new FileSaveResult(WriteAtomic(RecoveryFolder, FileNameFor(run), stream => ResultFile.Write(stream, run)), error, true);
            }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new FileSaveResult(null, $"{error} Wiederherstellung ebenfalls fehlgeschlagen: {ex2.Message}", false);
            }
        }
    }

    /// <summary>Dateien, die mangels Zugriff auf den Ergebnisordner im Wiederherstellungsordner liegen.</summary>
    public IReadOnlyList<ResultFileInfo> PendingRecovery()
    {
        if (RecoveryFolder is null || !Directory.Exists(RecoveryFolder)) return [];
        return new DirectoryInfo(RecoveryFolder).GetFiles("*" + ResultFile.Extension)
            .OrderBy(f => f.LastWriteTimeUtc)
            .Select(f => new ResultFileInfo(f.FullName, f.Name, f.LastWriteTime, f.Length))
            .ToList();
    }

    /// <summary>
    /// Dateien aus dem Wiederherstellungsordner in den Ergebnisordner zurückkopieren (atomar, ohne Überschreiben) und erst
    /// nach erfolgreichem Schreiben im Wiederherstellungsordner löschen. Beschädigte Dateien bleiben liegen und werden gemeldet.
    /// </summary>
    public (int Restored, IReadOnlyList<string> Problems) RestoreRecovered()
    {
        var problems = new List<string>();
        var restored = 0;
        foreach (var file in PendingRecovery())
        {
            try
            {
                var bytes = File.ReadAllBytes(file.Path);
                var content = ResultFile.Read(bytes);
                if (content.Problems.Count > 0)
                {
                    problems.Add($"{file.FileName}: beschädigt ({content.Problems[0]}) – bleibt im Wiederherstellungsordner.");
                    continue;
                }
                var existing = System.IO.Path.Combine(Folder, file.FileName);
                if (File.Exists(existing) && File.ReadAllBytes(existing).AsSpan().SequenceEqual(bytes))
                {
                    File.Delete(file.Path); // bereits zurückkopiert
                    restored++;
                    continue;
                }
                WriteAtomic(Folder, file.FileName, bytes);
                File.Delete(file.Path);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ResultFileException)
            {
                problems.Add($"{file.FileName}: {ex.Message}");
            }
        }
        return (restored, problems);
    }

    public IReadOnlyList<ResultFileInfo> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return new DirectoryInfo(Folder).GetFiles("*" + ResultFile.Extension)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new ResultFileInfo(f.FullName, f.Name, f.LastWriteTime, f.Length))
            .ToList();
    }

    /// <summary>Temporär schreiben, auf Datenträger flushen, schließen und atomar umbenennen (Standard: nie überschreiben).</summary>
    internal static string WriteAtomic(string folder, string fileName, byte[] bytes, bool overwrite = false)
        => WriteAtomic(folder, fileName, stream => stream.Write(bytes), overwrite);

    internal static string WriteAtomic(string folder, string fileName, Action<Stream> write, bool overwrite = false)
    {
        Directory.CreateDirectory(folder);
        var final = System.IO.Path.Combine(folder, fileName);
        var temp = System.IO.Path.Combine(folder, "." + fileName + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                write(fs);
                fs.Flush(flushToDisk: true);
            }
            if (overwrite)
            {
                File.Move(temp, final, overwrite: true);
                return final;
            }
            var target = final;
            for (var n = 2; ; n++)
            {
                try
                {
                    File.Move(temp, target, overwrite: false);
                    return target;
                }
                catch (IOException) when (File.Exists(target) && n < 100)
                {
                    target = System.IO.Path.Combine(folder,
                        System.IO.Path.GetFileNameWithoutExtension(fileName) + $"_{n}" + System.IO.Path.GetExtension(fileName));
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Sanitize(string text)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var sb = new StringBuilder();
        foreach (var c in text.Trim()) sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        var s = sb.ToString();
        return s.Length > 60 ? s[..60] : s;
    }
}
