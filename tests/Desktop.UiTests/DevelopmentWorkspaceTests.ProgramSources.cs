using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Programs;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyProgramSources(Dispatcher dispatcher)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-sources-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var catalog = new FolderProgramCatalogStore(directory);
        Task.Run(async () =>
        {
            await catalog.UpgradeAsync(); await catalog.SaveAsync(ProgramCatalog.Empty with
            { Sources = [new("source", "AC-Prüfung", "Git", "https://example.invalid/program.git", "main", "program.tproj.json")] });
        }).GetAwaiter().GetResult();
        var hub = new ReleaseHub(new ReleaseService(new FolderReleaseStore(Path.Combine(directory, "releases"))) { ProgramCatalog = catalog });
        var shell = new ShellViewModel(project: SampleProjects.AcPruefung(), releases: hub); shell.Login();
        var view = new ReleasesView { DataContext = shell };
        var host = new Window { Content = view, Width = 1280, Height = 900, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        Exception? failure = null;
        host.Show(); Sta.Flush(dispatcher);
        dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
        {
            var window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Programmquellen & Versionen");
            try
            {
                await shell.ReleasesPage!.Sources.RefreshAsync();
                var panel = Assert.IsType<ProgramSourcesView>(window.Content); var model = shell.ReleasesPage.Sources;
                Assert.Single(model.Sources); Assert.Contains("bereit", model.SchemaText);
                Assert.Contains("Git", model.Providers); Assert.Contains("SVN", model.Providers);
                var line = Descendants(panel).OfType<TextBox>().Single(t => t.GetBindingExpression(TextBox.TextProperty)?.ResolvedSourcePropertyName == "Line");
                line.Text = "integration"; line.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Equal("integration", model.Line);
                await model.SaveSourceAsync(); Assert.Equal("integration", model.Selected!.Line);
                window.UpdateLayout(); CaptureVisual(window, "programmquellen.png");
                var scroll = Descendants(panel).OfType<ScrollViewer>().Single(v => v.Content is StackPanel); scroll.ScrollToEnd(); Sta.Flush(dispatcher); window.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                CaptureVisual(window, "programmquellen-platzzuordnung.png");
            }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        }));
        try
        {
            var button = Descendants(view).OfType<Button>().Single(b => b.Content?.ToString() == "Programmquellen & Versionen …");
            typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, []);
            Assert.Null(failure);
        }
        finally { host.Close(); hub.DisposeAsync().AsTask().GetAwaiter().GetResult(); Directory.Delete(directory, true); }
    }
}
