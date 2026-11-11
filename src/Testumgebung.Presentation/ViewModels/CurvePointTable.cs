using System.Globalization;
using Testumgebung.Domain.Curves;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Hält nur bearbeitete Zeilen vor. Die Tabelle lädt auch bei großen Kurven jeweils eine Seite.</summary>
public sealed class CurvePointTable(CurveDefinition source)
{
    public const int PageSize = 500;
    private readonly Dictionary<int, Dictionary<int, Row>> _edits = [];
    private readonly Dictionary<int, int> _added = [];
    public CurveDefinition Source { get; } = source;

    public int PageCount(int channel) => Math.Max(1, (SlotCount(channel) + PageSize - 1) / PageSize);

    private int SlotCount(int channel) => Source.Count + _added.GetValueOrDefault(channel);

    public IEnumerable<Row> ReadPage(int channel, int page)
    {
        var end = Math.Min(SlotCount(channel), (page + 1) * PageSize);
        for (var index = page * PageSize; index < end; index++)
        {
            if (_edits.GetValueOrDefault(channel)?.GetValueOrDefault(index) is { } edit)
            {
                if (!edit.Removed) yield return edit;
            }
            else
            {
                yield return new Row(index, Format(Source.TimeOf(index)), Format(Source.Channels[channel].Samples[index]));
            }
        }
    }

    public Row Add(int channel)
    {
        var index = SlotCount(channel);
        _added[channel] = _added.GetValueOrDefault(channel) + 1;
        var row = new Row(index, "", "");
        Edits(channel)[index] = row;
        return row;
    }

    public void Remove(int channel, Row row)
    {
        row.Removed = true;
        Edits(channel)[row.Index] = row;
    }

    public void KeepChanges(int channel, IEnumerable<Row> rows)
    {
        foreach (var row in rows.Where(row => row.Changed))
            Edits(channel)[row.Index] = row;
    }

    public CurveDefinition Build()
    {
        var result = Source;
        foreach (var (channel, edits) in _edits.OrderBy(entry => entry.Key))
        {
            var points = new List<(double Time, double Value)>(SlotCount(channel) - edits.Values.Count(row => row.Removed));
            for (var index = 0; index < SlotCount(channel); index++)
            {
                if (edits.TryGetValue(index, out var row))
                {
                    if (!row.Removed)
                        points.Add((Parse(row.Time, "Zeit", channel, index), Parse(row.Value, "Wert", channel, index)));
                }
                else
                    points.Add((Source.TimeOf(index), Source.Channels[channel].Samples[index]));
            }
            if (points.Count == Source.Count && points.Select((point, index) =>
                point.Time == Source.TimeOf(index) && point.Value == Source.Channels[channel].Samples[index]).All(equal => equal))
                continue;
            result = CurveOps.ReplacePoints(result, channel, points.ToArray());
        }
        return result;
    }

    private Dictionary<int, Row> Edits(int channel)
    {
        if (!_edits.TryGetValue(channel, out var rows)) _edits[channel] = rows = [];
        return rows;
    }

    private double Parse(string text, string column, int channel, int index)
    {
        if ((double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
             double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value))
            return value;
        throw new ArgumentException($"{Source.Channels[channel].Id}, Punkt {index + 1}: {column} muss eine endliche Zahl sein.");
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.CurrentCulture);

    public sealed class Row
    {
        private readonly string _originalTime;
        private readonly string _originalValue;

        internal Row(int index, string time, string value)
        {
            Index = index;
            Time = _originalTime = time;
            Value = _originalValue = value;
        }

        public int Index { get; }
        public int Number => Index + 1;
        public string Time { get; set; }
        public string Value { get; set; }
        internal bool Removed { get; set; }
        internal bool Changed => Time != _originalTime || Value != _originalValue;
    }
}
