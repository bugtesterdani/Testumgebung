/**
 * @file
 * @brief Schreibt Diagnoseinformationen zu unbehandelten Fehlern für die nachträgliche Untersuchung.
 */

using System.IO;
using System.Text.Json;

namespace Testumgebung.Desktop;

internal static class FaultLog
{
    public static string? Write(Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "diagnose");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"fehler-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                applicationVersion = typeof(App).Assembly.GetName().Version?.ToString(),
                runtime = Environment.Version.ToString(),
                exception = error.ToString(),
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var old in Directory.EnumerateFiles(directory, "fehler-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(50))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceError("Fehlerprotokoll konnte nicht geschrieben werden: {0}; ursprünglicher Fehler: {1}", ex, error);
            return null;
        }
    }
}
