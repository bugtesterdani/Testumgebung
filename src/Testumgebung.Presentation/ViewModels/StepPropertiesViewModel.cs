using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

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
    public bool IsText => !IsChoice && !IsBool && !IsCode && !IsHidden && !IsTable && !IsCabLabel;
    public bool IsLabelTemplate => _owner.Step.BlockType == LabelPrintingBlocks.Print && Descriptor.Name == "ZplVorlage";
    public bool IsCabLabel => _owner.Step.BlockType == LabelPrintingBlocks.Print && Descriptor.Name == LabelPrintingBlocks.CabDataParameter;
    public string CabFileStatus => string.IsNullOrWhiteSpace(Value) ? "Keine Datei eingebettet" : "Druckdatei eingebettet";
    public bool IsMultiline => Descriptor.Name is "Verbindungsoptionen" or "Header" or "Body" or "ZplVorlage";

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
            OnPropertyChanged(nameof(CabFileStatus));
            OnPropertyChanged(nameof(HasVisualBuilder));
            _owner.ParametersChanged();
        }
    }

    internal void RefreshValue()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(CabFileStatus));
    }

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
        if (Diagnostics.Any(message => message.Contains(parameter.Name, StringComparison.OrdinalIgnoreCase)
            || message.Contains(parameter.Label, StringComparison.OrdinalIgnoreCase))) return true;
        if (parameter.Advanced && !ShowAdvanced) return false;
        if (parameter.VisibleWhen is not { } condition) return true;
        return condition.Matches(name => Step.Parameters.GetValueOrDefault(name,
            Block?.Parameters.FirstOrDefault(field => field.Name == name)?.Default ?? ""));
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

    // These legacy storage fields are edited in the channel table, not as duplicate text inputs.
    internal bool IsParameterActive(string name)
        => Step.BlockType is not (TraceBlocks.Start or TraceBlocks.Record)
            || name is not ("Einheiten" or "Grenzkurven" or TraceBlocks.AbortCurvesParameter);
    public void SaveLabelLayout(LabelLayout layout)
    {
        var zpl = layout.ToZpl();
        Step.Parameters[LabelLayout.Parameter] = layout.Save();
        Step.Parameters["ZplVorlage"] = zpl;
        ParametersChanged();
        Parameters.FirstOrDefault(row => row.IsLabelTemplate)?.RefreshValue();
    }

    public void SaveCabLabelLayout(LabelLayout layout)
    {
        var data = CabLabelLayout.Render(layout, out var textOffset);
        Step.Parameters[LabelPrintingBlocks.CabTextOffsetParameter] = textOffset.ToString(CultureInfo.InvariantCulture);
        Step.Parameters[CabLabelLayout.Parameter] = layout.Save();
        SetCabPrintData(data);
    }

    public void ImportCabPrintData(byte[] data)
    {
        Step.Parameters.Remove(CabLabelLayout.Parameter);
        Step.Parameters.Remove(LabelPrintingBlocks.CabTextOffsetParameter);
        SetCabPrintData(data);
    }

    private void SetCabPrintData(byte[] data)
    {
        Step.Parameters[LabelPrintingBlocks.CabDataParameter] = Convert.ToBase64String(data);
        ParametersChanged();
        Parameters.FirstOrDefault(row => row.IsCabLabel)?.RefreshValue();
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
