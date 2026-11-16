using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

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
