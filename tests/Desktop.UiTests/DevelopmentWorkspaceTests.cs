using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testumgebung.Desktop.Views;
using Testumgebung.Presentation.ViewModels;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Hardware;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    [Fact]
    public void Gemeinsamer_Arbeitsplatz_laed_alle_Reiter_und_rendert_in_zwei_Groessen()
    {
        Sta.Run(dispatcher =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, LicenseTermsConsent = () => true };
            app.InitializeComponent();
            InteractionBoundaryTests.VerifyPlotGestures();
            VerifyEditorKeyboard();
            VerifyEditorDragDrop();
            VerifySettingsPasswords();
            VerifyReleaseErrorLayout(dispatcher);
            var view = new DevelopmentWorkspace { DataContext = new ShellViewModel() };
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
            VerifyWindowLifecycle(dispatcher, root.FullName);
            var output = Path.Combine(root.FullName, "artifacts", "verification");
            Directory.CreateDirectory(output);
            VerifyAdaptiveLayout(dispatcher);
            VerifyPairedView(dispatcher, output);
            VerifyCustomDevices(dispatcher, output);
            foreach (var size in new[] { new Size(1280, 900), new Size(1920, 1080) })
            {
                view.Width = size.Width;
                view.Height = size.Height;
                view.Measure(size);
                view.Arrange(new Rect(size));
                view.UpdateLayout();
                var tabs = Descendants(view).OfType<TabControl>().First();
                Assert.Equal(new[] { "Protokoll", "Variablen", "Debug" }, tabs.Items.OfType<TabItem>().Select(t => t.Header));
                for (var tab = 0; tab < 3; tab++)
                {
                    tabs.SelectedIndex = tab;
                    view.UpdateLayout();
                    Sta.Flush(dispatcher);
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"arbeitsplatz-{size.Width}-{tab}.png"));
                    encoder.Save(stream);
                }
                Assert.True(tabs.ActualHeight >= 140);
                Assert.True(Descendants(view).OfType<GridSplitter>().Count() >= 3);
            }
            var example = OrderExample.Load(Path.Combine(root.FullName, "docs", "beispiele"));
            var project = example.Project;
            var setup = example.Setup;
            var releaseHub = new Testumgebung.Presentation.Releases.ReleaseHub(new Testumgebung.Persistence.Releases.ReleaseService(
                new Testumgebung.Persistence.Releases.FolderReleaseStore(Path.Combine(output, "empty-releases-" + Guid.NewGuid()))));
            var shell = new ShellViewModel(project: project, stations: new StationCoordinator(setup, new ResourceManager()), releases: releaseHub);
            VerifyVariableEditing(shell, dispatcher);
            shell.Library.ImportAssembly(typeof(Testumgebung.Contracts.Waveform).Assembly.Location);
            foreach (var page in new UserControl[] { new OperatorView(), new SelectionView(), new LibraryView(), new ReleasesView(), new SystemView(), new PairedStationsView() })
            {
                page.DataContext = shell;
                page.Width = 1280;
                page.Height = 900;
                page.Measure(new Size(1280, 900));
                page.Arrange(new Rect(0, 0, 1280, 900));
                page.UpdateLayout();
                Sta.Flush(dispatcher);
                if (page is OperatorView)
                {
                    Assert.DoesNotContain(Descendants(page).OfType<TextBlock>(), t => t.Text == "Verfügbare Testprogramme am Prüfplatz");
                    Assert.Contains(Descendants(page).OfType<TextBlock>(), t => t.Text == "Fertigungsauftrag erfassen");
                    var scan = Descendants(page).OfType<TextBox>().Single(t => t.Name == "KennungBox");
                    Assert.True(scan.IsEnabled);
                    Assert.Equal("ScanText", scan.GetBindingExpression(TextBox.TextProperty)!.ParentBinding.Path.Path);
                }
                else if (page is SystemView)
                {
                    var create = Descendants(page).OfType<Button>().Single(b => b.Content?.ToString() == "Python-Kopie erstellen und Konsole öffnen");
                    Assert.Same(shell.SystemPage.Python.CreateCommand, create.Command);
                    Assert.Contains(Descendants(page).OfType<TextBox>(), t => t.IsReadOnly && t.Text.Contains("python -m pip install"));
                    Assert.Contains(Descendants(page).OfType<Button>(), b => b.Content?.ToString() == "Diese Umgebung verwenden" && ReferenceEquals(b.Command, shell.SystemPage.Python.UseEnvironmentCommand));
                }
                else if (page is SelectionView) Assert.Contains(Descendants(page).OfType<CheckBox>(), c => c.Content?.ToString()?.StartsWith("Fertigungsauftrag startet") == true && c.IsChecked == true);
                else if (page is LibraryView)
                {
                    Assert.Contains(Descendants(page).OfType<Button>(), b => b.Content?.ToString() == ".NET-DLL importieren / ersetzen");
                    Assert.Contains(Descendants(page).OfType<ListBox>(), b => b.Items.Count == 1 && b.Items[0] is Testumgebung.Domain.Code.ManagedAssembly);
                    shell.Library.AddCSharpCommand.Execute(null);
                    var block = shell.Library.Selected!;
                    foreach (var existing in block.Ports.ToArray()) block.RemovePortCommand.Execute(existing);
                    block.AddPortCommand.Execute(null);
                    var port = Assert.Single(block.Ports);
                    page.UpdateLayout();
                    Sta.Flush(dispatcher);
                    var typeEditor = Descendants(page).OfType<ComboBox>().Single(t => ReferenceEquals(t.DataContext, port)
                        && t.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path == "Type");
                    typeEditor.SelectedItem = Testumgebung.Contracts.VarType.String;
                    Sta.Flush(dispatcher);
                    Assert.Equal(Testumgebung.Contracts.VarType.String, port.Type);
                    var nameEditor = Descendants(page).OfType<TextBox>().Single(t => ReferenceEquals(t.DataContext, port)
                        && t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Name");
                    nameEditor.Text = "Auftrag";
                    Sta.Flush(dispatcher);
                    Assert.Equal("Auftrag", port.Name);
                    Assert.Equal("Auftrag", Assert.Single(block.Block.Ports).Name);
                    var testEditor = Descendants(page).OfType<TextBox>().Single(t => ReferenceEquals(t.DataContext, port)
                        && t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "TestValue");
                    testEditor.Text = "123C";
                    Sta.Flush(dispatcher);
                    Assert.Null(port.Error);
                    Assert.Equal("123C", Assert.Single(block.Block.Ports).TestValue);
                }
                if (page is ReleasesView)
                {
                    var programs = Descendants(page).OfType<ComboBox>().Single(c => c.Name == "ReleaseProgramsList");
                    Assert.Equal(new[] { "Programm A", "Programm B", "Programm C", "Programm D" },
                        programs.Items.Cast<ReleaseProgramRow>().Select(t => t.Name));
                    programs.SelectedValue = "Programm D";
                    Sta.Flush(dispatcher);
                    Assert.Equal("Programm D", shell.ReleasesPage!.ProjectName);
                    Assert.Equal("Programm A", shell.Editor.Project.Definition.Name);
                    Assert.Empty(shell.ReleasesPage.Releases);
                    Assert.Contains("kein Release", shell.ReleasesPage.ProgramStatus);
                }
                var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, page.GetType().Name + "-auftrag.png"));
                encoder.Save(stream);
            }
            if (Environment.GetEnvironmentVariable("TESTUMGEBUNG_HANDBUCH_BILDER") is { Length: > 0 } handbook)
                CaptureHandbook(shell, handbook, root.FullName, dispatcher);
        });
    }

    private sealed class NoDatabase : Testumgebung.Presentation.Settings.IDatabaseProviderRegistry
    {
        public System.Data.Common.DbProviderFactory? Factory(Testumgebung.DatabaseAdapters.DatabaseProvider provider) => null;
    }

    private static void VerifySettingsPasswords()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tu-ui-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var settings = new Testumgebung.Presentation.Settings.AppSettings
        {
            ResultFolder = Path.Combine(folder, "results"),
            Database = new Testumgebung.Presentation.Settings.DatabaseSettings { Provider = Testumgebung.Presentation.Settings.DatabaseChoice.None }
        };
        var services = new Testumgebung.Presentation.Settings.ResultServices(settings, new NoDatabase(), Path.Combine(folder, "outbox"), Path.Combine(folder, "db"),
            recoveryFolder: Path.Combine(folder, "recovery"), releaseFolder: Path.Combine(folder, "releases"), journalFolder: Path.Combine(folder, "journal"));
        try
        {
            var store = new Testumgebung.Presentation.Settings.SettingsStore(Path.Combine(folder, "settings.json"), Testumgebung.Presentation.Settings.PlainSecretProtector.Instance);
            var shell = new ShellViewModel(services: services, settingsStore: store);
            var page = shell.SettingsPage!;
            var view = new SettingsView();
            var mqtt = (PasswordBox)view.FindName("MqttPasswordBox");
            var http = (PasswordBox)view.FindName("HttpHeaderBox");
            var opc = (PasswordBox)view.FindName("OpcUaPasswordBox");
            // Without a settings context, input must remain local to the view.
            mqtt.Password = "local"; http.Password = "local"; opc.Password = "local";
            view.DataContext = shell;
            Assert.Equal(page.MqttPassword, mqtt.Password);
            Assert.Equal(page.HttpHeaderValue, http.Password);
            Assert.Equal(page.OpcUaPassword, opc.Password);
            foreach (var input in new[] { "secret", "", " A=B;C " })
            {
                page.MqttPassword = input; page.HttpHeaderValue = input; page.OpcUaPassword = input;
                Assert.Equal(input, mqtt.Password); Assert.Equal(input, http.Password); Assert.Equal(input, opc.Password);
                mqtt.Password = input + "mqtt"; http.Password = input + "http"; opc.Password = input + "opc";
                Assert.Equal(mqtt.Password, page.MqttPassword);
                Assert.Equal(http.Password, page.HttpHeaderValue);
                Assert.Equal(opc.Password, page.OpcUaPassword);
            }
            page.ResultFolder = Path.Combine(folder, "changed"); // Unrelated notifications must not alter password boxes.
            Assert.Equal(page.MqttPassword, mqtt.Password);
            view.Visibility = Visibility.Collapsed;
            view.Visibility = Visibility.Visible;
            view.DataContext = null;
            page.MqttPassword = "detached";
            Assert.NotEqual("detached", mqtt.Password);
            view.DataContext = shell;
            Assert.Equal("detached", mqtt.Password);
        }
        finally
        {
            Task.Run(async () => await services.DisposeAsync()).GetAwaiter().GetResult();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void VerifyEditorKeyboard()
    {
        var shell = new ShellViewModel();
        var editor = shell.Editor;
        var view = new EditorView();
        var method = typeof(EditorView).GetMethod("TryHandleTreeKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool Key(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers = System.Windows.Input.ModifierKeys.None)
            => (bool)method.Invoke(view, new object[] { key, modifiers })!;
        Assert.False(Key(System.Windows.Input.Key.Delete));
        view.DataContext = shell;
        editor.SelectedNode = editor.Nodes[1];
        Assert.False(Key(System.Windows.Input.Key.Delete));
        Assert.False(Key(System.Windows.Input.Key.Escape));
        Assert.False(Key(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Alt));
        var step = editor.FindNode("belastung")!.Children.First(n => !n.IsExit);
        editor.SelectedNode = step;
        var parent = step.Parent!;
        var count = parent.Children.Count;
        Assert.True(Key(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(count, parent.Children.Count);
        Assert.True(Key(System.Windows.Input.Key.D, System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(count + 1, editor.FindNode(parent.Id)!.Children.Count);
        Assert.True(Key(System.Windows.Input.Key.Delete));
        Assert.Equal(count, editor.FindNode(parent.Id)!.Children.Count);
        editor.SelectedNode = editor.FindNode(step.Id);
        Assert.True(Key(System.Windows.Input.Key.X, System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(count - 1, editor.FindNode(parent.Id)!.Children.Count);
        editor.SelectedNode = editor.FindNode(parent.Id);
        Assert.True(Key(System.Windows.Input.Key.V, System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(count, editor.FindNode(parent.Id)!.Children.Count);
        Assert.True(Key(System.Windows.Input.Key.F9));
        editor.SelectedNode = editor.Nodes[0];
        Assert.True(Key(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control));
        Assert.True(Key(System.Windows.Input.Key.D, System.Windows.Input.ModifierKeys.Control));
    }

    private static void VerifyVariableEditing(ShellViewModel shell, System.Windows.Threading.Dispatcher dispatcher)
    {
        shell.Editor.SelectedNode = shell.Editor.FindNode(shell.Editor.Project.Definition.Loop.Id);
        var properties = (GroupPropertiesViewModel)shell.Editor.Properties!;
        properties.Variables.AddCommand.Execute(null);
        var row = properties.Variables.Rows.Last();
        var page = new EditorView { DataContext = shell, Width = 1920, Height = 1080 };
        page.Measure(new Size(1920, 1080));
        page.Arrange(new Rect(0, 0, 1920, 1080));
        page.UpdateLayout();
        Sta.Flush(dispatcher);
        TextBox Text(string path) => Descendants(page).OfType<TextBox>().Single(t => ReferenceEquals(t.DataContext, row)
            && t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == path);
        Text("Name").Text = "ComPort";
        var type = Descendants(page).OfType<ComboBox>().Single(t => ReferenceEquals(t.DataContext, row));
        type.SelectedItem = Testumgebung.Contracts.VarType.String;
        Text("Initial").Text = "COM7";
        Sta.Flush(dispatcher);
        Assert.Equal("ComPort", row.Name);
        Assert.Equal(Testumgebung.Contracts.VarType.String, row.Type);
        Assert.Null(row.Error);
        var saved = new ProjectSerializer().Clone(shell.Editor.Project).Definition.Loop.Variables.Single(v => v.Name == "ComPort");
        Assert.Equal(Testumgebung.Contracts.VarType.String, saved.Type);
        Assert.Equal("COM7", saved.Initial);
        properties.Variables.RemoveCommand.Execute(row);
        VerifyParallelEditing(shell, page, dispatcher);
        shell.Editor.SelectedNode = null;
    }

    private static void VerifyParallelEditing(ShellViewModel shell, EditorView page, System.Windows.Threading.Dispatcher dispatcher)
    {
        var group = shell.Editor.Project.Definition.AllNodes().OfType<Testumgebung.Domain.Definitions.GroupDefinition>().First(g => g.IsDutRun);
        shell.Editor.SelectedNode = shell.Editor.FindNode(group.Id);
        page.UpdateLayout();
        Sta.Flush(dispatcher);
        var check = Descendants(page).OfType<CheckBox>().Single(c =>
            c.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == "Parallel");
        Assert.False(group.Parallel);
        check.IsChecked = true;
        Sta.Flush(dispatcher);
        Assert.True(group.Parallel);
        Assert.True(((Testumgebung.Domain.Definitions.GroupDefinition)new ProjectSerializer().Clone(shell.Editor.Project).Definition.Find(group.Id)!).Parallel);
        Assert.Contains("parallel", shell.Editor.SelectedNode!.Annotation);
        check.IsChecked = false;
        Sta.Flush(dispatcher);
        Assert.False(group.Parallel);
    }

    private static void CaptureHandbook(ShellViewModel shell, string folder, string root,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        Directory.CreateDirectory(folder);
        void Capture(UserControl page, string name, Action? prepare = null)
        {
            page.DataContext = shell;
            page.Background = Brushes.WhiteSmoke;
            page.Width = 1920;
            page.Height = 1080;
            void Layout()
            {
                page.Measure(new Size(1920, 1080));
                page.Arrange(new Rect(0, 0, 1920, 1080));
                page.UpdateLayout();
                Sta.Flush(dispatcher);
            }
            Layout();
            prepare?.Invoke();
            Layout();
            var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, name + ".png"));
            encoder.Save(stream);
        }

        Capture(new OperatorView(), "01-werker");
        Capture(new SelectionView(), "02-auftragsauswahl");
        Capture(new EditorView(), "03-ablaufeditor");
        var block = shell.Library.Selected!;
        block.Name = "Auftragsauswertung (DLL)";
        block.Ports.Single().TestValue = "A123";
        block.Source = "var daten = MeinePruefbibliothek.Auftragsauswertung.Lesen(Auftrag);\n"
            + "SelectTest(daten.Testprogramm, null, Auftrag, \"Artikel=\" + daten.Artikel);\nreturn Pass(daten.Testprogramm);";
        var dll = Path.Combine(root, "artifacts", "verification", "dll-handbuch", "bin", "Release", "net10.0", "MeinePruefbibliothek.dll");
        foreach (var assembly in shell.Library.Assemblies.ToArray())
        {
            shell.Library.SelectedAssembly = assembly;
            shell.Library.RemoveAssemblyCommand.Execute(null);
        }
        if (File.Exists(dll)) shell.Library.ImportAssembly(dll);
        Capture(new LibraryView(), "04-dll-baustein");
        shell.Editor.SelectedNode = shell.Editor.FindNode(shell.Editor.Project.Definition.Init.Id);
        var palette = shell.Editor.Palette.SelectMany(c => c.Items)
            .Single(p => p.BlockType == Testumgebung.Engine.Blocks.BlockCatalog.LibraryBlock);
        var inserted = shell.Editor.Insert(palette);
        var bindings = ((StepPropertiesViewModel)shell.Editor.Properties!).Bindings!;
        bindings.SelectedBlock = bindings.Blocks.Single(b => b.Id == block.Id);
        bindings.Rows.Single().Value = "Auftrag";
        shell.Editor.Validate();
        var editor = new EditorView();
        Capture(editor, "05-baustein-einfuegen", () =>
        {
            var heading = Descendants(editor).OfType<TextBlock>().FirstOrDefault(t => t.Text == "CodeBlocks");
            heading?.BringIntoView();
        });
        Capture(new StationsView(), "06-pruefplatz");
        Capture(new SettingsView(), "07-einstellungen");
        Capture(new ReleasesView(), "08-releases");
        Capture(new ResultsView(), "09-ergebnisse");
        Capture(new DebugView(), "10-debugger");
        Capture(new CurvesView(), "11-kurven");
        Capture(new ReportsView(), "12-auswertung");
        Capture(new SystemView(), "13-system");
    }

    private static void VerifyPairedView(System.Windows.Threading.Dispatcher dispatcher, string output)
    {
        var project = new ProjectSerializer().Load(Path.Combine(Directory.GetParent(output)!.Parent!.FullName,
            "docs", "beispiele", "zwei-pruefplaetze.tproj.json"));
        Assert.Empty(project.Definition.Validate());
        var shell = new ShellViewModel(Testumgebung.Presentation.Mvvm.ImmediateDispatcher.Instance, project: project,
            stations: new StationCoordinator(StationSetup.Default(project, 2), new ResourceManager()));
        shell.PairedStations.Enabled = true; shell.PairedStations.Order = "A123";
        var first = shell.PairedStations.First!; var second = shell.PairedStations.Second!;
        using var stop = new CancellationTokenSource();
        Task<object?> Ask(StationViewModel station, string text) => ((Testumgebung.Engine.IOperatorInterface)station).AskAsync("paired-preview",
            new Testumgebung.Domain.Definitions.OperatorPrompt(Testumgebung.Domain.Definitions.OperatorPromptKind.Input, text)
            { DisplayId = station.Name, StepName = "Seriennummer erfassen", Required = true }, stop.Token);
        var answerFirst = Ask(first, "Seriennummer für Seite 1 scannen");
        var answerSecond = Ask(second, "Seriennummer für Seite 2 scannen");
        first.ActiveContent!.Input = "SN-001"; second.ActiveContent!.Input = "SN-002";
        first.Measurements.Add(new MeasurementRow("Spannung", "230", "V", "220 – 240", Testumgebung.Contracts.ResultRating.Pass));
        second.Measurements.Add(new MeasurementRow("Spannung", "229", "V", "220 – 240", Testumgebung.Contracts.ResultRating.Pass));
        var view = new PairedStationsView { DataContext = shell };
        foreach (var size in new[] { new Size(1280, 900), new Size(1920, 1080) })
        {
            view.Width = size.Width; view.Height = size.Height;
            view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout(); Sta.Flush(dispatcher);
            Assert.Equal(2, Descendants(view).OfType<PairedStationPane>().Count());
            Assert.Equal(2, Descendants(view).OfType<TextBox>().Count(t => t.IsEnabled
                && t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Input"));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"paired-stations-{size.Width}.png")); encoder.Save(stream);
        }
        first.ActiveContent!.ConfirmCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => answerFirst.IsCompletedSuccessfully, TimeSpan.FromSeconds(5)));
        Assert.False(answerSecond.IsCompleted);
        second.ActiveContent!.ConfirmCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => answerSecond.IsCompletedSuccessfully, TimeSpan.FromSeconds(5)));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
