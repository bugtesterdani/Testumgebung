/**
 * @file
 * @brief Verwaltet eine einzelne Werkeraufforderung mit Eingabezustand und Validierung.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Ein Inhalt auf der Werkeroberfläche: Anweisung, Bild, Eingabe oder Auswahl.</summary>
public sealed class OperatorContentViewModel : ObservableObject
{
    private TaskCompletionSource<object?>? _answer;
    private string _input;
    private string? _selectedChoice;
    private string? _error;
    private bool _isAnswered;

    public OperatorContentViewModel(OperatorPrompt prompt)
    {
        Prompt = prompt;
        _input = prompt.DefaultValue switch
        {
            null => "",
            double d => Format.Number(d),
            var v => v.ToString() ?? "",
        };
        ConfirmCommand = new RelayCommand(Confirm, () => IsAwaiting);
        ChooseCommand = new RelayCommand(p =>
        {
            SelectedChoice = p as string;
            Confirm();
        }, _ => IsAwaiting);
    }

    public OperatorPrompt Prompt { get; }
    public string DisplayId => Prompt.DisplayId;
    public string Title => Prompt.StepName;
    public string Text => Prompt.Text;
    public string? ImagePath => Prompt.ImagePath;
    public bool HasImage => !string.IsNullOrWhiteSpace(Prompt.ImagePath);
    public bool IsInput => Prompt.Kind == OperatorPromptKind.Input;
    public bool IsChoice => Prompt.Kind == OperatorPromptKind.Choice;
    public bool IsConfirmation => Prompt.Kind is OperatorPromptKind.Instruction or OperatorPromptKind.ImageConfirmation;
    public IReadOnlyList<string> Choices => Prompt.AllowedValues ?? [];
    public string? Unit => Prompt.Unit;
    public bool Required => Prompt.Required;

    public string RangeText => Prompt.Min is null && Prompt.Max is null
        ? ""
        : $"Zulässiger Bereich: {Format.Number(Prompt.Min)} – {Format.Number(Prompt.Max)}{(Prompt.Unit is null ? "" : " " + Prompt.Unit)}";

    public string ConfirmText => IsConfirmation ? "Bestätigen" : "Antwort bestätigen";

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand ChooseCommand { get; }

    public bool IsAwaiting => _answer is not null && !_isAnswered;

    public bool IsAnswered
    {
        get => _isAnswered;
        private set
        {
            SetProperty(ref _isAnswered, value);
            OnPropertyChanged(nameof(IsAwaiting));
            ConfirmCommand.RaiseCanExecuteChanged();
            ChooseCommand.RaiseCanExecuteChanged();
        }
    }

    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value)) Error = null;
        }
    }

    public string? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (SetProperty(ref _selectedChoice, value)) Error = null;
        }
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Antwortwert nach Bestätigung (für die Anzeige „letzter Wert“).</summary>
    public string AnswerText { get; private set; } = "";

    internal void Await(TaskCompletionSource<object?> answer)
    {
        _answer = answer;
        OnPropertyChanged(nameof(IsAwaiting));
        ConfirmCommand.RaiseCanExecuteChanged();
        ChooseCommand.RaiseCanExecuteChanged();
    }

    internal void Cancel()
    {
        _answer?.TrySetCanceled();
        IsAnswered = true;
    }

    /// <summary>Antwort prüfen und an die Engine übergeben. Fehler werden am Eingabefeld angezeigt.</summary>
    public void Confirm()
    {
        if (!IsAwaiting) return;
        object? raw = IsInput ? Input : IsChoice ? SelectedChoice : true;
        var (value, error) = Prompt.Validate(raw);
        if (error is not null)
        {
            Error = error;
            return;
        }
        AnswerText = value switch
        {
            null => "",
            double d => Format.Number(d) + (Unit is null ? "" : " " + Unit),
            bool => "bestätigt",
            var v => TypedValue.Format(v).Trim('"'),
        };
        OnPropertyChanged(nameof(AnswerText));
        IsAnswered = true;
        _answer!.TrySetResult(IsConfirmation ? true : value);
    }
}
