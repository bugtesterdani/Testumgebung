/**
 * @file
 * @brief Wählt einen vorhandenen Interpreter in der festgelegten Priorität ohne globale Umschaltung.
 */

namespace Testumgebung.Workers;

/// <summary>Ermittelt einen vorhandenen Interpreter, ohne andere Prüfplätze umzuschalten.</summary>
public static class PythonEnvironmentResolver
{
    /// <summary>Wählt Programm-, Variablen-, gespeicherte Umgebung oder die Standardlaufzeit in dieser Reihenfolge.</summary>
    /// <param name="workerDirectory">Basis für die Ermittlung der Standardlaufzeit.</param>
    /// <param name="programVenv">Programmspezifischer Interpreter oder Umgebungsordner.</param>
    /// <param name="variableVenv">Laufbezogene Umgebung aus der Variablenkonfiguration.</param>
    /// <param name="savedVenv">Gespeicherte benutzerbezogene Umgebung.</param>
    /// <returns>Pfad des zuerst auffindbaren Interpreters beziehungsweise der Standardauswahl.</returns>
    // Nur alte Pfade dürfen bei fehlendem Interpreter zur nächsten Stufe weiterfallen.
    public static string Resolve(string workerDirectory, string? programVenv, string? variableVenv, string? savedVenv,
        PythonEnvironments? environments = null)
    {
        foreach (var selection in new[] { programVenv, variableVenv, savedVenv })
        {
            var reference = selection?.Trim();
            if (reference == PythonEnvironments.BundledId)
            {
                var bundled = Path.Combine(workerDirectory, "python", "runtime", "python.exe");
                if (!File.Exists(bundled)) throw new FileNotFoundException("Mitgelieferte Python-Laufzeit fehlt.");
                return Path.GetFullPath(bundled);
            }
            if (reference?.StartsWith("id:", StringComparison.Ordinal) == true)
                return (environments ?? new PythonEnvironments(workerDirectory)).ResolveId(reference[3..]);
            if (Interpreter(selection) is { } legacy) return legacy;
        }
        return CodeRuntimeOptions.ResolvePython(workerDirectory);
    }

    /// <summary>Eine explizite ID bleibt verbindlich; alte fehlende Pfade lassen die Projektauswahl zu.</summary>
    public static string? ProgramSelection(string? selection, string? projectSelection)
        => selection?.Trim() == PythonEnvironments.BundledId || selection?.Trim().StartsWith("id:", StringComparison.Ordinal) == true
            ? selection!.Trim() : Interpreter(selection) is not null ? selection : projectSelection;

    public static string? Interpreter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (!Directory.Exists(path)) return null;
            foreach (var relative in new[] { "Scripts/python.exe", "python.exe", "bin/python" })
            {
                var executable = Path.Combine(path, relative);
                if (File.Exists(executable)) return Path.GetFullPath(executable);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
}
