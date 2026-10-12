using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class FolderDatabaseBoundaryTests
{
    [Theory]
    [InlineData("AZaz09-_", "AZaz09-_")]
    [InlineData("äÖ漢", "äÖ漢")]
    [InlineData("a/b\\c:?*|<>\".", "a_b_c________")]
    public async Task Storage_sanitizes_ids_and_does_not_overwrite_an_existing_run(string id, string file)
    {
        var folder = Path.Combine(Path.GetTempPath(), "folder-db-" + Guid.NewGuid());
        try
        {
            var db = new FolderRunDatabase(folder);
            Assert.Equal(0, db.Count); Assert.Null(await db.LoadRunAsync("missing", default));
            var run = new ResultRun { RunId = id, DutId = "original", Started = DateTimeOffset.UnixEpoch };
            await db.StoreAsync(run, default);
            Assert.True(File.Exists(Path.Combine(folder, file + ResultFile.Extension)));
            await db.StoreAsync(new ResultRun { RunId = id, DutId = "replacement" }, default);
            Assert.Equal(1, db.Count); Assert.Equal("original", (await db.LoadRunAsync(id, default))!.DutId);
            Assert.Null(await db.LoadRunAsync("missing", default));
            Assert.Single(await db.QueryRunsAsync(new RunQuery(DutId: "original"), default));
            Assert.Empty(await db.QueryRunsAsync(new RunQuery(DutId: "other"), default));
            Assert.Contains(folder, db.Name); Assert.Contains("Testumgebung", FolderRunDatabase.DefaultFolder);
            db.Offline = true;
            await Assert.ThrowsAsync<IOException>(() => db.StoreAsync(run, default));
            await Assert.ThrowsAsync<IOException>(() => db.LoadRunAsync(id, default));
            await Assert.ThrowsAsync<IOException>(() => db.QueryRunsAsync(new RunQuery(), default));
            db.Offline = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.StoreAsync(new ResultRun { RunId = "cancelled" }, new CancellationToken(true)));
            Assert.Equal(1, db.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
