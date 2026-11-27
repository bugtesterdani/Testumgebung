/**
 * @file
 * @brief Erstellt beschreibbare Python-Laufzeitkopien und verwaltet die benutzerbezogene Umgebungsauswahl.
 */

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;

namespace Testumgebung.Workers;

/// <summary>Beschreibbare Kopien der gebündelten Python-Laufzeit und deren benutzerbezogene Auswahl.</summary>
public sealed class PythonEnvironments(string? workerDirectory = null, string? environmentRoot = null, string? selectionFile = null, string? registryKey = null)
{
    private readonly string _source = Path.Combine(workerDirectory ?? Path.Combine(AppContext.BaseDirectory, "workers"), "python", "runtime");
    private readonly string _root = environmentRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "PythonEnvironments");
    private readonly string _importRoot = environmentRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "PythonEnvironments");
    private static readonly string[] RuntimeFiles = ["python.exe", "python315.dll", "python315.zip", "LICENSE.txt", "pip-bootstrap/pip.whl"];
    private readonly string _selectionFile = selectionFile ?? DefaultSelectionFile;
    private readonly string? _registryKey = registryKey ?? (selectionFile is null ? RegistryKey : null);
    public static string DefaultSelectionFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "python-environment.json");
    public const string RegistryKey = @"Software\Testumgebung";
    public const string RegistryValue = "PythonVenv";

    private static readonly object CatalogGate = new();

    public const string BundledId = "bundled";
    public const string ManifestFile = "testumgebung-python.json";
    public sealed record EnvironmentInfo(string Id, string Executable)
    {
        public string Selection => "id:" + Id;
        public override string ToString() => Id;
    }

    public IReadOnlyList<EnvironmentInfo> List()
    {
        if (!Directory.Exists(_importRoot)) return [];
        return Directory.EnumerateDirectories(_importRoot).Select(directory =>
        {
            var manifest = Path.Combine(directory, ManifestFile);
            try { return File.Exists(manifest) ? new EnvironmentInfo(ReadId(manifest), Path.Combine(directory, "python.exe")) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }).OfType<EnvironmentInfo>().OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static string ReadId(string manifest)
    {
        string? id;
        try { id = JsonSerializer.Deserialize<string>(File.ReadAllText(manifest)); }
        catch (JsonException ex) { throw new InvalidDataException("Ungültige Python-Umgebungsdatei.", ex); }
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Ungültige Python-Umgebungs-ID.");
        return id!;
    }

    public string EnsureId(string executable)
    {
        lock (CatalogGate) return EnsureIdCore(executable);
    }

    private static string EnsureIdCore(string executable)
    {
        var manifest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, ManifestFile);
        if (File.Exists(manifest)) return ReadId(manifest);
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(manifest, JsonSerializer.Serialize(id));
        return id;
    }

    public string ResolveId(string id)
    {
        var matches = List().Where(item => item.Id == id).ToArray();
        if (matches.Length != 1 || !File.Exists(matches[0].Executable))
            throw new FileNotFoundException($"Python-Umgebung '{id}' fehlt oder ist nicht eindeutig. Bitte die passende Umgebung importieren.");
        return matches[0].Executable;
    }

    public const string Commands = "python --version\npython -m pip --version\npython -m pip list\npython -m pip install PAKETNAME\npython -m pip install -r requirements.txt\npython -m pip freeze > requirements.txt\npython -m pip check";

    public string Create()
    {
        foreach (var file in RuntimeFiles)
            if (!File.Exists(Path.Combine(_source, file))) throw new FileNotFoundException($"Mitgelieferte Python-Laufzeit unvollständig: {file}");
        var destination = NewEnvironmentPath(_root);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(_source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetAttributes(target, FileAttributes.Normal);
        }
        Directory.CreateDirectory(Path.Combine(destination, "Lib", "site-packages"));
        File.WriteAllLines(Path.Combine(destination, "python315._pth"), ["python315.zip", ".", "Lib/site-packages", "pip-bootstrap/pip.whl", "import site"]);
        WriteConsoleFiles(destination);
        EnsureId(Path.Combine(destination, "python.exe"));
        return Path.Combine(destination, "python.exe");
    }

    private static string NewEnvironmentPath(string root)
        => Path.Combine(root, $"Python-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

    private static void WriteConsoleFiles(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "BEFEHLE.txt"), Commands.Replace("\n", Environment.NewLine));
        File.WriteAllText(Path.Combine(directory, "Konsole.ps1"), ConsoleScript, new System.Text.UTF8Encoding(true));
    }

    public static bool CanExport(string? executable)
        => executable is not null && Path.GetFileName(executable).Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(executable) is { Length: > 0 } directory && IsPortableRuntime(directory);

    private static bool IsPortableRuntime(string directory)
        => File.Exists(Path.Combine(directory, "python315._pth"))
            && RuntimeFiles.All(file => File.Exists(Path.Combine(directory, file)));

    /// <summary>Exports the portable runtime and its installed packages as a ZIP snapshot.</summary>
    public void Export(string executable, string archivePath)
    {
        if (!CanExport(executable)) throw new InvalidDataException("Exportierbar sind vollständige portable Python-Umgebungen der Testumgebung.");
        var source = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var destination = Path.GetFullPath(archivePath);
        if (IsWithin(source, destination)) throw new IOException("Das ZIP außerhalb der Python-Umgebung speichern.");
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, $".python-{Guid.NewGuid():N}.tmp");
        try
        {
            EnsureId(executable);
            WriteArchive(source, staging);
            File.Move(staging, destination, overwrite: true);
        }
        finally { File.Delete(staging); }
    }

    private static bool IsWithin(string directory, string file)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void WriteArchive(string directory, string path)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var files = Directory.GetFiles(directory, "*", options);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
        }
    }

    /// <summary>Restores a ZIP into a new per-user folder. Selection remains unchanged until explicitly activated.</summary>
    public string Import(string archivePath)
    {
        lock (CatalogGate) return ImportCore(archivePath);
    }

    private string ImportCore(string archivePath)
    {
        var destination = NewEnvironmentPath(_importRoot);
        try
        {
            ExtractArchive(archivePath, destination);
            if (!IsPortableRuntime(destination))
                throw new InvalidDataException("Das ZIP enthält keine vollständige portable Python-Umgebung der Testumgebung.");
            Directory.CreateDirectory(Path.Combine(destination, "Lib", "site-packages"));
            var executable = Path.Combine(destination, "python.exe");
            var id = EnsureId(executable);
            if (List().Any(item => item.Id == id && item.Executable != executable))
                throw new InvalidDataException($"Python-Umgebung '{id}' ist bereits vorhanden. Bestehende Umgebungen werden nicht überschrieben.");
            WriteConsoleFiles(destination);
            return Path.Combine(destination, "python.exe");
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private static void ExtractArchive(string path, string directory)
    {
        using var archive = ZipFile.OpenRead(path);
        // Reject drive and alternate stream syntax before the framework checks extraction boundaries.
        if (archive.Entries.Any(entry => entry.FullName.Contains(':')))
            throw new InvalidDataException("Das ZIP enthält einen ungültigen Dateipfad.");
        archive.ExtractToDirectory(directory);
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

    public string? LoadSelection(bool preserveReference = false)
    {
        try
        {
            if (File.Exists(_selectionFile))
            {
                var path = JsonSerializer.Deserialize<string>(File.ReadAllText(_selectionFile));
                if (path == BundledId || path?.StartsWith("id:", StringComparison.Ordinal) == true)
                {
                    if (preserveReference) return path;
                    // Preserve a missing explicit selection so callers cannot silently use another runtime.
                    try { return PythonEnvironmentResolver.Resolve(Path.GetDirectoryName(Path.GetDirectoryName(_source))!, path, null, null, this); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return path; }
                }
                return PythonEnvironmentResolver.Interpreter(path);
            }
            if (_registryKey is not null && OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(_registryKey);
                if (PythonEnvironmentResolver.Interpreter(key?.GetValue(RegistryValue) as string) is { } saved) return saved;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void SaveSelection(string? executable)
    {
        if (executable is not null && (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)))
            throw new FileNotFoundException("Python-Interpreter fehlt oder der Pfad ist nicht absolut.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_selectionFile))!);
        var selection = List().FirstOrDefault(item => item.Executable == executable)?.Selection ?? executable;
        File.WriteAllText(_selectionFile, JsonSerializer.Serialize(selection));
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
