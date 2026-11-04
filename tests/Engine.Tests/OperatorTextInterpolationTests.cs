using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class OperatorTextInterpolationTests
{
    [Theory]
    [InlineData("Anweisung", true)]
    [InlineData("Anweisung", false)]
    [InlineData("Bild mit Bestätigung", true)]
    [InlineData("Eingabe", true)]
    [InlineData("Auswahl", true)]
    public async Task Displayed_text_resolves_values_in_the_active_scope_and_preserves_prompt_options(string kind, bool wait)
    {
        const string template = "STM-Zielversion {ZielVersion}; STM sichtbar {ModulAnzahl}/5.\n{GewaehltesZip}; danach {ModulAnzahl + 1}.";
        var step = BlockCatalog.Default.Create(BlockCatalog.Operator, "prompt", "USB-Update bestätigen", new Dictionary<string, string>
        {
            ["Art"] = kind, ["Text"] = template, ["Warten"] = wait.ToString(), ["Datentyp"] = "Text",
            ["Bild"] = "usb.png", ["ErlaubteWerte"] = "ok;nein", ["Ziel"] = "Antwort", ["Anzeige"] = BlockCatalog.DisplayModes[1],
        });
        var group = Group("usb").Var("ZielVersion", VarType.String, "1.2.3").Var("ModulAnzahl", VarType.Int, 3)
            .Var("GewaehltesZip", VarType.String, "firmware.zip").Var("Antwort", VarType.String).Add(step);
        var op = new ScriptedOperator().Answer("USB-Update bestätigen", p => p.ExpectsValue ? "ok" : true);
        var run = new TestRun(Test(Group("loop").Var("ZielVersion", VarType.String, "äußerer Wert").Add(group)),
            new TestRunOptions { Clock = new VirtualClock(), Operator = op });

        Assert.Equal(ResultRating.NResult, (await run.RunAsync().Within()).Rating);
        var shown = Assert.Single(run.Events.OfType<OperatorContentShown>()).Prompt;
        Assert.Equal("STM-Zielversion 1.2.3; STM sichtbar 3/5.\nfirmware.zip; danach 4.", shown.Text);
        Assert.Equal("usb.png", shown.ImagePath);
        Assert.Equal(OperatorDisplayMode.UntilGroupEnd, shown.DisplayMode);
        Assert.Equal(new[] { "ok", "nein" }, shown.AllowedValues);
        Assert.Equal(template, step.Parameters["Text"]);
        Assert.Equal(wait ? 1 : 0, op.Asked.Count);
        Assert.Equal(kind is "Eingabe" or "Auswahl" ? "ok" : null, run.Steps("prompt").Single().Value);
    }

    [Fact]
    public async Task Repeated_step_uses_fresh_values_without_mutating_earlier_displays()
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Operator, "prompt", "Fortschritt", new Dictionary<string, string>
        { ["Text"] = "Modul {N}; {{N}} bleibt Text.", ["Warten"] = "false" });
        var group = Group("usb").Add(Steps.SetValue("inc", "N", "N + 1"), step);
        group.Repetition = Repetition.Times(2);
        var run = new TestRun(Test(Group("loop").Var("N", VarType.Int, 0).Add(group)), new TestRunOptions { Clock = new VirtualClock() });

        Assert.Equal(ResultRating.NResult, (await run.RunAsync().Within()).Rating);
        Assert.Equal(new[] { "Modul 1; {N} bleibt Text.", "Modul 2; {N} bleibt Text." },
            run.Events.OfType<OperatorContentShown>().Select(e => e.Prompt.Text));
    }

    [Theory]
    [InlineData("Unbekannt: {Fehlt}")]
    [InlineData("Unvollständig: {Fehlt")]
    public async Task Invalid_placeholder_fails_before_an_operator_can_confirm_it(string text)
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Operator, "prompt", "USB", new Dictionary<string, string> { ["Text"] = text });
        var op = new ScriptedOperator().Answer("USB", _ => true);
        var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions { Clock = new VirtualClock(), Operator = op });

        Assert.Equal(ResultRating.Error, (await run.RunAsync().Within()).Rating);
        Assert.Empty(op.Asked);
        Assert.Empty(run.Events.OfType<OperatorContentShown>());
        Assert.Contains("Fehlt", run.Steps("prompt").Single().Message);
    }
}
