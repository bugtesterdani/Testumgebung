using Testumgebung.Contracts;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Engine.Tests;

public sealed class RemoteSettingsClientTests
{
    private static readonly RemoteStationSettings Initial = new(false, "", "", 0, 0, false, false);

    [Theory]
    [InlineData("lab_1", true)]
    [InlineData("Lab-02", true)]
    [InlineData("../../lab", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void An_opted_in_agent_needs_a_stable_station_id(string? id, bool valid)
        => Assert.Equal(valid, RemoteSettingsClient.IsValidStationId(id));

    [Fact]
    public async Task A_lost_acknowledgment_does_not_reapply_an_already_saved_change()
    {
        var desired = Initial with { ErrorStreak = 3 };
        var client = new RemoteSettingsClient();
        var current = Initial;
        var changes = new List<long>();
        var errors = new List<Exception>();
        var reports = new List<RemoteSettingsReport>();

        Task<RemoteSettingsChange?> Exchange(RemoteSettingsReport report)
        {
            reports.Add(report);
            return reports.Count switch
            {
                2 => Task.FromException<RemoteSettingsChange?>(new IOException("Verbindung verloren")),
                4 => Task.FromResult<RemoteSettingsChange?>(null),
                _ => Task.FromResult<RemoteSettingsChange?>(new(7, Initial, desired)),
            };
        }

        string Apply(RemoteSettingsChange change)
        {
            if (current != change.Desired)
            {
                changes.Add(change.Revision);
                current = change.Desired;
            }
            return "applied";
        }

        await client.PollAsync(Exchange, () => new(current, ["P1", "P2"]), Apply, errors.Add);
        await client.PollAsync(Exchange, () => new(current, ["P1", "P2"]), Apply, errors.Add);

        Assert.Equal([7L], changes);
        Assert.IsType<IOException>(Assert.Single(errors));
        Assert.Equal((null, Initial), (reports[0].Revision, reports[0].Current));
        Assert.Equal((7L, "applied", desired), (reports[3].Revision, reports[3].Status, reports[3].Current));
    }
}
