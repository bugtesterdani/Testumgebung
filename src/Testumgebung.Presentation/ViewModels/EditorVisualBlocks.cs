/**
 * @file
 * @brief Zusätzliche Blockdarstellung und Steuerungsvorlagen auf dem vorhandenen Projektmodell.
 */
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private bool _visualBlocks;
    private Testumgebung.Presentation.Mvvm.RelayCommand? _selectBlockCommand;
    public Testumgebung.Presentation.Mvvm.RelayCommand SelectBlockCommand => _selectBlockCommand ??= new(p =>
    { if (p is NodeViewModel node && ReferenceEquals(FindNode(node.Id), node)) SelectedNode = node; });
    public bool VisualBlocks { get => _visualBlocks; set { if (SetProperty(ref _visualBlocks, value)) OnPropertyChanged(nameof(Palette)); } }

    public static IReadOnlyList<PaletteItem> ControlBlocks { get; } =
    [
        new("@when", "Wenn …", "Gruppe nur bei erfüllter Bedingung ausführen.", NodeIcon.Group, "when"),
        new("@ifelse", "Wenn … dann … sonst", "Bedingung einmal auswerten und genau einen Zweig ausführen.", NodeIcon.Group, "ifelse"),
        new("@repeat", "Wiederhole … mal", "Eine Gruppe eine feste Anzahl wiederholen.", NodeIcon.Group, "repeat"),
        new("@while", "Wiederhole solange …", "Bedingung vor jeder Iteration prüfen.", NodeIcon.Group, "while"),
        new("@until", "Wiederhole bis …", "Bedingung nach jeder Iteration prüfen.", NodeIcon.Group, "until"),
        new("@parallel", "Parallel ausführen", "Alle direkten Kinder gleichzeitig starten und gemeinsam abwarten.", NodeIcon.Group, "parallel")
    ];

    /// <summary>Palette direkt vor, nach oder in eine Gruppe einfügen; ein atomarer Undo-Schritt.</summary>
    public NodeViewModel InsertAt(PaletteItem item, NodeViewModel parent, int index)
    {
        if (!CanInsertAt(parent, index)) throw new InvalidOperationException("Keine gültige Einfügeposition im aktuellen Projekt.");
        NodeDefinition node = item.Preset is null
            ? item.BlockType is null ? new GroupDefinition(NewId("gruppe"), "Neue Gruppe") : Catalog.Create(item.BlockType, NewId("schritt"), item.DisplayName)
            : CreateControl(item.Preset);
        parent.Group!.Children.Insert(index, node); parent.IsExpanded = true;
        MarkStructureChanged(); RebuildTree(node.Id); return SelectedNode!;
    }

    public bool CanInsertAt(NodeViewModel parent, int index) => ReferenceEquals(FindNode(parent.Id), parent) && parent.Group is { } group && index >= 0 && index <= group.Children.Count;

    private GroupDefinition CreateControl(string preset)
    {
        if (preset == "ifelse") return CreateIfElse();
        var group = new GroupDefinition(NewId("steuerung"), "Steuerung");
        switch (preset)
        {
            case "when": group.Name = "Wenn"; group.EntryCondition = "true"; break;
            case "repeat": group.Name = "Wiederholen"; group.Repetition = Repetition.Times(3); break;
            case "while": group.Name = "Solange"; group.Repetition = new(RepeatMode.While, Expression: "false"); break;
            case "until": group.Name = "Bis"; group.Repetition = new(RepeatMode.Until, Expression: "true"); break;
            case "parallel": group.Name = "Parallel"; group.Parallel = true; break;
            default: throw new ArgumentException("Unbekannte Steuerungsvorlage.", nameof(preset));
        }
        return group;
    }

    private GroupDefinition CreateIfElse()
    {
        var group = new GroupDefinition(NewId("wenn"), "Wenn / Sonst");
        var decision = "_BlockZweig_" + group.Id;
        group.Variables.Add(new(decision, VarType.Bool, false));
        var capture = Catalog.Create(BlockCatalog.SetValue, NewId("entscheidung"), "Bedingung einmal prüfen",
            new Dictionary<string, string> { ["Ziel"] = decision, ["Ausdruck"] = "true" });
        capture.Description = "Bedingung hier bearbeiten. Die lokale Entscheidungsvariable hält den gewählten Zweig während Dann/Sonst fest.";
        group.Children.Add(capture);
        group.Children.Add(new GroupDefinition(NewId("dann"), "Dann") { EntryCondition = decision });
        group.Children.Add(new GroupDefinition(NewId("sonst"), "Sonst") { EntryCondition = "!" + decision });
        return group;
    }

    /// <summary>Eine Dialogübernahme von davor oder danach liegenden Tippfolgen im Undo-Verlauf trennen.</summary>
    public void ApplyVisualEdit(Action write)
    {
        BreakMerge();
        try { write(); }
        finally { BreakMerge(); }
    }

    /// <summary>Neue Variable ohne Ausdruckssyntax deklarieren; bestehende Variablen bleiben unverändert.</summary>
    public string? DeclareVisualVariable(string groupId, string name, VarType type, string initial)
    {
        if (Project.Definition.Find(groupId) is not GroupDefinition group) return "Gruppe nicht gefunden.";
        name = name.Trim();
        if (!ExpressionVariableChoices.IsPath(name)) return "Einen Variablennamen eingeben, z. B. Spannung oder Zaehler.";
        if (group.Variables.Any(v => v.Name == name)) return "Dieser Name ist in der Gruppe bereits vergeben.";
        if (name.StartsWith("_BlockZweig_", StringComparison.Ordinal)) return "Diesen Namen verwendet die Wenn/Sonst-Vorlage intern.";
        var row = new VariableRowViewModel(new(name, type, null), () => { }) { Initial = initial };
        var definition = row.ToDefinition();
        if (definition is null) return row.Error;
        group.Variables.Add(definition); MarkStructureChanged(); RebuildTree(SelectedNode?.Id); return null;
    }
}
