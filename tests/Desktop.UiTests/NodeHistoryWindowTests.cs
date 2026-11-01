using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testumgebung.Desktop.Views;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyNodeHistoryWindow(ShellViewModel shell, EditorView editor, System.Windows.Threading.Dispatcher dispatcher)
    {
        var selected = shell.Editor.SelectedNode!;
        var button = Descendants(editor).OfType<Button>().Single(b => b.Content?.ToString() == "Änderungshistorie");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var opened = Application.Current.Windows.OfType<NodeHistoryWindow>().Single();
        Assert.Contains(selected.Name, opened.Title);
        Assert.Contains(selected.Id, ((NodeHistoryViewModel)opened.DataContext).Identity);
        opened.Close();

        var folder = Path.Combine(Path.GetTempPath(), "node-history-ui-" + Guid.NewGuid());
        var store = new FolderReleaseStore(folder);
        var project = new ProjectSerializer().Clone(shell.Editor.Project);
        var node = project.Definition.Find(selected.Id)!;
        var firstTime = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        void Add(string version, int days, string comment)
        {
            project.Definition.Version = version;
            node.Description = comment;
            var time = firstTime.AddDays(days);
            var bytes = ReleasePackage.Build(project, "Tester", time).Bytes;
            var info = new ReleaseInfo("ui-" + version, project.Definition.Name, version, ReleaseState.Archived,
                ReleasePackage.Sha256(bytes), bytes.Length, "Tester", time, ReleasedAt: time);
            store.CreateAsync(info, bytes, new AuditDraft(time, "Tester", "Fixture", version)).GetAwaiter().GetResult();
        }
        Add("1", 0, "Erste Vorgabe\nBegründung: Geräteanlauf");
        Add("2", 1, "Neue Vorgabe\nNach Firmwareprüfung angepasst");
        var model = new NodeHistoryViewModel(new ReleaseService(store), project.Definition.Name, selected.Id, selected.Name);
        var window = new NodeHistoryWindow(model) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (model.Rows.Count == 0 && DateTime.UtcNow < deadline) { Sta.Flush(dispatcher); Thread.Sleep(10); }
            Assert.NotEmpty(model.Rows);
            model.Filter = "Kommentar";
            var grid = Descendants(window).OfType<DataGrid>().Single();
            window.UpdateLayout(); Sta.Flush(dispatcher);
            Assert.Equal(2, grid.Items.Count);
            grid.SelectedItem = model.Rows[0]; Sta.Flush(dispatcher);
            Assert.Equal(model.Rows[0], model.Selected);
            Assert.Contains(Descendants(window).OfType<TextBox>(), b => b.IsReadOnly && b.Text.Contains("Nach Firmwareprüfung"));
            var check = Descendants(window).OfType<CheckBox>().Single(); check.IsChecked = true; Sta.Flush(dispatcher);
            Assert.Single(model.Rows);
            window.Width = 1100; window.Height = 760; window.UpdateLayout(); Sta.Flush(dispatcher);
            var bitmap = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
            var output = Path.Combine(root.FullName, "artifacts", "verification", "node-history.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(output); encoder.Save(stream);
        }
        finally { window.Close(); Directory.Delete(folder, true); }
    }
}
