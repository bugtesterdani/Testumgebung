using System.Text.Json;
namespace Testumgebung.Contracts;

/// <summary>Replayable provenance and envelope violations saved in Waveform.Quality; original samples remain identifiable.</summary>
public sealed record EnvelopeTraceMetadata(string Curve, string Hash, string Channel, double[] OriginalTimes, double[] Violations, int ViolationCount, double MaximumDeviation, bool Complete)
{
    public IReadOnlyList<EnvelopeViolation> Details { get; init; } = [];
    public EnvelopeEarlyStop? EarlyStop { get; init; }
    public double? PlannedDuration { get; init; }
    public const string Prefix = "Grenzband:";
    public string Encode() => Prefix + JsonSerializer.Serialize(this);
    public static EnvelopeTraceMetadata? Decode(string? text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<EnvelopeTraceMetadata>(text[Prefix.Length..]); }
        catch (JsonException) { return null; }
    }
}
public sealed record EnvelopeViolation(double Time, double Value, double Lower, double Upper, double Deviation);

/// <summary>Automatic FAIL trigger, distinct from operator cancellation. Percent refers to the absolute limit value.</summary>
public sealed record EnvelopeEarlyStop(double Percent, string Channel, double Time, double Value, double Lower, double Upper)
{
    public string? AbortCurve { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? AbsoluteDeviation { get; init; }
}
