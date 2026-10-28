using System.Diagnostics;
using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class SshIntegrationFactAttribute : FactAttribute
{
    public SshIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TESTUMGEBUNG_SSH_TEST_PYTHON")))
            Skip = "Lokale SSH/SFTP-Gegenstelle: Python mit Paramiko über TESTUMGEBUNG_SSH_TEST_PYTHON angeben.";
    }
}

public sealed class SshIntegrationTests
{
    [SshIntegrationFact]
    public async Task SSH_SFTP_echte_Gegenstelle_Passwort_Schluessel_Hostkey_Exitcode_und_Dateitransfer()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var folder = Path.Combine(Path.GetTempPath(), "tu-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var server = new Process { StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("TESTUMGEBUNG_SSH_TEST_PYTHON")!)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        server.StartInfo.ArgumentList.Add(Path.Combine(root.FullName, "tests", "Fixtures", "ssh_server.py"));
        server.StartInfo.ArgumentList.Add(folder);
        server.Start();
        try
        {
            var line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(line);
            using var ready = JsonDocument.Parse(line);
            var endpoint = "127.0.0.1:" + ready.RootElement.GetProperty("port").GetInt32();
            var values = new Dictionary<string, string> { ["Benutzer"] = "test", ["Passwort"] = "fixture-password", ["HostKeySHA256"] = ready.RootElement.GetProperty("fingerprint").GetString()! };
            using var device = new RemoteDevice(RemoteDevice.SshAdapter, endpoint, new InstrumentOptions(values));
            var result = await device.ExecuteAsync(new("ssh", "hello"), default);
            Assert.Equal(0, result.Code);
            Assert.Equal("executed:hello", result.Text);
            result = await device.ExecuteAsync(new("ssh", "fail"), default);
            Assert.Equal(7, result.Code);
            Assert.Equal("command failed", result.Error);

            var upload = Path.Combine(folder, "upload.bin");
            var download = Path.Combine(folder, "download.bin");
            var bytes = Enumerable.Range(0, 65537).Select(i => (byte)i).ToArray();
            await File.WriteAllBytesAsync(upload, bytes);
            result = await device.ExecuteAsync(new("sftp.upload", "/remote.bin", LocalPath: upload), default);
            Assert.Equal(bytes.Length.ToString(), result.Text);
            await device.ExecuteAsync(new("sftp.download", "/remote.bin", LocalPath: download), default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(download));
            await Assert.ThrowsAnyAsync<Exception>(() => device.ExecuteAsync(new("sftp.upload", "/remote.bin", LocalPath: upload), default));
            await Assert.ThrowsAsync<IOException>(() => device.ExecuteAsync(new("sftp.download", "/remote.bin", LocalPath: download), default));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(download));
            await device.ExecuteAsync(new("sftp.upload", "/remote.bin", LocalPath: upload, Overwrite: true), default);
            await device.ExecuteAsync(new("sftp.download", "/remote.bin", LocalPath: download, Overwrite: true), default);

            values.Remove("Passwort");
            values["Schlüsseldatei"] = ready.RootElement.GetProperty("key").GetString()!;
            await device.ConfigureAsync(endpoint, values, default);
            var command = BlockCatalog.Default.Create(NetworkBlocks.Ssh, "ssh", "SSH", new Dictionary<string, string>
            { ["Ressource"] = "SSH", ["Befehl"] = "hello", ["Ziel"] = "Antwort", ["StatusZiel"] = "Code" });
            var loop = Group("loop").Var("Antwort", VarType.String).Var("Code", VarType.Int).Add(command);
            var run = new TestRun(Test(loop), new TestRunOptions { Hardware = new HardwareRegistry().Bind("SSH", device, "") });
            await run.RunAsync().Within();
            string ReadLog()
            {
                using var stream = new FileStream(Path.Combine(folder, "ssh.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            Assert.True(run.Steps("ssh").Single().Rating == ResultRating.Pass, run.Steps("ssh").Single().Message + "\n" + ReadLog());
            Assert.Equal("executed:hello", run.Steps("ssh").Single().Value);
            await Assert.ThrowsAsync<CommunicationTimeoutException>(() => device.ExecuteAsync(new("ssh", "slow", Timeout: TimeSpan.FromMilliseconds(300)), default));

            values["HostKeySHA256"] = "SHA256:wrong";
            await device.ConfigureAsync(endpoint, values, default);
            await Assert.ThrowsAnyAsync<Exception>(() => device.ExecuteAsync(new("ssh", "hello"), default));
            await VerifyAcceptedKeysAsync(device, endpoint, folder, ready.RootElement.GetProperty("fingerprint").GetString()!);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task VerifyAcceptedKeysAsync(RemoteDevice device, string endpoint, string folder, string original)
    {
        var values = new Dictionary<string, string>
        {
            ["Benutzer"] = "test", ["Passwort"] = "fixture-password", ["HostKeySHA256"] = "SHA256:old",
            ["HostKeyPruefung"] = "Akzeptieren", ["HostKeyProtokollieren"] = "true",
        };
        await device.ConfigureAsync(endpoint, values, default);
        var result = await device.ExecuteAsync(new("ssh", "rotate-host-key"), default);
        Assert.Equal(original, result.HostKeySHA256);
        var replacement = await device.ExecuteAsync(new("ssh", "hello"), default);
        Assert.NotEqual(original, replacement.HostKeySHA256);
        Assert.StartsWith("SHA256:", replacement.HostKeySHA256);
        Assert.NotEmpty(replacement.HostKeyBase64);
        Assert.Contains(device.Log, l => l.Contains(replacement.HostKeySHA256, StringComparison.Ordinal));
        Assert.DoesNotContain(device.Log, l => l.Contains("fixture-password", StringComparison.Ordinal));
        var transfer = await device.ExecuteAsync(new("sftp.download", "/remote.bin", LocalPath: Path.Combine(folder, "accepted.bin")), default);
        Assert.Equal(replacement.HostKeySHA256, transfer.HostKeySHA256);
        Assert.Equal(replacement.HostKeyBase64, transfer.HostKeyBase64);
        await VerifyHostKeyTargetsAsync(device, replacement);
        values.Remove("HostKeySHA256");
        values.Remove("HostKeyProtokollieren");
        await device.ConfigureAsync(endpoint, values, default);
        var logCount = device.Log.Count;
        Assert.Equal(replacement.HostKeySHA256, (await device.ExecuteAsync(new("ssh", "hello"), default)).HostKeySHA256);
        Assert.Equal(logCount, device.Log.Count);
        values["Passwort"] = "wrong";
        await device.ConfigureAsync(endpoint, values, default);
        await Assert.ThrowsAnyAsync<Renci.SshNet.Common.SshAuthenticationException>(() => device.ExecuteAsync(new("ssh", "hello"), default));
        values["Passwort"] = "fixture-password";
        values["HostKeySHA256"] = original;
        values["HostKeyPruefung"] = "Pruefen";
        await device.ConfigureAsync(endpoint, values, default);
        await Assert.ThrowsAnyAsync<Exception>(() => device.ExecuteAsync(new("ssh", "hello"), default));
    }

    private static async Task VerifyHostKeyTargetsAsync(RemoteDevice device, RemoteResponse expected)
    {
        var step = BlockCatalog.Default.Create(NetworkBlocks.Ssh, "key", "Hostkey erfassen", new Dictionary<string, string>
        { ["Ressource"] = "SSH", ["Befehl"] = "hello", ["HostKeyZiel"] = "Fingerprint", ["HostKeyDatenZiel"] = "PublicKey" });
        var loop = Group("loop").Var("Fingerprint", VarType.String).Var("PublicKey", VarType.String).Add(step,
            new StepDefinition("verify", "Werte prüfen", new DelegateAction(ctx =>
            {
                Assert.Equal(expected.HostKeySHA256, ctx.Get("Fingerprint"));
                Assert.Equal(expected.HostKeyBase64, ctx.Get("PublicKey"));
                return Task.FromResult(StepOutcome.Pass());
            })));
        var run = new TestRun(Test(loop), new TestRunOptions { Hardware = new HardwareRegistry().Bind("SSH", device, "") });
        await run.RunAsync().Within();
        Assert.All(run.Events.OfType<StepCompleted>(), s => Assert.Equal(ResultRating.Pass, s.Rating));
    }

}
