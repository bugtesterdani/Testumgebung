using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Domain.Scopes;

/// <summary>Variable konnte nicht aufgelöst werden. Keine stillschweigende Neuanlage (Abschnitt 2.2, Vorschlag).</summary>
public sealed class VariableNotFoundException(string message) : Exception(message);

/// <summary>Eine lebende Variable in einem Scope.</summary>
public sealed class Variable
{
    internal Variable(string name, VarType type, object value, Scope owner)
    {
        Name = name;
        Type = type;
        _value = value;
        Owner = owner;
    }

    private object _value;

    public string Name { get; }
    public VarType Type { get; }

    /// <summary>Scope (Gruppe), in dem die Variable definiert ist – „Herkunftsordner“.</summary>
    public Scope Owner { get; }

    public object Value
    {
        get { lock (Owner.SyncRoot) return _value; }
    }

    /// <summary>Typgeprüftes Schreiben. Int → Double ist erlaubt, alles andere wirft <see cref="TypeMismatchException"/>.</summary>
    public void Set(object? value)
    {
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
        foreach (var v in group.Variables) scope.Define(v);
        return scope;
    }

    public Variable Define(VariableDefinition definition)
        => Define(definition.Name, definition.Type, definition.Initial);

    public Variable Define(string name, VarType type, object? initial = null)
    {
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

    public bool TryGetLocal(string name, out Variable variable)
    {
        lock (SyncRoot) return _variables.TryGetValue(name, out variable!);
    }

    /// <summary>Löst einen Variablennamen (optional mit <c>../</c>-Präfixen) auf.</summary>
    public Variable Lookup(string path)
    {
        if (TryLookup(path, out var v, out var error)) return v;
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
        for (var i = 0; i < levels; i++)
        {
            if (start!.IsInterfaceBoundary || start.Parent is null)
            {
                error = $"'{path}': {levels} Ebene(n) nach oben überschreitet {(start.IsInterfaceBoundary ? "die Bausteingrenze" : "die oberste Gruppe")} ab '{GroupName}'.";
                return false;
            }
            start = start.Parent;
        }

        for (var s = start; s is not null; s = s.Parent)
        {
            if (s.TryGetLocal(name, out variable)) return true;
            if (s.IsInterfaceBoundary) break;
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
        for (var s = this; s is not null; s = s.Parent)
        {
            foreach (var v in s.Variables.OrderBy(v => v.Name, StringComparer.Ordinal))
                if (seen.Add(v.Name)) result.Add(v);
            if (s.IsInterfaceBoundary) break;
        }
        return result;
    }

    /// <summary>Lesbarer Debug-Schnappschuss (kein aktiver Scope).</summary>
    public ScopeSnapshot Snapshot()
        => new(Id, GroupId, GroupName, Variables.Select(v => new VariableSnapshot(v.Name, v.Type, v.Value, GroupName)).ToList());

    /// <summary>Beim Verlassen der Gruppe: Variablen verworfen; erneuter Eintritt erzeugt einen neuen Scope.</summary>
    public void Discard() => IsDiscarded = true;

    public override string ToString() => $"{GroupName} [{Id}]";
}

public sealed record VariableSnapshot(string Name, VarType Type, object Value, string OriginGroup);

public sealed record ScopeSnapshot(string ScopeId, string? GroupId, string GroupName, IReadOnlyList<VariableSnapshot> Variables);
