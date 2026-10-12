using System.Diagnostics;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Workers;

namespace Testumgebung.Presentation.ViewModels;

public sealed class PythonEnvironmentViewModel : ObservableObject
{
    private readonly CodeRuntime? _runtime;
    private readonly PythonEnvironments _environments;
    private readonly Func<bool> _anyRunning;
    private readonly Action<ProcessStartInfo> _openConsole;
    private bool _busy;
    private string? _createdExecutable;
    private string _status = "Eine temporäre Kopie erstellen, Pakete in der Konsole installieren und danach die Umgebung aktivieren.";

    public PythonEnvironmentViewModel(CodeRuntime? runtime, Func<bool> anyRunning, PythonEnvironments? environments = null,
        Action<ProcessStartInfo>? openConsole = null)
    {
        _runtime = runtime;
        _anyRunning = anyRunning;
        _environments = environments ?? new PythonEnvironments(runtime?.Options.WorkerDirectory);
        _openConsole = openConsole ?? (start => Process.Start(start)?.Dispose());
        _createdExecutable = _environments.LoadSelection();
        CreateCommand = new RelayCommand(() => _ = CreateAsync(), () => !_busy);
        OpenConsoleCommand = new RelayCommand(OpenConsole, () => !_busy && _createdExecutable is not null);
        UseEnvironmentCommand = new RelayCommand(() => Select(_createdExecutable), () => !_busy && _runtime is not null && _createdExecutable is not null);
        UseBundledCommand = new RelayCommand(() => Select(null), () => !_busy && _runtime is not null);
    }

    public RelayCommand CreateCommand { get; }
    public RelayCommand OpenConsoleCommand { get; }
    public RelayCommand UseEnvironmentCommand { get; }
    public RelayCommand UseBundledCommand { get; }
    public string Commands => PythonEnvironments.Commands;
    public string EnvironmentPath => _createdExecutable is null ? "Noch keine externe Kopie erstellt." : Path.GetDirectoryName(_createdExecutable)!;
    public string ActiveInterpreter => _runtime is null ? "Keine Code-Laufzeit angebunden." : CodeRuntimeOptions.ResolvePython(_runtime.Options.WorkerDirectory, _runtime.Options.PythonExecutable);
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public async Task CreateAsync()
    {
        if (_busy) return;
        _busy = true;
        Refresh();
        Status = "Python-Laufzeit wird in einen neuen temporären Ordner kopiert …";
        try
        {
            _createdExecutable = await Task.Run(_environments.Create);
            Status = "Umgebung erstellt. Nach der Paketinstallation „Diese Umgebung verwenden“ wählen. Temporäre Ordner können durch die Windows-Bereinigung entfernt werden.";
            OpenConsole();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status = "Python-Umgebung konnte nicht erstellt oder geöffnet werden: " + ex.Message; }
        finally { _busy = false; Refresh(); }
    }

    public void OpenConsole()
    {
        if (_createdExecutable is null) return;
        try { _openConsole(PythonEnvironments.ConsoleStartInfo(_createdExecutable)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status = "Konsole konnte nicht geöffnet werden: " + ex.Message; }
    }

    public void Select(string? executable)
    {
        if (_runtime is null) return;
        if (_anyRunning()) { Status = "Bitte zuerst alle Prüfungen und Auftragsauswertungen beenden. Die Python-Laufzeit bleibt unverändert."; return; }
        try
        {
            if (executable is not null && !File.Exists(executable)) throw new FileNotFoundException("Python-Umgebung wurde entfernt. Bitte neu erstellen.");
            var previous = _runtime.Options.PythonExecutable;
            _runtime.SelectPython(executable);
            try { _environments.SaveSelection(executable); }
            catch { _runtime.SelectPython(previous); throw; }
            Status = executable is null ? "Die mitgelieferte Python-Laufzeit ist aktiv." : "Die externe Python-Kopie ist aktiv. Neue Python-Bausteine verwenden ihre installierten Pakete.";
            OnPropertyChanged(nameof(ActiveInterpreter));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status = "Python-Laufzeit konnte nicht gewechselt werden: " + ex.Message; }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(EnvironmentPath));
        CreateCommand.RaiseCanExecuteChanged();
        OpenConsoleCommand.RaiseCanExecuteChanged();
        UseEnvironmentCommand.RaiseCanExecuteChanged();
        UseBundledCommand.RaiseCanExecuteChanged();
    }
}
