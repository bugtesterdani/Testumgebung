namespace Testumgebung.Workers;

/// <summary>Ermittelt einen vorhandenen Interpreter, ohne andere Prüfplätze umzuschalten.</summary>
public static class PythonEnvironmentResolver
{
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
