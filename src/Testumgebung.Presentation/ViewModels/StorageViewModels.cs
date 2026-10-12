using System.Collections.ObjectModel;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Storage;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Eine Filterzeile (Ziel) im Eigenschaftenbereich: Erben / An / Aus mit Anzeige des effektiven Werts.</summary>
public sealed class StorageRowViewModel : ObservableObject
{
    public const string InheritText = "Erben";
    public const string OnText = "An";
    public const string OffText = "Aus";

    private readonly StorageSettingsViewModel _owner;

    internal StorageRowViewModel(StorageSettingsViewModel owner, StorageTarget target)
    {
        _owner = owner;
        Target = target;
    }

    public StorageTarget Target { get; }
    public string Label => StorageSettings.Label(Target);
    private static readonly IReadOnlyList<string> AllOptions = [InheritText, OnText, OffText];

    /// <summary>Instanzeigenschaft, damit WPF binden kann.</summary>
    public IReadOnlyList<string> Options => AllOptions;

    public string Selected
    {
        get => _owner.Node.Storage.Get(Target) switch { true => OnText, false => OffText, _ => InheritText };
        set
        {
            bool? v = value switch { OnText => true, OffText => false, _ => null };
            if (_owner.Node.Storage.Get(Target) == v) return;
            _owner.Node.Storage = _owner.Node.Storage.With(Target, v);
            _owner.Changed();
        }
    }

    /// <summary>Effektiver Wert inkl. Herkunft, z. B. „→ aus (von Messgruppe)“.</summary>
    public string EffectiveText
    {
        get
        {
            var (value, source) = _owner.Resolver().Explain(_owner.Node.Id, Target);
            var text = value ? "an" : "aus";
            return source is null ? $"→ {text}" : $"→ {text} ({(source == "Standard" ? "Standard" : "von " + source)})";
        }
    }

    public bool IsOff => !_owner.Resolver().Explain(_owner.Node.Id, Target).Value;

    internal void Refresh() => OnPropertiesChanged(nameof(Selected), nameof(EffectiveText), nameof(IsOff));
}

/// <summary>
/// Filter für Anzeige, Ergebnisdatei und Datenbank (Abschnitt 9.1). An Gruppen als vererbbare Defaults,
/// an Schritten als explizite Overrides. Prüfablauf und Bewertung bleiben unverändert.
/// </summary>
public sealed class StorageSettingsViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;

    public StorageSettingsViewModel(EditorViewModel editor, NodeDefinition node)
    {
        _editor = editor;
        Node = node;
        foreach (var t in StorageSettings.Targets) Rows.Add(new StorageRowViewModel(this, t));
        ResetCommand = new RelayCommand(() =>
        {
            Node.Storage = StorageSettings.Inherit;
            Changed();
        }, () => !Node.Storage.IsInherit);
    }

    public NodeDefinition Node { get; }
    public ObservableCollection<StorageRowViewModel> Rows { get; } = [];
    public RelayCommand ResetCommand { get; }

    public bool IsGroup => Node is GroupDefinition;

    public string Hint => IsGroup
        ? "Gruppendefault – gilt für alle Elemente darunter, sofern dort nichts anderes eingestellt ist."
        : "Override für diesen Schritt.";

    /// <summary>Kennzeichnung der technischen Mindestmenge (Instanzeigenschaft für die Bindung).</summary>
    public string MinimalSet => StorageSettings.MinimalSetDescription;

    /// <summary>Kurzfassung für den Ablaufbaum, z. B. „Datei aus“.</summary>
    public string Summary => string.Join(", ", Rows.Where(r => r.IsOff).Select(r => r.Label + " aus"));

    internal StorageResolver Resolver() => new(_editor.Project.Definition);

    internal void Changed()
    {
        _editor.MarkChanged();
        foreach (var r in Rows) r.Refresh();
        ResetCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(Summary));
    }
}
