using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Testumgebung.Identity;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Settings;
using Testumgebung.Presentation.ViewModels;
using Testumgebung.Desktop.Views;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyLocalPasswordLogin(Dispatcher dispatcher)
    {
        var row = new RightsEditRow { Upn = "tester@simuliert.local", Name = "Tester", Develop = true };
        var dialog = new LocalPasswordWindow(row) { ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        dialog.Show(); Sta.Flush(dispatcher);
        var password = (PasswordBox)dialog.FindName("NewPassword"); var confirmation = (PasswordBox)dialog.FindName("Confirmation");
        Click(dialog, "Übernehmen"); Assert.False(string.IsNullOrEmpty(((TextBlock)dialog.FindName("Error")).Text));
        password.Password = "temporary-123"; confirmation.Password = "different"; Click(dialog, "Übernehmen"); Assert.Null(row.PasswordHash);
        confirmation.Password = "temporary-123"; CaptureVisual(dialog, "lokales-passwort-setzen.png");
        // DialogResult requires modal use; invoke the real modal dialog on a fresh instance.
        dialog.Close(); Assert.Empty(password.Password); Assert.Empty(confirmation.Password);
        var modal = new LocalPasswordWindow(row) { ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            ((PasswordBox)modal.FindName("NewPassword")).Password = "temporary-123";
            ((PasswordBox)modal.FindName("Confirmation")).Password = "temporary-123"; Click(modal, "Übernehmen");
        }));
        Assert.True(modal.ShowDialog()); Assert.True(LocalPasswordHash.Verify("temporary-123", row.PasswordHash));

        var directory = Path.Combine(Path.GetTempPath(), "tu-login-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var configStore = new AccessConfigStore(Path.Combine(directory, "access.json"));
        configStore.Save(new AccessConfig { AllowLocalPasswords = true, Users = [new RightsEntry { Upn = row.Upn, Name = row.Name, PasswordHash = row.PasswordHash, Develop = true }] });
        var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"), new DpapiSecretProtector());
        settingsStore.Save(new AppSettings { ResultFolder = Path.Combine(directory, "results"), Database = new DatabaseSettings { Provider = DatabaseChoice.None } });
        var startup = new MainWindowStartup
        {
            AccessStore = configStore, SettingsStore = settingsStore, StationStore = new StationSetupStore(Path.Combine(directory, "stations.json")), Arguments = [],
            ShowMessage = (_, _, _) => MessageBoxResult.No,
            CreateResultServices = loaded => new ResultServices(loaded, new NoDatabase(), Path.Combine(directory, "outbox"), Path.Combine(directory, "database"),
                recoveryFolder: Path.Combine(directory, "recovery"), releaseFolder: Path.Combine(directory, "releases"), journalFolder: Path.Combine(directory, "journal"))
        };
        var window = new MainWindow(startup) { ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Sta.Flush(dispatcher); var shell = window.Shell;
            Assert.True(shell.IsLocalPasswordLogin); Assert.False(shell.CanEnableDevelopmentLogin);
            var input = (PasswordBox)window.FindName("LocalLoginPassword"); var toggle = (ToggleButton)window.FindName("UserToggle");
            toggle.IsChecked = true; Sta.Flush(dispatcher); Assert.True(input.IsVisible); shell.LoginName = "Tester";
            input.Password = "wrong"; shell.LoginCommand.Execute(null); Wait(() => !shell.IsSigningIn, dispatcher);
            Assert.False(shell.IsSignedIn); Wait(() => input.Password.Length == 0, dispatcher);
            input.Password = "temporary-123";
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
            input.RaiseEvent(key); Wait(() => shell.IsSignedIn, dispatcher); Wait(() => input.Password.Length == 0, dispatcher);
            Assert.True(key.Handled); Assert.True(shell.IsDeveloper); Assert.Contains("lokaler Passwortzugang", shell.UserDetail);
            shell.Page = Testumgebung.Presentation.ViewModels.ShellPage.Access;
            var settingsView = new SettingsView { DataContext = shell };
            var settingsHost = new Window { Content = settingsView, Width = 1280, Height = 1000, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
            settingsHost.Show(); Sta.Flush(dispatcher);
            try
            {
                var grid = Descendants(settingsView).OfType<DataGrid>().Single(g => ReferenceEquals(g.ItemsSource, shell.AccessPage.Users));
                grid.BringIntoView(); settingsHost.UpdateLayout(); Sta.Flush(dispatcher);
                var set = Descendants(grid).OfType<Button>().Single(b => b.Content?.ToString() == "Setzen / ändern");
                var before = shell.AccessPage.Users[0].PasswordHash; Exception? error = null;
                dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    var editor = Application.Current.Windows.OfType<LocalPasswordWindow>().Single();
                    try
                    {
                        ((PasswordBox)editor.FindName("NewPassword")).Password = "temporary-456";
                        ((PasswordBox)editor.FindName("Confirmation")).Password = "temporary-456";
                        CaptureVisual(editor, "lokales-passwort-setzen.png"); Click(editor, "Übernehmen");
                    }
                    catch (Exception ex) { error = ex; editor.Close(); }
                }));
                set.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Null(error);
                Assert.NotEqual(before, shell.AccessPage.Users[0].PasswordHash);
                Assert.Equal(before, configStore.Load()!.Users[0].PasswordHash); // staging alone never changes the actual login
                shell.SwitchAccountCommand.Execute(null); Wait(() => !shell.IsSignedIn, dispatcher); Assert.Empty(input.Password);
                set.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(Application.Current.Windows.OfType<LocalPasswordWindow>());
            }
            finally { settingsHost.Close(); }

            window.Close(); Wait(() => closed, dispatcher);
        }
        finally
        {
            if (!closed) { window.CloseAfterFault(); Wait(() => closed, dispatcher); }
            Wait(() => TryDeleteWindowDirectory(directory), dispatcher);
        }
    }
}
