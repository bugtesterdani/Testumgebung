using System.Globalization;
using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

internal sealed partial class CabPreviewContext
{
    private CabPreviewValue Text(CabContentField field, int id, IReadOnlyList<string> args)
    {
        if (id == 88) return fields[field.Name];
        if (id == 89) return Substring(field, args);
        if (id == 96) return Selection(field, args);
        if (id == 97) return Serial(field, args);
        if (id == 99) return Split(args);
        var value = Resolve(args[0]); RequireText(value);
        var text = value.Text;
        switch (id)
        {
            case 85:
                Note(field, "Unicode-Länge: Vorschau zählt Unicode-Skalarwerte, keine Bytes oder Grapheme.");
                return new(Invariant(text.EnumerateRunes().Count()));
            case 86: Note(field, "Kleinschreibung verwendet die eingestellte Vorschaukultur."); return new(text.ToLower(Culture));
            case 104: Note(field, "Großschreibung verwendet die eingestellte Vorschaukultur."); return new(text.ToUpper(Culture));
            case 87: return new(text.TrimStart(' ', '\t'));
            case 93: return new(text.TrimEnd(' ', '\t'));
            case 102: return new(text.Trim(' ', '\t'));
            default: throw new InvalidOperationException();
        }
    }

    private CabPreviewValue Substring(CabContentField field, IReadOnlyList<string> args)
    {
        var value = fields[field.Name]; RequireResolved(value);
        var start = Integer(args[0]);
        if (value.Bytes is { } binary)
        {
            var count = args.Count > 1 ? Integer(args[1]) : binary.Length - start + 1;
            if (start < 1 || count < 0 || start - 1 > binary.Length - count) throw new FormatException("Teilbereich liegt außerhalb der Binärdaten.");
            return BinaryValue(binary.AsSpan(start - 1, count).ToArray());
        }
        var runes = value.Text.EnumerateRunes().ToArray();
        var length = args.Count > 1 ? Integer(args[1]) : runes.Length - start + 1;
        if (start < 1 || length < 0 || start - 1 > runes.Length - length) throw new FormatException("Teilbereich liegt außerhalb des Textes; Druckerverhalten ist nicht spezifiziert.");
        Note(field, "Teilstrings zählen in der Vorschau Unicode-Skalarwerte.");
        return new(string.Concat(runes.Skip(start - 1).Take(length).Select(r => r.ToString())));
    }

    private CabPreviewValue Split(IReadOnlyList<string> args)
    {
        var value = Resolve(args[0]); RequireText(value);
        var separator = args.Count == 3 ? Resolve(args[2]).Text : "\u001d";
        if (separator.EnumerateRunes().Count() != 1) throw new FormatException("Vorschau benötigt einen einzelnen Trenner; Mehrzeichenverhalten ist nicht spezifiziert.");
        var index = Integer(args[1]);
        var parts = value.Text.Split(separator, StringSplitOptions.None);
        if (index < 1 || index > parts.Length) throw new FormatException("SPLIT-Index liegt außerhalb der Liste.");
        return new(parts[index - 1]);
    }

    private CabPreviewValue Selection(CabContentField field, IReadOnlyList<string> args)
    {
        if (!fields.TryGetValue(args[1], out var list)) throw new FormatException("SELECT benötigt ein zuvor definiertes Listenfeld.");
        RequireText(list);
        var choice = settings.SelectionExamples.GetValueOrDefault(args[1], args[2]);
        var index = Integer(choice);
        if (index < 1 || index > list.Text.Split('\u001d').Length) throw new FormatException("SELECT-Beispielindex liegt außerhalb der Liste.");
        Note(field, "Offline-Beispielauswahl: expliziter Beispielindex bzw. dokumentierte Vorauswahl, keine Benutzereingabe am Drucker.", "preview-selection");
        return new(Invariant(index));
    }

    private CabPreviewValue Serial(CabContentField field, IReadOnlyList<string> args)
    {
        var value = Resolve(args[0]); RequireResolved(value);
        if (args.Count > 1 && args[1].Length > 0) _ = Number(args[1]);
        if (args.Count > 2 && args[2].Length > 0 && Number(args[2]) <= 0) throw new FormatException("SER-Wiederholungszahl muss positiv sein.");
        Note(field, "Vorschau zeigt nur den ersten Serienwert; kein Zähler wird verändert.", "preview-serial");
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (value.Text.Any(c => alphabet.IndexOf(c) < 0 || alphabet.IndexOf(c) >= _base)) throw new FormatException("Serienstart passt nicht zur Zählbasis.");
        return new(FillLeadingZeros(value.Text));
    }
}
