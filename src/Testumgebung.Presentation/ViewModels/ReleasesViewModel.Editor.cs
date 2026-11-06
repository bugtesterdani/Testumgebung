using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class ReleasesViewModel
{
    /// <summary>Open the selected immutable package as an independently editable local copy.</summary>
    public RelayCommand LoadInEditorCommand { get; }
    public bool CanLoadInEditor => !_busy && _session.CanDevelop && (_selected is not null || DraftSource() is not null);
    public event Action? EditorLoaded;

    public async Task<bool> LoadInEditorAsync()
    {
        if (!CanLoadInEditor) return false;
        var info = _selected; var name = ProjectName; var previous = _editor.Project;
        var local = info is null ? DraftSource() : null;
        if (!Begin()) return false;
        try
        {
            var project = info is not null ? await ReadEditorPackageAsync(info).ConfigureAwait(false)
                : new ProjectSerializer(_editor.Catalog).Clone(local!);
            return await ApplyEditorProjectAsync(project, previous, name, info?.ReleaseId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ReleasePackageException or ProjectFormatException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.Data.Common.DbException)
        {
            _ui.Post(() => Done("Prüfprogramm konnte nicht in den Ablaufeditor geladen werden: " + ex.Message, true));
            return false;
        }
    }

    private async Task<TestProject> ReadEditorPackageAsync(ReleaseInfo info)
    {
        var package = await _hub.Service.OpenAsync(info).ConfigureAwait(false);
        if (!package.IsIntact) throw new ReleasePackageException("Paket beschädigt: " + string.Join("; ", package.Problems));
        // Opening historical source for editing does not grant permission to execute it in today's runtime.
        return package.Project;
    }

    private Task<bool> ApplyEditorProjectAsync(TestProject project, TestProject previous, string name, string? releaseId)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try { completion.TrySetResult(ApplyEditorProject(project, previous, name, releaseId)); }
            catch (Exception ex) { Done("Editorprojekt konnte nicht übernommen werden: " + ex.Message, true); completion.TrySetResult(false); }
        });
        return completion.Task;
    }

    private bool ApplyEditorProject(TestProject project, TestProject previous, string name, string? releaseId)
    {
        if (!_session.CanDevelop) { Done("Zum Laden in den Ablaufeditor ist das Recht Entwickeln erforderlich.", true); return false; }
        if (ProjectName != name || _selected?.ReleaseId != releaseId || !ReferenceEquals(previous, _editor.Project))
        { Done("Auswahl oder Editorprojekt hat sich während des Ladens geändert. Bitte erneut laden.", false); return false; }
        if (!_editor.PrepareProjectReplacement()) { Done("Laden abgebrochen; bisheriges Editorprojekt beibehalten.", false); return false; }
        if (!_session.CanDevelop) { Done("Anmeldung hat sich geändert. Bitte erneut anmelden und laden.", true); return false; }
        _editor.Load(project); // No package path becomes the save destination.
        _editor.MarkChanged(); // Saving the new working copy requires an explicit local file destination.
        Done($"{project.Definition.Name} v{project.Definition.Version} als lokale Bearbeitungsfassung geladen. Gespeicherte Freigaben bleiben unverändert.", false);
        EditorLoaded?.Invoke();
        return true;
    }
}
