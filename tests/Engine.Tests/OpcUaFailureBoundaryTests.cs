using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class OpcUaFailureBoundaryTests
{
    private sealed class Peer : IHardwareAdapter, IOpcUaClient
    {
        public string Name => "OPC fixture"; public object? Value; public bool FailConnect;
        public ChannelCapabilities GetCapabilities(string channel) => new("", -100, 100);
        public Task<double> ReadAsync(string channel, CancellationToken cancellationToken) => Task.FromResult(0d);
        public Task WriteAsync(string channel, double value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken cancellationToken) => FailConnect ? Task.FromException(new OpcUaException("offline")) : Task.CompletedTask;
        Task<object?> IOpcUaClient.ReadAsync(string nodeId, CancellationToken cancellationToken) => Task.FromResult(Value);
        public Task WriteAsync(string nodeId, object? value, CancellationToken cancellationToken) { Value = value; return Task.CompletedTask; }
        public Task<IReadOnlyList<object?>> CallAsync(string objectNodeId, string methodNodeId, IReadOnlyList<object?> arguments, CancellationToken cancellationToken) => Task.FromResult(arguments);
        public Task<IOpcUaSubscription> SubscribeAsync(string nodeId, int samplingIntervalMs, CancellationToken cancellationToken) => Task.FromResult<IOpcUaSubscription>(new Subscription(nodeId));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Subscription(string node) : IOpcUaSubscription
        {
            public string NodeId => node; public object? Current => null; public int Changes => 0;
            public Task<object?> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromException<object?>(new CommunicationTimeoutException("no change", []));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("read-node")]
    [InlineData("write-node")]
    [InlineData("write-value")]
    [InlineData("method-object")]
    [InlineData("method-name")]
    [InlineData("subscribe-node")]
    [InlineData("subscribe-timeout")]
    [InlineData("offline")]
    [InlineData("wrong-device")]
    public async Task Opc_errors_produce_error_results_instead_of_false_success(string scenario)
    {
        var parameters = new Dictionary<string, string> { ["Ressource"] = "OPC", ["Knoten"] = "ns=2;s=A", ["Objekt"] = "ns=2;s=O", ["Methode"] = "ns=2;s=M" };
        parameters["Aktion"] = scenario.StartsWith("write") ? "Schreiben" : scenario.StartsWith("method") ? "Methode aufrufen" : scenario.StartsWith("subscribe") ? "Abonnieren (auf Änderung warten)" : "Lesen";
        if (scenario.EndsWith("node")) parameters["Knoten"] = ""; if (scenario == "method-object") parameters["Objekt"] = ""; if (scenario == "method-name") parameters["Methode"] = "";
        var peer = new Peer { FailConnect = scenario == "offline" }; var adapter = scenario == "wrong-device" ? (IHardwareAdapter)new Plain() : peer;
        var step = BlockCatalog.Default.Create(BusBlocks.OpcUaAccess, "op", "OPC", parameters); var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions { Hardware = new HardwareRegistry().Bind("OPC", adapter, "") }); await run.RunAsync().Within();
        var result = Assert.Single(run.Steps("op")); Assert.Equal(ResultRating.Error, result.Rating); Assert.NotEmpty(result.Message!);
    }
    private sealed class Plain : IHardwareAdapter
    {
        public string Name => "not OPC"; public ChannelCapabilities GetCapabilities(string channel) => new("", 0, 1);
        public Task<double> ReadAsync(string channel, CancellationToken cancellationToken) => Task.FromResult(0d); public Task WriteAsync(string channel, double value, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(VarType.Int, "42", 42L)]
    [InlineData(VarType.Int, 42.6, 43L)]
    [InlineData(VarType.Double, "1,25", 1.25)]
    [InlineData(VarType.Double, "not numeric", null)]
    [InlineData(VarType.Bool, "true", true)]
    [InlineData(VarType.String, true, "1")]
    [InlineData(VarType.String, 42L, "42")]
    [InlineData(VarType.String, 1.25, "1.25")]
    public async Task Read_target_converts_worker_scalars_to_declared_variable_type(VarType type, object read, object? expected)
    {
        var peer = new Peer { Value = read }; var step = BlockCatalog.Default.Create(BusBlocks.OpcUaAccess, "op", "OPC", new Dictionary<string, string> { ["Ressource"] = "OPC", ["Knoten"] = "ns=2;s=A", ["Ziel"] = "Output", ["Untergrenze"] = "0" });
        var verify = new StepDefinition("verify", "verify", new DelegateAction(c => { Assert.Equal(expected, c.Get("Output")); return Task.FromResult(StepOutcome.Pass()); }));
        var run = new TestRun(Test(Group("loop").Var("Output", type).Add(step, verify)), new TestRunOptions { Hardware = new HardwareRegistry().Bind("OPC", peer, "") }); await run.RunAsync().Within();
        Assert.Equal(expected is null ? ResultRating.Error : read is double or long ? ResultRating.Pass : ResultRating.NResult, Assert.Single(run.Steps("op")).Rating);
        if (expected is not null) Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("verify")).Rating);
    }
    [Theory]
    [InlineData("true;false;1;1.25;text", "True;False;1;1.25;text")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public async Task Code_method_call_parses_scalar_arguments(string? arguments, string expected)
    {
        var peer = new Peer(); var actual = await CodeDevices.ExecuteAsync(peer, "", "opcua.call", new Dictionary<string, object?> { ["object"] = "O", ["method"] = "M", ["args"] = arguments }, default); Assert.Equal(expected, actual);
    }
}
