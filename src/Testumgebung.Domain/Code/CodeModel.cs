/**
 * @file
 * @brief Definiert Codebausteine, Ports und Host-Rückrufe als gemeinsame Schnittstelle aller Programmiersprachen.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Domain.Code;

/// <summary>Ausführungstyp eines CodeBlocks (Abschnitt 3).</summary>
public enum CodeLanguage
{
    CSharp,
    Python,
    /// <summary>C++: Build zu Bibliothek mit stabiler C-Schnittstelle, eigener Native-Worker.</summary>
    Cpp,
    /// <summary>LabVIEW-VI (Prototyp): als DLL mit definiertem Connector gebaut, Aufruf über generierten C++-Glue.</summary>
    LabView,
}

public enum PortDirection
{
    /// <summary>Eingang: Wert aus Konstante, Ausdruck oder Ordnervariable.</summary>
    Input,
    /// <summary>Ausgang: schreibt in eine konfigurierte Zielvariable.</summary>
    Output,
    /// <summary>Ein- und Ausgang: liest und schreibt dieselbe Variable.</summary>
    InOut,
}

/// <summary>Typisierter Ein- oder Ausgang eines Bausteins.</summary>
public sealed record CodePort(
    string Name,
    VarType Type,
    PortDirection Direction,
    string? Unit = null,
    string? Description = null,
    object? TestValue = null)
{
    public bool IsInput => Direction is PortDirection.Input or PortDirection.InOut;
    public bool IsOutput => Direction is PortDirection.Output or PortDirection.InOut;
}

/// <summary>
/// Wiederverwendbarer Codebaustein der Projektbibliothek. Schnittstellengrenze:
/// kein impliziter Zugriff auf Variablen des aufrufenden Tests.
/// </summary>
public sealed class CodeBlockDefinition(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; set; } = name;
    public string Version { get; set; } = "0.1";
    public string? Description { get; set; }
    public CodeLanguage Language { get; set; } = CodeLanguage.CSharp;
    public string Source { get; set; } = "";
    public List<CodePort> Ports { get; } = [];

    public override string ToString() => $"{Name} v{Version}";
}

/// <summary>Compilerdiagnose, Zeilen bezogen auf den Quelltext des Nutzers (1-basiert).</summary>
public sealed record CodeDiagnostic(int Line, int Column, string Severity, string Message, string? Code = null)
{
    public bool IsError => Severity == "error";

    public override string ToString() => Line > 0 ? $"Zeile {Line}, Spalte {Column}: {Message}" : Message;
}

public sealed record CompileResult(bool Success, IReadOnlyList<CodeDiagnostic> Diagnostics, string RuntimeInfo)
{
    public int Errors => Diagnostics.Count(d => d.IsError);
    public int Warnings => Diagnostics.Count(d => !d.IsError);
}

/// <summary>
/// Aufrufauftrag. <see cref="Variables"/>: alle an den Code übergebenen Namen mit Typ
/// (Ports eines Bibliotheksbausteins bzw. sichtbare Ordnervariablen eines CodeBlocks im Ablauf).
/// <see cref="Debug"/>: Zeilen-Debugging für diesen Aufruf (Haltepunkte, Einzelschritt), null = aus.
/// </summary>
public sealed record ManagedAssembly(string Name, byte[] Data);

public sealed record CodeRequest(
    CodeLanguage Language,
    string Source,
    IReadOnlyList<CodePort> Variables,
    string InvocationId = "",
    CodeDebugConfig? Debug = null)
{
    public IReadOnlyList<ManagedAssembly> Assemblies { get; init; } = [];
}

/// <summary>
/// Startkonfiguration des Zeilen-Debuggers eines CodeBlock-Aufrufs. Wird dem Worker beim Aufruf mitgegeben;
/// am Haltepunkt liefert der Host eine aktualisierte Steuerung (<see cref="CodeDebugStop"/>).
/// </summary>
public sealed record CodeDebugConfig(bool Enabled, IReadOnlyList<int> Breakpoints, bool StopOnEntry, bool Step)
{
    public static readonly CodeDebugConfig Off = new(false, [], false, false);
}

/// <summary>Ein Debug-Ereignis aus dem Worker: die erreichte Nutzercode-Zeile mit Art und Variablen-Schnappschuss.</summary>
public sealed record CodeDebugEvent(int Line, string Kind, IReadOnlyDictionary<string, object?> Variables)
{
    public Func<CancellationToken, Task<NativeDebugSnapshot>>? InspectNativeAsync { get; init; }
}

public sealed record NativeDebugSnapshot(IReadOnlyDictionary<string, string> Variables, IReadOnlyList<string> Frames, string? Error = null);

/// <summary>
/// Steuerung nach einem Haltepunkt. <see cref="Command"/>: „continue“ (bis zum nächsten Halt), „step“ (nächste Zeile),
/// „stop“ (Ausführung abbrechen). <see cref="Breakpoints"/>/<see cref="Step"/>: aktualisierter Zustand für den Worker.
/// </summary>
public sealed record CodeDebugStop(string Command, IReadOnlyList<int> Breakpoints, bool Step)
{
    public static readonly CodeDebugStop Continue = new("continue", [], false);
}

/// <summary>Debug-Fähigkeit einer Sprache: vollständiges Zeilen-Stepping oder reduzierter Modus (nur Eintritt/Austritt).</summary>
public enum CodeDebugCapability
{
    /// <summary>Haltepunkt je Zeile, Einzelschritt, Variablen-Inspektion (Python, C#).</summary>
    FullLineStepping,
    /// <summary>Kein Zeilen-Stepping; nur Halt bei Eintritt und Austritt mit Variablen-Schnappschuss (C++, LabVIEW).</summary>
    EntryExitOnly,
}

/// <summary>Ergebnis eines Aufrufs (gemeinsamer Vertrag aller Sprachen).</summary>
public sealed record CodeResult(
    ResultRating Rating,
    IReadOnlyDictionary<string, object?> Values,
    object? Value = null,
    string? Unit = null,
    double? LowLimit = null,
    double? HighLimit = null,
    string? Message = null,
    bool Cancelled = false);

/// <summary>Fehler aus dem Nutzercode oder Worker (wird zu ERROR mit Fehlerdetails).</summary>
public sealed class CodeExecutionException(string message, string? details = null) : Exception(message)
{
    public string? Details { get; } = details;
}

/// <summary>
/// Rückrufe aus dem Code an den Host. Gerätezugriff läuft über die zentrale Hardwarevermittlung,
/// damit Ressourcenlocks sprachübergreifend wirken.
/// </summary>
public interface ICodeHost
{
    /// <summary>Liest eine Variable aus dem für diesen Aufruf sichtbaren Scope.</summary>
    /// <param name="path">Variablenpfad, gegebenenfalls relativ zum aufrufenden Scope.</param>
    /// <returns>Aktueller Wert der aufgelösten Variable.</returns>
    object Get(string path);
    /// <summary>Schreibt in eine sichtbare Variable; die Typprüfung erfolgt im Host.</summary>
    /// <param name="path">Pfad der Zielvariable.</param>
    /// <param name="value">Zu übernehmender Wert.</param>
    void Set(string path, object? value);
    /// <summary>Liest einen numerischen Gerätewert über die zentrale Ressourcenvermittlung.</summary>
    /// <param name="resource">Abstrakter Ressourcenname aus der Prüfplatzzuordnung.</param>
    /// <returns>Messwert in der Einheit des zugeordneten Kanals.</returns>
    Task<double> ReadAsync(string resource);
    /// <summary>Schreibt einen numerischen Gerätewert über die zentrale Ressourcenvermittlung.</summary>
    /// <param name="resource">Abstrakter Ressourcenname aus der Prüfplatzzuordnung.</param>
    /// <param name="value">Sollwert in der Einheit des zugeordneten Kanals.</param>
    Task WriteAsync(string resource, double value);
    /// <summary>Zeigt eine Werkeraufforderung und wartet entsprechend ihrem Eingabe- und Anzeigemodus.</summary>
    /// <param name="prompt">Aufforderung einschließlich Typ, Grenzwerten und Eingaberegeln.</param>
    /// <returns>Antwortwert; Aufforderungen ohne Eingabewert können null liefern.</returns>
    Task<object?> AskAsync(OperatorPrompt prompt);
    /// <summary>Fügt eine Diagnosemeldung zum Protokoll des aktuellen Codeaufrufs hinzu.</summary>
    /// <param name="message">Anzuzeigender Text.</param>
    void Log(string message);

    /// <summary>
    /// Kommunikation mit dem Prüfling (Operationen comm.*, modbus.*, can.*, uds, j1939.*, lin.*; Argument „resource“ = abstrakter Name)
    /// über die zentrale Hardwarevermittlung – Reservierungen und Zugriffsprotokoll gelten wie bei den Bausteinen.
    /// Ergebnis: null, bool, long, double, string oder ein Wörterbuch (z. B. CAN-Rahmen).
    /// </summary>
    /// <param name="operation">Operationskennung des Kommunikationsprotokolls.</param>
    /// <param name="arguments">Benannte Argumente, einschließlich des abstrakten Ressourcennamens.</param>
    /// <returns>Sprachübergreifend serialisierbares Operationsergebnis.</returns>
    /// <exception cref="NotSupportedException">Die Standardimplementierung stellt keine Gerätekommunikation bereit.</exception>
    Task<object?> DeviceAsync(string operation, IReadOnlyDictionary<string, object?> arguments)
        => throw new NotSupportedException("Kommunikationsgeräte sind in diesem Kontext nicht verfügbar.");

    /// <summary>
    /// Zeilen-Debugging: Der Worker meldet eine erreichte Nutzercode-Zeile; der Host entscheidet (Haltepunkt/Einzelschritt)
    /// und liefert die Steuerung zurück. Standard: fortsetzen (kein Debugging in diesem Kontext).
    /// </summary>
    /// <param name="debugEvent">Erreichte Zeile und für den Debugger sichtbare Variablen.</param>
    /// <returns>Entscheidung zum Fortsetzen, Halten oder Einzelschritt.</returns>
    Task<CodeDebugStop> DebugAsync(CodeDebugEvent debugEvent)
        => Task.FromResult(CodeDebugStop.Continue);

    /// <summary>
    /// Eigener Auswahlablauf: Aus dem CodeBlock heraus das Testprogramm bestimmen (Testkennung, optional Version/Auftrag/
    /// Prüfparameter „Name=Wert;…“). Ein einzelner CodeBlock kann damit die komplette Auswahl definieren.
    /// </summary>
    /// <param name="test">Kennung des auszuwählenden Testprogramms.</param>
    /// <param name="version">Gewünschte Version oder null für die Auswahl ohne Versionsvorgabe.</param>
    /// <param name="order">Zugeordneter Fertigungsauftrag.</param>
    /// <param name="parameters">Prüfparameter im Format Name=Wert;….</param>
    /// <param name="pythonVenv">Optionaler programmspezifischer Python-Umgebungsordner.</param>
    /// <remarks>Die Standardimplementierung delegiert an die Überladung ohne Python-Umgebung.</remarks>
    void SelectTest(string test, string? version, string? order, string? parameters, string? pythonVenv)
        => SelectTest(test, version, order, parameters);

    /// <summary>Wählt im Auswahlablauf ein Testprogramm ohne programmspezifische Python-Umgebung.</summary>
    /// <param name="test">Kennung des Testprogramms.</param>
    /// <param name="version">Optionale Versionsvorgabe.</param>
    /// <param name="order">Fertigungsauftrag.</param>
    /// <param name="parameters">Prüfparameter im Format Name=Wert;….</param>
    /// <exception cref="NotSupportedException">Der Host unterstützt keinen eigenen Auswahlablauf.</exception>
    void SelectTest(string test, string? version, string? order, string? parameters)
        => throw new NotSupportedException("SelectTest ist nur im eigenen Auswahlablauf (CodeBlock) verfügbar.");
}

/// <summary>Zuordnung Sprache → Debug-Fähigkeit (einheitliche Oberfläche, je Sprache unterschiedliche Tiefe).</summary>
public static class CodeDebug
{
    public static CodeDebugCapability CapabilityOf(CodeLanguage language) => language switch
    {
        // Python (settrace), C# (Roslyn-Instrumentierung), C++ (Quelltext-Instrumentierung): volles Zeilen-Stepping.
        CodeLanguage.Python or CodeLanguage.CSharp or CodeLanguage.Cpp => CodeDebugCapability.FullLineStepping,
        // LabVIEW: kompilierte VI ohne Quelltext → nur Eintritt/Austritt.
        _ => CodeDebugCapability.EntryExitOnly,
    };
}

/// <summary>Ausführungsumgebung für Codebausteine (isolierte Worker-Prozesse).</summary>
public interface ICodeRuntime
{
    /// <summary>Laufbezogene Python-Auswahl; andere Sprachen bleiben unverändert.</summary>
    ICodeRuntime WithPythonEnvironment(string? programVenv, string? variableVenv) => this;

    /// <summary>Beschreibung der Umgebung, z. B. „Python 3.12.3“.</summary>
    Task<string> GetRuntimeInfoAsync(CodeLanguage language, CancellationToken cancellationToken);

    /// <summary>Prüft den Code und seine Schnittstelle in der zur Sprache gehörenden Laufzeit.</summary>
    /// <param name="request">Quelltext, Ports, Bibliotheken und sprachabhängige Optionen.</param>
    /// <param name="cancellationToken">Abbruchsignal für die Build-Prüfung.</param>
    /// <returns>Build-Ergebnis einschließlich Compilerdiagnosen.</returns>
    Task<CompileResult> CompileAsync(CodeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Führt Code aus. Bei Cancellation erhält der Code ein kooperatives Abbruchsignal; reagiert er nicht
    /// innerhalb von <paramref name="abortTimeout"/>, wird der Worker-Prozess beendet.
    /// </summary>
    Task<CodeResult> InvokeAsync(
        CodeRequest request,
        IReadOnlyDictionary<string, object?> values,
        ICodeHost host,
        TimeSpan abortTimeout,
        CancellationToken cancellationToken);
}
