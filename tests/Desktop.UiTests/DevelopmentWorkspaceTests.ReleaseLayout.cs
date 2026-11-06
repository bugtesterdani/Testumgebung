using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Testumgebung.Contracts;
using Testumgebung.Desktop.Views;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;
public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyReleaseSelectionAndWorkflow(Dispatcher dispatcher)
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-layout-" + Guid.NewGuid().ToString("N"));
        var project = new TestProject(new Testumgebung.Domain.Definitions.TestDefinition(
            "Prüfprogramm zur Versionsangleichung – mit einem sehr langen Namen", "0.29",
            new("init", "Start"), new Testumgebung.Domain.Definitions.GroupDefinition("loop", "Prüfung").Add(
                Testumgebung.Engine.Blocks.BlockCatalog.Default.Create(Testumgebung.Engine.Blocks.BlockCatalog.SetRating, "check")), new("final", "Ende")));
        var hub = new ReleaseHub(new ReleaseService(new FolderReleaseStore(directory)));
        var ui = Testumgebung.Presentation.Mvvm.SynchronizationContextDispatcher.FromCurrent();
        var shell = new ShellViewModel(ui, project: project, releases: hub);
        shell.Login(); Sta.Flush(dispatcher);
        var page = shell.ReleasesPage!;
        var window = new Window { Content = new Border { Background = Brushes.White, Child = new ReleasesView { DataContext = shell } },
            Width = 1280, Height = 780, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        try
        {
            var view = (ReleasesView)((Border)window.Content).Child;
            var combo = (ComboBox)view.FindName("ReleaseProgramsList");
            var scroll = (ScrollViewer)view.FindName("ReleaseMainScroll");
            window.Show(); Layout();
            Assert.Equal(project.Definition.Name, Assert.IsType<ReleaseProgramRow>(combo.SelectedItem).Name);
            var draft = Task.Run(() => hub.Service.CreateDraftAsync(project, "0.29", new Actor("author@fixture", UserRights.Develop))).GetAwaiter().GetResult();
            for (var i = 0; i < 4; i++)
            {
                Task.Run(() => hub.RefreshAsync()).GetAwaiter().GetResult(); Sta.Flush(dispatcher); Layout();
                Assert.Equal(project.Definition.Name, Assert.IsType<ReleaseProgramRow>(combo.SelectedItem).Name);
                CaptureVisual(window, "releases-selection.png");
                Assert.True(Descendants(combo).OfType<TextBlock>().Any(text => text.Text == project.Definition.Name),
                    $"Name not rendered: Text='{combo.Text}', SelectionBoxItem='{combo.SelectionBoxItem}', SelectedIndex={combo.SelectedIndex}");
                Assert.Same(page.SelectedProgram, combo.SelectedItem);
            }
            Assert.True(SpinWait.SpinUntil(() => { Sta.Flush(dispatcher); return page.Package.Count > 0; }, TimeSpan.FromSeconds(5)));
            Assert.Equal(draft.ReleaseId, page.Selected!.ReleaseId);
            Assert.Equal("Noch nicht freigegeben", page.ReleaseByText);
            foreach (var size in new[] { new Size(1280, 780), new Size(1920, 1080) })
            {
                window.Width = size.Width; window.Height = size.Height; scroll.ScrollToTop(); Layout();
                var package = (DataGrid)view.FindName("ReleasePackageList");
                Assert.True(package.ActualWidth > 200); Assert.InRange(package.ActualHeight, 200, 220);
                CaptureVisual(window, $"releases-overview-{size.Width}.png");
                var review = (Button)view.FindName("ReleaseReviewButton"); review.BringIntoView(); Layout();
                Assert.True(review.ActualWidth > 100); Assert.True(review.ActualHeight > 20);
                var approve = (Button)view.FindName("ReleaseApproveButton"); approve.BringIntoView(); Layout();
                var bounds = approve.TransformToAncestor(scroll).TransformBounds(new Rect(approve.RenderSize));
                Assert.True(bounds.Top >= -1 && bounds.Bottom <= scroll.ActualHeight + 1, "Freigabe muss per Scrollen erreichbar sein.");
                CaptureVisual(window, $"releases-workflow-{size.Width}.png");
            }
            var loadButton = (Button)view.FindName("LoadReleaseInEditorButton");
            Assert.Same(page.LoadInEditorCommand, loadButton.Command); Assert.True(loadButton.IsEnabled);
            shell.Page = ShellPage.Releases;
            var previous = shell.Editor.Project; loadButton.Command.Execute(null);
            Assert.True(SpinWait.SpinUntil(() => { Sta.Flush(dispatcher); return !ReferenceEquals(previous, shell.Editor.Project); }, TimeSpan.FromSeconds(5)));
            Assert.Equal(ShellPage.Editor, shell.Page); Assert.True(shell.Editor.IsDirty); Assert.Null(shell.Editor.FilePath);
            void Layout() { Sta.Flush(dispatcher); window.UpdateLayout(); Sta.Flush(dispatcher); }
        }
        finally { window.Close(); hub.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
