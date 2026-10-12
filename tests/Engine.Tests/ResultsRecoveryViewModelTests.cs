using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ResultsRecoveryViewModelTests
{
    [Theory]
    [InlineData("files")]
    [InlineData("none")]
    [InlineData("blocked")]
    public async Task Recovery_ui_stores_aborted_run_and_keeps_failed_journal(string destination)
    {
        var root = Path.Combine(Path.GetTempPath(), "tu-recovery-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal = new RunJournal(Path.Combine(root, "journal"));
            using (var writer = journal.Begin(new JournalHeader { RunId = "R1", DutId = "SN123", Station = "Station", TestName = "Test", TestVersion = "1", Started = DateTimeOffset.UtcNow }))
                Assert.NotNull(writer);
            var target = Path.Combine(root, "results");
            var recovery = Path.Combine(root, "recovery");
            if (destination == "blocked") { File.WriteAllText(target, "block"); File.WriteAllText(recovery, "block"); }
            var files = destination == "none" ? null : new ResultFileStore(target, recovery);
            var pipeline = new ResultPipeline(files, null) { Journal = journal };
            var queue = new QueueDispatcher();
            var vm = new ResultsViewModel(pipeline, queue, new FakeDialogs(root), () => "Tester");
            Assert.Single(vm.Incomplete); Assert.True(vm.HasIncomplete);
            Assert.True(vm.RecoverCommand.CanExecute(null));
            var run = vm.SelectedIncomplete;
            await vm.RecoverSelectedAsync();
            queue.Pump();
            Assert.NotNull(vm.RecoveryStatus);
            if (destination == "blocked")
            {
                Assert.Contains("nicht gespeichert", vm.RecoveryStatus);
                Assert.Single(journal.FindIncomplete()); Assert.Single(vm.Incomplete);
            }
            else
            {
                Assert.Contains("ERROR", vm.RecoveryStatus);
                Assert.Empty(journal.FindIncomplete()); Assert.Empty(vm.Incomplete);
                Assert.False(vm.RecoverCommand.CanExecute(null));
                if (files is not null)
                {
                    var info = Assert.Single(files.List());
                    var saved = ResultFile.Read(info.Path).Run;
                    Assert.Equal("SN123", saved.DutId); Assert.Equal(ResultRating.Error, saved.Rating);
                    Assert.Single(vm.ResultFiles);
                    vm.SelectedFile = vm.ResultFiles[0]; Assert.NotNull(vm.Document);
                    vm.Open(null); Assert.Null(vm.Document);
                    vm.Open(Path.Combine(root, "absent.tres")); Assert.NotNull(vm.Error);
                }
            }
            vm.SelectedIncomplete = null;
            await vm.RecoverSelectedAsync(); vm.DiscardSelected();
            vm.SelectedIncomplete = run;
            if (destination == "blocked")
            {
                vm.DiscardSelected(); Assert.Empty(journal.FindIncomplete());
                Assert.Contains("verworfen", vm.RecoveryStatus!);
                Assert.Single(Directory.GetFiles(Path.Combine(journal.Folder, RunJournal.DiscardedFolderName)));
            }
            vm.RestoreRecovery(); vm.SettingsChanged();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Missing_pipeline_disables_storage_commands_and_has_no_side_effects()
    {
        var vm = new ResultsViewModel(null, new QueueDispatcher(), null);
        Assert.Empty(vm.ResultFiles); Assert.Empty(vm.Incomplete);
        Assert.False(vm.HasDatabase); Assert.False(vm.CanSimulateOutage);
        Assert.False(vm.OpenFolderCommand.CanExecute(null)); Assert.False(vm.RestoreRecoveryCommand.CanExecute(null));
        Assert.False(vm.RecoverCommand.CanExecute(null)); Assert.False(vm.DiscardIncompleteCommand.CanExecute(null));
        Assert.False(vm.RetryNowCommand.CanExecute(null));
        await vm.RecoverSelectedAsync(); vm.DiscardSelected(); vm.RestoreRecovery();
        vm.ExportReport("unused.pdf"); vm.ExportCsv("unused.csv"); vm.DatabaseOffline = true;
        Assert.False(vm.DatabaseOffline); Assert.Null(vm.Document); Assert.Null(vm.Error);
    }
}
