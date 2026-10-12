using Testumgebung.DatabaseAdapters;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Tests;

public sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            Skip = $"Integrationstest: {variable} ist nicht gesetzt.";
    }
}

public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (!NativeSqliteFactory.IsAvailable) Skip = "Native SQLite-Bibliothek ist nicht verfügbar.";
    }
}

public sealed class CppFactAttribute : FactAttribute
{
    public CppFactAttribute()
    {
        var configured = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CXX");
        if (!string.IsNullOrWhiteSpace(configured) || BundledToolchain.Compiler is not null) return;
        var names = OperatingSystem.IsWindows() ? new[] { "g++.exe", "clang++.exe", "cl.exe" } : new[] { "g++", "clang++" };
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        if (!paths.Any(p => names.Any(n => File.Exists(Path.Combine(p, n)))))
            Skip = "Keine C++-Toolchain im Paket oder PATH verfügbar.";
    }
}

public sealed class CppTheoryAttribute : TheoryAttribute
{
    public CppTheoryAttribute() => Skip = new CppFactAttribute().Skip;
}

public sealed class GdbFactAttribute : FactAttribute
{
    public GdbFactAttribute() => Skip = new CppFactAttribute().Skip
        ?? (BundledToolchain.Debugger is null ? "Mitgeliefertes GDB ist nicht verfügbar." : null);
}
