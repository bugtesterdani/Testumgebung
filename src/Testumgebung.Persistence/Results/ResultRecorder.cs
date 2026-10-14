/**
 * @file
 * @brief Sammelt Engine-Ereignisse zu vollständigen Prüflingsläufen, bevor zielabhängige Filter angewendet werden.
 */

using System.Globalization;
using System.Security.Cryptography;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Storage;
using Testumgebung.Engine;

namespace Testumgebung.Persistence.Results;

/// <summary>
/// Zeichnet die Ereignisse je Prüflingstest auf (interne Zwischenaufzeichnung) und liefert am Ende des Prüflings
/// einen vollständigen, ungefilterten <see cref="ResultRun"/>. Filter werden erst danach je Ziel angewendet.
/// </summary>
public sealed class ResultRecorder
{
    private readonly TestRun _run;
    private List<ResultNode> _structure;
    private TestDefinition _structureOf;
    private readonly object _gate = new();
    private Builder? _current;
    private readonly RunJournal? _journal;
    private readonly StorageResolver? _resolver;
    private JournalWriter? _writer;
    private int _written;

    /// <param name="journal">Zwischenaufzeichnung je Prüflingstest (Wiederherstellung nach Absturz/Stromausfall).</param>
    /// <param name="resolver">Filter, die eine spätere Wiederherstellung beachten muss.</param>
    public ResultRecorder(TestRun run, RunJournal? journal = null, StorageResolver? resolver = null)
    {
        _run = run;
        _journal = journal;
        _resolver = resolver;
        _structure = BuildStructure(run.Definition);
        _structureOf = run.Definition;
        run.EventRaised += OnEvent;
    }

    /// <summary>Wird im Engine-Thread ausgelöst – Empfänger dürfen nicht blockieren.</summary>
    public event Action<ResultRun>? RunRecorded;

    public static List<ResultNode> BuildStructure(TestDefinition definition)
    {
        var list = new List<ResultNode>();
        foreach (var root in definition.AllRoots) Add(root, null, false);
        return list;

        void Add(NodeDefinition node, string? parent, bool isExit)
        {
            switch (node)
            {
                case GroupDefinition g:
                    list.Add(new ResultNode(g.Id, parent, g.Name, isExit ? "exit" : "gruppe", null, g.Description));
                    foreach (var c in g.Children) Add(c, g.Id, false);
                    if (g.ExitGroup is not null) Add(g.ExitGroup, g.Id, true);
                    break;
                case StepDefinition s:
                    list.Add(new ResultNode(s.Id, parent, s.Name, "schritt", s.BlockType, s.Description));
                    break;
            }
        }
    }

    private void OnEvent(EngineEvent e)
    {
        lock (_gate)
        {
            switch (e)
            {
                case DutRunStarted d:
                    if (!ReferenceEquals(_structureOf, _run.Definition))
                    {
                        // Neue Version ab diesem Prüfling (V01): Struktur der aktiven Definition
                        _structure = BuildStructure(_run.Definition);
                        _structureOf = _run.Definition;
                    }
                    _current = new Builder(d.DutRunId);
                    BeginJournal(d);
                    return;
                case DutRunCompleted dc when _current is not null:
                    var result = _current.Build(this, dc);
                    _current = null;
                    _writer?.SetDutId(dc.DutId);
                    CloseJournal();
                    RunRecorded?.Invoke(result);
                    return;
                case StateChanged { State: EngineState.Completed }:
                    CloseJournal();
                    return;
            }
            if (_current is null) return;
            _current.Add(e);
            if (_writer is not null)
                for (; _written < _current.Events.Count; _written++) _writer.Append(_current.Events[_written]);
        }
    }

    private void BeginJournal(DutRunStarted d)
    {
        CloseJournal();
        if (_journal is null) return;
        var def = _run.Definition;
        var dutGroup = def.AllNodes().OfType<GroupDefinition>().FirstOrDefault(g => g.Id == d.NodeId);
        var idVariable = dutGroup?.DutIdVariable ?? "Kennung";
        var header = new JournalHeader
        {
            RunId = d.DutRunId,
            StationRunId = d.RunId,
            Station = _run.Options.StationName,
            TestName = def.Name,
            TestVersion = def.Version,
            Started = d.Time,
            Order = d.Order,
            DutId = _run.Options.Presets?.GetValueOrDefault(idVariable) as string,
            DutIdWriters = def.AllNodes().OfType<StepDefinition>()
                .Where(s => (s.Parameters.TryGetValue("Ziel", out var target) && target == idVariable)
                            || s.Action is Testumgebung.Engine.Blocks.SetValueAction { Target: var t } && t == idVariable).Select(s => s.Id).ToList(),
            Structure = _structure,
        };
        if (_resolver is not null)
        {
            foreach (var n in def.AllNodes())
            {
                if (!_resolver.IsEnabled(n.Id, StorageTarget.File)) header.ExcludedFromFile.Add(n.Id);
                if (!_resolver.IsEnabled(n.Id, StorageTarget.Database)) header.ExcludedFromDatabase.Add(n.Id);
            }
        }
        _writer = _journal.Begin(header);
        _written = 0;
    }

    private void CloseJournal()
    {
        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>Wert in die speicherbaren Typen überführen (Kurven separat).</summary>
    internal static object? StorableValue(object? value) => value switch
    {
        null or bool or long or double or string => value,
        int i => (long)i,
        float f => (double)f,
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        WaveformSet or Waveform => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private sealed class Builder(string runId)
    {
        private readonly List<ResultEvent> _events = [];
        private readonly List<ResultWaveform> _waveforms = [];
        private readonly Dictionary<string, ResultAttachment> _attachments = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _attachmentByPath = new(StringComparer.OrdinalIgnoreCase);
        private bool _development;
        private long _seq;

        public List<ResultEvent> Events => _events;

        public void Add(EngineEvent e)
        {
            switch (e)
            {
                case StepCompleted sc:
                    AddStep(sc);
                    break;
                case GroupCompleted gc:
                    _events.Add(new ResultEvent(++_seq, ResultEventKind.Group, gc.NodeId, gc.Path, gc.Iteration, gc.Time, gc.Rating,
                        (long)gc.Iterations, Message: gc.IsExit ? "Exit" : null));
                    break;
                case GroupNotEntered gn:
                    _events.Add(new ResultEvent(++_seq, ResultEventKind.GroupNotEntered, gn.NodeId, gn.Path, gn.Iteration, gn.Time, Message: gn.Reason));
                    break;
                case OperatorContentShown os:
                    _events.Add(new ResultEvent(++_seq, ResultEventKind.OperatorShown, os.NodeId, os.Path, os.Iteration, os.Time,
                        Message: os.Prompt.Text, AttachmentId: Attach(os.Prompt.ImagePath)));
                    break;
                case OperatorAnswered oa:
                    _events.Add(new ResultEvent(++_seq, ResultEventKind.OperatorAnswer, oa.NodeId, oa.Path, oa.Iteration, oa.Time, Value: StorableValue(oa.Value)));
                    break;
                case Diagnostic dg:
                    _events.Add(new ResultEvent(++_seq, ResultEventKind.Diagnostic, dg.NodeId, "", 0, dg.Time, Message: dg.Message));
                    break;
                case ResourceNotAcquired rn:
                    _events.Add(new ResultEvent(++_seq, ResultEventKind.Diagnostic, rn.NodeId, rn.Path, rn.Iteration, rn.Time, ResultRating.Error,
                        Message: $"Ressourcen nicht erhalten ({string.Join(", ", rn.Resources)}): {rn.Reason}"));
                    break;
                case OperatorAbortRequested ab:
                    _events.Add(new ResultEvent(++_seq, ResultEventKind.Abort, null, "", 0, ab.Time, Value: (long)ab.Number, Message: ab.Effect));
                    break;
                case DevelopmentEvent dev:
                    _development = true;
                    _events.Add(new ResultEvent(++_seq, ResultEventKind.Development, null, "", 0, dev.Time,
                        Message: $"{dev.Kind}: {dev.Details}", Value: dev.User));
                    break;
            }
        }

        private void AddStep(StepCompleted sc)
        {
            var seq = ++_seq;
            List<string>? ids = null;
            var waves = sc.Value switch
            {
                WaveformSet set => set.Channels,
                Waveform w => [w],
                _ => null,
            };
            if (waves is not null)
            {
                ids = [];
                foreach (var w in waves)
                {
                    var id = $"{sc.NodeId}#{seq}/{w.Channel}";
                    ids.Add(id);
                    _waveforms.Add(new ResultWaveform(id, seq, sc.NodeId, w));
                }
            }
            _events.Add(new ResultEvent(seq, ResultEventKind.Step, sc.NodeId, sc.Path, sc.Iteration, sc.Time, sc.Rating,
                StorableValue(sc.Value), sc.Unit, sc.LowLimit, sc.HighLimit, sc.Message, sc.Cancelled, ids));
        }

        /// <summary>Bild einmalig einbetten (SHA-256 als Id; gleicher Inhalt → gleicher Anhang).</summary>
        private string? Attach(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (_attachmentByPath.TryGetValue(path, out var known)) return known;
            string? id = null;
            try
            {
                if (File.Exists(path))
                {
                    var data = File.ReadAllBytes(path);
                    id = Convert.ToHexStringLower(SHA256.HashData(data));
                    if (!_attachments.ContainsKey(id))
                        _attachments[id] = new ResultAttachment(id, Path.GetFileName(path), MediaTypeOf(path), data);
                }
            }
            catch (IOException) { id = null; }
            catch (UnauthorizedAccessException) { id = null; }
            _attachmentByPath[path] = id;
            return id;
        }

        private static string MediaTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };

        public ResultRun Build(ResultRecorder owner, DutRunCompleted dc) => new()
        {
            RunId = runId,
            StationRunId = dc.RunId,
            Station = owner._run.Options.StationName,
            DutId = dc.DutId,
            Order = dc.Order ?? "",
            TestName = owner._run.Definition.Name,
            TestVersion = dc.TestVersion,
            Started = dc.Started,
            Ended = dc.Time,
            Rating = dc.Status.Rating,
            OperatorAbort = dc.Status.OperatorAbort,
            IsDevelopmentRun = _development,
            Structure = owner._structure,
            Events = _events,
            Waveforms = _waveforms,
            Attachments = _attachments.Values.ToList(),
        };
    }
}

/// <summary>Anwendung der unabhängigen Filter (Abschnitt 9.1) auf einen aufgezeichneten Prüflingstest.</summary>
public static class ResultFilter
{
    public const string FileTarget = "Ergebnisdatei";
    public const string DatabaseTarget = "Datenbank";

    public static ResultRun ForFile(ResultRun run, StorageResolver resolver)
        => Apply(run, resolver, StorageTarget.File, StorageTarget.FileWaveforms, FileTarget);

    public static ResultRun ForDatabase(ResultRun run, StorageResolver resolver)
        => Apply(run, resolver, StorageTarget.Database, StorageTarget.DatabaseWaveforms, DatabaseTarget);

    /// <summary>
    /// Ereignisse, Kurven und Anhänge entfernen, die für das Ziel ausgeschlossen sind. Header und Struktur
    /// (technische Mindestmenge) bleiben immer erhalten; Bewertungen im Header ändern sich nicht.
    /// </summary>
    public static ResultRun Apply(ResultRun run, StorageResolver resolver, StorageTarget values, StorageTarget waveforms, string label)
    {
        var keptWaves = run.Waveforms.Where(w => resolver.IsEnabled(w.NodeId, waveforms)).ToList();
        var keptIds = keptWaves.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var events = run.Events
            .Where(e => resolver.IsEnabled(e.NodeId, values))
            .Select(e => e.WaveformIds is null ? e : e with { WaveformIds = e.WaveformIds.Where(keptIds.Contains).ToList() })
            .ToList();
        var usedAttachments = events.Select(e => e.AttachmentId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return run.CopyWith(label, events, keptWaves, run.Attachments.Where(a => usedAttachments.Contains(a.Id)).ToList());
    }
}
