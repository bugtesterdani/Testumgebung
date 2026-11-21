using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Lossless framing only. Command semantics and validation are layered above this tree.</summary>
public static class CabParser
{
    public static CabDocument Parse(ReadOnlySpan<byte> source, CabParseOptions? options = null)
        => new Scanner(source.ToArray(), options ?? new()).Read();

    private sealed class Scanner(byte[] data, CabParseOptions options)
    {
        private readonly List<CabSyntaxNode> _nodes = [];
        private readonly List<CabParseDiagnostic> _diagnostics = [];
        private readonly Dictionary<string, (int Count, int First)> _endings = [];
        private string _codePage = options.SetupCodePage;
        private CabUnit _unit;
        private int _position;

        internal CabDocument Read()
        {
            while (_position < data.Length)
            {
                var start = _position;
                if (data[start] == 0x1b) ReadEscape();
                else if (!ReadBlock()) ReadLine();
                if (_position <= start) throw new InvalidOperationException("Parser made no progress.");
            }
            CountTextEndings();
            var ending = _endings.OrderByDescending(e => e.Value.Count).ThenBy(e => e.Value.First)
                .Select(e => e.Key).FirstOrDefault() ?? "\r\n";
            return new(data, GroupJobs(_nodes), _diagnostics.AsReadOnly(), ending);
        }

        private static IReadOnlyList<CabSyntaxNode> GroupJobs(List<CabSyntaxNode> nodes)
        {
            var grouped = new List<CabSyntaxNode>();
            List<CabSyntaxNode>? job = null;
            foreach (var node in nodes)
            {
                if (node.Kind == CabNodeKind.LabelCommand && node.Command == "J")
                {
                    if (job is not null) grouped.Add(Job(job));
                    job = [];
                }
                if (job is null) grouped.Add(node); else job.Add(node);
                if (node.Kind == CabNodeKind.LabelCommand && node.Command == "A" && job is not null)
                { grouped.Add(Job(job)); job = null; }
            }
            if (job is not null) grouped.Add(Job(job));
            return grouped.AsReadOnly();
        }

        private static CabSyntaxNode Job(List<CabSyntaxNode> nodes)
            => new(CabNodeKind.Job, new(nodes[0].Range.Offset, nodes[^1].Range.End - nodes[0].Range.Offset),
                "J", nodes[0].CodePage, nodes[0].Unit, nodes.AsReadOnly());

        private void ReadEscape()
        {
            var start = _position;
            if (Matches(start, "\u001b.")) { ReadBinary(true); return; }
            if (Matches(start, "\u001b:")) { ReadBinary(false); return; }
            var token = EscapeToken(start);
            _position += token.Length;
            if (token is "\u001bo" or "\u001bxin") ReadThroughSemicolon();
            else if (token == "\u001bv" && _position < data.Length && data[_position] is not (10 or 13 or 0x1b)) _position++;
            var parameters = _position > start + token.Length
                ? new[] { Node(CabNodeKind.Parameter, start + token.Length, _position - start - token.Length - (data[_position - 1] == ';' ? 1 : 0), "value") }
                : [];
            _nodes.Add(Node(CabNodeKind.EscapeCommand, start, _position - start, token, parameters));
            if (token == "\u001bo" && data[_position - 1] == ';')
                _codePage = Encoding.ASCII.GetString(data, start + 2, _position - start - 3);
            if (token == "\u001b" || token.Length == 2 && !"?abcfgijloprstvyz\u001b".Contains(token[1]))
                Diagnose("Unbekannter oder unvollständiger ESC-Befehl bleibt erhalten.", start, _position - start);
        }

        private string EscapeToken(int at)
        {
            foreach (var token in new[] { "\u001bend-of-data", "\u001bxout", "\u001bxin", "\u001b!\u001b!", "\u001bp0", "\u001bp1", "\u001b\u001b" })
                if (Matches(at, token)) return token;
            return Encoding.Latin1.GetString(data, at, Math.Min(2, data.Length - at));
        }

        private void ReadThroughSemicolon()
        {
            while (_position < data.Length && data[_position] is not ((byte)';' or 10 or 13 or 0x1b)) _position++;
            if (_position < data.Length && data[_position] == ';') _position++;
            else Diagnose("ESC-Parameterabschluss fehlt.", _position, 0);
        }

        private void ReadBinary(bool escaped)
        {
            var start = _position;
            _position += 2;
            var children = new List<CabSyntaxNode>();
            var closed = false;
            while (_position < data.Length)
            {
                if (escaped && Matches(_position, "\u001b\u001b")) { _position += 2; continue; }
                var close = escaped ? "\u001b." : "\u001bend-of-data";
                if (Matches(_position, close)) { _position += close.Length; closed = true; break; }
                if (escaped && data[_position] == 0x1b)
                {
                    var token = EscapeToken(_position);
                    children.Add(Node(CabNodeKind.EscapeCommand, _position, token.Length, token));
                    _position += token.Length;
                }
                else _position++;
            }
            if (!closed) Diagnose("Binärblock ist nicht abgeschlossen; Restbytes bleiben erhalten.", start, _position - start);
            _nodes.Add(Node(CabNodeKind.BinaryBlock, start, _position - start, escaped ? "ESC." : "ESC:", children.AsReadOnly()));
        }

        private bool ReadBlock()
        {
            if (_position > 0 && data[_position - 1] is not (10 or 13)) return false;
            foreach (var pair in new[] { ("<BASE64>", "</BASE64>"), ("<ABC>", "</ABC>"), ("<ENCRYPTED JOB>", "</ENCRYPTED JOB>") })
            {
                if (!Matches(_position, pair.Item1)) continue;
                var start = _position;
                var end = Find(_position + pair.Item1.Length, pair.Item2);
                _position = end < 0 ? data.Length : end + pair.Item2.Length;
                if (end < 0) Diagnose("Blockabschluss fehlt; Restbytes bleiben erhalten.", start, _position - start);
                _nodes.Add(Node(pair.Item1 == "<BASE64>" ? CabNodeKind.BinaryBlock : CabNodeKind.Block,
                    start, _position - start, pair.Item1));
                return true;
            }
            if (!Matches(_position, "<ENCRYPTED LABEL:")) return false;
            var offset = _position;
            _position = data.Length;
            Diagnose("Verschlüsselter Etikettinhalt hat keine dokumentierte Payload-Grenze und bleibt undurchsichtig.", offset, data.Length - offset);
            _nodes.Add(Node(CabNodeKind.Block, offset, data.Length - offset, "<ENCRYPTED LABEL:"));
            return true;
        }

        private void ReadLine()
        {
            var start = _position;
            while (_position < data.Length && data[_position] is not (10 or 13 or 0x1b)) _position++;
            var end = _position;
            var first = start;
            while (first < end && data[first] is 32 or 9) first++;
            var command = first < end ? ((char)data[first]).ToString() : "";
            var kind = Kind(command);
            // A tag-delimited HTML field can contain physical line endings.
            if (command == "W")
            {
                var html = Find(first, "<HTML>");
                if (html >= first && html < end)
                {
                    var close = Find(html + 6, "</HTML>");
                    if (close >= 0 && !data.AsSpan(first, close + 7 - first).Contains((byte)0x1b)) end = _position = close + 7;
                    else if (close < 0) Diagnose("HTML-Abschluss fehlt.", html, end - html);
                }
            }
            var children = kind is CabNodeKind.ImmediateCommand or CabNodeKind.LabelCommand
                ? CabParameterReader.Read(data, first, end, _codePage, _unit) : [];
            var node = Node(kind, start, end - start, command, children);
            if (kind == CabNodeKind.RawLine && first < end) Diagnose("Unbekannte Rohzeile bleibt erhalten.", first, end - first);
            if (node.Range.Length > 0)
            {
                _nodes.Add(node);
                if (Decode(node) is null) Diagnose("Bytes sind mit der aktiven Codepage nicht dekodierbar.", start, end - start);
            }
            if (command == "m") UpdateUnit(first, end);
            ReadEnding();
        }

        private static CabNodeKind Kind(string command)
        {
            if (command == ";") return CabNodeKind.Comment;
            if (command.Length == 0) return CabNodeKind.RawLine;
            if ("acdefijlmpqrstvxz".Contains(command, StringComparison.Ordinal)) return CabNodeKind.ImmediateCommand;
            return "ABCDEFGHIJMOPRSTWX".Contains(command, StringComparison.Ordinal)
                ? CabNodeKind.LabelCommand : CabNodeKind.RawLine;
        }

        private string? Decode(CabSyntaxNode node)
        {
            try { return CabEncoding.Get(node.CodePage).GetString(data, node.Range.Offset, node.Range.Length); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or DecoderFallbackException) { return null; }
        }

        private void UpdateUnit(int first, int end)
        {
            var argument = Encoding.ASCII.GetString(data, first + 1, end - first - 1).Trim();
            if (argument == "m") _unit = CabUnit.Millimetres;
            else if (argument == "i") _unit = CabUnit.Inches;
            else Diagnose("Unbekannte Maßeinheit; vorherige Einstellung bleibt aktiv.", first, end - first);
        }

        private void ReadEnding()
        {
            if (_position >= data.Length || data[_position] is not (10 or 13)) return;
            var start = _position++;
            if (data[start] == 13 && _position < data.Length && data[_position] == 10) _position++;
            _nodes.Add(Node(CabNodeKind.RawLine, start, _position - start, ""));
        }

        private void CountTextEndings()
        {
            foreach (var node in _nodes.Where(n => n.Kind != CabNodeKind.BinaryBlock
                && !n.Command.StartsWith("<ENCRYPTED", StringComparison.Ordinal)))
            for (var at = node.Range.Offset; at < node.Range.End; at++)
            {
                if (data[at] is not (10 or 13)) continue;
                var start = at;
                if (data[at] == 13 && at + 1 < node.Range.End && data[at + 1] == 10) at++;
                var ending = Encoding.ASCII.GetString(data, start, at - start + 1);
                var previous = _endings.GetValueOrDefault(ending, (0, start));
                _endings[ending] = (previous.Item1 + 1, previous.Item2);
            }
        }

        private CabSyntaxNode Node(CabNodeKind kind, int start, int length, string command, IReadOnlyList<CabSyntaxNode>? children = null)
            => new(kind, new(start, length), command, _codePage, _unit, children ?? []);
        private void Diagnose(string message, int start, int length) => _diagnostics.Add(new(message, new(start, length)));
        private bool Matches(int at, string token)
        {
            if (at > data.Length - token.Length) return false;
            for (var i = 0; i < token.Length; i++)
                if (data[at + i] != token[i]) return false;
            return true;
        }
        private int Find(int at, string token)
        {
            var relative = data.AsSpan(at).IndexOf(Encoding.ASCII.GetBytes(token));
            return relative < 0 ? -1 : at + relative;
        }
    }
}
