using System.Reflection;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class StationStorageDisplayBoundaryTests
{
    [Theory]
    [InlineData("none", false)]
    [InlineData("success", true)]
    [InlineData("failed", false)]
    [InlineData("recovered", true)]
    [InlineData("no-message", false)]
    public void Storage_display_preserves_file_errors_and_database_outbox_warnings(string mode, bool hasDut)
    {
        var ui = new QueueDispatcher(); var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung()); var station = shell.SelectedStation;
        station.WarningDelay = _ => Task.CompletedTask;
        var file = mode switch { "none" => null, "success" => new FileSaveResult("C:/fixture/run.tres", null, false), "failed" => new FileSaveResult(null, "disk full", false), "recovered" => new FileSaveResult("C:/fixture/recovery.tres", "disk full", true), _ => new FileSaveResult(null, null, false) };
        var apply = typeof(StationViewModel).GetMethod("ApplyStorageReport", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var database in new OutboxSubmitResult?[] { null, new(true, null), new(false, null), new(false, "offline") })
        {
            var report = new StorageReport("run", hasDut ? "SN" : "", file, database); apply.Invoke(station, [report]); Assert.Same(report, station.StorageReports.Last());
            if (mode == "success") { Assert.Equal(file!.Path, station.LastResultFile); Assert.Null(station.StorageError); }
            if (mode == "failed") Assert.Equal("disk full", station.StorageError);
            if (mode == "recovered") Assert.Contains("Lokal gesichert", station.StorageError);
            if (database is { Stored: false }) Assert.NotEmpty(station.StorageWarnings);
            ui.Pump(); Assert.Empty(station.StorageWarnings);
        }
        for (var i = 0; i < 51; i++) apply.Invoke(station, [new StorageReport("tail-" + i, "SN", null, null)]);
        Assert.Equal(50, station.StorageReports.Count); Assert.Equal("tail-1", station.StorageReports[0].RunId);
        shell.Editor.FindNode("spannung")!.RunMarker = RunMarker.Done; var next = shell.Stations.Last(); shell.SelectStationCommand.Execute(next); Assert.Same(next, shell.SelectedStation); Assert.All(shell.Editor.AllNodes(), n => Assert.Equal(RunMarker.None, n.RunMarker));
    }
}
