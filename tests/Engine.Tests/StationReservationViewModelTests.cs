using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class StationReservationViewModelTests
{
    private sealed class Resources : IResourceManager
    {
        public IReadOnlyList<ResourceOwnership> Items = [];
        public Task<IResourceLease> AcquireAsync(ResourceRequest request, TimeSpan? timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool IsHeldBy(string resource, string runId) => Items.Any(x => x.Resource == resource && x.OwnerRunId == runId);
        public IReadOnlyList<ResourceOwnership> Snapshot() => Items;
    }

    public static IEnumerable<object[]> Owners()
    {
        foreach (var owner in new[] { "none", "P1", "P2", "anonymous" })
            foreach (var waiting in new[] { false, true })
                foreach (var scope in new[] { false, true })
                    yield return [owner, waiting, scope];
    }

    [Theory, MemberData(nameof(Owners))]
    public void Reservation_status_tracks_owner_wait_queue_and_release(string owner, bool waiting, bool scope)
    {
        var group = new GroupDefinition("loop", "Loop");
        group.Resources.Add("R");
        var project = new TestProject(Kit.Test(group));
        var assignment = new ResourceAssignment { Name = "R", Device = "device", Channel = "ch", RequiresLock = true };
        var setup = new StationSetup
        {
            Devices = [new() { Id = "device", Name = "Gerät", Shared = true }],
            Stations = [new() { Name = "P1", Resources = [assignment] }, new() { Name = "P2", Resources = [new() { Name = "R", Device = "device", Channel = "ch", RequiresLock = true }, new() { Name = "Alias", Device = "device", Channel = "ch", RequiresLock = true }] }]
        };
        var resources = new Resources();
        var coordinator = new StationCoordinator(setup, resources);
        var shell = new ShellViewModel(project: project, stations: coordinator);
        var page = shell.StationsPage;
        page.SelectedStation = "P1";
        page.SelectedGroup = group;
        var key = setup.LockKey("P1", assignment);
        resources.Items = [new(key, owner == "none" ? null : "run", 1, waiting ? ["wait-run"] : [],
            owner is "none" or "anonymous" ? null : owner, waiting ? ["P2"] : null,
            scope ? "Messgruppe" : null, scope ? DateTimeOffset.UnixEpoch : null)];
        page.RefreshLive();
        Assert.Equal(owner == "P1", page.Acquired);
        Assert.Equal(owner == "P1" ? "Ja" : "Nein", page.AcquiredText);
        var row = Assert.Single(page.Rows);
        if (owner == "none") Assert.Contains(waiting ? "wartend" : "Bereit", row.Status);
        else Assert.Contains("Reserviert", row.Status);
        Assert.Equal(owner == "P1", page.WaitStatus.Single(x => x.Station == "P1").IsOwner);
        Assert.Equal(waiting, page.WaitStatus.Single(x => x.Station == "P2").IsWaiting);
        Assert.Equal(owner == "none" ? "—" : "run / " + (scope ? $"Messgruppe seit {DateTimeOffset.UnixEpoch.ToLocalTime():HH:mm:ss}" : "Gruppe"), page.ScopeText);
        Assert.Contains(page.Details, d => d.Label == "Gemeinsam mit" && d.Value.Contains("P2 (Alias)"));
        resources.Items = [];
        page.RefreshLive();
        Assert.False(page.Acquired);
        Assert.Equal("Bereit", row.Status);
        Assert.All(page.WaitStatus, w => Assert.False(w.IsOwner || w.IsWaiting));
        page.SelectedGroup = null;
        Assert.Empty(page.Reservations);
        Assert.Empty(page.WaitStatus);
        page.SelectedRow = null;
        Assert.Empty(page.Details);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("scalar", false)]
    [InlineData("scalar", true)]
    [InlineData("wave", false)]
    [InlineData("wave", true)]
    [InlineData("output", false)]
    [InlineData("output", true)]
    [InlineData("unbounded", false)]
    [InlineData("unbounded", true)]
    public void Resource_details_describe_available_measurement_and_output_capabilities(string kind, bool missingDevice)
    {
        var group = new GroupDefinition("loop", "Loop");
        group.Resources.Add("R");
        var project = new TestProject(Kit.Test(group));
        if (kind is "scalar" or "wave" or "unbounded")
            project.Simulation.Add(new("R", "ch", "V", 1, Min: kind == "unbounded" ? -1e9 : 0, Max: kind == "unbounded" ? 1e9 : 10, Frequency: kind == "wave" ? 50 : 0));
        if (kind == "output") project.SimulationOutputs.Add(new("R", "ch", "V", 0, 10, "1000", "L1"));
        var setup = new StationSetup
        {
            Devices = missingDevice ? [] : [new() { Id = "device", Name = "Gerät" }],
            Stations = [new() { Name = "P1", Resources = [new() { Name = "R", Device = "device", Channel = "ch" }] }]
        };
        var shell = new ShellViewModel(project: project, stations: new StationCoordinator(setup, new Resources()));
        var page = shell.StationsPage;
        Assert.Contains("R", page.DetailsTitle);
        var actions = page.Details.Single(d => d.Label == "Unterstützte Aktionen").Value;
        Assert.Equal(kind == "wave", actions.Contains("Kurve aufnehmen"));
        Assert.Equal(kind == "output", actions.Contains("Übertragen"));
        Assert.Equal(kind is "wave" or "output" ? "Unterstützt" : "—", page.Details.Single(d => d.Label == "Synchrone Zeitbasis").Value);
        if (kind == "unbounded") Assert.Contains(page.Details, d => d.Value.Contains("unbegrenzt"));
        if (missingDevice) Assert.Contains(page.Details, d => d.Value.Contains("unbekannt"));
        page.SelectedGroup = group;
        page.TimeoutEnabled = true;
        page.TimeoutEnabled = true;
        Assert.Equal("30", page.TimeoutSeconds);
        foreach (var invalid in new[] { "", "0", "-1", "invalid" })
        {
            page.TimeoutSeconds = invalid;
            Assert.NotNull(page.Error);
            Assert.Equal(TimeSpan.FromSeconds(30), group.ResourceTimeout);
        }
        page.TimeoutSeconds = "1,25";
        Assert.Null(page.Error);
        Assert.Equal("1.25", page.TimeoutSeconds);
        page.TimeoutEnabled = false;
        Assert.Null(group.ResourceTimeout);
        Assert.Equal("", page.TimeoutSeconds);
        page.SelectedGroup = null;
        page.TimeoutEnabled = true; page.TimeoutSeconds = "10";
        Assert.False(page.TimeoutEnabled);
        Assert.Equal("", page.GroupPath);
        page.SelectedStation = "missing";
        Assert.Empty(page.Rows);
        Assert.Empty(page.Details);
    }
}
