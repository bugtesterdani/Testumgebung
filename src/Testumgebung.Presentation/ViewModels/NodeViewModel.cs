/**
 * @file
 * @brief Übersetzt einen Definitionsknoten in eine grafische Ablaufkarte und deren Bearbeitungszustand.
 */

using System.Collections.ObjectModel;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Symbolart für Ablaufkarten (die View bildet sie auf Glyphen ab).</summary>
public enum NodeIcon
{
    Section,
    Group,
    DutGroup,
    ExitGroup,
    Measure,
    Arrow,
    Doc,
    Check,
    Operator,
    Wait,
    Error,
    Digital,
    Code,
    LibraryBlock,
}

/// <summary>Karte in der hierarchischen, eingerückten Ablaufliste.</summary>
public sealed class NodeViewModel : ObservableObject
{
    private bool _isExpanded = true;
    private bool _isSelected;
    private string _number = "";
    private bool _hasBreakpoint;
    private string? _diagnostic;
    private RunMarker _runMarker;

    public NodeViewModel(NodeDefinition definition, NodeViewModel? parent, bool isExit = false, bool isSection = false)
    {
        Definition = definition;
        Parent = parent;
        IsExit = isExit;
        IsSection = isSection;
    }

    public NodeDefinition Definition { get; }

    /// <summary>Zusatzhinweis (z. B. Schnittstelle eines Bausteinkörpers).</summary>
    public string? Note { get; init; }
    public NodeViewModel? Parent { get; }
    public ObservableCollection<NodeViewModel> Children { get; } = [];

    public string Id => Definition.Id;

    /// <summary>Exit-Gruppe einer Gruppe (fest am Ende, nicht verschiebbar).</summary>
    public bool IsExit { get; }

    /// <summary>Init / Loop / Finalization – nicht lösch- oder verschiebbar.</summary>
    public bool IsSection { get; }

    public bool IsGroup => Definition is GroupDefinition;
    public GroupDefinition? Group => Definition as GroupDefinition;
    public StepDefinition? Step => Definition as StepDefinition;

    public bool CanMove => !IsExit && !IsSection;

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    public string Name
    {
        get => Definition.Name;
        set
        {
            if (Definition.Name == value || string.IsNullOrWhiteSpace(value)) return;
            Definition.Name = value;
            OnPropertyChanged();
        }
    }

    public string Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool HasBreakpoint
    {
        get => _hasBreakpoint;
        set => SetProperty(ref _hasBreakpoint, value);
    }

    /// <summary>Konfigurationsfehler (rot markiert).</summary>
    public string? Diagnostic
    {
        get => _diagnostic;
        set
        {
            if (SetProperty(ref _diagnostic, value)) OnPropertyChanged(nameof(HasDiagnostic));
        }
    }

    public bool HasDiagnostic => _diagnostic is not null;

    /// <summary>Laufmarkierung im Debugger (aktuell, pausiert, ausgeführt).</summary>
    public RunMarker RunMarker
    {
        get => _runMarker;
        set => SetProperty(ref _runMarker, value);
    }

    public NodeIcon Icon => Definition switch
    {
        GroupDefinition when IsSection => NodeIcon.Section,
        GroupDefinition when IsExit => NodeIcon.ExitGroup,
        GroupDefinition g when g.IsDutRun => NodeIcon.DutGroup,
        GroupDefinition => NodeIcon.Group,
        StepDefinition s => s.BlockType is null ? NodeIcon.Code : IconFor(s.BlockType),
        _ => NodeIcon.Doc,
    };

    private static readonly IReadOnlyDictionary<string, NodeIcon> BlockIcons = new Dictionary<string, NodeIcon>(StringComparer.Ordinal)
    {
        [BlockCatalog.Measure] = NodeIcon.Measure,
        [BlockCatalog.Output] = NodeIcon.Arrow,
        [BlockCatalog.SetValue] = NodeIcon.Arrow,
        [BlockCatalog.DigitalRead] = NodeIcon.Digital,
        [BlockCatalog.DigitalWrite] = NodeIcon.Digital,
        [BlockCatalog.Evaluate] = NodeIcon.Check,
        [BlockCatalog.SetRating] = NodeIcon.Check,
        [BlockCatalog.Operator] = NodeIcon.Operator,
        [BlockCatalog.Wait] = NodeIcon.Wait,
        [BlockCatalog.RaiseError] = NodeIcon.Error,
        [BlockCatalog.Code] = NodeIcon.Code,
        [BlockCatalog.LibraryBlock] = NodeIcon.LibraryBlock,
        [BlockCatalog.GroupBlock] = NodeIcon.LibraryBlock,
    };

    internal static NodeIcon IconFor(string? type)
        => type is not null && BlockIcons.TryGetValue(type, out var icon) ? icon : NodeIcon.Doc;

    /// <summary>Kurzinfo neben dem Namen, z. B. „(wiederholt bis Werkerabbruch)“, „×3“, „[Prüfling]“.</summary>
    public string Annotation
    {
        get
        {
            if (Definition is not GroupDefinition g) return "";
            var parts = new List<string>();
            if (Note is not null) parts.Add(Note);
            if (IsExit) parts.Add("Exit");
            if (g.IsDutRun) parts.Add("Prüfling-Run");
            if (g.Parallel) parts.Add("parallel");
            parts.Add(g.Repetition.Mode switch
            {
                RepeatMode.Endless => "wiederholt bis Werkerabbruch",
                RepeatMode.Count => $"×{g.Repetition.Count}",
                RepeatMode.Duration => $"{g.Repetition.Duration.TotalSeconds:0.#} s",
                RepeatMode.While => $"solange {g.Repetition.Expression}",
                RepeatMode.Until => $"bis {g.Repetition.Expression}",
                _ => "",
            });
            if (g.EntryCondition is not null) parts.Add($"wenn {g.EntryCondition}");
            if (g.Resources.Count > 0) parts.Add("🔒 " + string.Join(", ", g.Resources));
            if (g.Variables.Count > 0) parts.Add($"{g.Variables.Count} Var.");
            var text = string.Join(" · ", parts.Where(p => p.Length > 0));
            return text.Length == 0 ? "" : $"({text})";
        }
    }

    public void Refresh() => OnPropertiesChanged(nameof(Name), nameof(Annotation), nameof(Icon));

    public IEnumerable<NodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }

    public override string ToString() => $"{Number} {Name}";
}

public enum RunMarker
{
    None,
    Running,
    Paused,
    Done,
}
