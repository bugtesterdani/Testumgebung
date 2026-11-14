/**
 * @file
 * @brief Bindet bearbeitbare Definitionen an Eigenschaftsfelder und übernimmt validierte Änderungen ins Projekt.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public abstract class PropertiesViewModel : ObservableObject
{
    public abstract string Title { get; }
    public virtual NodeOptionsViewModel? Options => null;
}

/// <summary>Bearbeitbare Variablendefinition einer Gruppe.</summary>
public sealed class VariableRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private VarType _type;
    private string _initial;
    private string? _error;

    public VariableRowViewModel(VariableDefinition definition, Action changed)
    {
        _changed = changed;
        _name = definition.Name;
        _type = definition.Type;
        _initial = definition.Initial switch
        {
            null => "",
            string text => text,
            var value => TypedValue.Format(value),
        };
    }

    public static IReadOnlyList<VarType> Types { get; } = Enum.GetValues<VarType>();

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value.Trim())) _changed(); }
    }

    public VarType Type
    {
        get => _type;
        set { if (SetProperty(ref _type, value)) _changed(); }
    }

    public string Initial
    {
        get => _initial;
        set { if (SetProperty(ref _initial, value)) _changed(); }
    }

    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }

    public VariableDefinition? ToDefinition()
    {
        if (Name == Scope.GroupResultVariable)
        {
            Error = "Schreibgeschützte Systemvariable";
            return null;
        }
        if (string.IsNullOrWhiteSpace(Name) || Name.Contains('/') || Name.Contains(' '))
        {
            Error = "Ungültiger Name";
            return null;
        }
        try
        {
            var initial = string.IsNullOrEmpty(Initial) ? null : TypedValue.Parse(Initial, Type);
            Error = null;
            return new VariableDefinition(Name, Type, initial);
        }
        catch (TypeMismatchException ex)
        {
            Error = ex.Message;
            return null;
        }
    }
}

/// <summary>Variablenliste mit Hinzufügen/Entfernen, schreibt in die Gruppendefinition.</summary>
public sealed class VariableListViewModel : ObservableObject
{
    private readonly GroupDefinition _group;
    private readonly Action _changed;

    public VariableListViewModel(GroupDefinition group, Action changed)
    {
        _group = group;
        _changed = changed;
        foreach (var variable in group.Variables) Rows.Add(new VariableRowViewModel(variable, Apply));
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(parameter => Remove(parameter as VariableRowViewModel));
    }

    public ObservableCollection<VariableRowViewModel> Rows { get; } = [];
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public void Add()
    {
        var index = 1;
        while (Rows.Any(variableRowViewModel => variableRowViewModel.Name == $"Variable{index}")) index++;
        Rows.Add(new VariableRowViewModel(new VariableDefinition($"Variable{index}", VarType.Double, 0.0), Apply));
        Apply();
    }

    public void Remove(VariableRowViewModel? row)
    {
        if (row is null) return;
        Rows.Remove(row);
        Apply();
    }

    private void Apply()
    {
        var defs = Rows.Select(variableRowViewModel => variableRowViewModel.ToDefinition()).ToList();
        foreach (var dup in Rows.GroupBy(variableRowViewModel => variableRowViewModel.Name).Where(group => group.Count() > 1).SelectMany(group => group))
            dup.Error = "Name doppelt";
        if (defs.Any(variable => variable is null) || Rows.Any(variableRowViewModel => variableRowViewModel.Error is not null)) return;
        _group.Variables.Clear();
        _group.Variables.AddRange(defs!);
        _changed();
    }
}

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

/// <summary>Ein Parameter eines Katalogbausteins.</summary>
public sealed class ParameterRowViewModel : ObservableObject
{
    private readonly StepPropertiesViewModel _owner;

    public ParameterRowViewModel(StepPropertiesViewModel owner, ParameterDescriptor descriptor)
    {
        _owner = owner;
        Descriptor = descriptor;
    }

    public string NodeId => _owner.Step.Id;
    public StepPropertiesViewModel Owner => _owner;
    public ParameterDescriptor Descriptor { get; }
    public string Label => Descriptor.Label;
    public string? Hint => Descriptor.Hint;
    public ParameterKind Kind => Descriptor.Kind;
    public IReadOnlyList<string> Choices => Descriptor.Choices ?? [];
    public bool IsChoice => Kind == ParameterKind.Choice;
    public bool IsBool => Kind == ParameterKind.Bool;
    public bool IsCode => Kind == ParameterKind.Code;
    public bool IsTable => Kind == ParameterKind.Table;
    public bool IsText => !IsChoice && !IsBool && !IsCode && !IsHidden && !IsTable;
    public bool IsMultiline => Descriptor.Name is "Verbindungsoptionen" or "Header" or "Body";

    /// <summary>Wird durch eine eigene Oberfläche ersetzt (z. B. Bausteinauswahl).</summary>
    private readonly bool _hidden;
    public bool IsHidden { get => _hidden || !_owner.IsParameterActive(Descriptor.Name) || !_owner.IsMetadataVisible(Descriptor); init => _hidden = value; }
    internal void RefreshVisibility()
    {
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsText));
    }
    public bool IsExpression => Kind is ParameterKind.Expression;
    public bool IsVariable => Kind is ParameterKind.Variable;
    public bool HasVisualBuilder => IsExpression || IsVariable && !Value.StartsWith("_BlockZweig_", StringComparison.Ordinal);

    public string Value
    {
        get => _owner.Step.Parameters.TryGetValue(Descriptor.Name, out var value) ? value : Descriptor.Default;
        set
        {
            if (Value == value) return;
            _owner.Step.Parameters[Descriptor.Name] = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoolValue));
            OnPropertyChanged(nameof(HasVisualBuilder));
            _owner.ParametersChanged();
        }
    }

    internal void RefreshValue() => OnPropertyChanged(nameof(Value));

    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }
}

/// <summary>Eigenschaften eines Schritts: Name, Beschreibung und die Bausteinparameter.</summary>
public sealed class StepPropertiesViewModel : PropertiesViewModel
{
    private readonly EditorViewModel _editor;
    private readonly NodeViewModel _node;
    private IReadOnlyList<string> _diagnostics = [];

    public StepPropertiesViewModel(EditorViewModel editor, NodeViewModel node)
    {
        _editor = editor;
        _node = node;
        if (Step.BlockType is { } text && editor.Catalog.TryGet(text, out var block))
        {
            Block = block;
            foreach (var parameter in block.Parameters)
            {
                var hidden = text is BlockCatalog.LibraryBlock or BlockCatalog.GroupBlock && parameter.Name == "Baustein";
                Parameters.Add(new ParameterRowViewModel(this, parameter) { IsHidden = hidden });
            }
        }
        if (Step.BlockType == BlockCatalog.Code) CodeTools = new CodeToolsViewModel(editor, node);
        if (Step.BlockType is BlockCatalog.LibraryBlock or BlockCatalog.GroupBlock) Bindings = new LibraryBindingsViewModel(editor, this);
        _diagnostics = editor.DiagnosticsFor(Step);
        Storage = new StorageSettingsViewModel(editor, node.Definition);
        Options = new(editor, node);
    }

    /// <summary>Filter Anzeige/Datei/Datenbank (Override).</summary>
    public StorageSettingsViewModel Storage { get; }
    public override NodeOptionsViewModel Options { get; }

    /// <summary>Werkzeuge für CodeBlocks im Ablauf (Build prüfen, Variablen, Umwandeln).</summary>
    public CodeToolsViewModel? CodeTools { get; }

    /// <summary>Bausteinauswahl und Ein-/Ausgangsbindungen eines Bibliotheksbausteins.</summary>
    public LibraryBindingsViewModel? Bindings { get; }

    public StepDefinition Step => _node.Step!;
    public BlockDescriptor? Block { get; }
    public override string Title => Block?.DisplayName ?? "Schritt (Code)";
    public string BlockDescription => Block?.Description ?? "Im Code definierter Schritt – nicht im Editor konfigurierbar.";

    public ObservableCollection<ParameterRowViewModel> Parameters { get; } = [];

    public bool HasAdvancedParameters => Block?.Parameters.Any(parameter => parameter.Advanced) == true;

    private bool _showAdvanced;
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (!SetProperty(ref _showAdvanced, value)) return;
            foreach (var parameter in Parameters) parameter.RefreshVisibility();
        }
    }

    internal bool IsMetadataVisible(ParameterDescriptor parameter)
    {
        if (!parameter.Advanced && parameter.VisibleWhen is null) return true;
        // A diagnostic can originate in an inactive transport field. Keep those fields editable until it is fixed.
        if (HasDiagnostics) return true;
        if (parameter.Advanced && !ShowAdvanced) return false;
        if (parameter.VisibleWhen is not { } condition) return true;
        var controllingField = Block?.Parameters.FirstOrDefault(field => field.Name == condition.Parameter);
        return Step.Parameters.GetValueOrDefault(condition.Parameter, controllingField?.Default ?? "") == condition.Value;
    }

    public string Name
    {
        get => _node.Name;
        set { _node.Name = value; _editor.MarkChanged(); OnPropertyChanged(); }
    }

    public string Description
    {
        get => Step.Description ?? "";
        set { Step.Description = value; _node.Refresh(); _editor.MarkChanged(); OnPropertyChanged(); }
    }

    public IReadOnlyList<string> Diagnostics
    {
        get => _diagnostics;
        private set
        {
            _diagnostics = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDiagnostics));
        }
    }

    public bool HasDiagnostics => _diagnostics.Count > 0;

    internal bool IsParameterActive(string name)
    {
        if (Step.BlockType is TraceBlocks.Start or TraceBlocks.Record)
            return TraceTableVisibility(name) ?? TraceMarginVisibility(name) ?? IsTraceParameterActive(name);
        if (Step.BlockType == BlockCatalog.CurveOutput)
            return IsCurveOutputParameterActive(name);
        if (Step.BlockType is ProcessBlocks.Communication or ProcessBlocks.StatusEnd)
            return IsProcessParameterActive(Step.BlockType, name);
        if (Step.BlockType != BlockCatalog.Evaluate) return true;
        var fixedResult = Step.Parameters.GetValueOrDefault("Art", "Bedingung prüfen") == BlockCatalog.FixedRatingMode;
        return name switch { "Bedingung" => !fixedResult, "Bewertung" => fixedResult, _ => true };
    }

    private bool IsProcessParameterActive(string type, string name)
    {
        if (name == ProcessBlocks.ActionParameter) return true;
        var action = Step.Parameters.GetValueOrDefault(ProcessBlocks.ActionParameter)
            ?? Block!.Parameters[0].Default;
        var legacyType = ProcessBlocks.ActionBlock(type, action);
        return legacyType is not null && _editor.Catalog.Get(legacyType).Parameters.Any(field => field.Name == name);
    }

    private bool IsCurveOutputParameterActive(string name) => name is not ("Kurve" or "Version")
        || Step.Parameters.GetValueOrDefault(CurveOutputBlocks.ActionParameter, CurveOutputBlocks.Load) == CurveOutputBlocks.Load;

    private bool? TraceTableVisibility(string name) => name is "Einheiten" or "Grenzkurven" or TraceBlocks.AbortCurvesParameter
        ? false
        : null;

    private bool IsTraceParameterActive(string name)
    {

        var mode = Step.Parameters.GetValueOrDefault(TraceBlocks.EvaluationParameter, TraceBlocks.ValueBand);
        if (name is TraceBlocks.LowerFormulaParameter or TraceBlocks.UpperFormulaParameter)
            return mode == TraceBlocks.FormulaBand;
        return name switch
        {
            "Grenzkurven" or TraceBlocks.AbortCurvesParameter => mode == TraceBlocks.ValueBand,
            "Steigungsfenster" => mode is TraceBlocks.DisjointSlope or TraceBlocks.RollingSlope,
            "SteigungMin" or "SteigungMax" => mode is not (TraceBlocks.ValueBand or TraceBlocks.FormulaBand),
            _ => true
        };
    }

    private bool? TraceMarginVisibility(string name)
    {
        if (name is not (TraceBlocks.StopMarginModeParameter or TraceBlocks.PercentMarginParameter or TraceBlocks.AbsoluteMarginParameter))
            return null;
        var enabled = Step.Parameters.GetValueOrDefault(TraceBlocks.StopEnabledParameter, "");
        if (!(enabled.Equals("true", StringComparison.OrdinalIgnoreCase) || enabled.Equals("ja", StringComparison.OrdinalIgnoreCase) || enabled == "1"))
            return false;
        var mode = Step.Parameters.GetValueOrDefault(TraceBlocks.StopMarginModeParameter, TraceBlocks.PercentMargin);
        return name switch
        {
            TraceBlocks.PercentMarginParameter => mode == TraceBlocks.PercentMargin,
            TraceBlocks.AbsoluteMarginParameter => mode == TraceBlocks.AbsoluteMargin,
            _ => true
        };
    }

    public void ApplyTableValues(IReadOnlyDictionary<string, string> values)
    {
        var changed = Parameters.Where(row => values.TryGetValue(row.Descriptor.Name, out var value) && row.Value != value).ToArray();
        if (changed.Length == 0) return;
        foreach (var row in changed) Step.Parameters[row.Descriptor.Name] = values[row.Descriptor.Name];
        ParametersChanged();
        foreach (var row in changed) row.RefreshValue();
    }

    internal void ParametersChanged()
    {
        Diagnostics = _editor.DiagnosticsFor(Step);
        foreach (var parameter in Parameters) parameter.RefreshVisibility();
        _node.Diagnostic = Diagnostics.FirstOrDefault();
        _node.Refresh();
        _editor.MarkChanged();
    }
}

internal static class Format
{
    public static string Number(double? value, string format = "0.###")
        => value?.ToString(format, CultureInfo.GetCultureInfo("de-DE")) ?? "";
}
