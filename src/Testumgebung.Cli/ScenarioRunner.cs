/**
 * @file
 * @brief Führt ein Prüfprogramm mit einem Szenario in der Projektsimulation aus und vergleicht mit den Erwartungen.
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Persistence;

namespace Testumgebung.Cli;

public static class ScenarioRunner
{
    /// <summary>Simuliert das Szenario mit dem (bereits geprüften) Projekt und trägt die Prüfpunkte in den Bericht ein.</summary>
    public static async Task RunAsync(Scenario scenario, TestProject project, ICodeRuntime runtime, Report report, bool printTrace)
    {
        var suite = $"{ProjectChecks.SuiteName(scenario.SourcePath)} ({scenario.Name})";
        var trace = new StringBuilder();
        var problems = new ConcurrentQueue<string>();

        // Nur für diesen Lauf: begrenzte Zahl Prüflinge statt endlosem Werkerbetrieb.
        project.Definition.Loop.Repetition = Repetition.Times(scenario.Duts);
        var (adapter, registry) = SampleProjects.CreateSimulation(project, scenario.Seed);
        foreach (var (name, element) in scenario.Simulation)
        {
            var resource = project.Simulation.FirstOrDefault(resource2 => resource2.Name == name);
            if (resource is null)
            {
                report.Fail(suite, "Simulation", $"Ressource „{name}“ ist im Projekt nicht unter „simulation“ angelegt " +
                    $"(vorhanden: {string.Join(", ", project.Simulation.Select(resource2 => resource2.Name))}).");
                return;
            }
            Scenario.SimulationValues(element, out var values);
            var next = 0;
            var gate = new object();
            adapter.SetInput(resource.Channel, () => { lock (gate) return values[Math.Min(next++, values.Count - 1)]; });
        }

        var op = new ScenarioOperator(scenario, problems, trace);
        var run = new TestRun(project.Definition, new TestRunOptions
        {
            Hardware = registry,
            Operator = op,
            CodeRuntime = runtime,
            StationName = "Simulation",
            Clock = scenario.RealTime ? SystemClock.Instance : new VirtualClock(),
        }, runId: "Szenario");
        op.Run = run;

        var starts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        run.EventRaised += engineEvent =>
        {
            Trace(trace, engineEvent);
            if (scenario.Abort is { } abort && engineEvent is StepStarted started && started.NodeId == abort.AtStep
                && starts.AddOrUpdate(started.NodeId, 1, (_, count) => count + 1) == abort.Occurrence)
            {
                trace.AppendLine($"Szenario: Werkerabbruch bei {abort.AtStep} (#{abort.Occurrence})");
                run.RequestAbort();
            }
        };

        RunStatus? status = null;
        var task = Task.Run(run.RunAsync);
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(scenario.TimeoutSeconds))).ConfigureAwait(false);
        if (finished != task)
        {
            problems.Enqueue($"Lauf nach {scenario.TimeoutSeconds} s nicht beendet – Werkerabbruch ausgelöst.");
            run.RequestAbort();
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != task)
            {
                run.RequestAbort(); // zweiter Abbruch beendet auch laufende Exit-Gruppen
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            }
        }
        if (task.IsCompletedSuccessfully)
        {
            status = task.Result;
        }
        else if (task.IsFaulted)
        {
            problems.Enqueue("Lauf abgebrochen: " + task.Exception!.GetBaseException().Message);
        }

        string output;
        lock (trace) output = trace.ToString();
        if (printTrace) Console.Write(output);
        if (problems.IsEmpty && status is not null) report.Pass(suite, "Lauf", status.ToString(), output);
        else report.Fail(suite, "Lauf", string.Join(Environment.NewLine, problems.DefaultIfEmpty("Lauf hängt (keine Rückkehr der Engine).")), output);

        Compare(scenario, suite, run.Events, status, report);
    }

    private static void Compare(Scenario scenario, string suite, IReadOnlyList<EngineEvent> events, RunStatus? status, Report report)
    {
        var expect = scenario.Expect;
        if (expect.Status is { } wanted)
        {
            var actual = status?.ToString() ?? "kein Ergebnis";
            if (StatusMatches(wanted, status)) report.Pass(suite, "Gesamtergebnis", actual);
            else report.Fail(suite, "Gesamtergebnis", $"erwartet {wanted}, erhalten {actual}");
        }

        CompareDuts(expect, suite, events, report);
        CompareSteps(expect, suite, events, report);
    }

    private static void CompareDuts(ScenarioExpectation expect, string suite, IReadOnlyList<EngineEvent> events, Report report)
    {
        var duts = events.OfType<DutRunCompleted>().ToList();
        if (expect.Duts.Count > 0)
        {
            if (duts.Count != expect.Duts.Count)
                report.Fail(suite, "Prüflinge", $"erwartet {expect.Duts.Count} abgeschlossene Prüflinge, erhalten {duts.Count} ({Describe(duts)})");
            for (var index = 0; index < Math.Min(duts.Count, expect.Duts.Count); index++)
            {
                var dutExpectation = expect.Duts[index];
                var dutRunCompleted = duts[index];
                var wrong = new List<string>();
                if (dutExpectation.DutId is not null && dutExpectation.DutId != dutRunCompleted.DutId) wrong.Add($"Kennung erwartet „{dutExpectation.DutId}“, erhalten „{dutRunCompleted.DutId}“");
                if (dutExpectation.Rating is not null && !StatusMatches(dutExpectation.Rating, dutRunCompleted.Status)) wrong.Add($"Ergebnis erwartet {dutExpectation.Rating}, erhalten {dutRunCompleted.Status}");
                var name = $"Prüfling {index + 1}";
                if (wrong.Count == 0) report.Pass(suite, name, $"{dutRunCompleted.DutId}: {dutRunCompleted.Status}");
                else report.Fail(suite, name, string.Join("; ", wrong));
            }
        }

    }

    private static void CompareSteps(ScenarioExpectation expect, string suite, IReadOnlyList<EngineEvent> events, Report report)
    {
        var completed = events.OfType<StepCompleted>().ToList();
        foreach (var stepExpectation in expect.Steps)
        {
            CompareStep(stepExpectation, suite, completed, report);
        }
    }

    private static void CompareStep(StepExpectation stepExpectation, string suite, List<StepCompleted> completed, Report report)
    {
        var name = $"Schritt {stepExpectation.Step}" + (stepExpectation.Occurrence is { } offset ? $" #{offset}" : "");
        var all = completed.Where(stepCompleted => stepCompleted.NodeId == stepExpectation.Step).ToList();
        var wrong = new List<string>();
        if (stepExpectation.Count is { } count && all.Count != count) wrong.Add($"{all.Count}× ausgeführt statt {count}×");
        var selected = stepExpectation.Occurrence is { } count2 ? all.Skip(count2 - 1).Take(1).ToList() : all;
        if (selected.Count == 0 && (stepExpectation.Rating is not null || stepExpectation.Value is not null))
        {
            wrong.Add(all.Count == 0 ? "nicht ausgeführt" : $"nur {all.Count}× ausgeführt");
        }
        foreach (var stepCompleted in selected) CompareStepResult(stepExpectation, stepCompleted, wrong);
        if (wrong.Count == 0) report.Pass(suite, name, selected.Count == 1 ? $"{selected[0].Rating.ToDisplay()} {Text(selected[0].Value)}" : $"{selected.Count}×");
        else report.Fail(suite, name, string.Join("; ", wrong.Distinct()));
    }


    private static void CompareStepResult(StepExpectation expected, StepCompleted actual, List<string> problems)
    {
        if (expected.Rating is not null && Ratings.Parse(expected.Rating) != actual.Rating)
            problems.Add($"Bewertung {actual.Rating.ToDisplay()} statt {expected.Rating}{(actual.Message is null ? "" : $" ({actual.Message})")}");
        if (expected.Value is { } value && !SameValue(value, actual.Value, expected.Tolerance))
            problems.Add($"Wert {Text(actual.Value)} statt {value.GetRawText()}");
    }

    private static bool SameValue(JsonElement expected, object? actual, double tolerance)
    {
        return expected.ValueKind switch
        {
            JsonValueKind.Number => SameNumber(expected, actual, tolerance),
            JsonValueKind.True => actual is true,
            JsonValueKind.False => actual is false,
            JsonValueKind.String => string.Equals(Text(actual), expected.GetString(), StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool SameNumber(JsonElement expected, object? actual, double tolerance)
    {
        if (actual is not IConvertible || actual is string or bool or DateTime) return false;
        var number = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
        return Math.Abs(number - expected.GetDouble()) <= Math.Max(tolerance, 1e-9);
    }

    private static string Text(object? value) => value switch
    {
        null => "–",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool StatusMatches(string wanted, RunStatus? status)
    {
        if (status is not { } actual) return false;
        if (Normalize(wanted) == Normalize(actual.Outcome)) return true;
        // Old scenarios may additionally assert the diagnostic rating that preceded the abort.
        var parts = wanted.Split('+', StringSplitOptions.TrimEntries);
        return actual.OperatorAbort && parts.Length == 2 && parts[1] == "WERROR" && Normalize(parts[0]) == actual.Rating.ToDisplay();
    }

    private static string Normalize(string text) => string.Join(" ", text.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Describe(IEnumerable<DutRunCompleted> duts) => string.Join(", ", duts.Select(dutRunCompleted => $"{dutRunCompleted.DutId}: {dutRunCompleted.Status}"));

    private static void Trace(StringBuilder trace, EngineEvent engineEvent)
    {
        lock (trace)
        {
            switch (engineEvent)
            {
                case StepCompleted stepCompleted:
                    trace.AppendLine(DescribeStep(stepCompleted));
                    break;
                case DutRunStarted dutRunStarted:
                    trace.AppendLine($"Prüfling startet ({dutRunStarted.NodeId})");
                    break;
                case DutRunCompleted dutRunCompleted:
                    trace.AppendLine($"Prüfling {dutRunCompleted.DutId}: {dutRunCompleted.Status}");
                    break;
                case NodeSkipped skipped:
                    trace.AppendLine($"  Element {skipped.NodeId} übersprungen: {skipped.Reason}");
                    break;
                case GroupNotEntered groupNotEntered:
                    trace.AppendLine($"  Gruppe {groupNotEntered.NodeId} nicht betreten: {groupNotEntered.Reason}");
                    break;
                case Diagnostic diagnostic:
                    trace.AppendLine($"  Diagnose {diagnostic.NodeId}: {diagnostic.Message}");
                    break;
                case OperatorAbortRequested operatorAbortRequested:
                    trace.AppendLine($"Werkerabbruch #{operatorAbortRequested.Number}: {operatorAbortRequested.Effect}");
                    break;
                case ResourceNotAcquired resourceNotAcquired:
                    trace.AppendLine($"  Ressourcen {string.Join(", ", resourceNotAcquired.Resources)} nicht erhalten: {resourceNotAcquired.Reason}");
                    break;
            }
        }
    }

    private static string DescribeStep(StepCompleted stepCompleted)
    {
        var limits = stepCompleted.LowLimit is null && stepCompleted.HighLimit is null ? "" : $" [{Text(stepCompleted.LowLimit)} … {Text(stepCompleted.HighLimit)}]";
        return $"  {stepCompleted.Rating.ToDisplay(),-7} {stepCompleted.NodeId}: {Text(stepCompleted.Value)}{(stepCompleted.Unit is null ? "" : " " + stepCompleted.Unit)}{limits}" +
               (stepCompleted.Message is { Length: > 0 } ? $" – {stepCompleted.Message}" : "") + (stepCompleted.Cancelled ? " (abgebrochen)" : "");
    }

    /// <summary>Beantwortet Werkerinteraktionen aus dem Szenario; fehlende Antworten beenden den Lauf mit Werkerabbruch.</summary>
    private sealed class ScenarioOperator(Scenario scenario, ConcurrentQueue<string> problems, StringBuilder trace) : IOperatorInterface
    {
        private readonly ConcurrentDictionary<ScenarioAnswer, int> _used = new();

        public TestRun? Run { get; set; }

        public Task<object?> AskAsync(string runId, OperatorPrompt prompt, CancellationToken cancellationToken)
        {
            var stepId = prompt.DisplayId.Split('#')[0];
            var answer = scenario.Answers.FirstOrDefault(scenarioAnswer => scenarioAnswer.Step == stepId) ?? scenario.Answers.FirstOrDefault(scenarioAnswer => scenarioAnswer.Step == prompt.StepName);
            if (!prompt.ExpectsValue)
            {
                Log($"  Werker bestätigt {stepId}: {prompt.Text}");
                return Task.FromResult<object?>(null);
            }
            if (answer is null)
            {
                return Missing($"Keine Antwort im Szenario für Werkereingabe „{stepId}“ ({prompt.Kind}: {prompt.Text}). " +
                               $"In „answers“ ergänzen: {{\"step\": \"{stepId}\", \"value\": …}}");
            }
            JsonElement value;
            if (answer.Value is { } fixedValue)
            {
                value = fixedValue;
            }
            else
            {
                var index = _used.AddOrUpdate(answer, 0, (_, count) => count + 1);
                if (index >= answer.Values!.Count)
                    return Missing($"Antworten für „{answer.Step}“ aufgebraucht ({answer.Values.Count} Werte, {index + 1}. Abfrage).");
                value = answer.Values[index];
            }
            var text = Scenario.AnswerText(value);
            Log($"  Werker {stepId}: {prompt.Text} â†’ {text}");
            return Task.FromResult<object?>(text);
        }

        private Task<object?> Missing(string message)
        {
            problems.Enqueue(message);
            Log("  " + message);
            Run?.RequestAbort();
            return Task.FromResult<object?>(null);
        }

        private void Log(string line)
        {
            lock (trace) trace.AppendLine(line);
        }
    }
}
