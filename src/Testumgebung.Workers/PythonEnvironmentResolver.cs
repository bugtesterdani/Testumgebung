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
    // Nicht nur eine gesetzte Zeichenkette, sondern ein auffindbarer Interpreter zählt.
    // Ein fehlender Ordner lässt die Auswahl zur nächsten Prioritätsstufe weiterfallen.
    public static string Resolve(string workerDirectory, string? programVenv, string? variableVenv, string? savedVenv)
        => Interpreter(programVenv) ?? Interpreter(variableVenv) ?? Interpreter(savedVenv)
            ?? CodeRuntimeOptions.ResolvePython(workerDirectory);

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
