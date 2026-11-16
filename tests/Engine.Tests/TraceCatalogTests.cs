using Testumgebung.Engine.Blocks;

namespace Testumgebung.Engine.Tests;

public sealed class TraceCatalogTests
{
    [Fact]
    public void Manual_and_timed_recording_share_configuration_except_duration_and_timed_fields()
    {
        var catalog = BlockCatalog.Default;
        var manual = catalog.Get(TraceBlocks.Start).Parameters;
        var timed = catalog.Get(TraceBlocks.Record).Parameters.ToDictionary(parameter => parameter.Name);

        foreach (var parameter in manual)
        {
            var timedParameter = timed[parameter.Name];
            if (parameter.Name == "Dauer")
            {
                Assert.Equal("0", parameter.Default);
                Assert.Equal("1", timedParameter.Default);
                Assert.Equal(parameter.Kind, timedParameter.Kind);
            }
            else
            {
                Assert.Equal(parameter with { Choices = null }, timedParameter with { Choices = null });
                Assert.Equal(parameter.Choices, timedParameter.Choices);
            }
            timed.Remove(parameter.Name);
        }

        Assert.Equal(
            new[] { "ErgebnisZiel", "Intervall", "Quelle", "Ressourcen", "Werte", TraceBlocks.ExitDutOnEarlyStopParameter }.Order(StringComparer.Ordinal),
            timed.Keys.Order(StringComparer.Ordinal));
    }
}
