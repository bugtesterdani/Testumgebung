using System.Data.Common;
using System.Reflection;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Settings;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class SettingsPersistenceContractTests
{
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
    private sealed class Registry : IDatabaseProviderRegistry
    {
        public DbProviderFactory? Factory(DatabaseProvider provider) => null;
    }

    [Fact]
    public async Task Paired_display_is_saved_restored_and_requires_developer_rights_and_idle_stations()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tu-paired-setting-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var settings = new AppSettings { ResultFolder = Path.Combine(folder, "results"), Database = new() { Provider = DatabaseChoice.None } };
            await using var services = new ResultServices(settings, new Registry(), Path.Combine(folder, "outbox"), Path.Combine(folder, "db"), recoveryFolder: Path.Combine(folder, "recovery"), releaseFolder: Path.Combine(folder, "releases"));
            var store = new SettingsStore(Path.Combine(folder, "settings.json"), PlainSecretProtector.Instance);
            var shell = new ShellViewModel(ui: new InlineDispatcher(), services: services, settingsStore: store);
            var page = shell.SettingsPage!; var names = page.StationNames;
            page.ShowPairedStations = true; page.FirstPairedStation = names[0]; page.SecondPairedStation = names[1];
            page.Save(); Assert.Contains("Entwicklerberechtigung", page.SaveStatus); Assert.False(shell.PairedStations.Enabled); Assert.False(File.Exists(store.Path));
            await shell.LoginAsync(false); Assert.True(shell.IsDeveloper);
            page.SecondPairedStation = names[0]; page.Save(); Assert.Contains("verschiedene", page.SaveStatus);
            page.SecondPairedStation = "missing"; page.Save(); Assert.Contains("vorhandene", page.SaveStatus);
            page.SecondPairedStation = names[1]; page.Save(); Assert.True(shell.PairedStations.Enabled); Assert.True(store.Load().ShowPairedStations);
            var restarted = new ShellViewModel(ui: new InlineDispatcher(), services: services, settingsStore: store);
            Assert.True(restarted.PairedStations.Enabled); Assert.Equal(names[1], restarted.PairedStations.Second!.Name);
            shell.Coordinator.TryBeginRun(names[0], shell.Editor.Project.Definition);
            page.ShowPairedStations = false;
            page.Save(); Assert.Contains("Ende", page.SaveStatus); Assert.True(shell.PairedStations.Enabled);
            shell.Coordinator.EndRun(names[0]);
            page.Revert(); Assert.True(page.ShowPairedStations);
            page.ShowPairedStations = false; page.Save(); Assert.False(shell.PairedStations.Enabled);
        }
        finally { Directory.Delete(folder, true); }
    }

    public static IEnumerable<object?[]> EditableFields()
    {
        foreach (var p in typeof(SettingsViewModel).GetProperties().Where(p => p.SetMethod?.IsPublic == true))
        {
            if (p.PropertyType == typeof(string))
            {
                foreach (var text in new[] { "", "  ", " Wert " }) yield return [p.Name, text];
                yield return [p.Name, null];
            }
            if (p.PropertyType == typeof(bool)) { yield return [p.Name, true]; yield return [p.Name, false]; }
            if (p.PropertyType == typeof(double))
                foreach (var value in new[] { -1d, 0d, 0.25d, 10d, 100000d }) yield return [p.Name, value];
            if (p.PropertyType == typeof(int))
                foreach (var value in new[] { -1, 0, 1, 100, 100000 }) yield return [p.Name, value];
        }
    }

    [Theory, MemberData(nameof(EditableFields))]
    public async Task Every_editable_setting_keeps_normalized_value_after_save_reload(string name, object? input)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tu-setting-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings = new AppSettings { ResultFolder = Path.Combine(folder, "results"), Database = new DatabaseSettings { Provider = DatabaseChoice.None } };
            await using var services = new ResultServices(settings, new Registry(), Path.Combine(folder, "outbox"), Path.Combine(folder, "db"),
                recoveryFolder: Path.Combine(folder, "recovery"), releaseFolder: Path.Combine(folder, "releases"));
            var store = new SettingsStore(Path.Combine(folder, "settings.json"), PlainSecretProtector.Instance);
            var page = new SettingsViewModel(services, store, new InlineDispatcher());
            var property = typeof(SettingsViewModel).GetProperty(name)!;
            property.SetValue(page, input);
            var normalized = property.GetValue(page);
            // Result paths must stay in the disposable fixture folder.
            if (name == nameof(SettingsViewModel.ResultFolder))
            {
                page.ResultFolder = Path.Combine(folder, "edited-results");
                normalized = page.ResultFolder;
            }
            page.Save();
            Assert.True(File.Exists(store.Path), page.SaveStatus);
            Assert.DoesNotContain("fehlgeschlagen", page.SaveStatus ?? "");
            services.Apply(store.Load());
            var loaded = new SettingsViewModel(services, store, new InlineDispatcher());
            Assert.Equal(normalized, property.GetValue(loaded));
            property.SetValue(page, input);
            page.Revert();
            Assert.Equal(property.GetValue(loaded), property.GetValue(page));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
