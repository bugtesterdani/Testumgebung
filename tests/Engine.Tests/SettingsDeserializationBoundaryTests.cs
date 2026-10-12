using System.Text.Json.Nodes;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Engine.Tests;

public sealed class SettingsDeserializationBoundaryTests
{
    private sealed class Protector : ISecretProtector
    {
        public string Protect(string plain) => "test:" + plain;
        public string? Unprotect(string stored) => stored.StartsWith("test:") ? stored[5..] : null;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"database\":null}")]
    [InlineData("{\"outbox\":null}")]
    [InlineData("{\"mqtt\":null}")]
    [InlineData("{\"http\":null}")]
    [InlineData("{\"opcUa\":null}")]
    [InlineData("{\"database\":null,\"outbox\":null,\"mqtt\":null,\"http\":null,\"opcUa\":null}")]
    public void Missing_sections_are_restored_to_usable_defaults(string json)
    {
        var store = new SettingsStore("unused.json", new Protector());
        var settings = store.Deserialize(json);
        Assert.NotNull(settings.Database); Assert.NotNull(settings.Outbox); Assert.NotNull(settings.Mqtt); Assert.NotNull(settings.Http); Assert.NotNull(settings.OpcUa);
        Assert.Equal(DatabaseChoice.Simulated, settings.Database.Provider); Assert.Equal(30, settings.Outbox.RetryIntervalSeconds);
        Assert.Equal(8883, settings.Mqtt.Port); Assert.Empty(store.Warnings);
        Assert.NotNull(store.Deserialize(store.Serialize(settings)));
    }

    public static IEnumerable<object[]> Secrets()
    {
        foreach (var section in new[] { "database", "mqtt", "http", "opcUa" })
            foreach (var secret in new string?[] { null, "", "invalid", "test:", "test:password" }) yield return [section, secret!];
    }

    [Theory, MemberData(nameof(Secrets))]
    public void Secret_decryption_warnings_and_values_match_each_destination(string section, string? secret)
    {
        var key = section == "database" ? "connectionString" : section == "http" ? "headerValue" : "password";
        var json = new JsonObject { [section] = new JsonObject { [key] = secret } };
        var store = new SettingsStore("unused.json", new Protector());
        var settings = store.Deserialize(json.ToJsonString());
        var actual = section switch { "database" => settings.Database.ConnectionString, "mqtt" => settings.Mqtt.Password, "http" => settings.Http.HeaderValue, _ => settings.OpcUa.Password };
        var expected = secret?.StartsWith("test:") == true ? secret[5..] : section == "database" ? "" : null;
        Assert.Equal(expected, actual);
        Assert.Equal(secret == "invalid" || (section == "database" && secret == "test:") ? 1 : 0, store.Warnings.Count);
    }
}
