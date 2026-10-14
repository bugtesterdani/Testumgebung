/**
 * @file
 * @brief Ergänzt die Release-Ansicht um den Vergleich freigegebener Pakete und des Editorstands.
 */

using System.Collections.ObjectModel;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Versionsvergleich: gewählter Release (bzw. Editorstand) gegen einen anderen Release oder den Editorstand.</summary>
public sealed partial class ReleasesViewModel
{
    public const string EditorTarget = "Editorstand (Entwurf)";

    private string? _compareWith;
    private string? _compareStatus;
    private RelayCommand? _compareCommand;

    /// <summary>Vergleichsbasis: „Editorstand“ oder eine Version.</summary>
    public IReadOnlyList<string> CompareTargets
        => [EditorTarget, .. Releases.Where(r => !ReferenceEquals(r, _selected)).Select(r => $"v{r.Version} ({r.StateText})")];

    public string? CompareWith
    {
        get => _compareWith;
        set => SetProperty(ref _compareWith, value);
    }

    public ObservableCollection<DiffEntry> Differences { get; } = [];

    public string? CompareStatus
    {
        get => _compareStatus;
        private set => SetProperty(ref _compareStatus, value);
    }

    public RelayCommand CompareCommand => _compareCommand ??= new RelayCommand(() => _ = CompareAsync());

    private async Task<TestProject?> LoadAsync(string? target)
    {
        if (target is null or EditorTarget) return _editor.Project;
        var release = Releases.FirstOrDefault(r => target.StartsWith($"v{r.Version} (", StringComparison.Ordinal));
        if (release is null) return null;
        var opened = await _hub.Service.OpenAsync(release).ConfigureAwait(true);
        return opened.Project;
    }

    /// <summary>Basis (CompareWith) → gewählter Release; ohne Auswahl der Editorstand.</summary>
    public async Task CompareAsync()
    {
        Differences.Clear();
        try
        {
            var before = await LoadAsync(_compareWith ?? EditorTarget).ConfigureAwait(true);
            var after = _selected is null ? _editor.Project : (await _hub.Service.OpenAsync(_selected).ConfigureAwait(true)).Project;
            if (before is null)
            {
                CompareStatus = "Vergleichsbasis nicht gefunden.";
                return;
            }
            foreach (var d in ProjectDiff.Compare(before, after)) Differences.Add(d);
            var to = _selected is null ? "Editorstand" : $"v{_selected.Version}";
            CompareStatus = Differences.Count == 0 ? $"{_compareWith ?? EditorTarget} → {to}: keine Unterschiede."
                : $"{_compareWith ?? EditorTarget} → {to}: {Differences.Count} Änderung(en).";
        }
        catch (Exception ex) when (ex is ReleasePackageException or ProjectFormatException or IOException or InvalidOperationException)
        {
            CompareStatus = "Vergleich nicht möglich: " + ex.Message;
        }
    }
}
