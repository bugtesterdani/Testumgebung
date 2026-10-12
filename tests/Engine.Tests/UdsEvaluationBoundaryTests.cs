using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class UdsEvaluationBoundaryTests
{
    public static IEnumerable<object[]> Numbers()
    {
        foreach (var width in new[] { 1, 2, 4, 8 })
            foreach (var signed in new[] { false, true })
                foreach (var negative in new[] { false, true }) yield return [width, signed, negative];
    }

    private static async Task<StepCompleted> Run(Dictionary<string, string> parameters, string response)
    {
        parameters["Ressource"] = "CAN"; parameters["Anfrage"] = "22 F1 90";
        var step = BlockCatalog.Default.Create(BusBlocks.UdsRequest, "uds", "UDS", parameters);
        var project = new TestProject(Test(Group("loop").Add(step)));
        project.SimulationComm.Add(new SimulatedComm("CAN", "can", CommKind.Can, "uds 0x7E0 0x7E8\nuds: 22 F1 90 => " + response));
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = SampleProjects.CreateSimulation(project).Registry });
        await run.RunAsync().Within();
        return Assert.Single(run.Steps("uds"));
    }

    [Theory, MemberData(nameof(Numbers))]
    public async Task Big_endian_values_sign_extend_before_scaling(int width, bool signed, bool negative)
    {
        var data = Enumerable.Repeat(negative ? (byte)255 : (byte)0, width).ToArray();
        if (!negative) data[^1] = 2;
        var result = await Run(new() { ["Darstellung"] = signed ? "Zahl mit Vorzeichen" : "Zahl", ["Faktor"] = "0.5", ["Offset"] = "3", ["Untergrenze"] = "-100" }, "62 F1 90 " + string.Join(" ", data.Select(b => b.ToString("X2"))));
        var raw = !negative ? 2d : signed ? -1d : width == 8 ? (double)ulong.MaxValue : Math.Pow(256, width) - 1;
        Assert.Equal(raw * .5 + 3, Assert.IsType<double>(result.Value)); Assert.Equal(ResultRating.Pass, result.Rating);
    }

    [Theory]
    [InlineData("AbByte", "0", ResultRating.Error, "beginnt bei 1")]
    [InlineData("AbByte", "10", ResultRating.Fail, "kürzer als Byte")]
    [InlineData("Laenge", "10", ResultRating.Fail, "kürzer als")]
    [InlineData("Laenge", "0", ResultRating.Error, "1–8 Bytes")]
    [InlineData("ErwarteteNrc", "0x31", ResultRating.Fail, "positive Antwort")]
    public async Task Invalid_response_slices_and_expected_rejection_are_reported(string key, string value, ResultRating rating, string message)
    {
        var result = await Run(new() { ["Darstellung"] = "Zahl", [key] = value }, "62 F1 90 02");
        Assert.Equal(rating, result.Rating); Assert.Contains(message, result.Message);
    }
}
