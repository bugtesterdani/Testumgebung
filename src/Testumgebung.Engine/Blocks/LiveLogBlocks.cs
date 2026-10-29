/** @file @brief Verbindet Logquellen, Fortschrittsregeln, Mitschrift und Werkeranzeige. */
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Processes;
namespace Testumgebung.Engine.Blocks;

public static class LiveLogBlocks
{
    public const string Monitor = "anzeige.live-log";
    public static void Register(BlockCatalog catalog)
        => catalog.Register(new(Monitor, "Live-Log und Fortschritt", "Anzeige", "wait", "Zeigt laufende stdout/stderr-Ausgabe oder eine anwachsende Logdatei. Fortschritt aus Textregeln oder current/total-Regex. Keine Exitcode-Bewertung.",
        [new("Quelle", "Quelle", ParameterKind.Choice, "Prozess-stdout", Choices: ["Prozess-stdout", "Prozess-stderr", "Datei"]),
         new("Prozess", "Prozesskennung", ParameterKind.Text, Hint: "Prozess zuerst ohne Warten starten; {Variable} erlaubt."),
         new("Datei", "Absoluter Logdateipfad", ParameterKind.Text),
         new("AbEnde", "Datei erst ab aktuellem Ende lesen", ParameterKind.Bool, "false"),
         new("Kodierung", "Dateikodierung", ParameterKind.Text, "utf-8"),
         new("Mitschrift", "Optionale UTF-8-Mitschrift (absoluter Pfad)", ParameterKind.Text, Hint: "Anhängen; Quelle und Mitschrift müssen verschieden sein."),
         new("Textregeln", "Text-/Regex-Regeln (JSON-Liste)", ParameterKind.Text, "[]", Hint: "[{\"text\":\"Start\",\"percent\":10,\"status\":\"Vorbereitung\"}]"),
         new("ZahlenRegex", "Zahlenmuster mit current und total", ParameterKind.Text, Hint: @"(?<current>\d+) von (?<total>\d+)"),
         new("Statusvorlage", "Status bei Zahlentreffer", ParameterKind.Text, "{current} / {total}"),
         new("EndeRegex", "Optionales Ende-Muster", ParameterKind.Text, Hint: "Datei: z. B. ^FERTIG$; Prozess: sonst bis Ende des Streams."),
         new("Zeitlimit", "Gesamtzeitlimit [ms]", ParameterKind.Number, "300000")], Create));
    private static IStepAction Create(BlockParameters p)
    {
        var timeout = p.Number("Zeitlimit") ?? 300000;
        if (!double.IsFinite(timeout) || timeout < 1 || timeout > int.MaxValue) throw new BlockConfigurationException("Zeitlimit muss zwischen 1 und 2147483647 ms liegen.");
        // Validate rules before any file or process is touched.
        try { _ = new LiveProgressParser(p.Text("Textregeln"), p.Text("ZahlenRegex"), p.Text("Statusvorlage"), p.Text("EndeRegex")); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { throw new BlockConfigurationException(ex.Message); }
        var source = p.Choice("Quelle");
        var address = p.Text(source == "Datei" ? "Datei" : "Prozess");
        if (string.IsNullOrWhiteSpace(address)) throw new BlockConfigurationException("Dateipfad bzw. Prozesskennung fehlt.");
        return new MonitorAction(source, address, p.Bool("AbEnde"), p.Text("Kodierung"), p.Text("Mitschrift"), p.Text("Textregeln"), p.Text("ZahlenRegex"), p.Text("Statusvorlage"), p.Text("EndeRegex"), TimeSpan.FromMilliseconds(timeout));
    }
    private sealed record MonitorAction(string Source, string Address, bool FromEnd, string Encoding, string Log,
        string Rules, string Numbers, string Status, string End, TimeSpan Timeout) : IStepAction
    {
        public async Task<StepOutcome> ExecuteAsync(IStepContext ctx)
        {
            var address = CommunicationBlocks.Interpolate(Address, ctx);
            var log = CommunicationBlocks.Interpolate(Log, ctx);
            var parser = new LiveProgressParser(Rules, Numbers, Status, End);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            deadline.CancelAfter(Timeout);
            await using var writer = OpenLog(log, Source == "Datei" ? address : null);
            ctx.ReportLiveOutput(new("", Status: "Warte auf Ausgabe …", Reset: true));
            var source = Source == "Datei" ? LiveLogSources.File(address, Encoding, FromEnd, deadline.Token)
                : LiveLogSources.Process(ctx, address, Source == "Prozess-stdout" ? ProcessOutputStream.StandardOutput : ProcessOutputStream.StandardError, deadline.Token);
            try
            {
                await foreach (var chunk in source.ConfigureAwait(false))
                {
                    var text = chunk.Text;
                    if (chunk.ResetLine) parser.DiscardPartialLine();
                    if (writer is not null) { await writer.WriteAsync(text.AsMemory(), deadline.Token).ConfigureAwait(false); await writer.FlushAsync(deadline.Token).ConfigureAwait(false); }
                    parser.Push(text);
                    ctx.ReportLiveOutput(new(text, parser.Percent, parser.Status));
                    if (parser.Finished) break;
                }
                parser.Push("", final: true);
                ctx.ReportLiveOutput(new("", parser.Percent, parser.Status));
                return StepOutcome.Unrated(message: "Live-Quelle abgeschlossen; Prozess-Exitcode separat bewerten.");
            }
            catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested)
            { throw new TimeoutException("Live-Log: Gesamtzeitlimit überschritten. Ende-Muster und Prozess prüfen."); }
        }
    }
    private static StreamWriter? OpenLog(string path, string? source)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Mitschrift benötigt einen absoluten Pfad.");
        if (source is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Logquelle und Mitschrift dürfen nicht dieselbe Datei sein.");
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
    }
}
