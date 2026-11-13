using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Testumgebung.Identity;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Settings;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyWindowLifecycle(Dispatcher dispatcher, string projectDirectory)
    {
        // OnStartup selects OnLastWindowClose for production. This fixture opens/closes several independent windows.
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        foreach (var mode in new[] { "normal", "dirty-save", "fault", "example", "bad-stations", "running" })
        {
            var directory = Path.Combine(Path.GetTempPath(), "tu-ui-window-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var settings = new AppSettings { ResultFolder = Path.Combine(directory, "results"), Database = new DatabaseSettings { Provider = DatabaseChoice.None } };
            var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"), new DpapiSecretProtector()); settingsStore.Save(settings);
            var stationPath = Path.Combine(directory, "stations.json");
            if (mode == "bad-stations") File.WriteAllText(stationPath, "{ invalid }");
            var answer = MessageBoxResult.Cancel; var prompts = new List<string>();
            ResultServices? services = null;
            var startup = new MainWindowStartup
            {
                SettingsStore = settingsStore,
                AccessStore = new AccessConfigStore(Path.Combine(directory, "access.json")),
                StationStore = new StationSetupStore(stationPath),
                Arguments = mode == "example" ? ["--development", "--example-order"] : mode == "running" ? ["--development"] : [],
                ExamplesDirectory = Path.Combine(projectDirectory, "docs", "beispiele"),
                ShowMessage = (text, _, _) => { prompts.Add(text); return answer; },
                CreateResultServices = loaded => services = new ResultServices(loaded, new NoDatabase(), Path.Combine(directory, "outbox"), Path.Combine(directory, "database"),
                    recoveryFolder: Path.Combine(directory, "recovery"), releaseFolder: Path.Combine(directory, "releases"), journalFolder: Path.Combine(directory, "journal"))
            };
            var window = new MainWindow(startup) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var closed = false; window.Closed += (_, _) => closed = true;
            try
            {
                window.Show(); Sta.Flush(dispatcher);
                Assert.Same(window.Shell, window.DataContext); Assert.NotNull(services);
                Assert.DoesNotContain(Descendants(window).OfType<System.Windows.Controls.CheckBox>(), b => b.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == "PairedStations.Enabled");
                Assert.Empty(Descendants(window).OfType<Views.PairedStationsView>().SelectMany(v => Descendants(v).OfType<System.Windows.Controls.ComboBox>()));
                if (mode == "normal") VerifyGroupedNavigation(window, dispatcher);
                if (mode == "example")
                {
                    Assert.Contains("Programme A, B, C und D", window.Title); Assert.Equal("A123", window.Shell.SelectedStation.Selection.ScanText); Assert.True(window.Shell.SelectedStation.UseDraft);
                }
                if (mode == "bad-stations") Assert.NotNull(window.Shell.SelectedStation.Coordinator?.LoadError);
                Task? running = null;
                if (mode == "running")
                {
                    var loop = new GroupDefinition("loop", "Loop");
                    loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "wait", "Wait", new Dictionary<string, string> { ["Dauer"] = "10000" }));
                    window.Shell.Editor.Load(new TestProject(new TestDefinition("Window test", "1", new GroupDefinition("init", "Init"), loop, new GroupDefinition("end", "End"))));
                    window.Shell.Login(); Sta.Flush(dispatcher);
                    window.Shell.SelectedStation.UseDraft = true;
                    running = window.Shell.SelectedStation.StartAsync(); Wait(() => window.Shell.SelectedStation.IsRunning, dispatcher);
                    answer = MessageBoxResult.No; window.Close();
                    Assert.False(closed); Assert.True(window.Shell.SelectedStation.IsRunning); Assert.Single(prompts);
                    answer = MessageBoxResult.Yes; window.Close();
                }
                else if (mode == "dirty-save")
                {
                    Assert.True(window.Shell.Editor.Save(Path.Combine(directory, "project.tproj.json")));
                    window.Shell.Editor.MarkChanged(); window.Close();
                    Assert.False(closed); Assert.True(window.IsEnabled); Assert.Single(prompts);
                    answer = MessageBoxResult.Yes; window.Close();
                }
                else if (mode == "fault") { window.CloseAfterFault(); window.Close(); }
                else window.Close();
                Wait(() => closed, dispatcher);
                running?.GetAwaiter().GetResult();
                Assert.DoesNotContain(prompts, text => text.StartsWith("Der Abschluss konnte"));
                if (mode == "dirty-save") Assert.False(window.Shell.Editor.IsDirty);
            }
            finally
            {
                if (!closed) { window.CloseAfterFault(); Wait(() => closed, dispatcher); }
                // Closing the window can still leave a queued audit read finishing.
                // Retry only Windows sharing/lock violations, never other IO errors.
                Wait(() => TryDeleteWindowDirectory(directory), dispatcher,
                    "Temporäre Fensterdaten sind nach dem Schließen weiterhin gesperrt.");
            }
        }
    }

    private static void VerifyGroupedNavigation(MainWindow window, Dispatcher dispatcher)
    {
        var buttons = Descendants(window).OfType<RadioButton>().ToArray();
        var areas = buttons.Where(button => button.GroupName == "Nav").ToArray();
        Assert.Equal(7, areas.Length);

        void Select(RadioButton button)
        {
            Assert.True(button.IsVisible);
            Assert.True(button.Command.CanExecute(button.CommandParameter));
            button.Command.Execute(button.CommandParameter);
            Sta.Flush(dispatcher);
            Assert.True(button.IsChecked);
        }

        var results = areas.Single(button => AutomationProperties.GetName(button) == "Ergebnisse");
        Select(results);
        Assert.Equal(ShellPage.Protocol, window.Shell.Page);
        Assert.Equal("Aktueller Lauf", Assert.Single(buttons, button => button.GroupName == "TabsProtocol" && button.IsVisible).Content);
        window.Shell.Page = ShellPage.Results;
        Assert.Equal(ShellPage.Protocol, window.Shell.Page);

        window.Shell.Session.EnableDevelopmentMode();
        window.Shell.Login();
        Sta.Flush(dispatcher);
        Assert.True(window.Shell.IsDeveloper);
        Select(results);
        Select(buttons.Single(button => Equals(button.CommandParameter, "Reports")));
        Assert.True(results.IsChecked);
        Select(areas.Single(button => AutomationProperties.GetName(button) == "Verwaltung"));
        Select(buttons.Single(button => Equals(button.CommandParameter, "Python")));
        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "Python-Umgebung und Konsole");
        Select(buttons.Single(button => Equals(button.CommandParameter, "Access")));
        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "Zugang und Rechte (dieser Prüfplatz)");
        window.Shell.LogoutCommand.Execute(null);
        Sta.Flush(dispatcher);
        Assert.Equal(ShellPage.Operator, window.Shell.Page);
        Assert.False(buttons.Single(button => Equals(button.CommandParameter, "Access")).IsVisible);
    }

    [Fact]
    public void Window_cleanup_waits_for_a_real_file_lock_and_accepts_an_already_removed_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-ui-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var held = new FileStream(Path.Combine(directory, "audit.jsonl"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(TryDeleteWindowDirectory(directory));
                Assert.True(Directory.Exists(directory));
            }
            Assert.True(TryDeleteWindowDirectory(directory));
            Assert.False(Directory.Exists(directory));
            Assert.True(TryDeleteWindowDirectory(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static bool TryDeleteWindowDirectory(string directory)
    {
        try { Directory.Delete(directory, true); return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
    }

    private static void Wait(Func<bool> predicate, Dispatcher dispatcher, string message = "Fensterabschluss nicht rechtzeitig beendet.")
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Sta.Flush(dispatcher); Thread.Sleep(5); }
        Assert.True(predicate(), message);
    }
}
