namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationViewModel
{
    public string ProgramStationId => Environment.MachineName + "/" + Name;
    private string? _programVersionWarning;
    public string? ProgramVersionWarning { get => _programVersionWarning; private set => SetProperty(ref _programVersionWarning, value); }
    internal async Task RefreshProgramSourcesAsync()
    {
        if (_releases is not null && !UseDraft) await _releases.RefreshAsync().ConfigureAwait(true);
    }
    private async Task<(Testumgebung.Persistence.TestProject Project, Testumgebung.Persistence.Releases.ReleaseInfo? Release)> LoadCatalogTestAsync(Testumgebung.Persistence.Stations.AvailableTest test)
    {
        var assignment = _releases!.Catalog!.Stations.FirstOrDefault(assignment2 => assignment2.Station == ProgramStationId);
        var available = _releases.ForStation(ProgramStationId);
        // An automatic station follows a new central release even if it appeared between scan and Start.
        var followLatest = test.IsCurrent && assignment?.AutoUpdate != false;
        var selected = available.FirstOrDefault(release => release.ProjectName == test.Name && (followLatest || release.Version == test.Version))
            ?? throw new InvalidOperationException("Gewählte Version ist für diesen Platz nicht freigegeben. Auftrag neu auswählen.");
        var resolved = await _releases.OpenResolvedAsync(selected).ConfigureAwait(true);
        return (resolved.Project, resolved.Info);
    }
    private void UpdateProgramVersionWarning()
    {
        try
        {
            var resolved = _releases?.ResolveStation(ProgramStationId, _projectSource().Definition.Name);
            ProgramVersionWarning = resolved?.Warning;
            if (IsRunning && ActiveRelease is { } active && resolved is not null && active.ReleaseId != resolved.Release.ReleaseId)
                ProgramVersionWarning ??= $"Neue Freigabe {resolved.Release.Version} verfügbar. Der laufende Auftrag bleibt bei {active.Version}; Übernahme beim nächsten Auftragsstart.";
        }
        catch (InvalidOperationException ex) { ProgramVersionWarning = ex.Message; }
    }
}
