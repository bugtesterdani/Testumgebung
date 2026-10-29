/**
 * @file
 * @brief Definiert Schrittergebnisse und Werkeraufforderungen einschließlich typisierter Eingabeprüfung.
 */

using Testumgebung.Contracts;

namespace Testumgebung.Domain.Definitions;

/// <summary>Ergebnis einer Schrittausführung.</summary>
/// <param name="Rating">NRESULT für erfolgreiche unbewertete Aktionen (kein automatisches PASS).</param>
public sealed record StepOutcome(
    ResultRating Rating,
    object? Value = null,
    string? Unit = null,
    double? LowLimit = null,
    double? HighLimit = null,
    string? Message = null)
{
    public static StepOutcome Unrated(object? value = null, string? message = null) => new(ResultRating.NResult, value, Message: message);
    public static StepOutcome Pass(object? value = null) => new(ResultRating.Pass, value);
    public static StepOutcome Fail(string? message = null, object? value = null) => new(ResultRating.Fail, value, Message: message);
    public static StepOutcome Error(string message) => new(ResultRating.Error, Message: message);
}

/// <summary>Ausgabeschritte (Abschnitt 8) – getrennt ausführbar.</summary>
public enum CurveCommand
{
    Load,
    Transfer,
    Start,
    Stop,
}

public enum OperatorPromptKind
{
    /// <summary>Anweisung (Text, optional Bild) mit Bestätigung.</summary>
    Instruction,
    /// <summary>Bild mit Bestätigung.</summary>
    ImageConfirmation,
    /// <summary>Eingabe eines typisierten Werts.</summary>
    Input,
    /// <summary>Auswahl aus erlaubten Werten.</summary>
    Choice,
}

/// <summary>Anzeigedauer eines Inhalts auf der Werkeroberfläche (Abschnitt 4).</summary>
public enum OperatorDisplayMode
{
    /// <summary>Neue Inhalte ersetzen die vorherigen (Standard).</summary>
    ReplacePrevious,
    /// <summary>Inhalt bleibt bis zum Ende seiner Gruppe sichtbar.</summary>
    UntilGroupEnd,
}

/// <summary>Werkerinteraktion (Abschnitt 4). Die Engine leitet sie an die Prüfplatz-Oberfläche weiter.</summary>
public sealed record OperatorPrompt(
    OperatorPromptKind Kind,
    string Text,
    VarType? InputType = null,
    bool Required = false,
    object? DefaultValue = null,
    IReadOnlyList<string>? AllowedValues = null,
    bool WaitForAnswer = true,
    double? Min = null,
    double? Max = null,
    string? Unit = null,
    string? ImagePath = null,
    OperatorDisplayMode DisplayMode = OperatorDisplayMode.ReplacePrevious)
{
    /// <summary>Wird von der Engine gesetzt: Anzeige-ID und Scope der umgebenden Gruppe (für gezieltes Aufräumen).</summary>
    public string DisplayId { get; init; } = "";
    public string GroupScopeId { get; init; } = "";
    public string StepName { get; init; } = "";

    public bool ExpectsValue => Kind is OperatorPromptKind.Input or OperatorPromptKind.Choice;

    /// <summary>
    /// Prüft eine Antwort (Text aus der Oberfläche oder bereits typisierter Wert).
    /// Liefert den typisierten Wert oder eine Fehlermeldung für die Anzeige.
    /// </summary>
    public (object? Value, string? Error) Validate(object? answer)
    {
        if (!ExpectsValue) return (true, null);
        if (answer is null || (answer is string s0 && string.IsNullOrWhiteSpace(s0)))
            return Required ? (null, "Eingabe ist erforderlich") : (DefaultValue, null);

        if (Kind == OperatorPromptKind.Choice) return ValidateChoice(answer);

        var type = InputType ?? VarType.String;
        object value;
        try
        {
            value = answer is string str ? TypedValue.Parse(str.Trim(), type) : TypedValue.Coerce(answer, type);
        }
        catch (TypeMismatchException ex)
        {
            return (null, ex.Message);
        }

        return ValidateInputValue(value);
    }

    private (object? Value, string? Error) ValidateInputValue(object value)
    {
        if (value is long or double)
        {
            var d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if ((Min is { } min && d < min) || (Max is { } max && d > max))
                return (null, $"Zulässiger Bereich: {Min} – {Max}{(Unit is null ? "" : " " + Unit)}");
        }
        if (AllowedValues is { Count: > 0 } && !AllowedValues.Contains(TypedValue.Format(value).Trim('"')))
            return (null, "Wert ist nicht erlaubt");
        return (value, null);
    }

    private (object? Value, string? Error) ValidateChoice(object answer)
    {
        var text = answer.ToString()!;
        if (AllowedValues is { Count: > 0 } && !AllowedValues.Contains(text)) return (null, "Wert ist nicht erlaubt");
        return (text, null);
    }

}

/// <summary>
/// Laufzeitkontext eines Schritts. Ein Schritt im Ablauf (CodeBlock) darf auf Variablen
/// seines Ordners zugreifen; Hardware nur über abstrakte Namen.
/// </summary>
public interface IStepContext
{
    Processes.IProcessSession Processes => throw new NotSupportedException("Prozessaufrufe werden hier nicht unterstützt.");
    string ProcessOwner => RunId;
    string RunId { get; }
    string StepId { get; }
    IReadOnlyList<Testumgebung.Domain.Code.ManagedAssembly> ManagedAssemblies => [];
    string Path { get; }
    CancellationToken CancellationToken { get; }
    IClock Clock { get; }

    object Get(string name);
    void Set(string name, object? value);

    Task<double> ReadAsync(string resource);
    Task WriteAsync(string resource, double value);

    /// <summary>Ausgabekurve des laufenden Projekts (Version leer = neueste).</summary>
    Curves.CurveDefinition? FindCurve(string id, string? version) => null;

    /// <summary>Busbeschreibung (DBC/LDF) des laufenden Projekts oder null.</summary>
    BusDatabaseDefinition? FindBusDatabase(string name) => null;

    /// <summary>Kurvenausgabe an einem Gerät über den abstrakten Namen (Laden mit Kurve, sonst ohne).</summary>
    Task<OutputState> CurveOutputAsync(string resource, CurveCommand command, OutputCurve? curve)
        => throw new NotSupportedException("Kurvenausgabe wird in diesem Kontext nicht unterstützt.");

    /// <summary>
    /// Gerät über den abstrakten Namen verwenden (Kommunikation: Byte-Strom, Modbus, CAN) – mit derselben Reservierungsprüfung
    /// und demselben Zugriffsprotokoll wie Messen/Ausgeben. <paramref name="action"/> erhält Adapter, Kanal und Abbruch.
    /// </summary>
    Task<T> UseDeviceAsync<T>(string resource, string operation, Func<IHardwareAdapter, string, CancellationToken, Task<T>> action)
        => throw new NotSupportedException("Gerätezugriff wird in diesem Kontext nicht unterstützt.");

    /// <summary>Vorbelegter Wert aus der Auswahl (z. B. Kennung) oder null.</summary>
    object? Preset(string name) => null;

    /// <summary>Geräteeignung einer Kurve für einen Ausgang (ohne Zustandsänderung).</summary>
    IReadOnlyList<SuitabilityIssue> CheckOutput(string resource, OutputCurve curve)
        => throw new NotSupportedException("Kurvenausgabe wird in diesem Kontext nicht unterstützt.");

    /// <summary>Kurvenaufnahme über abstrakte Namen; alle Kanäle eines Aufrufs mit gemeinsamer Zeitbasis.</summary>
    Task<WaveformSet> CaptureAsync(IReadOnlyList<string> resources, double sampleRate, int samples)
        => throw new NotSupportedException("Kurvenaufnahme wird in diesem Kontext nicht unterstützt.");

    /// <summary>Werkerinteraktion; liefert Antwort bzw. Eingabewert (null bei „fortsetzen ohne Warten“).</summary>
    Task<object?> AskOperatorAsync(OperatorPrompt prompt);

    void Log(string message);

    /// <summary>Aktualisiert die laufende Werkeranzeige ohne Eingabeaufforderung oder Bewertungsänderung.</summary>
    void ReportLiveOutput(LiveOutputUpdate update) { update.Validate(); Log(update.Text); }


    /// <summary>Im Ordner sichtbare Variablen (Verdeckung berücksichtigt) – für CodeBlocks im Ablauf.</summary>
    IReadOnlyList<(string Name, VarType Type)> VisibleVariables();

    /// <summary>Python-Umgebung aus dem Auswahlprogramm dieses Laufs.</summary>
    string? ProgramPythonVenv => null;
    PairedStationContext? PairedStation => null;

    /// <summary>Ausführungsumgebung für Codebausteine; null, wenn nicht konfiguriert.</summary>
    Code.ICodeRuntime? CodeRuntime { get; }

    /// <summary>Zeilen-Debug-Startkonfiguration dieses Schritts (Haltepunkte/Einzelschritt); aus, wenn kein Debugger wartet.</summary>
    Code.CodeDebugConfig CodeDebugConfig => Code.CodeDebugConfig.Off;

    /// <summary>Meldet eine erreichte Nutzercode-Zeile an den Debugger und liefert die Steuerung (fortsetzen/Schritt/stopp).</summary>
    Task<Code.CodeDebugStop> CodeDebugAsync(Code.CodeDebugEvent debugEvent, Code.CodeDebugCapability capability)
        => Task.FromResult(Code.CodeDebugStop.Continue);

    /// <summary>Bibliotheksbaustein des laufenden Projekts.</summary>
    Code.CodeBlockDefinition? FindLibraryBlock(string id);

    /// <summary>Grafischer Baustein des laufenden Projekts.</summary>
    GroupBlockDefinition? FindGroupBlock(string id);

    /// <summary>Körper eines grafischen Bausteins ausführen; liefert Bewertung und Ausgangswerte.</summary>
    Task<(ResultRating Rating, IReadOnlyDictionary<string, object?> Outputs)> ExecuteGroupBlockAsync(
        GroupBlockDefinition block, IReadOnlyDictionary<string, object?> inputs);
}

/// <summary>Ausführbare Aktion eines Schritts (Simulationsbaustein, CodeBlock-Adapter, …).</summary>
public interface IStepAction
{
    Task<StepOutcome> ExecuteAsync(IStepContext context);
}
