/**
 * @file
 * @brief Szenario-Datei (*.tsim.json): Werkereingaben, Simulationswerte und erwartetes Ergebnis eines simulierten Laufs.
 */

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Testumgebung.Cli;

/// <summary>Ein simulierter Lauf eines Prüfprogramms mit festgelegten Eingaben und Erwartungen.</summary>
public sealed class Scenario
{
    public const string FormatName = "testumgebung.szenario";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = "";
    public int FormatVersion { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Projektdatei, relativ zur Szenario-Datei.</summary>
    public string Project { get; set; } = "";

    /// <summary>Anzahl Durchläufe der Loop-Gruppe (Prüflinge); ersetzt die Wiederholung des Loops nur für diesen Lauf.</summary>
    public int Duts { get; set; } = 1;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Startwert für das Rauschen der Projektsimulation (reproduzierbar).</summary>
    public int Seed { get; set; } = 1;

    /// <summary>true: echte Wartezeiten; false (Standard): virtuelle Uhr, Wartezeiten vergehen sofort.</summary>
    public bool RealTime { get; set; }

    /// <summary>Simulierte Eingänge je abstraktem Ressourcennamen: Zahl (fest) oder Liste (je Lesezugriff der nächste Wert).</summary>
    public Dictionary<string, JsonElement> Simulation { get; set; } = new(StringComparer.Ordinal);

    public List<ScenarioAnswer> Answers { get; set; } = [];

    /// <summary>Werkerabbruch, sobald ein Schritt (ID) zum n-ten Mal startet.</summary>
    public ScenarioAbort? Abort { get; set; }

    public ScenarioExpectation Expect { get; set; } = new();

    [JsonIgnore] public string SourcePath { get; private set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Scenario Load(string path)
    {
        Scenario scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("leere Datei");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Szenario {Path.GetFileName(path)}: ungültiges JSON – {ex.Message}");
        }
        scenario.SourcePath = Path.GetFullPath(path);
        var problems = scenario.Check();
        if (problems.Count > 0)
            throw new InvalidDataException($"Szenario {Path.GetFileName(path)}: " + string.Join(" ", problems));
        return scenario;
    }

    /// <summary>Projektdatei als absoluter Pfad.</summary>
    public string ProjectPath => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath)!, Project));

    private List<string> Check()
    {
        var problems = new List<string>();
        if (Format != FormatName) problems.Add($"„format“ muss „{FormatName}“ sein.");
        if (FormatVersion != CurrentVersion) problems.Add($"„formatVersion“ muss {CurrentVersion} sein.");
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("„name“ fehlt.");
        if (string.IsNullOrWhiteSpace(Project)) problems.Add("„project“ fehlt (Projektdatei relativ zum Szenario).");
        if (Duts is < 1 or > 1000) problems.Add("„duts“ muss zwischen 1 und 1000 liegen.");
        if (TimeoutSeconds is < 1 or > 3600) problems.Add("„timeoutSeconds“ muss zwischen 1 und 3600 liegen.");
        CheckCollections(problems);
        CheckAnswers(problems);
        CheckExpectations(problems);
        if (Abort is { } abort && (string.IsNullOrWhiteSpace(abort.AtStep) || abort.Occurrence < 1))
            problems.Add("„abort“ braucht „atStep“ und „occurrence“ ≥ 1.");
        return problems;
    }

    private void CheckCollections(List<string> problems)
    {
        if (Simulation is null || Answers is null || Expect is null)
            throw new InvalidDataException("„simulation“, „answers“ und „expect“ dürfen nicht null sein.");
        if (Expect.Steps is null || Expect.Duts is null || Answers.Any(a => a is null)
            || Expect.Steps.Any(s => s is null) || Expect.Duts.Any(d => d is null))
            throw new InvalidDataException("Szenario-Listen und ihre Einträge dürfen nicht null sein.");
        foreach (var (name, value) in Simulation)
            if (!SimulationValues(value, out _)) problems.Add($"„simulation.{name}“ muss eine endliche Zahl oder eine Liste endlicher Zahlen sein.");
    }

    private void CheckAnswers(List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in Answers)
        {
            if (string.IsNullOrWhiteSpace(a.Step) || !seen.Add(a.Step)) problems.Add("Antwort ohne eindeutigen „step“.");
            if ((a.Value is null) == (a.Values is null)) problems.Add($"Antwort für „{a.Step}“: genau eines von „value“ oder „values“ angeben.");
            if (a.Values is { Count: 0 }) problems.Add($"Antwort für „{a.Step}“: „values“ ist leer.");
            var values = a.Values ?? (a.Value is { } v ? [v] : []);
            foreach (var value in values) _ = AnswerText(value);
        }
    }

    private void CheckExpectations(List<string> problems)
    {
        foreach (var step in Expect.Steps) CheckStepExpectation(step, problems);
        foreach (var rating in Expect.Duts.Select(d => d.Rating).Append(Expect.Status?.Split('+')[0]).Concat(Expect.Steps.Select(s => s.Rating)))
            if (rating is not null && Ratings.Parse(rating.Trim()) is null) problems.Add($"Unbekannte Bewertung „{rating}“ (PASS, FAIL, ERROR, NRESULT).");
        if (Expect.Status is { } status)
        {
            var parts = status.Split('+', StringSplitOptions.TrimEntries);
            if (parts.Length > 2 || (parts.Length == 2 && parts[1] != "WERROR"))
                problems.Add("Gesamtstatus erlaubt nur den Zusatz + WERROR.");
        }
    }

    private static void CheckStepExpectation(StepExpectation s, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(s.Step)) problems.Add("Erwartung ohne „step“.");
        if (s.Rating is null && s.Value is null && s.Count is null) problems.Add($"Erwartung für „{s.Step}“: „rating“, „value“ oder „count“ angeben.");
        if (s.Occurrence is < 1 || s.Count is < 0 || s.Tolerance < 0 || !double.IsFinite(s.Tolerance))
            problems.Add($"Erwartung für „{s.Step}“: ungültige occurrence, count oder tolerance.");
        if (s.Value is { } value) _ = AnswerText(value);
    }

    public static bool SimulationValues(JsonElement element, out IReadOnlyList<double> values)
    {
        values = [];
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
            values = [number];
            return true;
        }
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0) return false;
        var list = new List<double>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
            list.Add(number);
        }
        values = list;
        return true;
    }

    /// <summary>JSON-Wert als Text, wie ihn ein Werker eintippen würde (Zahlen mit Punkt, Bool als true/false).</summary>
    public static string AnswerText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new InvalidDataException($"Antwortwert {element.GetRawText()} ist weder Text, Zahl noch Bool."),
    };
}

/// <summary>Werkereingabe für einen Schritt (ID oder Name): ein fester Wert oder der Reihe nach verbrauchte Werte.</summary>
public sealed class ScenarioAnswer
{
    public string Step { get; set; } = "";
    public JsonElement? Value { get; set; }
    public List<JsonElement>? Values { get; set; }
}

public sealed class ScenarioAbort
{
    public string AtStep { get; set; } = "";
    public int Occurrence { get; set; } = 1;
}

public sealed class ScenarioExpectation
{
    /// <summary>Gesamtergebnis, z. B. „PASS“, „FAIL“ oder „PASS + WERROR“.</summary>
    public string? Status { get; set; }
    public List<DutExpectation> Duts { get; set; } = [];
    public List<StepExpectation> Steps { get; set; } = [];
}

public sealed class DutExpectation
{
    public string? DutId { get; set; }
    public string? Rating { get; set; }
}

/// <summary>Erwartung an einen Schritt (ID): Bewertung, Messwert (mit Toleranz) und/oder Anzahl Ausführungen.</summary>
public sealed class StepExpectation
{
    public string Step { get; set; } = "";
    public string? Rating { get; set; }
    public JsonElement? Value { get; set; }
    public double Tolerance { get; set; }
    public int? Count { get; set; }

    /// <summary>Nur die n-te Ausführung (1-basiert) prüfen; ohne Angabe alle.</summary>
    public int? Occurrence { get; set; }
}

internal static class Ratings
{
    public static Testumgebung.Contracts.ResultRating? Parse(string text) => text.Trim().ToUpperInvariant() switch
    {
        "PASS" => Testumgebung.Contracts.ResultRating.Pass,
        "FAIL" => Testumgebung.Contracts.ResultRating.Fail,
        "ERROR" => Testumgebung.Contracts.ResultRating.Error,
        "NRESULT" => Testumgebung.Contracts.ResultRating.NResult,
        _ => null,
    };

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
