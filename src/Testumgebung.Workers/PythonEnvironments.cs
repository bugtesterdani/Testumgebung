using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Testumgebung.Workers;

/// <summary>Beschreibbare Kopien der gebündelten Python-Laufzeit und deren benutzerbezogene Auswahl.</summary>
public sealed class PythonEnvironments(string? workerDirectory = null, string? environmentRoot = null, string? selectionFile = null, string? registryKey = null)
{
    private readonly string _source = Path.Combine(workerDirectory ?? Path.Combine(AppContext.BaseDirectory, "workers"), "python", "runtime");
    private readonly string _root = environmentRoot ?? Path.Combine(Path.GetTempPath(), "Testumgebung", "PythonEnvironments");
    private readonly string _selectionFile = selectionFile ?? DefaultSelectionFile;
    private readonly string? _registryKey = registryKey ?? (selectionFile is null ? RegistryKey : null);
    public static string DefaultSelectionFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "python-environment.json");
    public const string RegistryKey = @"Software\Testumgebung";
    public const string RegistryValue = "PythonVenv";

    public const string Commands = "python --version\npython -m pip --version\npython -m pip list\npython -m pip install PAKETNAME\npython -m pip install -r requirements.txt\npython -m pip freeze > requirements.txt\npython -m pip check";

    public string Create()
    {
        foreach (var file in new[] { "python.exe", "python314.dll", "python314.zip", "LICENSE.txt", "pip-bootstrap/pip.whl" })
            if (!File.Exists(Path.Combine(_source, file))) throw new FileNotFoundException($"Mitgelieferte Python-Laufzeit unvollständig: {file}");
        var destination = Path.Combine(_root, $"Python-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(_source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetAttributes(target, FileAttributes.Normal);
        }
        Directory.CreateDirectory(Path.Combine(destination, "Lib", "site-packages"));
        File.WriteAllLines(Path.Combine(destination, "python314._pth"), ["python314.zip", ".", "Lib/site-packages", "pip-bootstrap/pip.whl", "import site"]);
        File.WriteAllText(Path.Combine(destination, "BEFEHLE.txt"), Commands.Replace("\n", Environment.NewLine));
        File.WriteAllText(Path.Combine(destination, "Konsole.ps1"), ConsoleScript, new System.Text.UTF8Encoding(true));
        return Path.Combine(destination, "python.exe");
    }

    public static ProcessStartInfo ConsoleStartInfo(string executable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        if (!File.Exists(executable) || !File.Exists(Path.Combine(directory, "Konsole.ps1")))
            throw new FileNotFoundException("Python-Umgebung oder Konsolenskript fehlt. Bitte eine neue Umgebung erstellen.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Normal };
        foreach (var argument in new[] { "-NoProfile", "-NoExit", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(directory, "Konsole.ps1") })
            start.ArgumentList.Add(argument);
        return start;
    }

    public string? LoadSelection()
    {
        try
        {
            if (_registryKey is not null && OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(_registryKey);
                if (PythonEnvironmentResolver.Interpreter(key?.GetValue(RegistryValue) as string) is { } saved) return saved;
            }
            if (!File.Exists(_selectionFile)) return null;
            var path = JsonSerializer.Deserialize<string>(File.ReadAllText(_selectionFile));
            return PythonEnvironmentResolver.Interpreter(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void SaveSelection(string? executable)
    {
        if (executable is not null && (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)))
            throw new FileNotFoundException("Python-Interpreter fehlt oder der Pfad ist nicht absolut.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_selectionFile))!);
        File.WriteAllText(_selectionFile, JsonSerializer.Serialize(executable));
        if (_registryKey is not null && OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(_registryKey);
            if (executable is null) key.DeleteValue(RegistryValue, false);
            else key.SetValue(RegistryValue, executable, RegistryValueKind.String);
        }
    }

    private const string ConsoleScript = """
        Set-Location -LiteralPath $PSScriptRoot
        $env:PATH = $PSScriptRoot + ';' + $env:PATH
        $env:PYTHONHOME = $null
        $env:PYTHONPATH = $null
        $global:TestumgebungPython = Join-Path $PSScriptRoot 'python.exe'
        function global:python { & $global:TestumgebungPython @args }
        function global:pip { & $global:TestumgebungPython -m pip @args }
        Write-Host 'Python-Umgebung der Testumgebung' -ForegroundColor Cyan
        Write-Host $PSScriptRoot
        python --version
        python -m pip --version
        Write-Host 'Vorbereitete Befehle (PAKETNAME ersetzen):' -ForegroundColor Cyan
        Get-Content -LiteralPath (Join-Path $PSScriptRoot 'BEFEHLE.txt')
        Write-Host 'Danach in der Testumgebung: Diese Umgebung verwenden. Nach Paketänderungen erneut aktivieren.'
        """;
}
