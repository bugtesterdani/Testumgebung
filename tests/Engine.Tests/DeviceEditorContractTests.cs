using Testumgebung.Instruments;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class DeviceEditorContractTests
{
    public static IEnumerable<object[]> Families()
        => InstrumentCatalog.Families.Select(f => f.Adapter).Concat(new[] { "unknown", StationSetup.SimulationAdapter })
            .Distinct().SelectMany(adapter => new[] { false, true }.Select(existing => new object[] { adapter, existing }));

    [Theory, MemberData(nameof(Families))]
    public void Changing_adapter_selects_model_and_preserves_explicit_resource(string adapter, bool existing)
    {
        var changes = 0;
        var config = new DeviceConfig { Id = "device", Name = "Gerät", Resource = existing ? "EXPLICIT" : "" };
        var row = new DeviceRow(config, () => changes++);
        row.Adapter = adapter;
        var family = InstrumentCatalog.Family(adapter);
        Assert.Equal(adapter, config.Adapter); Assert.Equal(adapter, row.Adapter); Assert.Equal("device", row.Id);
        Assert.Equal(family?.Models.FirstOrDefault(), row.Model);
        Assert.Equal(existing ? "EXPLICIT" : family?.ResourceExample ?? "", row.Resource);
        Assert.Equal(adapter != StationSetup.SimulationAdapter, row.IsReal);
        Assert.Equal(family?.Models ?? Array.Empty<string>(), row.Models);
        Assert.False(string.IsNullOrWhiteSpace(row.Notes));
        row.Name = "Neue Bezeichnung"; row.Shared = true; row.LockPerChannel = true;
        row.Resource = " COM7 "; Assert.Equal("COM7", row.Resource);
        row.Model = "Modell";
        Assert.Equal("Neue Bezeichnung", config.Name); Assert.True(config.Shared); Assert.True(config.LockPerChannel);
        Assert.Contains("gemeinsam", row.Text);
        if (row.IsReal) Assert.Contains("Modell", row.Text);
        row.OptionsText = "Baud=115200\nName=A=B;C\nignored\n=invalid";
        Assert.Equal("115200", config.Options!["Baud"]); Assert.Equal("A=B;C", config.Options["Name"]);
        Assert.Equal(2, config.Options.Count);
        row.TimeoutText = "2500"; Assert.Equal(2500, config.TimeoutMs); Assert.Equal("2500", row.TimeoutText);
        foreach (var invalid in new[] { "", "0", "-1", "abc", "1.5", "2147483648" })
        { row.TimeoutText = invalid; Assert.Null(config.TimeoutMs); Assert.Equal("", row.TimeoutText); }
        row.Shared = false; row.LockPerChannel = false; row.Resource = null; row.Model = null;
        Assert.Null(config.Resource); Assert.Null(config.Model); Assert.False(config.Shared); Assert.False(config.LockPerChannel);
        var before = changes;
        row.Adapter = row.Adapter; row.Adapter = null!; row.Name = row.Name; row.Shared = row.Shared;
        row.LockPerChannel = row.LockPerChannel; row.Resource = row.Resource; row.Model = row.Model;
        row.OptionsText = row.OptionsText; row.TimeoutText = row.TimeoutText;
        Assert.Equal(before, changes);
    }
}
