/**
 * @file
 * @brief Stellt Benachrichtigungen, Befehle und UI-Dispatcher bereit, ohne WPF in die ViewModels zu koppeln.
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Testumgebung.Presentation.Mvvm;

/// <summary>Basis für ViewModels mit Änderungsbenachrichtigung.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void OnPropertiesChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}

/// <summary>Einfaches Command; <see cref="RaiseCanExecuteChanged"/> nach Zustandsänderungen aufrufen.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute(parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Überführt Aufrufe aus Engine-Threads in den UI-Thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Führt sofort aus (Tests, Konsolen).</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    private readonly object _gate = new();

    public static ImmediateDispatcher Instance { get; } = new();

    public void Post(Action action)
    {
        lock (_gate) action();
    }
}

/// <summary>Nutzt den SynchronizationContext des UI-Threads (WPF: DispatcherSynchronizationContext).</summary>
public sealed class SynchronizationContextDispatcher(SynchronizationContext context) : IUiDispatcher
{
    public static IUiDispatcher FromCurrent()
        => SynchronizationContext.Current is { } ctx ? new SynchronizationContextDispatcher(ctx) : ImmediateDispatcher.Instance;

    public void Post(Action action) => context.Post(_ => action(), null);
}

/// <summary>Dateiauswahl – in WPF über Standarddialoge, in Tests ersetzt.</summary>
public interface IFileDialogService
{
    string? OpenProject();
    string? SaveProject(string suggestedName);
    void ShowError(string message);

    /// <summary>Datei mit der zugeordneten Anwendung öffnen (z. B. VI in LabVIEW).</summary>
    void OpenExternal(string path);

    /// <summary>Beliebige Datei wählen (Filter im Format „Text|*.ext|…“), z. B. für den Kurvenimport.</summary>
    string? OpenFile(string title, string filter) => null;

    /// <summary>Speicherort wählen (z. B. Konfigurationssicherung).</summary>
    string? SaveFile(string title, string filter, string suggestedName) => null;
}
