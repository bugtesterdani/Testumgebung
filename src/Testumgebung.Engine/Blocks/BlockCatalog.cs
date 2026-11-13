/**
 * @file
 * @brief Ordnet grafische Bausteintypen ihren Parametern, Prüfregeln und ausführbaren Aktionen zu.
 */

using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Engine.Blocks;

public enum ParameterKind
{
    Text,
    Number,
    /// <summary>Typisierter Ausdruck (Variablen, Literale, Operatoren).</summary>
    Expression,
    /// <summary>Name einer Zielvariable im Ordner.</summary>
    Variable,
    /// <summary>Abstrakter Hardwarename aus der Prüfplatzkonfiguration.</summary>
    Resource,
    Choice,
    Bool,
    Image,
    /// <summary>Mehrzeiliger Quelltext (CodeBlock).</summary>
    Code,
}

public sealed record ParameterDescriptor(
    string Name,
    string Label,
    ParameterKind Kind,
    string Default = "",
    bool Required = false,
    IReadOnlyList<string>? Choices = null,
    string? Hint = null);

/// <summary>Beschreibung eines Bausteins der Palette.</summary>
public sealed record BlockDescriptor(
    string Type,
    string DisplayName,
    string Category,
    string Icon,
    string Description,
    IReadOnlyList<ParameterDescriptor> Parameters,
    Func<BlockParameters, IStepAction> Factory);

/// <summary>Fehlerhafte Bausteinkonfiguration (Diagnose im Editor, ERROR bei Ausführung).</summary>
public sealed class BlockConfigurationException(string message) : Exception(message);

/// <summary>Lesender Zugriff auf Bausteinparameter mit Typprüfung.</summary>
public sealed class BlockParameters(BlockDescriptor descriptor, IReadOnlyDictionary<string, string> values)
{
    /// <summary>Alle Rohwerte inkl. dynamischer Parameter (z. B. Bindungen „in.Spannung“).</summary>
    public IReadOnlyDictionary<string, string> Raw => values;

    public string Text(string name)
    {
        var text = values.TryGetValue(name, out var value) ? value : Descriptor(name).Default;
        if (Descriptor(name).Required && string.IsNullOrWhiteSpace(text))
            throw new BlockConfigurationException($"„{Descriptor(name).Label}“ ist erforderlich.");
        return text ?? "";
    }

    public string? Optional(string name)
    {
        var text = Text(name);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public double? Number(string name)
    {
        var text = Optional(name);
        if (text is null) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("de-DE"), out number)) return number;
        throw new BlockConfigurationException($"„{Descriptor(name).Label}“: '{text}' ist keine Zahl.");
    }

    public bool Bool(string name)
    {
        var text = Optional(name);
        return text is not null && (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("ja", StringComparison.OrdinalIgnoreCase));
    }

    public Expression? Expr(string name)
    {
        var text = Optional(name);
        if (text is null)
        {
            if (Descriptor(name).Required) throw new BlockConfigurationException($"„{Descriptor(name).Label}“ ist erforderlich.");
            return null;
        }
        try
        {
            return Expression.Parse(text);
        }
        catch (ExpressionException ex)
        {
            throw new BlockConfigurationException($"„{Descriptor(name).Label}“: {ex.Message}");
        }
    }

    public string Choice(string name)
    {
        var text = Text(name);
        var choices = Descriptor(name).Choices ?? [];
        if (!choices.Contains(text)) throw new BlockConfigurationException($"„{Descriptor(name).Label}“: '{text}' ist keine gültige Auswahl.");
        return text;
    }

    private ParameterDescriptor Descriptor(string name)
        => descriptor.Parameters.FirstOrDefault(parameter => parameter.Name == name)
           ?? throw new InvalidOperationException($"Parameter '{name}' ist im Baustein '{descriptor.Type}' nicht beschrieben.");
}

/// <summary>
/// Katalog der grafischen Bausteine (Palette). Schritte aus dem Katalog sind als Projektdatei speicherbar
/// und ohne Quellcode konfigurierbar (Etappe 3).
/// </summary>
public sealed class BlockCatalog
{
    private readonly Dictionary<string, BlockDescriptor> _blocks = new(StringComparer.Ordinal);

    private static readonly Lazy<BlockCatalog> LazyDefault = new(CreateDefault);

    /// <summary>Standardkatalog (lazy, damit statische Listen vorher initialisiert sind).</summary>
    public static BlockCatalog Default => LazyDefault.Value;

    public IReadOnlyList<BlockDescriptor> All => _blocks.Values.ToList();

    public IReadOnlyList<string> Categories => _blocks.Values.Select(block => block.Category).Distinct().ToList();

    public void Register(BlockDescriptor descriptor) => _blocks[descriptor.Type] = descriptor;

    public BlockDescriptor Get(string type)
        => _blocks.TryGetValue(type, out var block) ? block : throw new KeyNotFoundException($"Unbekannter Bausteintyp '{type}'.");

    public bool TryGet(string type, out BlockDescriptor descriptor) => _blocks.TryGetValue(type, out descriptor!);

    /// <summary>Neuen Schritt aus dem Katalog anlegen (Standardwerte gesetzt).</summary>
    public StepDefinition Create(string type, string id, string? name = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        var block = Get(type);
        var step = new StepDefinition(id, name ?? block.DisplayName, new ErrorAction("Nicht konfiguriert")) { BlockType = type };
        foreach (var parameter in block.Parameters) step.Parameters[parameter.Name] = parameter.Default;
        if (parameters is not null)
            foreach (var (key, value) in parameters) step.Parameters[key] = value;
        Rebuild(step);
        return step;
    }

    /// <summary>
    /// Aktion nach Parameteränderung neu erzeugen. Bei Fehlern erhält der Schritt eine Aktion,
    /// die ERROR mit der Diagnose liefert; die Diagnosen werden zurückgegeben.
    /// </summary>
    public IReadOnlyList<string> Rebuild(StepDefinition step)
    {
        if (step.BlockType is null) return [];
        if (!TryGet(step.BlockType, out var block))
        {
            var msg = $"Unbekannter Bausteintyp '{step.BlockType}'.";
            step.Action = new ErrorAction(msg);
            return [msg];
        }
        try
        {
            step.Action = block.Factory(new BlockParameters(block, step.Parameters));
            return [];
        }
        catch (BlockConfigurationException ex)
        {
            step.Action = new ErrorAction($"Konfigurationsfehler: {ex.Message}");
            return [ex.Message];
        }
    }

    // ------------------------------------------------------------------ Standardbausteine

    public const string SetValue = "wert.setzen";
    public const string Measure = "analog.messen";
    public const string Output = "analog.ausgeben";
    public const string Capture = "kurve.aufnehmen";
    public const string CurveOutput = "kurve.ausgabe";
    public const string CurveLoad = "kurve.laden";
    public const string CurveTransfer = "kurve.uebertragen";
    public const string CurveStart = "kurve.starten";
    public const string CurveStop = "kurve.stoppen";
    public const string DigitalRead = "digital.lesen";
    public const string DigitalWrite = "digital.setzen";
    public const string Evaluate = "bewertung";
    public const string SetRating = "bewertung.setzen";
    public const string Wait = "warten";
    public const string RaiseError = "fehler";
    public const string Operator = "werker";
    public const string Code = "code";
    public const string LibraryBlock = "baustein";
    public const string GroupBlock = "gruppenbaustein";
    public const string SelectTest = "auswahl.test";
    public const string HandoverWait = "pruefplatz.freigabe.warten";
    public const string HandoverRelease = "pruefplatz.freigabe.erteilen";

    /// <summary>Präfixe der Bindungsparameter eines Bibliotheksbausteins.</summary>
    public const string InputBindingPrefix = "in.";
    public const string OutputBindingPrefix = "out.";

    public static readonly string[] Languages = ["C#", "Python", "C++"];

    public static readonly string[] OperatorKinds = ["Anweisung", "Bild mit Bestätigung", "Eingabe", "Auswahl"];
    public static readonly string[] InputTypes = ["Zahl", "Ganzzahl", "Text", "Ja/Nein"];
    public static readonly string[] DisplayModes = ["Vorherigen Inhalt ersetzen", "Bis Gruppenende sichtbar"];
    public static readonly string[] Ratings = ["NRESULT", "PASS", "FAIL", "ERROR"];

    private static BlockCatalog CreateDefault()
    {
        // Eigene Registrierung hält den großen Katalog übersichtlich.
        var blockCatalog = new BlockCatalog();
        const string analog = "Analog messen / ausgeben";
        const string digital = "Digitale Ein-/Ausgänge";
        const string variables = "Variablen";
        const string rating = "Bewertung";
        const string flow = "Ablauf";
        const string op = "Werkerinteraktion";

        blockCatalog.Register(new(Measure, "Messung", analog, "measure",
            "Liest einen Messwert, schreibt ihn in die Zielvariable und bewertet gegen Grenzwerte.",
            [
                new("Ressource", "Ressource", ParameterKind.Resource, "Spannung_Pruefling", Required: true),
                new("Ziel", "Zielvariable", ParameterKind.Variable),
                new("Untergrenze", "Untergrenze", ParameterKind.Expression, Hint: "Zahl oder Ausdruck, z. B. GrenzwertMin"),
                new("Obergrenze", "Obergrenze", ParameterKind.Expression),
                new("Einheit", "Einheit", ParameterKind.Text, "V"),
            ],
            parameters2 => new MeasureExpressionAction(parameters2.Text("Ressource"), parameters2.Optional("Ziel"), parameters2.Expr("Untergrenze"), parameters2.Expr("Obergrenze"), parameters2.Text("Einheit"))));

        blockCatalog.Register(new(Output, "Ausgabe setzen", analog, "arrow",
            "Setzt einen analogen Ausgang bzw. eine Quelle (unbewertet).",
            [
                new("Ressource", "Ressource", ParameterKind.Resource, "AC_Quelle", Required: true),
                new("Wert", "Wert", ParameterKind.Expression, "0", Required: true),
            ],
            parameters2 =>
            {
                var resource = parameters2.Text("Ressource");
                var value = parameters2.Expr("Wert")!;
                return new DelegateAction(async ctx =>
                {
                    var value2 = ToDouble(value.Evaluate(new ContextResolver(ctx)), "Wert");
                    await ctx.WriteAsync(resource, value2).ConfigureAwait(false);
                    return StepOutcome.Unrated(value2);
                });
            }));

        blockCatalog.Register(new(Capture, "Kurve aufnehmen", analog, "measure",
            "Nimmt Kurven mehrerer Kanäle mit gemeinsamer Zeitbasis auf (z. B. L1/L2/L3); optional Bewertung des Effektivwerts je Kanal.",
            [
                new("Ressourcen", "Kanäle", ParameterKind.Text, "Spannung_L1, Spannung_L2, Spannung_L3", Required: true,
                    Hint: "Abstrakte Namen, durch Komma getrennt – alle am selben Gerät"),
                new("Abtastrate", "Abtastrate [Hz]", ParameterKind.Number, "5000", Required: true),
                new("Dauer", "Dauer [ms]", ParameterKind.Number, "100", Required: true),
                new("Untergrenze", "Effektivwert min", ParameterKind.Expression),
                new("Obergrenze", "Effektivwert max", ParameterKind.Expression),
                new("Einheit", "Einheit", ParameterKind.Text, "V"),
            ],
            parameters2 =>
            {
                var channels = parameters2.Text("Ressourcen").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (channels.Length == 0) throw new BlockConfigurationException("Mindestens ein Kanal erforderlich.");
                var rate = parameters2.Number("Abtastrate") ?? 0;
                var ms = parameters2.Number("Dauer") ?? 0;
                if (rate <= 0) throw new BlockConfigurationException("Abtastrate muss > 0 sein.");
                if (ms <= 0) throw new BlockConfigurationException("Dauer muss > 0 sein.");
                var samples = (int)Math.Max(1, Math.Round(rate * ms / 1000.0));
                if (samples > 50_000_000) throw new BlockConfigurationException("Zu viele Samples (max. 50 Mio. je Kanal).");
                var low = parameters2.Expr("Untergrenze");
                var high = parameters2.Expr("Obergrenze");
                var unit = parameters2.Text("Einheit");
                return new DelegateAction(async ctx =>
                {
                    var set = await ctx.CaptureAsync(channels, rate, samples).ConfigureAwait(false);
                    var resolver = new ContextResolver(ctx);
                    double? lo = low is null ? null : ToDouble(low.Evaluate(resolver), "Effektivwert min");
                    double? hi = high is null ? null : ToDouble(high.Evaluate(resolver), "Effektivwert max");
                    if (lo is null && hi is null) return new StepOutcome(ResultRating.NResult, set, unit);
                    var bad = set.Channels
                        .Select(waveform => (waveform.Channel, Rms: waveform.Rms()))
                        .Where(channelEntry => (lo is not null && channelEntry.Rms < lo) || (hi is not null && channelEntry.Rms > hi))
                        .Select(channelEntry => string.Create(CultureInfo.InvariantCulture, $"{channelEntry.Channel} {channelEntry.Rms:0.###} {unit}"))
                        .ToList();
                    return new StepOutcome(bad.Count == 0 ? ResultRating.Pass : ResultRating.Fail, set, unit, lo, hi,
                        bad.Count == 0 ? null : "Effektivwert außerhalb: " + string.Join(", ", bad));
                });
            }));

        const string output = "Kurvenausgabe";
        blockCatalog.Register(new(CurveLoad, "Kurve laden", output, "arrow",
            "Lädt eine Ausgabekurve (feste Version) und prüft die Geräteeignung. Keine automatische Skalierung oder Abtastratenänderung.",
            [
                new("Ressource", "Ausgabegerät", ParameterKind.Resource, "AC_Kurvenquelle", Required: true),
                new("Kurve", "Kurve (ID)", ParameterKind.Text, Required: true),
                new("Version", "Version", ParameterKind.Text, Hint: "Leer = neueste gespeicherte Version (für Freigaben feste Version angeben)"),
            ],
            parameters2 =>
            {
                var resource = parameters2.Text("Ressource");
                var id = parameters2.Text("Kurve");
                var version = parameters2.Optional("Version");
                return new DelegateAction(async ctx =>
                {
                    var curve = ctx.FindCurve(id, version);
                    if (curve is null) return StepOutcome.Error($"Kurve '{id}'{(version is null ? "" : $" v{version}")} ist im Projekt nicht vorhanden.");
                    var output = curve.ToOutput();
                    var errors = ctx.CheckOutput(resource, output).Where(issue => issue.IsError).ToList();
                    if (errors.Count > 0) return StepOutcome.Error("Gerät ungeeignet: " + string.Join(" ", errors.Select(issue => issue.Text)));
                    await ctx.CurveOutputAsync(resource, CurveCommand.Load, output).ConfigureAwait(false);
                    return StepOutcome.Unrated(curve.Key, $"{curve.Name} v{curve.Version} geladen ({curve.Channels.Count} Kanäle, {curve.Count} Samples)");
                });
            }));
        foreach (var (type, name, command, text) in new[]
                 {
                     (CurveTransfer, "Kurve übertragen", CurveCommand.Transfer, "Überträgt die geladene Kurve ins Gerät."),
                     (CurveStart, "Kurvenausgabe starten", CurveCommand.Start, "Startet die Ausgabe der übertragenen Kurve."),
                     (CurveStop, "Kurvenausgabe stoppen", CurveCommand.Stop, "Stoppt die Ausgabe (auch in Exit-Gruppen verwenden)."),
                 })
        {
            blockCatalog.Register(new(type, name, output, "arrow", text,
                [new("Ressource", "Ausgabegerät", ParameterKind.Resource, "AC_Kurvenquelle", Required: true)],
                parameters2 =>
                {
                    var resource = parameters2.Text("Ressource");
                    return new DelegateAction(async ctx =>
                    {
                        var state = await ctx.CurveOutputAsync(resource, command, null).ConfigureAwait(false);
                        return StepOutcome.Unrated(state.ToString());
                    });
                }));
        }

        blockCatalog.Register(new(DigitalRead, "Digitaleingang lesen", digital, "digital",
            "Liest einen digitalen Eingang; optional Bewertung gegen Erwartungswert.",
            [
                new("Ressource", "Ressource", ParameterKind.Resource, "Digital_Start", Required: true),
                new("Ziel", "Zielvariable (bool)", ParameterKind.Variable),
                new("Erwartet", "Erwartet", ParameterKind.Choice, "", Choices: ["", "true", "false"]),
            ],
            parameters2 =>
            {
                var resource = parameters2.Text("Ressource");
                var target = parameters2.Optional("Ziel");
                var expected = parameters2.Choice("Erwartet");
                return new DelegateAction(async ctx =>
                {
                    var state = await ctx.ReadAsync(resource).ConfigureAwait(false) >= 0.5;
                    if (target is not null) ctx.Set(target, state);
                    if (expected == "") return StepOutcome.Unrated(state);
                    return state == (expected == "true") ? StepOutcome.Pass(state) : StepOutcome.Fail($"Erwartet {expected}", state);
                });
            }));

        blockCatalog.Register(new(DigitalWrite, "Digitalausgang setzen", digital, "digital",
            "Setzt einen digitalen Ausgang (unbewertet).",
            [
                new("Ressource", "Ressource", ParameterKind.Resource, "Digital_Start", Required: true),
                new("Wert", "Wert", ParameterKind.Bool, "true"),
            ],
            parameters2 =>
            {
                var resource = parameters2.Text("Ressource");
                var value = parameters2.Bool("Wert");
                return new DelegateAction(async ctx =>
                {
                    await ctx.WriteAsync(resource, value ? 1 : 0).ConfigureAwait(false);
                    return StepOutcome.Unrated(value);
                });
            }));

        blockCatalog.Register(new(SetValue, "Wert setzen", variables, "doc",
            "Zielvariable = Ausdruck (unbewertet).",
            [
                new("Ziel", "Zielvariable", ParameterKind.Variable, Required: true),
                new("Ausdruck", "Ausdruck", ParameterKind.Expression, "0", Required: true),
            ],
            parameters2 => new SetValueAction(parameters2.Text("Ziel"), parameters2.Expr("Ausdruck")!)));

        EvaluationBlocks.Register(blockCatalog);

        RegisterHandover(blockCatalog);
        blockCatalog.Register(new(SelectTest, "Test auswählen", "Auswahl & Auftrag", "flow",
            "Ausgabevertrag des eigenen Auswahlablaufs: Testkennung, optional Version, Auftrag und Prüfparameter (Schlüssel=Wert;…).",
            [
                new("Testkennung", "Testkennung", ParameterKind.Expression, Required: true, Hint: "z. B. \"AC-Prüfung\" oder eine Variable"),
                new("Version", "Version (leer = aktuell freigegebene)", ParameterKind.Expression),
                new("Auftrag", "Auftrag", ParameterKind.Expression),
                new("Pruefparameter", "Prüfparameter", ParameterKind.Expression, Hint: "Text „GrenzwertMin=220;GrenzwertMax=240“"),
                new("PythonVenv", "Python-Umgebung (optional)", ParameterKind.Expression, Hint: "Absoluter Ordnerpfad als String oder Variable; fehlende Umgebung fällt auf die nächste Stufe zurück."),
            ],
            parameters2 =>
            {
                var test = parameters2.Expr("Testkennung")!;
                var version = parameters2.Expr("Version");
                var order = parameters2.Expr("Auftrag");
                var parameters = parameters2.Expr("Pruefparameter");
                var pythonVenv = parameters2.Expr("PythonVenv");
                return new DelegateAction(ctx =>
                {
                    var contextResolver = new ContextResolver(ctx);
                    string? Text(Testumgebung.Domain.Expressions.Expression? expression)
                        => expression?.Evaluate(contextResolver) is { } value2 ? Convert.ToString(value2, System.Globalization.CultureInfo.InvariantCulture) : null;
                    var selection = new TestSelectionValue(Text(test) ?? "", Text(version), Text(order), Text(parameters), Text(pythonVenv));
                    if (selection.Test.Length == 0) return Task.FromResult(new StepOutcome(ResultRating.Error, selection, Message: "Keine Testkennung ermittelt."));
                    return Task.FromResult(StepOutcome.Unrated(selection));
                });
            }));

        blockCatalog.Register(new(SetRating, "Bewertung setzen", rating, "check",
            "Setzt eine feste Bewertung.",
            [new("Bewertung", "Bewertung", ParameterKind.Choice, "PASS", Choices: Ratings)],
            parameters2 =>
            {
                var rating2 = (ResultRating)Array.IndexOf(Ratings, parameters2.Choice("Bewertung"));
                return new DelegateAction(_ => Task.FromResult(new StepOutcome(rating2)));
            }));

        blockCatalog.Register(new(Wait, "Warten", flow, "wait",
            "Wartet die angegebene Zeit (abbrechbar).",
            [new("Dauer", "Dauer [ms]", ParameterKind.Number, "1000", Required: true)],
            parameters2 =>
            {
                var ms = parameters2.Number("Dauer") ?? 0;
                if (ms < 0) throw new BlockConfigurationException("Dauer muss ≥ 0 sein.");
                var duration = TimeSpan.FromMilliseconds(ms);
                return new DelegateAction(async ctx =>
                {
                    await ctx.Clock.Delay(duration, ctx.CancellationToken).ConfigureAwait(false);
                    return StepOutcome.Unrated();
                });
            }));

        blockCatalog.Register(new(RaiseError, "Fehler erzeugen", flow, "error",
            "Erzeugt einen Ausführungsfehler (ERROR) – für Simulation und Tests.",
            [new("Meldung", "Meldung", ParameterKind.Text, "Simulierter Fehler")],
            parameters2 =>
            {
                var message = parameters2.Text("Meldung");
                return new DelegateAction(_ => Task.FromResult(StepOutcome.Error(message)));
            }));

        blockCatalog.Register(new(Operator, "Werkerinteraktion", op, "operator",
            "Anweisung, Bild mit Bestätigung, Eingabe oder Auswahl. Ausgang: Antwort bzw. Eingabewert.",
            [
                new("Art", "Art", ParameterKind.Choice, "Anweisung", Choices: OperatorKinds),
                new("Text", "Anweisungstext", ParameterKind.Text, "Bitte bestätigen", Required: true,
                    Hint: "Aktuelle Werte mit {Variable} oder {Ausdruck} einsetzen. Literale Klammern: {{ und }}."),
                new("Bild", "Bild (optional)", ParameterKind.Image),
                new("Datentyp", "Datentyp", ParameterKind.Choice, "Zahl", Choices: InputTypes),
                new("Pflicht", "Pflichtfeld", ParameterKind.Bool, "true"),
                new("Standardwert", "Standardwert", ParameterKind.Text),
                new("Min", "Erlaubter Bereich von", ParameterKind.Number),
                new("Max", "Erlaubter Bereich bis", ParameterKind.Number),
                new("Einheit", "Einheit", ParameterKind.Text),
                new("ErlaubteWerte", "Erlaubte Werte", ParameterKind.Text, Hint: "durch ; getrennt, z. B. i. O.;n. i. O."),
                new("Warten", "Auf Antwort warten", ParameterKind.Bool, "true"),
                new("Ziel", "Zielvariable", ParameterKind.Variable),
                new("Anzeige", "Inhalt im Verlauf", ParameterKind.Choice, DisplayModes[0], Choices: DisplayModes),
            ],
            CreateOperatorAction));

        blockCatalog.Register(new(Code, "CodeBlock", "CodeBlocks", "code",
            "C#- oder Python-Code im Ablauf. Greift auf die Variablen seines Ordners zu (als Felder bzw. lokale Namen).",
            [
                new("Sprache", "Sprache", ParameterKind.Choice, "C#", Choices: Languages),
                new("Quelltext", "Quelltext", ParameterKind.Code, "return Pass();", Required: true),
                new("Timeout", "Abbruch-Timeout [s]", ParameterKind.Number, "5",
                    Hint: "Reagiert der Code nicht auf den Abbruch, wird der Worker-Prozess danach beendet."),
            ],
            parameters2 => new CodeBlockAction(ParseLanguage(parameters2.Choice("Sprache")), parameters2.Text("Quelltext"), TimeoutOf(parameters2))));

        blockCatalog.Register(new(LibraryBlock, "Baustein", "CodeBlocks", "block",
            "Wiederverwendbarer Baustein aus der Bibliothek. Eingänge aus Ausdrücken, Ausgänge in Zielvariablen; kein impliziter Variablenzugriff.",
            [
                new("Baustein", "Baustein (ID)", ParameterKind.Text, Required: true),
                new("Timeout", "Abbruch-Timeout [s]", ParameterKind.Number, "5"),
            ],
            parameters2 => new LibraryBlockAction(parameters2.Text("Baustein"),
                parameters2.Raw.Where(kv => kv.Key.StartsWith(InputBindingPrefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key[InputBindingPrefix.Length..], kv => kv.Value),
                parameters2.Raw.Where(kv => kv.Key.StartsWith(OutputBindingPrefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key[OutputBindingPrefix.Length..], kv => kv.Value),
                TimeoutOf(parameters2))));

        blockCatalog.Register(new(GroupBlock, "Gruppenbaustein", "CodeBlocks", "block",
            "Wiederverwendbarer grafischer Baustein (aus einer Gruppe erstellt). Eingänge aus Ausdrücken, Ausgänge in Zielvariablen.",
            [new("Baustein", "Baustein (ID)", ParameterKind.Text, Required: true)],
            parameters2 => new GroupBlockAction(parameters2.Text("Baustein"),
                parameters2.Raw.Where(kv => kv.Key.StartsWith(InputBindingPrefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key[InputBindingPrefix.Length..], kv => kv.Value),
                parameters2.Raw.Where(kv => kv.Key.StartsWith(OutputBindingPrefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key[OutputBindingPrefix.Length..], kv => kv.Value))));

        TraceBlocks.Register(blockCatalog);
        ProcessBlocks.Register(blockCatalog);
        LiveLogBlocks.Register(blockCatalog);
        CommunicationBlocks.Register(blockCatalog);
        BusBlocks.Register(blockCatalog);
        return blockCatalog;
    }

    private static IStepAction CreateOperatorAction(BlockParameters parameters)
    {
        var kind = (OperatorPromptKind)Array.IndexOf(OperatorKinds, parameters.Choice("Art"));
        var type = parameters.Choice("Datentyp") switch
        {
            "Zahl" => VarType.Double,
            "Ganzzahl" => VarType.Int,
            "Ja/Nein" => VarType.Bool,
            _ => VarType.String,
        };
        var allowed = parameters.Optional("ErlaubteWerte")?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (kind == OperatorPromptKind.Choice && (allowed is null || allowed.Length == 0))
            throw new BlockConfigurationException("Auswahl benötigt erlaubte Werte.");
        var min = parameters.Number("Min");
        var max = parameters.Number("Max");
        if (min > max) throw new BlockConfigurationException("Bereich: Untergrenze größer als Obergrenze.");

        object? defaultValue = null;
        var defText = parameters.Optional("Standardwert");
        if (defText is not null && kind == OperatorPromptKind.Input)
        {
            try { defaultValue = TypedValue.Parse(defText, type); }
            catch (TypeMismatchException ex) { throw new BlockConfigurationException($"Standardwert: {ex.Message}"); }
        }
        else defaultValue = defText;

        var prompt = new OperatorPrompt(
            kind,
            parameters.Text("Text"),
            kind == OperatorPromptKind.Input ? type : kind == OperatorPromptKind.Choice ? VarType.String : null,
            parameters.Bool("Pflicht"),
            defaultValue,
            allowed,
            parameters.Bool("Warten"),
            min,
            max,
            parameters.Optional("Einheit"),
            parameters.Optional("Bild"),
            parameters.Choice("Anzeige") == DisplayModes[1] ? OperatorDisplayMode.UntilGroupEnd : OperatorDisplayMode.ReplacePrevious);
        var target = parameters.Optional("Ziel");

        return new DelegateAction(async ctx =>
        {
            // Auswahl-/Auftragsablauf: bereits erfasste Kennung o. Ä. nicht erneut abfragen
            if (target is not null && prompt.ExpectsValue && ctx.Preset(target) is { } preset)
            {
                var (value, error) = prompt.Validate(preset is string ? preset : Convert.ToString(preset, System.Globalization.CultureInfo.InvariantCulture));
                if (error is null)
                {
                    ctx.Set(target, value);
                    ctx.Log($"{target} aus der Auswahl übernommen: {value}");
                    return new StepOutcome(ResultRating.NResult, value, prompt.Unit);
                }
            }
            // Pro Ausführung eine neue Anzeige erzeugen: Vorlage und frühere Anzeigen bleiben unverändert.
            var displayPrompt = prompt with { Text = CommunicationBlocks.Interpolate(prompt.Text, ctx) };
            var answer = await ctx.AskOperatorAsync(displayPrompt).ConfigureAwait(false);
            if (target is not null && answer is not null && prompt.ExpectsValue) ctx.Set(target, answer);
            return new StepOutcome(ResultRating.NResult, answer, prompt.Unit);
        });
    }

    public static Testumgebung.Domain.Code.CodeLanguage ParseLanguage(string text) => text switch
    {
        "Python" => Testumgebung.Domain.Code.CodeLanguage.Python,
        "C++" => Testumgebung.Domain.Code.CodeLanguage.Cpp,
        "LabVIEW" => Testumgebung.Domain.Code.CodeLanguage.LabView,
        _ => Testumgebung.Domain.Code.CodeLanguage.CSharp,
    };

    public static string LanguageText(Testumgebung.Domain.Code.CodeLanguage language) => language switch
    {
        Testumgebung.Domain.Code.CodeLanguage.Python => "Python",
        Testumgebung.Domain.Code.CodeLanguage.Cpp => "C++",
        Testumgebung.Domain.Code.CodeLanguage.LabView => "LabVIEW",
        _ => "C#",
    };

    /// <summary>Sprachen der Bibliothek (LabVIEW nur als Bibliotheksbaustein, Bearbeitung extern).</summary>
    public static readonly string[] LibraryLanguages = ["C#", "Python", "C++", "LabVIEW"];

    private static TimeSpan TimeoutOf(BlockParameters parameters)
    {
        var seconds = parameters.Number("Timeout") ?? 5;
        if (seconds <= 0) throw new BlockConfigurationException("Abbruch-Timeout muss > 0 sein.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static void RegisterHandover(BlockCatalog blockCatalog)
    {
        blockCatalog.Register(new(HandoverWait, "Freigabe abwarten", "Zwei Prüfplätze", "flow",
            "Wartet auf die andere Seite; die konfigurierte erste Seite darf zu Beginn starten.",
            [new("Kanal", "Freigabekanal", ParameterKind.Text, "Pruefung", Required: true),
             new("ErsteSeite", "Erste Seite", ParameterKind.Choice, "1", Choices: ["1", "2"])],
            parameters => new HandoverWaitAction(parameters.Text("Kanal")!, int.Parse(parameters.Choice("ErsteSeite"), System.Globalization.CultureInfo.InvariantCulture))));
        blockCatalog.Register(new(HandoverRelease, "Andere Seite freigeben", "Zwei Prüfplätze", "flow",
            "Gibt den zuvor erhaltenen Kanal an die andere Seite weiter, z. B. nach Bestätigung der Entnahme.",
            [new("Kanal", "Freigabekanal", ParameterKind.Text, "Pruefung", Required: true)],
            parameters => new HandoverReleaseAction(parameters.Text("Kanal")!)));
    }

    internal static double ToDouble(object value, string what) => value switch
    {
        double number => number,
        long integerValue => integerValue,
        _ => throw new ExpressionException($"{what}: Zahl erwartet, erhalten {TypedValue.Format(value)}."),
    };
}

/// <summary>Liefert immer ERROR mit Diagnose (fehlkonfigurierter Baustein).</summary>
public sealed class ErrorAction(string message) : IStepAction
{
    public string Message { get; } = message;

    public Task<StepOutcome> ExecuteAsync(IStepContext context) => Task.FromResult(StepOutcome.Error(Message));
}

/// <summary>Variablenzugriff eines Schritts als Ausdrucks-Resolver.</summary>
/// <summary>Ergebnis des Bausteins „Test auswählen“ (Ausgabevertrag der Auswahl).</summary>
public sealed record TestSelectionValue(string Test, string? Version, string? Order, string? Parameters, string? PythonVenv = null)
{
    public override string ToString() => $"{Test}{(string.IsNullOrEmpty(Version) ? "" : " v" + Version)}{(string.IsNullOrEmpty(Order) ? "" : " · " + Order)}";
}

public sealed class ContextResolver(IStepContext context, IReadOnlyDictionary<string, object?>? locals = null) : IValueResolver
{
    public object Resolve(string name) => locals is not null && locals.TryGetValue(name, out var value) ? value ?? 0 : context.Get(name);
}

/// <summary>Messung mit Grenzwerten als Ausdruck (Zahl oder Variable wie <c>GrenzwertMin</c>).</summary>
public sealed class MeasureExpressionAction(string resource, string? target, Expression? low, Expression? high, string unit) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var value = await context.ReadAsync(resource).ConfigureAwait(false);
        if (target is not null) context.Set(target, value);
        var resolver = new ContextResolver(context);
        double? lo = low is null ? null : BlockCatalog.ToDouble(low.Evaluate(resolver), "Untergrenze");
        double? hi = high is null ? null : BlockCatalog.ToDouble(high.Evaluate(resolver), "Obergrenze");
        if (lo is null && hi is null) return new StepOutcome(ResultRating.NResult, value, unit);
        var ok = (lo is null || value >= lo) && (hi is null || value <= hi);
        return new StepOutcome(ok ? ResultRating.Pass : ResultRating.Fail, value, unit, lo, hi,
            ok ? null : $"{value.ToString("0.###", CultureInfo.InvariantCulture)} {unit} außerhalb {lo}–{hi} {unit}");
    }
}
