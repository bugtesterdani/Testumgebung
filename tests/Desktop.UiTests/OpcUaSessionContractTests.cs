using System.Reflection;
using System.IO;
using Opc.Ua;
using Opc.Ua.Client;
using Testumgebung.Contracts;
using Testumgebung.Instruments;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Results;
using Xunit;

namespace Testumgebung.Desktop.UiTests;

public sealed class OpcUaSessionContractTests
{
    public class SessionPeer : DispatchProxy
    {
        public string? Failure;
        public bool BadStatus;
        public bool Disposed;
        public bool Connected = true;
        public readonly TaskCompletionSource CloseCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<StatusCode>? CloseCompletion;
        public object? Value = 42;
        public object?[]? LastArguments;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            switch (method!.Name)
            {
                case "get_Connected": return Connected;
                case "Dispose": Disposed = true; return null;
                case "CloseAsync": CloseCalled.TrySetResult(); return CloseCompletion?.Task ?? Task.FromResult(StatusCodes.Good);
                case "ReadAsync":
                    Check(); LastArguments = arguments;
                    return new ValueTask<ReadResponse>(new ReadResponse { Results = [new DataValue(FixtureVariant(Value), BadStatus ? StatusCodes.BadNodeIdUnknown : StatusCodes.Good)] });
                case "WriteAsync":
                    Check(); LastArguments = arguments;
                    return new ValueTask<WriteResponse>(new WriteResponse { Results = [BadStatus ? StatusCodes.BadNotWritable : StatusCodes.Good] });
                case "CallAsync":
                    Check(); LastArguments = arguments;
                    return new ValueTask<CallResponse>(new CallResponse { Results = [new CallMethodResult { StatusCode = BadStatus ? StatusCodes.BadMethodInvalid : StatusCodes.Good, OutputArguments = [FixtureVariant(Value), new Variant("second")] }] });
                default: throw new NotSupportedException(method.Name);
            }
        }
        private void Check()
        {
            if (Failure == "cancel") throw new OperationCanceledException();
            if (Failure == "transport") throw new IOException("fixture transport failure");
        }
    }

    private static Variant FixtureVariant(object? value)
    {
        Assert.True(VariantHelper.TryCastFrom(value, out var variant));
        return variant;
    }

    private static (OpcUaClient Client, SessionPeer Peer) Client()
    {
        var session = DispatchProxy.Create<ISession, SessionPeer>();
        var peer = (SessionPeer)(object)session;
        var client = new OpcUaClient("opc.tcp://fixture", new InstrumentOptions());
        typeof(OpcUaClient).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, session);
        return (client, peer);
    }

    [Fact]
    public async Task Connected_session_reads_writes_calls_and_preserves_node_identifiers()
    {
        var (client, peer) = Client();
        await using (client)
        {
            await client.ConnectAsync(default);
            Assert.Equal(42L, await client.ReadAsync("ns=2;s=Value", default));
            var reads = Assert.IsType<ArrayOf<ReadValueId>>(peer.LastArguments![3]);
            Assert.Equal(NodeId.Parse("ns=2;s=Value"), Assert.Single(reads.ToArray()!).NodeId); Assert.Equal(Attributes.Value, reads[0].AttributeId);
            await client.WriteAsync("ns=2;s=Value", 12L, default);
            var writes = Assert.IsType<ArrayOf<WriteValue>>(peer.LastArguments![1]); Assert.Equal(12L, Assert.Single(writes.ToArray()!).Value.WrappedValue.AsBoxedObject());
            var output = await client.CallAsync("ns=2;s=Object", "ns=2;s=Method", [true, 1L, 1.5, "text", null], default);
            Assert.Equal(new object?[] { 42L, "second" }, output);
            var calls = Assert.IsType<ArrayOf<CallMethodRequest>>(peer.LastArguments![1]);
            Assert.Equal(NodeId.Parse("ns=2;s=Object"), Assert.Single(calls.ToArray()!).ObjectId); Assert.Equal(NodeId.Parse("ns=2;s=Method"), calls[0].MethodId); Assert.Equal(5, calls[0].InputArguments.Count);
        }
        Assert.True(peer.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Dispose_waits_for_async_close_and_releases_session_even_if_close_fails(bool notifier, bool fail)
    {
        var session = DispatchProxy.Create<ISession, SessionPeer>();
        var peer = (SessionPeer)(object)session;
        peer.CloseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IAsyncDisposable resource = notifier
            ? new OpcUaNotifier(new OpcUaNotifyOptions { EndpointUrl = "opc.tcp://fixture", Station = "P1" })
            : new OpcUaClient("opc.tcp://fixture", new InstrumentOptions());
        resource.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(resource, session);
        var disposal = resource.DisposeAsync().AsTask();
        await peer.CloseCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(disposal.IsCompleted);
        Assert.False(peer.Disposed);
        if (fail) peer.CloseCompletion.SetException(new IOException("close failed"));
        else peer.CloseCompletion.SetResult(StatusCodes.Good);
        await disposal;
        Assert.True(peer.Disposed);
    }

    [Theory]
    [InlineData("read", "bad")]
    [InlineData("read", "transport")]
    [InlineData("read", "cancel")]
    [InlineData("write", "bad")]
    [InlineData("write", "transport")]
    [InlineData("write", "cancel")]
    [InlineData("call", "bad")]
    [InlineData("call", "transport")]
    [InlineData("call", "cancel")]
    public async Task Errors_distinguish_rejection_transport_reset_and_cancellation(string operation, string failure)
    {
        var (client, peer) = Client(); await using (client)
        {
            peer.BadStatus = failure == "bad"; peer.Failure = failure;
            async Task Act()
            {
                if (operation == "read") await client.ReadAsync("ns=2;s=Value", default);
                else if (operation == "write") await client.WriteAsync("ns=2;s=Value", 1L, default);
                else await client.CallAsync("ns=2;s=Object", "ns=2;s=Method", [], default);
            }
            if (failure == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(Act);
            else
            {
                var error = await Assert.ThrowsAsync<OpcUaException>(Act);
                Assert.Contains(failure == "bad" ? "abgelehnt" : "fehlgeschlagen", error.Message);
            }
            Assert.Equal(failure == "transport", peer.Disposed);
        }
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("bad")]
    [InlineData("transport")]
    [InlineData("cancel")]
    public async Task Notifier_writes_all_fields_and_resets_after_failed_delivery(string failure)
    {
        var session = DispatchProxy.Create<ISession, SessionPeer>(); var peer = (SessionPeer)(object)session;
        peer.BadStatus = failure == "bad"; peer.Failure = failure;
        var options = new OpcUaNotifyOptions { EndpointUrl = "opc.tcp://fixture", NodePrefix = "ns=2;s=P1.", Station = "P1" };
        await using var notifier = new OpcUaNotifier(options);
        typeof(OpcUaNotifier).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(notifier, session);
        var notification = new RunNotification(RunNotificationKind.Stored, "run", "SN1", DateTimeOffset.UnixEpoch, 1, null);
        Assert.Contains("fixture", notifier.TargetFor(notification));
        Task Send() => (Task)typeof(OpcUaNotifier).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(notifier, [notification, CancellationToken.None])!;
        if (failure == "ok")
        {
            await Send();
            var writes = Assert.IsType<ArrayOf<WriteValue>>(peer.LastArguments![1]);
            Assert.Equal(OpcUaNotifyOptions.FieldNames.Length, writes.Count);
            Assert.Equal(OpcUaNotifyOptions.FieldNames.Select(options.NodeFor), writes.ToArray()!.Select(w => w.NodeId.ToString()));
            Assert.All(writes.ToArray()!, w => Assert.Equal(Attributes.Value, w.AttributeId));
        }
        else if (failure == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(Send);
        else if (failure == "bad") Assert.Contains("Schreiben abgelehnt", (await Assert.ThrowsAsync<InvalidOperationException>(Send)).Message);
        else await Assert.ThrowsAsync<IOException>(Send);
        Assert.Equal(failure is "bad" or "transport", peer.Disposed);
    }
}
