using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Testumgebung.Workers.Native;

internal enum ToolchainKind
{
    Msvc,
    Clang,
    Gcc,
}

internal sealed record CompilerDiagnostic(int Line, int Column, string Severity, string Message, string? Code);

internal sealed record BuildOutput(bool Success, string? LibraryPath, IReadOnlyList<CompilerDiagnostic> Diagnostics, string RawOutput);

/// <summary>
/// Definierte C++-Toolchain (Abschnitt 3): automatisch erkannt in der Reihenfolge MSVC → clang++ → g++
/// oder fest über die Umgebungsvariable <c>TESTUMGEBUNG_CXX</c> (Pfad zu cl.exe, clang++ oder g++).
/// Der Compiler und seine Version werden in der Laufzeitinfo gemeldet.
/// </summary>
internal sealed partial class Toolchain
{
    private Toolchain(ToolchainKind kind, string compiler, string version, string? vcvars)
    {
        Kind = kind;
        Compiler = compiler;
        Version = version;
        VcVars = vcvars;
    }

    public ToolchainKind Kind { get; }
    public string Compiler { get; }
    public string Version { get; }
    public string? VcVars { get; }

    public string LibraryExtension => OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    public override string ToString() => $"{Kind switch { ToolchainKind.Msvc => "MSVC", ToolchainKind.Clang => "clang++", _ => "g++" }} {Version}; compiler-sha256={CompilerHash()}".Trim();

    private string CompilerHash()
    {
        if (!File.Exists(Compiler)) return "system";
        using var stream = File.OpenRead(Compiler);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>Toolchain erkennen; null, wenn keine gefunden wurde (Fehlermeldung in <paramref name="error"/>).</summary>
    public static Toolchain? Detect(out string? error)
    {
        error = null;
        var configured = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CXX");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return DetectConfigured(configured, out error);
        }

        var windows = OperatingSystem.IsWindows();
        return DetectInstalled(windows, windows ? FindVcVars() : null, cmd => Probe(cmd, "--version"), out error);
    }

    private static Toolchain? DetectInstalled(bool windows, string? vcvars, Func<string, string?> probe, out string? error)
    {
        error = null;
        if (windows && vcvars is not null)
            return new Toolchain(ToolchainKind.Msvc, "cl", MsvcVersion(vcvars), vcvars);

        foreach (var (cmd, kind) in new[] { ("clang++", ToolchainKind.Clang), ("g++", ToolchainKind.Gcc) })
        {
            if (probe(cmd) is { } version) return new Toolchain(kind, cmd, version, null);
        }
        error = "Keine C++-Toolchain gefunden (MSVC Build Tools, clang++ oder g++). Pfad über TESTUMGEBUNG_CXX angeben.";
        return null;
    }

    private static Toolchain? DetectConfigured(string configured, out string? error)
    {
        error = null;
        var name = Path.GetFileNameWithoutExtension(configured).ToLowerInvariant();
        if (name == "cl")
            return new Toolchain(ToolchainKind.Msvc, configured, Probe(configured, "") ?? "", null);
        var kind = name.Contains("clang") ? ToolchainKind.Clang : ToolchainKind.Gcc;
        var version = Probe(configured, "--version");
        if (version is null)
        {
            error = $"Compiler '{configured}' (TESTUMGEBUNG_CXX) konnte nicht gestartet werden.";
            return null;
        }
        return new Toolchain(kind, configured, version, null);
    }

    /// <summary>Erste Zeile der Versionsausgabe oder null, wenn der Befehl nicht startet.</summary>
    private static string? Probe(string command, string arguments)
    {
        try
        {
            var r = Run(command, arguments, null, TimeSpan.FromSeconds(20));
            var line = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            var m = VersionNumber().Match(line);
            return m.Success ? m.Value : line;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }
    }

    private static string? FindVcVars()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        return FindVcVars(vswhere);
    }

    private static string? FindVcVars(string vswhere)
    {
        if (!File.Exists(vswhere)) return null;
        try
        {
            var r = Run(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath", null, TimeSpan.FromSeconds(20));
            var path = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (path is null) return null;
            var vcvars = Path.Combine(path, "VC", "Auxiliary", "Build", "vcvars64.bat");
            return File.Exists(vcvars) ? vcvars : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string MsvcVersion(string vcvars)
    {
        // …\VC\Auxiliary\Build\vcvars64.bat → Version aus Microsoft.VCToolsVersion.default.txt
        var file = Path.Combine(Path.GetDirectoryName(vcvars)!, "Microsoft.VCToolsVersion.default.txt");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
    }

    /// <summary>Übersetzt eine Quelldatei zu einer gemeinsam genutzten Bibliothek.</summary>
    public BuildOutput Build(string sourcePath, string outputPath)
    {
        var dir = Path.GetDirectoryName(sourcePath)!;
        var src = Path.GetFileName(sourcePath);
        var output = Path.GetFileName(outputPath);
        (string File, string Args) command = Kind switch
        {
            ToolchainKind.Msvc when VcVars is not null => ("cmd.exe",
                $"/d /s /c \"\"{VcVars}\" >nul && cl /nologo /EHsc /std:c++17 /utf-8 /O2 /LD \"{src}\" /Fe:\"{output}\"\""),
            ToolchainKind.Msvc => (Compiler, $"/nologo /EHsc /std:c++17 /utf-8 /O2 /LD \"{src}\" /Fe:\"{output}\""),
            _ when OperatingSystem.IsWindows() => (Compiler,
                $"-std=c++17 -O0 -g -fno-omit-frame-pointer -shared {(Kind == ToolchainKind.Gcc ? "-static-libgcc -static-libstdc++ " : "")}-o \"{output}\" \"{src}\""),
            _ => (Compiler, $"-std=c++17 -O2 -shared -fPIC -fvisibility=hidden -o \"{output}\" \"{src}\""),
        };
        var result = Run(command.File, command.Args, dir, TimeSpan.FromMinutes(3));
        var diagnostics = Parse(result.Output);
        var ok = result.ExitCode == 0 && File.Exists(outputPath);
        if (!ok && !diagnostics.Any(d => d.Severity == "error"))
        {
            var first = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? $"Compiler beendet mit Code {result.ExitCode}.";
            diagnostics = [.. diagnostics, new CompilerDiagnostic(0, 0, "error", first, null)];
        }
        return new BuildOutput(ok, ok ? outputPath : null, diagnostics, result.Output);
    }

    /// <summary>Diagnosen der Zeilen mit Dateiname „CodeBlock“ (#line-Zuordnung) bzw. Linkerfehler.</summary>
    private static IReadOnlyList<CompilerDiagnostic> Parse(string output)
    {
        var list = new List<CompilerDiagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = GccLine().Match(line);
            if (m.Success)
            {
                if (!IsUserFile(m.Groups["file"].Value)) continue;
                list.Add(new CompilerDiagnostic(int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value),
                    m.Groups["sev"].Value.Contains("error") ? "error" : "warning", m.Groups["msg"].Value.Trim(), null));
                continue;
            }
            m = MsvcLine().Match(line);
            if (m.Success)
            {
                if (!IsUserFile(m.Groups["file"].Value)) continue;
                list.Add(new CompilerDiagnostic(int.Parse(m.Groups["line"].Value),
                    m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 0,
                    m.Groups["sev"].Value.Contains("error") ? "error" : "warning", m.Groups["msg"].Value.Trim(), m.Groups["code"].Value));
            }
        }
        return list;
    }

    private static bool IsUserFile(string file) => Path.GetFileName(file.Trim()) == "CodeBlock";

    internal sealed record ProcessResult(int ExitCode, string Output);

    private static ProcessResult Run(string file, string arguments, string? workingDirectory, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        info.Environment["VSLANG"] = "1033"; // englische MSVC-Meldungen sind zuverlässig zu parsen
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"'{file}' konnte nicht gestartet werden.");
        var sb = new StringBuilder();
        var lockObj = new object();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lockObj) sb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lockObj) sb.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{file}' hat das Zeitlimit überschritten.");
        }
        process.WaitForExit();
        lock (lockObj) return new ProcessResult(process.ExitCode, sb.ToString());
    }

    [GeneratedRegex(@"^(?<file>.*?):(?<line>\d+):(?<col>\d+):\s*(?<sev>fatal error|error|warning):\s*(?<msg>.*)$")]
    private static partial Regex GccLine();

    [GeneratedRegex(@"^(?<file>.*?)\((?<line>\d+)(,(?<col>\d+))?\)\s*:\s*(?<sev>fatal error|error|warning)\s+(?<code>\w+)\s*:\s*(?<msg>.*)$")]
    private static partial Regex MsvcLine();

    [GeneratedRegex(@"\d+\.\d+(\.\d+)*")]
    private static partial Regex VersionNumber();
}
