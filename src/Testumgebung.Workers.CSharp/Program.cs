using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace Testumgebung.Workers.CSharp;

/// <summary>
/// Isolierter .NET-Worker. Protokoll: eine JSON-Nachricht pro Zeile über stdin/stdout.
/// <list type="bullet">
/// <item>Host → Worker: compile, invoke, cancel, reply</item>
/// <item>Worker → Host: hello, compiled, call, done, error</item>
/// </list>
/// Nutzercode wird als Methodenrumpf in eine generierte Klasse eingebettet; Variablen sind typisierte Felder.
/// </summary>
public static class Program
{
    private static readonly object WriteLock = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Running = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> Calls = new();
    private static readonly ConcurrentDictionary<string, CompiledBlock> Cache = new();
    private static readonly Lazy<MetadataReference[]> References = new(LoadReferences);
    private static readonly ConcurrentDictionary<Task, byte> Pending = new();
    private static long _callCounter;

    private static void Track(Task task)
    {
        Pending[task] = 0;
        task.ContinueWith(t => Pending.TryRemove(t, out _), TaskScheduler.Default);
    }

    public static async Task<int> Main()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Out = stdout;
        Console.SetOut(Console.Error); // Console.WriteLine im Nutzercode darf das Protokoll nicht stören
        Send(new JsonObject { ["t"] = "hello", ["runtime"] = $"Isolierter .NET-Worker ({Environment.Version})" });

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject msg;
            try
            {
                msg = JsonNode.Parse(line)!.AsObject();
            }
            catch (JsonException)
            {
                continue;
            }
            var type = (string?)msg["t"];
            var id = (string?)msg["id"] ?? "";
            switch (type)
            {
                case "compile":
                    Track(Task.Run(() =>
                    {
                        try { Send(CompileMessage(id, msg)); }
                        catch (Exception ex) { SendError(id, $"{ex.GetType().Name}: {ex.Message}", ex.ToString()); }
                    }));
                    break;
                case "invoke":
                    var cts = new CancellationTokenSource();
                    Running[id] = cts;
                    Track(Task.Run(() => Invoke(id, msg, cts.Token)).ContinueWith(_ => Running.TryRemove(id, out var _), TaskScheduler.Default));
                    break;
                case "cancel":
                    if (Running.TryGetValue(id, out var c)) c.Cancel();
                    break;
                case "reply":
                    if (Calls.TryRemove((string?)msg["cid"] ?? "", out var tcs)) tcs.TrySetResult(msg);
                    break;
                case "exit":
                    return 0;
            }
        }
        // Eingabe geschlossen (Host beendet): laufende Arbeiten kurz abschließen lassen.
        await Task.WhenAny(Task.WhenAll(Pending.Keys), Task.Delay(TimeSpan.FromSeconds(5)));
        return 0;
    }

    private static StreamWriter Out { get; set; } = null!;

    internal static void Send(JsonObject message)
    {
        var text = message.ToJsonString();
        lock (WriteLock) Out.WriteLine(text);
    }

    /// <summary>Synchroner Rückruf an den Host (Variablen, Hardware, Werker, Log).</summary>
    internal static JsonNode? Call(string invocationId, string op, JsonObject args)
    {
        var cid = $"c{Interlocked.Increment(ref _callCounter)}";
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Calls[cid] = tcs;
        Send(new JsonObject { ["t"] = "call", ["id"] = invocationId, ["cid"] = cid, ["op"] = op, ["args"] = args });
        var reply = tcs.Task.GetAwaiter().GetResult()!.AsObject();
        if (reply["error"] is { } error) throw new HostCallException((string?)error ?? "Fehler im Host");
        return reply["value"]?.DeepClone();
    }

    // ------------------------------------------------------------------ Übersetzen

    private sealed record Variable(string Name, string Type);

    private sealed record CompiledBlock(Assembly? Assembly, List<Diagnostic> Diagnostics, Dictionary<string, Variable> Fields);

    private static List<Variable> ReadVariables(JsonObject msg)
        => (msg["vars"] as JsonArray ?? [])
            .Select(v => new Variable((string)v!["name"]!, (string)v["type"]!))
            .ToList();

    private static JsonObject CompileMessage(string id, JsonObject msg)
    {
        var block = Compile((string?)msg["source"] ?? "", ReadVariables(msg), ReadAssemblies(msg));
        var diagnostics = new JsonArray();
        foreach (var d in block.Diagnostics) diagnostics.Add(ToJson(d));
        return new JsonObject
        {
            ["t"] = "compiled",
            ["id"] = id,
            ["ok"] = block.Assembly is not null,
            ["diagnostics"] = diagnostics,
        };
    }

    private static JsonObject ToJson(Diagnostic d)
    {
        var span = d.Location.GetMappedLineSpan();
        var inUserCode = span.Path == "CodeBlock";
        return new JsonObject
        {
            ["line"] = inUserCode ? span.StartLinePosition.Line + 1 : 0,
            ["column"] = inUserCode ? span.StartLinePosition.Character + 1 : 0,
            ["severity"] = d.Severity == DiagnosticSeverity.Error ? "error" : "warning",
            ["message"] = d.GetMessage(System.Globalization.CultureInfo.GetCultureInfo("de-DE")),
            ["code"] = d.Id,
        };
    }

    private static readonly HashSet<string> CSharpTypes = ["Bool", "Int", "Double", "String", "DateTime"];

    private static string ClrType(string type) => type switch
    {
        "Bool" => "bool",
        "Int" => "long",
        "Double" => "double",
        "String" => "string",
        "DateTime" => "global::System.DateTime",
        _ => "object",
    };

    /// <summary>Nur gültige C#-Bezeichner werden Felder; andere Namen bleiben über Var&lt;T&gt;("…") erreichbar.</summary>
    private static bool IsIdentifier(string name)
        => SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

    private sealed record AssemblyImage(string Name, byte[] Data);

    private static List<AssemblyImage> ReadAssemblies(JsonObject msg)
    {
        var result = new List<AssemblyImage>();
        foreach (var item in msg["assemblies"] as JsonArray ?? [])
        {
            var data = Convert.FromBase64String((string?)item!["data"] ?? "");
            using var pe = new PEReader(new MemoryStream(data, writable: false));
            if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly) throw new BadImageFormatException("Keine .NET-DLL: " + item["name"]);
            var metadata = pe.GetMetadataReader();
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (result.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("DLL doppelt: " + name);
            result.Add(new(name, data));
        }
        return result.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    private sealed class DependencyContext(string name, IReadOnlyList<AssemblyImage> assemblies) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var image = assemblies.FirstOrDefault(a => a.Name.Equals(assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            return image is null ? null : LoadFromStream(new MemoryStream(image.Data, writable: false));
        }
    }

    private static CompiledBlock Compile(string source, List<Variable> variables, List<AssemblyImage> assemblies, bool instrument = false)
    {
        var fields = variables.Where(v => IsIdentifier(v.Name) && CSharpTypes.Contains(v.Type)).ToDictionary(v => v.Name);
        var key = Hash(source + "\u0001" + string.Join(";", fields.Values.Select(v => v.Name + ":" + v.Type)) + "\u0001" + (instrument ? "dbg" : "")
            + "\u0001" + string.Join(";", assemblies.Select(a => a.Name + ":" + Convert.ToHexString(SHA256.HashData(a.Data)))));
        return Cache.GetOrAdd(key, _ =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Testumgebung.Code;");
            sb.AppendLine("#pragma warning disable CS0162, CS8321");
            sb.AppendLine("public sealed class __UserBlock : BlockBase");
            sb.AppendLine("{");
            foreach (var f in fields.Values) sb.AppendLine($"    public {ClrType(f.Type)} {f.Name};");
            sb.AppendLine("    public override Outcome Run()");
            sb.AppendLine("    {");
            sb.AppendLine("#line 1 \"CodeBlock\"");
            sb.AppendLine(source);
            sb.AppendLine("#line default");
            sb.AppendLine("        return NResult();");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            var options = new CSharpParseOptions(LanguageVersion.Latest);
            var wrapper = CSharpSyntaxTree.ParseText(sb.ToString(), options, path: "Wrapper.cs", encoding: Encoding.UTF8);
            if (instrument)
            {
                var rewritten = new DebugRewriter().Visit(wrapper.GetRoot());
                wrapper = CSharpSyntaxTree.Create((CSharpSyntaxNode)rewritten, options, "Wrapper.cs", Encoding.UTF8);
            }
            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(Prelude.Source, options, path: "Prelude.cs", encoding: Encoding.UTF8),
                wrapper,
            };
            var compilation = CSharpCompilation.Create(
                "CodeBlock_" + key[..12],
                trees,
                References.Value.Concat(assemblies.Select(a => MetadataReference.CreateFromImage(a.Data))),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable,
                    optimizationLevel: OptimizationLevel.Debug));

            using var pe = new MemoryStream();
            using var pdb = new MemoryStream();
            var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            var diagnostics = result.Diagnostics
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : 1)
                .ToList();
            if (!result.Success) return new CompiledBlock(null, diagnostics, fields);

            pe.Position = 0;
            pdb.Position = 0;
            var context = new DependencyContext("CodeBlock_" + key[..12], assemblies);
            var assembly = context.LoadFromStream(pe, pdb);
            return new CompiledBlock(assembly, diagnostics, fields);
        });
    }

    private static MetadataReference[] LoadReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return tpa
            .Where(p => Path.GetFileName(p).StartsWith("System", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(p).Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(p).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ------------------------------------------------------------------ Ausführen

    private static void Invoke(string id, JsonObject msg, CancellationToken token)
    {
        try
        {
            var variables = ReadVariables(msg);
            var source = (string?)msg["source"] ?? "";
            var assemblies = ReadAssemblies(msg);
            // Diagnose immer am nicht instrumentierten Build (korrekte Zeilennummern); bei Debug den instrumentierten Build ausführen.
            var plain = Compile(source, variables, assemblies);
            if (plain.Assembly is null)
            {
                var first = plain.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
                SendError(id, "Build fehlgeschlagen: " + (first is null ? "unbekannter Fehler" : FormatDiagnostic(first)), null);
                return;
            }
            var debug = msg["debug"] as JsonObject;
            var debugEnabled = debug != null && debug["enabled"] != null && debug["enabled"]!.GetValue<bool>();
            var block = debugEnabled ? Compile(source, variables, assemblies, instrument: true) : plain;
            if (block.Assembly is null) block = plain; // Instrumentierung fehlgeschlagen → ohne Debugger ausführen

            var type = block.Assembly.GetType("__UserBlock")!;
            var instance = Activator.CreateInstance(type)!;
            PopulateFields(type, instance, block, msg);
            var setup = type.GetMethod("__Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)!;
            setup.Invoke(instance, [new Func<string, string, JsonObject, JsonNode?>(Call), id, token]);
            if (debugEnabled) SetupDebugger(type, instance, debug!);

            object outcome;
            try
            {
                outcome = type.GetMethod("Run")!.Invoke(instance, null)!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                var ex = tie.InnerException;
                if ((ex is OperationCanceledException && token.IsCancellationRequested) || ex!.GetType().Name == "DebugStopException")
                {
                    Send(new JsonObject { ["t"] = "done", ["id"] = id, ["cancelled"] = true, ["rating"] = "NResult" });
                    return;
                }
                SendError(id, $"{ex.GetType().Name}: {ex.Message}{UserLine(ex)}", ex.ToString());
                return;
            }

            SendOutcome(id, outcome, block, type, instance, token);
        }
        catch (Exception ex)
        {
            SendError(id, $"{ex.GetType().Name}: {ex.Message}", ex.ToString());
        }
    }

    private static void PopulateFields(Type type, object instance, CompiledBlock block, JsonObject msg)
    {
        var values = msg["values"] as JsonObject ?? [];
        foreach (var (name, variable) in block.Fields)
        {
            if (!values.TryGetPropertyValue(name, out var node) || node is null) continue;
            type.GetField(name)!.SetValue(instance, FromJson(node, variable.Type));
        }
    }

    private static void SendOutcome(string id, object outcome, CompiledBlock block, Type type, object instance, CancellationToken token)
    {
        var result = new JsonObject { ["t"] = "done", ["id"] = id };
        var outcomeType = outcome.GetType();
        result["rating"] = outcomeType.GetProperty("Rating")!.GetValue(outcome)!.ToString();
        result["message"] = (string?)outcomeType.GetProperty("Message")!.GetValue(outcome);
        result["value"] = ToJson(outcomeType.GetProperty("Value")!.GetValue(outcome));
        result["unit"] = (string?)outcomeType.GetProperty("Unit")!.GetValue(outcome);
        result["low"] = ToJson(outcomeType.GetProperty("Low")!.GetValue(outcome));
        result["high"] = ToJson(outcomeType.GetProperty("High")!.GetValue(outcome));
        var outValues = new JsonObject();
        foreach (var name in block.Fields.Keys) outValues[name] = ToJson(type.GetField(name)!.GetValue(instance));
        result["values"] = outValues;
        result["cancelled"] = token.IsCancellationRequested;
        Send(result);
    }

    private static void SetupDebugger(Type type, object instance, JsonObject debug)
    {
        var bps = (debug["breakpoints"] as JsonArray ?? []).Select(n => n!.GetValue<int>()).ToArray();
        var entry = debug["entry"] != null && debug["entry"]!.GetValue<bool>();
        var step = debug["step"] != null && debug["step"]!.GetValue<bool>();
        type.GetMethod("__DbgSetup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)!
            .Invoke(instance, [true, bps, entry, step]);
        type.GetMethod("__DbgEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)!
            .Invoke(instance, null);
    }

    private static string FormatDiagnostic(Diagnostic d)
    {
        var span = d.Location.GetMappedLineSpan();
        return span.Path == "CodeBlock" ? $"Zeile {span.StartLinePosition.Line + 1}: {d.GetMessage()}" : d.GetMessage();
    }

    /// <summary>Zeilennummer im Nutzercode aus dem Stacktrace (#line-Zuordnung, eingebettetes PDB).</summary>
    private static string UserLine(Exception ex)
    {
        var trace = new System.Diagnostics.StackTrace(ex, fNeedFileInfo: true);
        foreach (var frame in trace.GetFrames())
        {
            if (frame.GetFileName() == "CodeBlock" && frame.GetFileLineNumber() > 0)
                return $" (Zeile {frame.GetFileLineNumber()})";
        }
        return "";
    }

    private static void SendError(string id, string message, string? details)
        => Send(new JsonObject { ["t"] = "error", ["id"] = id, ["message"] = message, ["details"] = details });

    internal static object? FromJson(JsonNode node, string type) => type switch
    {
        "Bool" => node.GetValue<bool>(),
        "Int" => node.GetValueKind() == JsonValueKind.Number ? (long)node.GetValue<double>() : long.Parse((string)node!),
        "Double" => node.GetValue<double>(),
        "String" => node.GetValueKind() == JsonValueKind.String ? (string?)node : node.ToJsonString(),
        "DateTime" => DateTime.Parse((string)node!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
        _ => node.ToJsonString(),
    };

    internal static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create((long)i),
        long l => JsonValue.Create(l),
        double d when double.IsFinite(d) => JsonValue.Create(d),
        double d => JsonValue.Create(d.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        float f => JsonValue.Create((double)f),
        string s => JsonValue.Create(s),
        DateTime dt => JsonValue.Create(dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
        Enum e => JsonValue.Create(e.ToString()),
        _ => JsonValue.Create(value.ToString()),
    };
}

/// <summary>Fehler bei einem Rückruf an den Host.</summary>
public sealed class HostCallException(string message) : Exception(message);

/// <summary>
/// Instrumentiert den Nutzercode für den Zeilen-Debugger (ADR 0017): vor jede Anweisung im Nutzercode (Pfad „CodeBlock“)
/// ein <c>__DbgLine(zeile)</c>, nach Deklarationen/Zuweisungen ein <c>__DbgVar("name", name)</c> zur Variablen-Erfassung.
/// Die Zeilennummer wird aus der ursprünglichen #line-Zuordnung berechnet; physische Verschiebungen durch die Einschübe
/// ändern die gemeldete Zeile daher nicht.
/// </summary>
internal sealed class DebugRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        var statements = new List<StatementSyntax>();
        for (var i = 0; i < node.Statements.Count; i++)
        {
            var original = node.Statements[i];
            var current = visited.Statements[i];
            var span = original.GetLocation().GetMappedLineSpan();
            if (span.IsValid && span.Path == "CodeBlock")
            {
                var line = span.StartLinePosition.Line + 1;
                if (line >= 1)
                {
                    statements.Add(Hook($"__DbgLine({line});"));
                    statements.Add(current);
                    foreach (var name in AssignedNames(original)) statements.Add(Hook($"__DbgVar(\"{name}\", {name});"));
                    continue;
                }
            }
            statements.Add(current);
        }
        return visited.WithStatements(SyntaxFactory.List(statements));
    }

    private static StatementSyntax Hook(string code)
        => SyntaxFactory.ParseStatement(code).WithTrailingTrivia(SyntaxFactory.LineFeed);

    private static IEnumerable<string> AssignedNames(StatementSyntax statement)
    {
        if (statement is LocalDeclarationStatementSyntax declaration)
        {
            foreach (var v in declaration.Declaration.Variables)
                if (v.Initializer is not null) yield return v.Identifier.Text;
        }
        else if (statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: IdentifierNameSyntax identifier } })
        {
            yield return identifier.Identifier.Text;
        }
    }
}
