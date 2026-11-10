/**
 * @file
 * @brief Verwaltet hierarchische Variablensichtbarkeit und Typprüfung über Eintritt und Austritt von Gruppen.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Results;

namespace Testumgebung.Domain.Scopes;

/// <summary>Variable konnte nicht aufgelöst werden. Keine stillschweigende Neuanlage (Abschnitt 2.2, Vorschlag).</summary>
public sealed class VariableNotFoundException(string message) : Exception(message);

/// <summary>Eine lebende Variable in einem Scope.</summary>
public sealed class Variable
{
    internal Variable(string name, VarType type, object value, Scope owner, Func<object>? computedValue = null)
    {
        Name = name;
        Type = type;
        _value = value;
        Owner = owner;
        _computedValue = computedValue;
    }

    private object _value;
    private readonly Func<object>? _computedValue;

    /// <summary>Berechnete Systemwerte dürfen weder von Schritten noch vom Debugger überschrieben werden.</summary>
    public bool IsReadOnly => _computedValue is not null;

    public string Name { get; }
    public VarType Type { get; }

    /// <summary>Scope (Gruppe), in dem die Variable definiert ist – „Herkunftsordner“.</summary>
    public Scope Owner { get; }

    public object Value
    {
        get
        {
            if (_computedValue is { } read) return read();
            lock (Owner.SyncRoot) return _value;
        }
    }

    /// <summary>Typgeprüftes Schreiben mit Int-zu-Double-Erweiterung; Systemvariablen sind schreibgeschützt.</summary>
    public void Set(object? value)
    {
        if (IsReadOnly) throw new InvalidOperationException($"Systemvariable '{Name}' ist schreibgeschützt.");
        var coerced = TypedValue.Coerce(value, Type);
        lock (Owner.SyncRoot) _value = coerced;
    }
}

/// <summary>
/// Laufzeit-Scope einer betretenen Gruppe (Abschnitt 2.2).
/// <list type="bullet">
/// <item>Initialisierung beim Eintritt, bleibt über Wiederholungen bestehen, wird beim Verlassen verworfen.</item>
/// <item><c>Name</c> sucht ab aktueller Gruppe aufwärts; <c>../Name</c> beginnt eine Ebene höher.</item>
/// <item>Eine eigene gleichnamige Definition verdeckt die äußere.</item>
/// <item>An einer Schnittstellengrenze (wiederverwendbarer Baustein) endet die Suche.</item>
/// </list>
/// </summary>
public sealed class Scope : IValueResolver
{
    /// <summary>Live-Teilbewertung dieser Gruppeninstanz: NRESULT, PASS, FAIL oder ERROR.</summary>
    public const string GroupResultVariable = "GruppenErgebnis";

    /// <summary>Gemeinsame Quelle für Engine-Aggregation und Systemvariable; parallele Zugriffe sind atomar.</summary>
    public ResultAccumulator GroupResult { get; } = new();

    private static long _nextId;
    private readonly Dictionary<string, Variable> _variables = new(StringComparer.Ordinal);

    internal object SyncRoot { get; } = new();

    public Scope(string groupName, Scope? parent, bool isInterfaceBoundary = false, string? groupId = null)
    {
        Id = $"S{Interlocked.Increment(ref _nextId)}";
        GroupName = groupName;
        GroupId = groupId;
        Parent = parent;
        IsInterfaceBoundary = isInterfaceBoundary;
    }

    public string Id { get; }
    public string GroupName { get; }
    public string? GroupId { get; }
    public Scope? Parent { get; }

    /// <summary>Wiederverwendbarer Baustein: Namenssuche überschreitet diese Grenze nicht nach oben.</summary>
    public bool IsInterfaceBoundary { get; }

    /// <summary>Wird gesetzt, sobald die Gruppe verlassen wurde. Ein verworfener Scope ist kein aktiver Scope mehr.</summary>
    public bool IsDiscarded { get; private set; }

    public IReadOnlyCollection<Variable> Variables
    {
        get { lock (SyncRoot) return _variables.Values.ToList(); }
    }

    public static Scope CreateFor(GroupDefinition group, Scope? parent)
    {
        var scope = new Scope(group.Name, parent, group.IsInterfaceBoundary, group.Id);
        foreach (var variable in group.Variables) scope.Define(variable);
        scope.DefineComputed(GroupResultVariable, VarType.String, () => scope.GroupResult.Rating.ToDisplay());
        return scope;
    }

    public Variable Define(VariableDefinition definition)
        => Define(definition.Name, definition.Type, definition.Initial);

    public Variable Define(string name, VarType type, object? initial = null)
    {
        if (name == GroupResultVariable) throw new InvalidOperationException($"Systemvariable '{name}' darf nicht selbst definiert werden.");
        if (name.Contains('/')) throw new ArgumentException($"Ungültiger Variablenname '{name}'.", nameof(name));
        var variable = new Variable(name, type, initial is null ? TypedValue.Default(type) : TypedValue.Coerce(initial, type), this);
        lock (SyncRoot)
        {
            if (_variables.ContainsKey(name))
                throw new InvalidOperationException($"Variable '{name}' ist in Gruppe '{GroupName}' doppelt definiert.");
            _variables[name] = variable;
        }
        return variable;
    }

    /// <summary>Systemwerte werden ausschließlich intern registriert und bei jedem Lesen neu bestimmt.</summary>
    private void DefineComputed(string name, VarType type, Func<object> read)
    {
        var variable = new Variable(name, type, TypedValue.Default(type), this, read);
        lock (SyncRoot) _variables.Add(name, variable);
    }

    public bool TryGetLocal(string name, out Variable variable)
    {
        lock (SyncRoot) return _variables.TryGetValue(name, out variable!);
    }

    /// <summary>Löst einen Variablennamen (optional mit <c>../</c>-Präfixen) auf.</summary>
    public Variable Lookup(string path)
    {
        if (TryLookup(path, out var variable, out var error)) return variable;
        throw new VariableNotFoundException(error!);
    }

    public bool TryLookup(string path, out Variable variable, out string? error)
    {
        variable = null!;
        error = null;
        var levels = 0;
        var name = path;
        while (name.StartsWith("../", StringComparison.Ordinal))
        {
            levels++;
            name = name[3..];
        }

        Scope? start = this;
        for (var index = 0; index < levels; index++)
        {
            if (start!.IsInterfaceBoundary || start.Parent is null)
            {
                error = $"'{path}': {levels} Ebene(n) nach oben überschreitet {(start.IsInterfaceBoundary ? "die Bausteingrenze" : "die oberste Gruppe")} ab '{GroupName}'.";
                return false;
            }
            start = start.Parent;
        }

        for (var start2 = start; start2 is not null; start2 = start2.Parent)
        {
            if (start2.TryGetLocal(name, out variable)) return true;
            if (start2.IsInterfaceBoundary) break;
        }

        error = $"Variable '{path}' ist ab Gruppe '{GroupName}' nicht definiert.";
        return false;
    }

    public object Get(string path) => Lookup(path).Value;

    public void Set(string path, object? value) => Lookup(path).Set(value);

    object IValueResolver.Resolve(string name) => Get(name);

    /// <summary>Alle aus diesem Scope sichtbaren Variablen (verdeckte äußere Definitionen ausgeschlossen).</summary>
    public IReadOnlyList<Variable> VisibleVariables()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Variable>();
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            foreach (var variable in scope.Variables.OrderBy(variable2 => variable2.Name, StringComparer.Ordinal))
                if (seen.Add(variable.Name)) result.Add(variable);
            if (scope.IsInterfaceBoundary) break;
        }
        return result;
    }

    /// <summary>Lesbarer Debug-Schnappschuss (kein aktiver Scope).</summary>
    public ScopeSnapshot Snapshot()
        => new(Id, GroupId, GroupName, Variables.Select(variable => new VariableSnapshot(variable.Name, variable.Type, variable.Value, GroupName)).ToList());

    /// <summary>Beim Verlassen der Gruppe: Variablen verworfen; erneuter Eintritt erzeugt einen neuen Scope.</summary>
    public void Discard() => IsDiscarded = true;

    public override string ToString() => $"{GroupName} [{Id}]";
}

public sealed record VariableSnapshot(string Name, VarType Type, object Value, string OriginGroup);

public sealed record ScopeSnapshot(string ScopeId, string? GroupId, string GroupName, IReadOnlyList<VariableSnapshot> Variables);
