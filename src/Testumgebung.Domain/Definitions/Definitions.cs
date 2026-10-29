/**
 * @file
 * @brief Beschreibt die hierarchische Prüfstruktur, Variablen und Wiederholungen unabhängig von ihrer Ausführung.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Domain.Definitions;

/// <summary>Element der hierarchischen Ablaufliste. Die Id bleibt beim Verschieben erhalten.</summary>
public abstract class NodeDefinition
{
    protected NodeDefinition(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id erforderlich.", nameof(id));
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Filter für Anzeige, Ergebnisdatei und Datenbank (null-Werte = erben).</summary>
    public global::Testumgebung.Domain.Storage.StorageSettings Storage { get; set; } = global::Testumgebung.Domain.Storage.StorageSettings.Inherit;

    public override string ToString() => $"{GetType().Name}({Id}: {Name})";
}

/// <summary>Variablendefinition einer Gruppe; Startwert wird bei jedem Gruppeneintritt gesetzt.</summary>
public sealed record VariableDefinition(string Name, VarType Type, object? Initial = null);

public enum RepeatMode
{
    /// <summary>Genau eine Iteration.</summary>
    Once,
    /// <summary>Feste Anzahl Iterationen.</summary>
    Count,
    /// <summary>Wiederholen, solange die Frist (ab Eintritt) nicht abgelaufen ist; Prüfung zwischen Iterationen.</summary>
    Duration,
    /// <summary>Vor jeder Iteration Ausdruck prüfen; wiederholen solange wahr.</summary>
    While,
    /// <summary>Nach jeder Iteration Ausdruck prüfen; wiederholen bis wahr.</summary>
    Until,
    /// <summary>Dauerhaft bis zum Werkerabbruch (Test Loop).</summary>
    Endless,
}

/// <summary>Wiederholungseinstellung einer Gruppe.</summary>
/// <param name="StopWithinIteration">
/// Nur Zeitspanne: Frist auch zwischen den Schritten einer Iteration prüfen – nach Ablauf werden die restlichen Schritte der
/// laufenden Iteration übersprungen (ein laufender Schritt wird nicht unterbrochen; Exit-Gruppe läuft). Standard: Frist nur
/// zwischen Iterationen (Abschnitt 15).
/// </param>
public sealed record Repetition(RepeatMode Mode, int Count = 1, TimeSpan Duration = default, string? Expression = null, bool StopWithinIteration = false)
{
    public static Repetition Once { get; } = new(RepeatMode.Once);
    public static Repetition Endless { get; } = new(RepeatMode.Endless);
    public static Repetition Times(int count) => new(RepeatMode.Count, Count: count);
    public static Repetition For(TimeSpan duration) => new(RepeatMode.Duration, Duration: duration);
    public static Repetition While(string expression) => new(RepeatMode.While, Expression: expression);
    public static Repetition Until(string expression) => new(RepeatMode.Until, Expression: expression);
}

/// <summary>Verhalten der Gruppenbewertung vor einer Wiederholung (setzt keine Variablen zurück).</summary>
public enum RepeatResultPolicy
{
    Keep,
    ResetToNResult,
}

/// <summary>Bewerteter oder unbewerteter Einzelschritt.</summary>
public sealed class StepDefinition(string id, string name, IStepAction action) : NodeDefinition(id, name)
{
    public IStepAction Action { get; set; } = action;

    /// <summary>Bausteintyp aus dem Katalog. null = nur im Code definiert (nicht als Projektdatei speicherbar).</summary>
    public string? BlockType { get; set; }

    /// <summary>Parameter des Bausteins als Text (typisierte Auswertung durch den Baustein).</summary>
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Gruppe mit Variablen, Eintrittsbedingung, Wiederholung, optionaler Exit-Gruppe und Ressourcenlocks.
/// Bedingungen existieren ausschließlich an Gruppen.
/// </summary>
public sealed class GroupDefinition(string id, string name) : NodeDefinition(id, name)
{
    private string? _entryCondition;
    private Expression? _parsedCondition;

    public List<NodeDefinition> Children { get; } = [];
    public List<VariableDefinition> Variables { get; } = [];

    /// <summary>Eintrittsbedingung als typisierter Ausdruck; null = immer.</summary>
    public string? EntryCondition
    {
        get => _entryCondition;
        set
        {
            _parsedCondition = value is null ? null : Expression.Parse(value);
            _entryCondition = value;
        }
    }

    public Expression? ParsedEntryCondition => _parsedCondition;

    public Repetition Repetition { get; set; } = Repetition.Once;
    public RepeatResultPolicy ResultBeforeRepeat { get; set; } = RepeatResultPolicy.Keep;

    /// <summary>Direkte Kinder gemeinsam starten und vor Wiederholung/Exit auf alle warten. Standard: nacheinander.</summary>
    public bool Parallel { get; set; }

    /// <summary>Exit-Gruppe: läuft nach allen regulären Wiederholungen bzw. beim Werkerabbruch.</summary>
    public GroupDefinition? ExitGroup { get; set; }

    /// <summary>Beim Eintritt atomar zu reservierende Ressourcen; Freigabe beim Verlassen (nach Exit).</summary>
    public List<string> Resources { get; } = [];

    /// <summary>Maximale Wartezeit auf Ressourcen; null = ohne Limit.</summary>
    public TimeSpan? ResourceTimeout { get; set; }

    /// <summary>Wiederverwendbarer Baustein: Namenssuche endet an dieser Grenze.</summary>
    public bool IsInterfaceBoundary { get; set; }

    /// <summary>Ein Durchlauf dieser Gruppe ist ein eigener Prüflingstest (Run) mit verpflichtender Kennung.</summary>
    public bool IsDutRun { get; set; }

    /// <summary>Variable mit der Prüflingskennung (bei <see cref="IsDutRun"/>).</summary>
    public string DutIdVariable { get; set; } = "Kennung";

    public GroupDefinition Add(params NodeDefinition[] nodes)
    {
        Children.AddRange(nodes);
        return this;
    }

    public GroupDefinition Var(string name, VarType type, object? initial = null)
    {
        Variables.Add(new VariableDefinition(name, type, initial));
        return this;
    }

    /// <summary>Alle Knoten unterhalb (inklusive Exit-Gruppen), Tiefensuche.</summary>
    public IEnumerable<NodeDefinition> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            if (child is GroupDefinition g)
                foreach (var d in g.Descendants()) yield return d;
        }
        if (ExitGroup is not null)
        {
            yield return ExitGroup;
            foreach (var d in ExitGroup.Descendants()) yield return d;
        }
    }
}

/// <summary>
/// Prüfplatzablauf: Test Start/Init, Test Loop/Testing (dauerhaft bis Werkerabbruch),
/// Test End/Finalization als Exit-Gruppe des Hauptablaufs.
/// </summary>
public sealed class TestDefinition
{
    public TestDefinition(string name, string version, GroupDefinition init, GroupDefinition loop, GroupDefinition finalization)
    {
        Name = name;
        Version = version;
        Init = init;
        Loop = loop;
        Finalization = finalization;
        Root = new GroupDefinition("main", "Hauptablauf");
        Root.Children.Add(init);
        Root.Children.Add(loop);
        Root.ExitGroup = finalization;
    }

    public string Name { get; }

    /// <summary>Ablaufversion; wird beim Anlegen eines Release-Entwurfs gesetzt.</summary>
    public string Version { get; set; }
    public GroupDefinition Init { get; }
    public GroupDefinition Loop { get; }
    public GroupDefinition Finalization { get; }

    /// <summary>Bibliothek wiederverwendbarer Codebausteine dieses Projekts.</summary>
    public List<Code.CodeBlockDefinition> Library { get; } = [];
    public List<Code.ManagedAssembly> Assemblies { get; } = [];

    /// <summary>Wiederverwendbare grafische Bausteine (aus Gruppen erstellt).</summary>
    public List<GroupBlockDefinition> GroupLibrary { get; } = [];

    /// <summary>Layout und Darstellung der Werkeransicht (Abschnitt 4).</summary>
    public OperatorLayout Layout { get; set; } = new();

    /// <summary>Ausgabekurven aller Versionen (Abschnitt 8); Teil des Release-Pakets.</summary>
    public Curves.CurveLibrary Curves { get; } = new();

    /// <summary>Busbeschreibungen (DBC/LDF) für Signal-, J1939- und LIN-Bausteine; Teil des Release-Pakets.</summary>
    public List<BusDatabaseDefinition> BusDatabases { get; } = [];

    public BusDatabaseDefinition? FindBusDatabase(string name)
        => BusDatabases.FirstOrDefault(d => d.Name == name) ?? BusDatabases.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Hauptablauf: Kinder = Init, Loop; Exit-Gruppe = Finalization.</summary>
    public GroupDefinition Root { get; }

    /// <summary>Hauptablauf und alle Körper grafischer Bausteine.</summary>
    public IEnumerable<GroupDefinition> AllRoots => GroupLibrary.Select(b => b.Body).Prepend(Root);

    /// <summary>Alle Knoten in Hauptablauf und Bausteinkörpern.</summary>
    public IEnumerable<NodeDefinition> AllNodes() => AllRoots.SelectMany(r => r.Descendants().Prepend(r));

    public NodeDefinition? Find(string id) => AllNodes().FirstOrDefault(n => n.Id == id);

    /// <summary>Grafischer Baustein, zu dessen Körper der Knoten gehört (null = Hauptablauf).</summary>
    public GroupBlockDefinition? OwningGroupBlock(string id)
        => GroupLibrary.FirstOrDefault(b => b.Body.Id == id || b.Body.Descendants().Any(n => n.Id == id));

    /// <summary>Elternkette (vom Wurzelknoten bis zum direkten Elternteil) eines Knotens.</summary>
    public IReadOnlyList<GroupDefinition> AncestorsOf(string id)
    {
        foreach (var root in AllRoots)
        {
            if (root.Id == id) return [];
            var path = new List<GroupDefinition>();
            if (Search(root, id, path)) return path;
        }
        throw new KeyNotFoundException($"Knoten '{id}' nicht gefunden.");

        static bool Search(GroupDefinition g, string id, List<GroupDefinition> path)
        {
            path.Add(g);
            foreach (var child in g.Children.Concat(g.ExitGroup is null ? [] : [g.ExitGroup]))
            {
                if (child.Id == id) return true;
                if (child is GroupDefinition cg && Search(cg, id, path)) return true;
            }
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    private void ValidateParallel(GroupDefinition group, List<string> diagnostics)
    {
        if (!group.Parallel) return;
        if (group.Repetition is { Mode: RepeatMode.Duration, StopWithinIteration: true })
            diagnostics.Add($"Gruppe '{group.Name}': Bei paralleler Ausführung gilt die Zeitspanne zwischen vollständigen Iterationen, nicht zwischen einzelnen Schritten.");
        if (ReferenceEquals(group, Root)) diagnostics.Add("Der Hauptablauf muss Init, Loop und Finalization nacheinander ausführen.");
        if (group.Children.OfType<GroupDefinition>().Any(g => g.IsDutRun || g.Descendants().OfType<GroupDefinition>().Any(c => c.IsDutRun)))
            diagnostics.Add($"Gruppe '{group.Name}': Prüflingsdurchläufe dürfen nicht parallel betreten werden. Parallelität innerhalb eines Prüflings oder getrennte Prüfplätze verwenden.");
    }

    /// <summary>Statische Prüfungen: eindeutige Ids, gültige Wiederholungen.</summary>
    public IReadOnlyList<string> Validate()
    {
        var diagnostics = new List<string>();
        var all = AllNodes().ToList();
        foreach (var dup in GroupLibrary.GroupBy(b => b.Id).Where(g => g.Count() > 1))
            diagnostics.Add($"Baustein-Id '{dup.Key}' ist mehrfach vergeben.");
        foreach (var dup in all.GroupBy(n => n.Id).Where(g => g.Count() > 1))
            diagnostics.Add($"Knoten-Id '{dup.Key}' ist mehrfach vergeben.");
        foreach (var g in all.OfType<GroupDefinition>())
        {
            ValidateParallel(g, diagnostics);
            var r = g.Repetition;
            if (r.Mode == RepeatMode.Count && r.Count < 1) diagnostics.Add($"Gruppe '{g.Name}': Anzahl muss ≥ 1 sein.");
            if (r.Mode == RepeatMode.Duration && r.Duration <= TimeSpan.Zero) diagnostics.Add($"Gruppe '{g.Name}': Zeitspanne muss > 0 sein.");
            if (r.Mode is RepeatMode.While or RepeatMode.Until)
            {
                if (string.IsNullOrWhiteSpace(r.Expression)) diagnostics.Add($"Gruppe '{g.Name}': Wiederholungsausdruck fehlt.");
                else
                {
                    try { Expression.Parse(r.Expression); }
                    catch (ExpressionException ex) { diagnostics.Add($"Gruppe '{g.Name}': {ex.Message}"); }
                }
            }
            foreach (var dupVar in g.Variables.GroupBy(v => v.Name).Where(x => x.Count() > 1))
                diagnostics.Add($"Gruppe '{g.Name}': Variable '{dupVar.Key}' doppelt definiert.");
        }
        return diagnostics;
    }
}

/// <summary>
/// Wiederverwendbarer grafischer Baustein (Abschnitt 3): typisierte Ein-/Ausgänge, interne Variablen im Körper,
/// Schnittstellengrenze – die Namenssuche im Körper endet an den Ports.
/// </summary>
public sealed class GroupBlockDefinition
{
    public GroupBlockDefinition(string id, string name, GroupDefinition body)
    {
        Id = id;
        Name = name;
        Body = body; // Die Schnittstellengrenze bildet der Port-Scope, in dem der Körper läuft.
    }

    public string Id { get; }
    public string Name { get; set; }
    public string Version { get; set; } = "0.1";
    public string? Description { get; set; }
    public List<Code.CodePort> Ports { get; } = [];
    public GroupDefinition Body { get; }

    public override string ToString() => $"{Name} v{Version}";
}
