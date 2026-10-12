using System.Text.RegularExpressions;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Persistence.Stations;

/// <summary>Kennungsregel: Muster → Test (und optional Version, Prüfparameter). Kleinere Priorität gewinnt.</summary>
public sealed class SelectionRule
{
    public int Priority { get; set; } = 1;

    /// <summary>Platzhalter: * beliebig, ? ein Zeichen, # eine Ziffer; „re:…“ = regulärer Ausdruck. Groß-/Kleinschreibung egal.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Testkennung = Name des Ablaufs (Release-Name).</summary>
    public string Test { get; set; } = "";

    /// <summary>Leer = aktuell freigegebene Version, sonst eine bestimmte freigegebene Version.</summary>
    public string? Version { get; set; }

    /// <summary>Prüfparameter „Name=Wert;…“ (setzen Variablen gleichen Namens im Prüfling).</summary>
    public string? Parameters { get; set; }

    public bool Matches(string id)
    {
        var p = Pattern.Trim();
        if (p.Length == 0) return false;
        if (p.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Regex.IsMatch(id, p[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        var regex = "^" + string.Concat(p.Select(ch => ch switch
        {
            '*' => ".*",
            '?' => ".",
            '#' => "[0-9]",
            _ => Regex.Escape(ch.ToString()),
        })) + "$";
        return Regex.IsMatch(id, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// Auswahl- und Auftragsablauf eines Prüfplatzes (Abschnitt 5): Methoden einzeln aktivierbar und kombinierbar.
/// Reihenfolge: eigener Auswahlablauf → Kennungsregeln → manuelle Auswahl. Keine eindeutige Zuordnung → kein Start.
/// </summary>
public sealed class SelectionConfig
{
    public bool OrderSession { get; set; } = true;
    public bool UseManual { get; set; } = true;
    public bool UseRules { get; set; } = true;
    public bool UseCustom { get; set; }
    public List<SelectionRule> Rules { get; set; } = [];
    public string NoMatchMessage { get; set; } = "Keine eindeutige Testzuordnung";
    public string AlternativeText { get; set; } = "Manuell auswählen";

    /// <summary>Eigener Auswahlablauf: Projektdatei (.tproj.json) mit dem Baustein „Test auswählen“.</summary>
    public string? CustomProject { get; set; }

    public bool Enabled => UseManual || UseRules || UseCustom;
}

/// <summary>Im Werkerbetrieb verfügbarer Test (freigegeben; im Entwicklungsmodus der Editorstand).</summary>
public sealed record AvailableTest(string Name, string Version, bool IsCurrent, string Origin)
{
    public string Title => $"{Name} v{Version}{(IsCurrent ? "" : " (" + Origin + ")")}";
}

public enum SelectionState
{
    /// <summary>Eindeutig – Start möglich.</summary>
    Unique,
    /// <summary>Mehrere Kandidaten – Werker wählt (falls manuelle Auswahl aktiv).</summary>
    Ambiguous,
    /// <summary>Keine Zuordnung.</summary>
    None,
}

/// <summary>Ergebnis der Zuordnung: Test, Auftrag, Prüfparameter, Herkunft und Begründung.</summary>
public sealed record SelectionOutcome(
    SelectionState State,
    AvailableTest? Test,
    string? Order,
    IReadOnlyDictionary<string, string> Parameters,
    string Source,
    string Message,
    IReadOnlyList<AvailableTest> Candidates,
    bool ManualAllowed)
{
    public string? PythonVenv { get; init; }

    public bool CanStart => State == SelectionState.Unique && Test is not null;
}

public static class TestSelector
{
    public static IReadOnlyDictionary<string, string> ParseParameters(string? text)
        => (text ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2, StringSplitOptions.TrimEntries)).Where(x => x.Length == 2 && x[0].Length > 0)
            .GroupBy(x => x[0]).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);

    /// <summary>Test + Version unter den verfügbaren auflösen (leer/„aktuell“ = aktuelle Freigabe).</summary>
    public static AvailableTest? Resolve(IReadOnlyList<AvailableTest> tests, string test, string? version)
    {
        var candidates = tests.Where(t => string.Equals(t.Name, test.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (string.IsNullOrWhiteSpace(version) || version.Trim().StartsWith("aktuell", StringComparison.OrdinalIgnoreCase))
            return candidates.FirstOrDefault(t => t.IsCurrent);
        return candidates.FirstOrDefault(t => t.Version == version.Trim());
    }

    /// <summary>
    /// Zuordnung für eine Kennung. <paramref name="custom"/> = Ergebnis des eigenen Auswahlablaufs (falls aktiv und gelaufen).
    /// Mehrere Regeltreffer gleicher Priorität mit verschiedenen Zielen = mehrdeutig.
    /// </summary>
    public static SelectionOutcome Select(SelectionConfig config, string id, string? order, IReadOnlyList<AvailableTest> tests, TestSelectionValue? custom = null, string? customError = null)
    {
        var manual = config.UseManual ? tests.Where(t => t.IsCurrent).ToList() : [];
        SelectionOutcome Fail(SelectionState state, string reason, IReadOnlyList<AvailableTest>? candidates = null)
            => new(state, null, order, new Dictionary<string, string>(), "", $"{config.NoMatchMessage}: {reason}",
                candidates ?? manual, config.UseManual);

        if (string.IsNullOrWhiteSpace(id)) return Fail(SelectionState.None, "Kennung fehlt.", []);

        if (config.UseCustom)
        {
            if (customError is not null) return Fail(SelectionState.None, "Eigener Auswahlablauf: " + customError);
            if (custom is not null)
            {
                var resolved = Resolve(tests, custom.Test, custom.Version);
                if (resolved is null)
                    return Fail(SelectionState.None, $"Eigener Auswahlablauf liefert „{custom.Test}“{(string.IsNullOrEmpty(custom.Version) ? "" : " v" + custom.Version)} – nicht freigegeben.");
                return new SelectionOutcome(SelectionState.Unique, resolved, string.IsNullOrWhiteSpace(custom.Order) ? order : custom.Order,
                    ParseParameters(custom.Parameters), "Eigener Auswahlablauf", $"Zuordnung eindeutig: {id} → {resolved.Title}", [resolved], config.UseManual) { PythonVenv = custom.PythonVenv };
            }
        }

        if (config.UseRules)
        {
            var hits = config.Rules.Where(r => r.Matches(id)).OrderBy(r => r.Priority).ToList();
            if (hits.Count > 0)
            {
                var best = hits.Where(r => r.Priority == hits[0].Priority).ToList();
                var targets = best.Select(r => (Rule: r, Test: Resolve(tests, r.Test, r.Version))).ToList();
                var distinct = targets.Select(t => t.Test?.Title ?? $"{t.Rule.Test} {t.Rule.Version}").Distinct().ToList();
                if (distinct.Count > 1)
                    return Fail(SelectionState.Ambiguous, $"Die Kennung passt zu mehreren Tests ({string.Join(", ", distinct)}).",
                        targets.Select(t => t.Test).OfType<AvailableTest>().Distinct().ToList() is { Count: > 0 } c && config.UseManual ? c : manual);
                var (rule, test) = targets[0];
                if (test is null)
                    return Fail(SelectionState.None, $"Regel {rule.Pattern} → „{rule.Test}“{(string.IsNullOrWhiteSpace(rule.Version) ? "" : " v" + rule.Version)} ist nicht freigegeben.");
                return new SelectionOutcome(SelectionState.Unique, test, order, ParseParameters(rule.Parameters),
                    $"Kennungsregel {rule.Pattern} (Priorität {rule.Priority})", $"Zuordnung eindeutig: {id} → {test.Title}", [test], config.UseManual);
            }
        }

        return config.UseManual
            ? Fail(SelectionState.Ambiguous, "Test manuell auswählen.", manual)
            : Fail(SelectionState.None, "Keine Regel passt – ohne manuelle Auswahl kein Start.", []);
    }

    /// <summary>Werker wählt aus den Kandidaten (nur bei aktiver manueller Auswahl).</summary>
    public static SelectionOutcome Manual(SelectionOutcome previous, AvailableTest test, string id)
        => previous.ManualAllowed && previous.Candidates.Contains(test)
            ? previous with
            {
                State = SelectionState.Unique,
                Test = test,
                Source = "Manuell ausgewählt",
                Message = $"Manuell zugeordnet: {id} → {test.Title}",
            }
            : throw new InvalidOperationException("Manuelle Auswahl ist nicht zugelassen.");

    /// <summary>
    /// Eigenen Auswahlablauf ausführen: Projekt mit Vorbelegung Kennung/Auftrag starten, Ergebnis des Bausteins
    /// „Test auswählen“ übernehmen. Ohne diesen Baustein oder bei ERROR → Fehlertext.
    /// </summary>
    public static async Task<(TestSelectionValue? Value, string? Error)> RunCustomAsync(TestProject project, string id, string? order,
        TestRunOptions baseOptions, CancellationToken cancellationToken = default)
    {
        var presets = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Kennung"] = id };
        if (!string.IsNullOrWhiteSpace(order)) presets["Auftrag"] = order;
        var run = new TestRun(project.Definition, new TestRunOptions
        {
            Clock = baseOptions.Clock,
            Resources = baseOptions.Resources,
            Hardware = baseOptions.Hardware,
            Operator = baseOptions.Operator,
            StationName = baseOptions.StationName,
            CodeRuntime = baseOptions.CodeRuntime,
            Presets = presets,
            SessionPresets = presets,
            DutLimit = 1,
        }, runId: $"{baseOptions.StationName.Replace(' ', '-')}-Auswahl-{DateTime.Now:HHmmss}");
        TestSelectionValue? value = null;
        string? error = null;
        run.EventRaised += e =>
        {
            if (e is StepCompleted { Value: TestSelectionValue v } sc)
            {
                if (sc.Rating == ResultRating.Error) error = sc.Message ?? "Baustein „Test auswählen“ meldet ERROR.";
                else value = v;
            }
        };
        using var reg = cancellationToken.Register(run.RequestAbort);
        var status = await run.RunAsync().ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return (null, "abgebrochen");
        if (status.Rating == ResultRating.Error || status.OperatorAbort)
            return (null, "Auswahlablauf endete mit Fehler oder Abbruch.");
        if (value is null && error is null)
            error = status.Rating == ResultRating.Error ? "Auswahlablauf endete mit ERROR." : "Der Auswahlablauf hat keinen Baustein „Test auswählen“ ausgeführt.";
        return (value, error);
    }
}
