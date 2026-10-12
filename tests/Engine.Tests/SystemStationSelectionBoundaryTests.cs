using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Pilot;

namespace Testumgebung.Engine.Tests;

public sealed class SystemStationSelectionBoundaryTests
{
    [Theory]
    [InlineData("disabled")]
    [InlineData("no-rules")]
    [InlineData("empty")]
    [InlineData("manual")]
    [InlineData("custom")]
    [InlineData("rule")]
    [InlineData("load-error")]
    public async Task System_check_detects_rule_only_selection_without_rules(string mode)
    {
        var setup = StationSetup.Default(SampleProjects.AcPruefung(), 1);
        setup.Stations[0].Selection = new SelectionConfig { UseRules = mode is not ("no-rules" or "disabled"), UseManual = mode == "manual", UseCustom = mode == "custom" };
        if (mode == "rule") setup.Stations[0].Selection!.Rules.Add(new SelectionRule { Pattern = "A*", Test = "AC-Prüfung" });
        var coordinator = new StationCoordinator(setup, new ResourceManager()) { LoadError = mode == "load-error" ? "fixture broken" : null };
        var items = await SystemCheck.RunAsync(new SystemCheckContext { Coordinator = coordinator, ProbeDevices = true, RunProcess = (_, _) => null, VisaAvailable = () => false, DaqmxAvailable = () => false });
        Assert.Equal(mode == "empty", items.Any(i => i.Name.EndsWith(" · Auswahl") && i.Level == CheckLevel.Error));
        if (mode == "load-error") Assert.Contains(items, i => i.Name == "Prüfplatzkonfiguration" && i.Level == CheckLevel.Error && i.Detail == "fixture broken");
    }
}
