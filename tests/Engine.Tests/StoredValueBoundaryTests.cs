using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class StoredValueBoundaryTests
{
    private sealed class TextValue { public override string ToString() => "fixture"; }
    public static IEnumerable<object?[]> Cases()
    {
        yield return [null, null]; yield return [true, true]; yield return [false, false];
        yield return [42L, 42L]; yield return [2.5, 2.5]; yield return ["text", "text"];
        yield return [42, 42L]; yield return [2.5f, 2.5];
        var date = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc); yield return [date, date];
        yield return [new DateTimeOffset(date).ToOffset(TimeSpan.FromHours(2)), date];
        yield return [new Waveform("C", "V", 1, DateTimeOffset.UnixEpoch, [1]), null]; yield return [new WaveformSet([]), null];
        yield return [12.5m, "12.5"]; yield return [new TextValue(), "fixture"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Recorder_converts_scalar_types_invariantly_and_keeps_curves_separate(object? value, object? expected)
    {
        var method = typeof(ResultRecorder).GetMethod("StorableValue", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [value]));
    }

    private static Type Worker => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "workers", "csharp", "Testumgebung.Workers.CSharp.dll")).GetType("Testumgebung.Workers.CSharp.Program", true)!;
    [Theory]
    [InlineData("true", "Bool", true)]
    [InlineData("false", "Bool", false)]
    [InlineData("123.9", "Int", 123L)]
    [InlineData("\"123\"", "Int", 123L)]
    [InlineData("2.5", "Double", 2.5)]
    [InlineData("\"text\"", "String", "text")]
    [InlineData("123", "String", "123")]
    [InlineData("{\"value\":1}", "Unknown", "{\"value\":1}")]
    public void Csharp_worker_reads_wire_values_without_losing_scalar_type(string json, string type, object expected)
    {
        var result = Worker.GetMethod("FromJson", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [JsonNode.Parse(json), type]);
        if (type == "Unknown") Assert.Equal(JsonNode.Parse((string)expected)!.ToJsonString(), result);
        else Assert.Equal(expected, result);
    }

    [Fact]
    public void Csharp_worker_serializes_numeric_dates_enums_and_fallback_values()
    {
        var serialize = Worker.GetMethod("ToJson", BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(object)], null)!;
        var date = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        foreach (var (value, json) in new (object?, string)[] { (null, "null"), (true, "true"), (1, "1"), (1L, "1"), (2.5, "2.5"), (2.5f, "2.5"), ("text", "\"text\""), (double.PositiveInfinity, "\"Infinity\""), (double.NaN, "\"NaN\""), (date, "\"" + date.ToString("O", CultureInfo.InvariantCulture) + "\""), (ResultRating.Pass, "\"Pass\""), (new TextValue(), "\"fixture\"") })
        {
            var node = (JsonNode?)serialize.Invoke(null, [value]); Assert.Equal(json, node?.ToJsonString() ?? "null");
        }
        var readDate = Worker.GetMethod("FromJson", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [JsonValue.Create(date.ToString("O", CultureInfo.InvariantCulture)), "DateTime"]);
        Assert.Equal(date, readDate);
    }
}
