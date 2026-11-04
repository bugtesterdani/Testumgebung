using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Presentation.Releases;

/// <summary>Loads the central default once, preserving any project opened or edited while the database request is in flight.</summary>
public sealed class ProgramStartupLoader(ReleaseHub hub, EditorViewModel editor, Func<StationViewModel> station, Func<bool> anyRunning)
{
    private bool _loaded, _loading;
    public string? Error { get; private set; }
    private bool EditorAvailable => !editor.IsDirty && editor.FilePath is null && !anyRunning();
    public async Task<bool> TryLoadAsync()
    {
        if (_loaded || _loading || !EditorAvailable || hub.Catalog is null) return false;
        _loading = true;
        try { return await LoadAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Error = ex.Message; return false; }
        finally { _loading = false; }
    }
    private async Task<bool> LoadAsync()
    {
        var chosen = hub.ResolveStation(station().ProgramStationId, "");
        if (chosen is null) return false;
        var loaded = await hub.OpenResolvedAsync(chosen.Release);
        if (!EditorAvailable) return false;
        editor.Load(loaded.Project); _loaded = true; Error = null; return true;
    }
}
