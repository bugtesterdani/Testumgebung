using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Eigenschaften einer Gruppe (Abbildung 9: Bezeichnung, Bedingung, Wiederholung, Exit, Variablen).</summary>
public sealed class GroupPropertiesViewModel : PropertiesViewModel
{
    private readonly EditorViewModel _editor;
    private readonly NodeViewModel _node;
    private string _conditionText;
    private string? _conditionError;
    private string _repeatExpression;
    private string? _repeatError;
    private string _resourcesText;

    public GroupPropertiesViewModel(EditorViewModel editor, NodeViewModel node)
    {
        _editor = editor;
        _node = node;
        _conditionText = Group.EntryCondition ?? "";
        _repeatExpression = Group.Repetition.Expression ?? "";
        _resourcesText = string.Join(", ", Group.Resources);
        Variables = new VariableListViewModel(Group, Changed);
        CreateBlockCommand = new RelayCommand(() => editor.CreateGroupBlock(_node), () => CanCreateBlock);
        Storage = new StorageSettingsViewModel(editor, node.Definition);
        Options = new(editor, node);
    }

    /// <summary>Filter Anzeige/Datei/Datenbank (Gruppendefault).</summary>
    public StorageSettingsViewModel Storage { get; }
    public override NodeOptionsViewModel Options { get; }

    private GroupDefinition Group => _node.Group!;

    /// <summary>„Aus Gruppe erstellen“: wiederverwendbarer grafischer Baustein.</summary>
    public RelayCommand CreateBlockCommand { get; }

    public bool CanCreateBlock => _editor.CanCreateGroupBlock(_node);

    public override string Title => _node.IsExit ? "Exit-Gruppe" : "Gruppe";

    public static IReadOnlyList<RepeatMode> RepeatModes { get; } = Enum.GetValues<RepeatMode>();
    public static IReadOnlyList<RepeatResultPolicy> ResultPolicies { get; } = Enum.GetValues<RepeatResultPolicy>();

    public bool IsSection => _node.IsSection;

    public string Name
    {
        get => _node.Name;
        set { _node.Name = value; Changed(); OnPropertyChanged(); }
    }

    public string Description
    {
        get => Group.Description ?? "";
        set { Group.Description = value; Changed(); OnPropertyChanged(); }
    }

    public string EntryCondition
    {
        get => _conditionText;
        set
        {
            SetProperty(ref _conditionText, value);
            try
            {
                Group.EntryCondition = string.IsNullOrWhiteSpace(value) ? null : value;
                ConditionError = null;
                Changed();
            }
            catch (ExpressionException ex)
            {
                ConditionError = ex.Message;
            }
        }
    }

    public string? ConditionError
    {
        get => _conditionError;
        private set => SetProperty(ref _conditionError, value);
    }

    public RepeatMode RepeatMode
    {
        get => Group.Repetition.Mode;
        set
        {
            Group.Repetition = Group.Repetition with { Mode = value, Count = Math.Max(1, Group.Repetition.Count) };
            if (value is RepeatMode.While or RepeatMode.Until) RepeatExpression = _repeatExpression;
            if (value == RepeatMode.Duration && Group.Repetition.Duration <= TimeSpan.Zero) DurationSeconds = 10;
            Changed();
            OnPropertiesChanged(nameof(RepeatMode), nameof(ShowCount), nameof(ShowDuration), nameof(ShowExpression), nameof(CanStopWithinIteration));
        }
    }

    public bool CanStopWithinIteration => ShowDuration && !Parallel;

    public bool ShowCount => RepeatMode == RepeatMode.Count;
    public bool ShowDuration => RepeatMode == RepeatMode.Duration;
    public bool ShowExpression => RepeatMode is RepeatMode.While or RepeatMode.Until;

    public int Count
    {
        get => Group.Repetition.Count;
        set
        {
            Group.Repetition = Group.Repetition with { Count = Math.Max(1, value) };
            Changed();
            OnPropertyChanged();
        }
    }

    /// <summary>Zeitspanne auch innerhalb einer Iteration prüfen (restliche Schritte überspringen).</summary>
    public bool StopWithinIteration
    {
        get => Group.Repetition.StopWithinIteration;
        set
        {
            if (Group.Repetition.StopWithinIteration == value) return;
            Group.Repetition = Group.Repetition with { StopWithinIteration = value };
            _editor.MarkChanged();
            OnPropertyChanged();
        }
    }

    public double DurationSeconds
    {
        get => Group.Repetition.Duration.TotalSeconds;
        set
        {
            Group.Repetition = Group.Repetition with { Duration = TimeSpan.FromSeconds(Math.Max(0.001, value)) };
            Changed();
            OnPropertyChanged();
        }
    }

    public string RepeatExpression
    {
        get => _repeatExpression;
        set
        {
            SetProperty(ref _repeatExpression, value);
            try
            {
                if (string.IsNullOrWhiteSpace(value)) throw new ExpressionException("Ausdruck erforderlich.");
                Expression.Parse(value);
                Group.Repetition = Group.Repetition with { Expression = value };
                RepeatError = null;
                Changed();
            }
            catch (ExpressionException ex)
            {
                RepeatError = ex.Message;
            }
        }
    }

    public string? RepeatError
    {
        get => _repeatError;
        private set => SetProperty(ref _repeatError, value);
    }

    public bool Parallel
    {
        get => Group.Parallel;
        set
        {
            if (Group.Parallel == value) return;
            Group.Parallel = value;
            if (value) Group.Repetition = Group.Repetition with { StopWithinIteration = false };
            Changed();
            OnPropertiesChanged(nameof(Parallel), nameof(StopWithinIteration), nameof(CanStopWithinIteration));
        }
    }

    public RepeatResultPolicy ResultBeforeRepeat
    {
        get => Group.ResultBeforeRepeat;
        set { Group.ResultBeforeRepeat = value; Changed(); OnPropertyChanged(); }
    }

    public bool HasExitGroup
    {
        get => Group.ExitGroup is not null;
        set
        {
            if (value == HasExitGroup || _node.IsExit) return;
            if (value) Group.ExitGroup = new GroupDefinition(_editor.NewId("exit"), "Exit – " + Group.Name);
            else Group.ExitGroup = null;
            _editor.RebuildTree(selectId: _node.Id);
            OnPropertyChanged();
        }
    }

    public bool CanHaveExit => !_node.IsExit && _node.Definition != _editor.Project.Definition.Finalization;

    public string Resources
    {
        get => _resourcesText;
        set
        {
            SetProperty(ref _resourcesText, value);
            Group.Resources.Clear();
            Group.Resources.AddRange(value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct());
            Changed();
        }
    }

    public double? ResourceTimeoutSeconds
    {
        get => Group.ResourceTimeout?.TotalSeconds;
        set
        {
            Group.ResourceTimeout = value is > 0 ? TimeSpan.FromSeconds(value.Value) : null;
            Changed();
            OnPropertyChanged();
        }
    }

    public bool IsDutRun
    {
        get => Group.IsDutRun;
        set { Group.IsDutRun = value; Changed(); OnPropertyChanged(); }
    }

    public string DutIdVariable
    {
        get => Group.DutIdVariable;
        set { Group.DutIdVariable = value; Changed(); OnPropertyChanged(); }
    }

    public VariableListViewModel Variables { get; }

    private void Changed()
    {
        _node.Refresh();
        _editor.MarkChanged();
    }
}
