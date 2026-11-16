using System.Net;
using System.Net.Http.Json;
using Testumgebung.Contracts;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Engine.Tests;

public sealed class RemoteSettingsClientTests
{
    private static readonly string Token = new('f', 64);
    private static readonly RemoteStationSettings Initial = new(false, "", "", 0, 0, false, false);

    [Theory]
    [InlineData(null, "lab", true)]
    [InlineData("http://example.test", "lab", true)]
    [InlineData("https://user@example.test", "lab", true)]
    [InlineData("https://example.test/?x=1", "lab", true)]
    [InlineData("https://example.test/#test", "lab", true)]
    [InlineData("https://example.test", "../../lab", true)]
    [InlineData("https://example.test", null, true)]
    [InlineData("https://example.test", "lab", false)]
    public void Agent_requires_trusted_transport_id_and_token(string? address, string? id, bool validToken)
        => Assert.Null(RemoteSettingsClient.TryCreate(address, id, validToken ? Token : "short"));

    [Fact]
    public async Task Reports_current_settings_then_retries_acknowledgment_without_applying_twice()
    {
        var desired = Initial with { ErrorStreak = 3 };
        var handler = new FakeTransport(
            new(HttpStatusCode.OK) { Content = JsonContent.Create(new RemoteSettingsChange(7, Initial, desired)) },
            new(HttpStatusCode.ServiceUnavailable), new(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler);
        var client = RemoteSettingsClient.TryCreate("https://example.test/", "lab_1", Token, http)!;
        var current = Initial;
        var changes = new List<long>();
        var errors = new List<Exception>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await client.PollAsync(() => new(current, ["P1", "P2"]), change =>
            {
                changes.Add(change.Revision);
                current = change.Desired;
                return "applied";
            }, errors.Add);
        }
        Assert.Equal([7L], changes);
        Assert.IsType<HttpRequestException>(Assert.Single(errors));
        Assert.Equal(new RemoteSettingsReport(Initial, handler.Reports[0].Stations), handler.Reports[0]);
        Assert.Equal((7L, "applied", desired), (handler.Reports[1].Revision, handler.Reports[1].Status, handler.Reports[1].Current));
        Assert.Equal(handler.Reports[1].Revision, handler.Reports[2].Revision);
        Assert.Equal(handler.Reports[1].Status, handler.Reports[2].Status);
        Assert.Equal(handler.Reports[1].Current, handler.Reports[2].Current);
    }

    private sealed class FakeTransport(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public List<RemoteSettingsReport> Reports { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://example.test/agent/lab_1", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            Reports.Add((await request.Content!.ReadFromJsonAsync<RemoteSettingsReport>(cancellationToken))!);
            return responses[Reports.Count - 1];
        }
    }
}
