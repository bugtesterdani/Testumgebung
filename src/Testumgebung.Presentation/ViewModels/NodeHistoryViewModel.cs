/**
 * @file
 * @brief Zeigt die freigegebene Wertehistorie genau einer Ablaufkarte.
 */
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed class NodeHistoryViewModel : ObservableObject
{
    private readonly ReleaseService? _service;
    private readonly string _projectName, _nodeId;
    private IReadOnlyList<NodeValuePeriod> _rows = [];
    private string _filter = "", _status = "Noch nicht geladen.";
    private bool _latestOnly, _busy;
    private NodeValuePeriod? _selected;

    public NodeHistoryViewModel(ReleaseService? service, string projectName, string nodeId, string nodeName)
    {
        _service = service; _projectName = projectName; _nodeId = nodeId;
        Title = $"Änderungshistorie – {nodeName}";
        Identity = $"{projectName} · Element-ID: {nodeId}";
        ReloadCommand = new RelayCommand(() => _ = LoadAsync(), () => !_busy);
    }

    public string Title { get; }
    public string Identity { get; }
    public RelayCommand ReloadCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) RefreshRows(); } }
    public bool LatestOnly { get => _latestOnly; set { if (SetProperty(ref _latestOnly, value)) RefreshRows(); } }
    public NodeValuePeriod? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public IReadOnlyList<NodeValuePeriod> Rows => _rows.Where(Matches).Reverse().ToList();

    private bool Matches(NodeValuePeriod row) => (!LatestOnly || row.IsLatestValue) &&
        (string.IsNullOrWhiteSpace(Filter) || string.Join(" ", row.Field, row.Before, row.Value,
            row.Since.Version, row.Until?.Version).Contains(Filter, StringComparison.CurrentCultureIgnoreCase));

    private void RefreshRows()
    {
        OnPropertyChanged(nameof(Rows));
        if (Selected is not null && !Rows.Contains(Selected)) Selected = null;
    }

    /// <summary>Bei einer Lücke keine vermeintlichen Gültigkeitszeiträume aus Teilpaketen anzeigen.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_busy) return;
        _busy = true; ReloadCommand.RaiseCanExecuteChanged();
        _rows = []; Selected = null; RefreshRows(); Status = "Freigegebene Pakete werden geprüft …";
        try
        {
            if (_service is null) { Status = "Keine Release-Ablage eingerichtet."; return; }
            _rows = await NodeReleaseHistory.LoadAsync(_service, _projectName, _nodeId, ct);
            Status = _rows.Count == 0 ? "Für diese Element-ID ist noch kein freigegebener Stand vorhanden."
                : $"{_rows.Count} Wertezeiträume. Der erste Stand ist nur ab der frühesten vorhandenen Freigabe belegt.";
            RefreshRows();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Status = "Laden abgebrochen."; }
        catch (Exception ex) when (ex is ReleaseRuleException or ReleasePackageException or ProjectFormatException
            or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or System.Data.Common.DbException)
        { Status = "Historie nicht vollständig belegbar: " + ex.Message; }
        finally { _busy = false; ReloadCommand.RaiseCanExecuteChanged(); }
    }
}
