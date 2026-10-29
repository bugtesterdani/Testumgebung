/**
 * @file
 * @brief Bindet Codeaufrufe an Schrittscopes und Ports; Bibliotheksgrenzen bestimmen die sichtbaren Variablen.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Engine.Blocks;

/// <summary>Rückrufe eines Codebausteins auf den Schrittkontext.</summary>
internal sealed class StepCodeHost(IStepContext context, bool allowVariables, CodeDebugCapability capability = CodeDebugCapability.FullLineStepping) : ICodeHost
{
    public object Get(string path)
        => allowVariables
            ? context.Get(path)
            : throw new InvalidOperationException("Bausteingrenze: kein impliziter Zugriff auf äußere Variablen – Eingänge verwenden.");

    public void Set(string path, object? value)
    {
        if (!allowVariables)
            throw new InvalidOperationException("Bausteingrenze: kein impliziter Zugriff auf äußere Variablen – Ausgänge verwenden.");
        context.Set(path, value);
    }

    public Task<double> ReadAsync(string resource) => context.ReadAsync(resource);

    public Task WriteAsync(string resource, double value) => context.WriteAsync(resource, value);

    public Task<object?> AskAsync(OperatorPrompt prompt) => context.AskOperatorAsync(prompt);

    public void Log(string message) => context.Log(message);

    public void ReportLiveOutput(LiveOutputUpdate update) => context.ReportLiveOutput(update);


    public Task<object?> DeviceAsync(string operation, IReadOnlyDictionary<string, object?> arguments) => CodeDevices.ExecuteAsync(context, operation, arguments);

    public Task<CodeDebugStop> DebugAsync(CodeDebugEvent debugEvent) => context.CodeDebugAsync(debugEvent, capability);

    /// <summary>Im eigenen Auswahlablauf aus dem CodeBlock gesetzte Auswahl (Testprogramm + Auftrag/Parameter).</summary>
    public TestSelectionValue? Selection { get; private set; }

    public void SelectTest(string test, string? version, string? order, string? parameters)
        => SelectTest(test, version, order, parameters, null);

    public void SelectTest(string test, string? version, string? order, string? parameters, string? pythonVenv)
        => Selection = new TestSelectionValue(test, version, order, parameters, pythonVenv);
}

internal static class CodeInvocation
{
    private static long _counter;

    public static string NextId(IStepContext ctx) => $"{ctx.RunId}/{ctx.StepId}/{Interlocked.Increment(ref _counter)}";

    public static ICodeRuntime Runtime(IStepContext ctx)
    {
        var runtime = ctx.CodeRuntime ?? throw new CodeExecutionException("Keine Code-Ausführungsumgebung konfiguriert (Worker).");
        var hasVariable = ctx.VisibleVariables().Any(v => v.Name == "PythonVenv" && v.Type == VarType.String);
        return runtime.WithPythonEnvironment(ctx.ProgramPythonVenv, hasVariable ? ctx.Get("PythonVenv") as string : null);
    }

    public static StepOutcome ToOutcome(CodeResult result, IStepContext ctx)
    {
        if (result.Cancelled)
        {
            if (ctx.CancellationToken.IsCancellationRequested) throw new OperationCanceledException(result.Message, ctx.CancellationToken);
            return StepOutcome.Error(result.Message ?? "Code abgebrochen.");
        }
        return new StepOutcome(result.Rating, result.Value, result.Unit, result.LowLimit, result.HighLimit, result.Message);
    }
}

/// <summary>
/// CodeBlock im Ablauf: darf auf die Variablen seines Ordners zugreifen. Sichtbare Variablen werden
/// als Felder (C#) bzw. lokale Namen (Python) übergeben; geänderte Werte werden zurückgeschrieben.
/// Verdeckte äußere Variablen sind über <c>Var("../Name")</c> erreichbar.
/// </summary>
public sealed class CodeBlockAction(CodeLanguage language, string source, TimeSpan abortTimeout) : IStepAction
{
    public CodeLanguage Language { get; } = language;
    public string Source { get; } = source;

    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var runtime = CodeInvocation.Runtime(context);
        var visible = context.VisibleVariables();
        var ports = visible.Select(v => new CodePort(v.Name, v.Type, PortDirection.InOut)).ToList();
        var before = visible.ToDictionary(v => v.Name, v => (object?)context.Get(v.Name), StringComparer.Ordinal);

        var codeHost = new StepCodeHost(context, allowVariables: true, CodeDebug.CapabilityOf(Language));
        var result = await runtime.InvokeAsync(
            new CodeRequest(Language, Source, ports, CodeInvocation.NextId(context), context.CodeDebugConfig) { Assemblies = context.ManagedAssemblies },
            before, codeHost, abortTimeout, context.CancellationToken).ConfigureAwait(false);

        foreach (var (name, value) in result.Values)
        {
            if (before.TryGetValue(name, out var old) && Equals(old, value)) continue;
            context.Set(name, value);
        }
        var outcome = CodeInvocation.ToOutcome(result, context);
        // Eigener Auswahlablauf: hat der CodeBlock SelectTest(...) aufgerufen, ist dessen Auswahl das Schritt­ergebnis.
        if (!result.Cancelled && outcome.Rating != ResultRating.Error && codeHost.Selection is { } selection)
            outcome = outcome with { Value = selection };
        return outcome;
    }
}

/// <summary>
/// Bibliotheksbaustein: typisierte Eingänge aus Ausdrücken (Konstante oder Ordnervariable),
/// Ausgänge in konfigurierte Zielvariablen. Die Namenssuche überschreitet die Bausteingrenze nicht.
/// </summary>
public sealed class LibraryBlockAction(
    string blockId,
    IReadOnlyDictionary<string, string> inputs,
    IReadOnlyDictionary<string, string> outputs,
    TimeSpan abortTimeout) : IStepAction
{
    public string BlockId { get; } = blockId;

    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var block = context.FindLibraryBlock(BlockId)
                    ?? throw new CodeExecutionException($"Baustein '{BlockId}' ist nicht in der Bibliothek.");
        var runtime = CodeInvocation.Runtime(context);
        var resolver = new ContextResolver(context);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var port in block.Ports)
        {
            if (port.Direction == PortDirection.Output) continue;
            if (!inputs.TryGetValue(port.Name, out var text) || string.IsNullOrWhiteSpace(text))
                throw new CodeExecutionException($"Eingang '{port.Name}' ist nicht verbunden.");
            object value;
            try
            {
                value = Expression.Parse(text).Evaluate(resolver);
                values[port.Name] = TypedValue.Coerce(value, port.Type);
            }
            catch (Exception ex) when (ex is ExpressionException or TypeMismatchException or Domain.Scopes.VariableNotFoundException)
            {
                throw new CodeExecutionException($"Eingang '{port.Name}': {ex.Message}");
            }
        }

        var result = await runtime.InvokeAsync(
            new CodeRequest(block.Language, block.Source, block.Ports, CodeInvocation.NextId(context), context.CodeDebugConfig) { Assemblies = context.ManagedAssemblies },
            values, new StepCodeHost(context, allowVariables: false, CodeDebug.CapabilityOf(block.Language)), abortTimeout, context.CancellationToken).ConfigureAwait(false);

        if (!result.Cancelled)
        {
            foreach (var port in block.Ports.Where(p => p.IsOutput))
            {
                var target = port.Direction == PortDirection.InOut
                    ? inputs.GetValueOrDefault(port.Name)
                    : outputs.GetValueOrDefault(port.Name);
                if (string.IsNullOrWhiteSpace(target) || !result.Values.TryGetValue(port.Name, out var v)) continue;
                context.Set(target.Trim(), v);
            }
        }
        var outcome = CodeInvocation.ToOutcome(result, context);
        return outcome with { Message = outcome.Message ?? $"{block.Name} v{block.Version}" };
    }
}

/// <summary>Bindungen eines Bausteinaufrufs auswerten (gemeinsam für Code- und Gruppenbausteine).</summary>
internal static class PortBinding
{
    public static Dictionary<string, object?> Inputs(IStepContext context, IEnumerable<CodePort> ports, IReadOnlyDictionary<string, string> inputs)
    {
        var resolver = new ContextResolver(context);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var port in ports)
        {
            if (port.Direction == PortDirection.Output) continue;
            if (!inputs.TryGetValue(port.Name, out var text) || string.IsNullOrWhiteSpace(text))
                throw new CodeExecutionException($"Eingang '{port.Name}' ist nicht verbunden.");
            try
            {
                values[port.Name] = TypedValue.Coerce(Expression.Parse(text).Evaluate(resolver), port.Type);
            }
            catch (Exception ex) when (ex is ExpressionException or TypeMismatchException or Domain.Scopes.VariableNotFoundException)
            {
                throw new CodeExecutionException($"Eingang '{port.Name}': {ex.Message}");
            }
        }
        return values;
    }

    public static void Outputs(IStepContext context, IEnumerable<CodePort> ports, IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> outputs, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var port in ports.Where(p => p.IsOutput))
        {
            var target = port.Direction == PortDirection.InOut ? inputs.GetValueOrDefault(port.Name) : outputs.GetValueOrDefault(port.Name);
            if (string.IsNullOrWhiteSpace(target) || !values.TryGetValue(port.Name, out var v)) continue;
            context.Set(target.Trim(), v);
        }
    }
}

/// <summary>
/// Grafischer Baustein: der Körper (Gruppe) läuft mit eigenem Schnittstellen-Scope in der Engine –
/// Werkerabbruch, Exit-Gruppen und Haltepunkte gelten wie im Hauptablauf.
/// </summary>
public sealed class GroupBlockAction(
    string blockId,
    IReadOnlyDictionary<string, string> inputs,
    IReadOnlyDictionary<string, string> outputs) : IStepAction
{
    public string BlockId { get; } = blockId;

    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var block = context.FindGroupBlock(BlockId)
                    ?? throw new CodeExecutionException($"Grafischer Baustein '{BlockId}' ist nicht in der Bibliothek.");
        var values = PortBinding.Inputs(context, block.Ports, inputs);
        var (rating, outputValues) = await context.ExecuteGroupBlockAsync(block, values).ConfigureAwait(false);
        context.CancellationToken.ThrowIfCancellationRequested();
        PortBinding.Outputs(context, block.Ports, inputs, outputs, outputValues);
        return new StepOutcome(rating, Message: $"{block.Name} v{block.Version}");
    }
}
