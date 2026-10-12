using System.Text.Json;
using Testumgebung.Persistence.Releases;

namespace Testumgebung.Engine.Tests;

public sealed class ExternalRuntimeInventoryTests
{
    [Fact]
    public async Task Konfigurierte_Herstellerdatei_bindet_Inhalt_und_fehlende_Datei_blockiert()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var driver = Path.Combine(directory, "driver.bin");
            var config = Path.Combine(directory, "inventory.json");
            await File.WriteAllTextAsync(driver, "Version 1");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new[] { new ExternalRuntimeFile("Prüftreiber", driver) }));
            var before = new RuntimeSnapshot(await ExternalRuntimeInventory.CaptureAsync(default, config));
            Assert.Contains(before.Components, c => c.Name == "Extern/Konfiguriert/Prüftreiber" && c.Sha256?.Length == 64);
            await File.WriteAllTextAsync(driver, "Version 2");
            var after = new RuntimeSnapshot(await ExternalRuntimeInventory.CaptureAsync(default, config));
            Assert.NotEmpty(before.Compare(after));
            File.Delete(driver);
            await Assert.ThrowsAsync<FileNotFoundException>(() => ExternalRuntimeInventory.CaptureAsync(default, config));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
