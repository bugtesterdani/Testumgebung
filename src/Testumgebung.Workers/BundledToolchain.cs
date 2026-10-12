namespace Testumgebung.Workers;

/// <summary>Portable Toolchain neben der Anwendung; in Entwicklungs-Checkouts im Projektverzeichnis.</summary>
public static class BundledToolchain
{
    public static string? FindDirectory(string? start = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        for (var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "toolchains", "w64devkit");
            if (File.Exists(Path.Combine(candidate, "bin", "g++.exe")) && File.Exists(Path.Combine(candidate, "bin", "gdb.exe"))) return candidate;
        }
        return null;
    }
    public static string? Compiler => FindDirectory() is { } dir ? Path.Combine(dir, "bin", "g++.exe") : null;
    public static string? Debugger => FindDirectory() is { } dir ? Path.Combine(dir, "bin", "gdb.exe") : null;
}
