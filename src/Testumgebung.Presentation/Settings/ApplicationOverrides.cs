using Testumgebung.Workers;

namespace Testumgebung.Presentation.Settings;

public static class ApplicationOverrides
{
    public static string? Value(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public static CodeRuntimeOptions Runtime(AppSettings settings, string? selectedPython, Func<string, string?>? environment = null)
    {
        environment ??= Value;
        return new()
    {
            PythonExecutable = environment("TESTUMGEBUNG_PYTHON") ?? settings.PythonExecutable ?? selectedPython,
            CppCompiler = environment("TESTUMGEBUNG_CXX") ?? settings.CppCompiler,
            CppCacheDirectory = environment("TESTUMGEBUNG_CPP_CACHE") ?? settings.CppCacheDirectory
        };
    }

    public static DatabaseSettings Database(DatabaseSettings configured) =>
        string.Equals(Value("TESTUMGEBUNG_DB"), "aus", StringComparison.OrdinalIgnoreCase)
            ? new DatabaseSettings { Provider = DatabaseChoice.None }
            : configured;
}
