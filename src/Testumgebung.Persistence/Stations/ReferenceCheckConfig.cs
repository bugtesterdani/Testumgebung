using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Persistence.Stations;

/// <summary>Referenzprüfung bei Schichtbeginn und nach konfigurierten Produktionsintervallen.</summary>
public sealed class ReferenceCheckConfig
{
    public bool Enabled { get; set; }
    public string GoodId { get; set; } = "";
    public string BadId { get; set; } = "";
    public string ShiftStarts { get; set; } = "06:00;14:00;22:00";
    public int DutInterval { get; set; }
    public double HoursInterval { get; set; }

    public string? Validate()
    {
        if (ReadShifts().Length == 0) return "Schichtbeginn als Uhrzeit im Format HH:mm angeben.";
        if (!Enabled) return null;
        if (string.IsNullOrWhiteSpace(GoodId) || string.IsNullOrWhiteSpace(BadId) || GoodId.Trim() == BadId.Trim())
            return "Für die Referenzprüfung zwei unterschiedliche Kennungen für Gut- und Schlechtmuster eintragen.";
        if (DutInterval < 0 || HoursInterval < 0 || !double.IsFinite(HoursInterval))
            return "Referenzintervalle müssen positiv sein oder 0 (ausgeschaltet).";
        return null;
    }

    public TimeOnly[] ReadShifts()
    {
        var shifts = new List<TimeOnly>();
        foreach (var part in (ShiftStarts ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TimeOnly.TryParseExact(part, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start)) return [];
            shifts.Add(start);
        }
        return shifts.Distinct().Order().ToArray();
    }

    public ResultRunKind Match(string id)
        => id == GoodId?.Trim() ? ResultRunKind.GoodReference
            : id == BadId?.Trim() ? ResultRunKind.BadReference : ResultRunKind.Production;
}

/// <summary>Referenzfreigabe eines Prüfplatzes. Nach einem Neustart ist eine erneute Prüfung erforderlich.</summary>
public sealed class ReferenceCheckState
{
    private DateTime? _shift;
    private DateTimeOffset? _qualified;
    private int _productionCount;
    private bool _good, _bad;

    public bool IsDue(ReferenceCheckConfig config, DateTimeOffset now)
    {
        if (!config.Enabled) return false;
        if (config.Validate() is not null) return true;
        if (_qualified is null || _shift != ShiftStart(config, now.LocalDateTime)) return true;
        return (config.DutInterval > 0 && _productionCount >= config.DutInterval)
            || (config.HoursInterval > 0 && (now - _qualified.Value).TotalHours >= config.HoursInterval);
    }

    public void RecordProduction() => _productionCount++;

    public bool RecordReference(ReferenceCheckConfig config, ResultRunKind kind, RunStatus status, DateTimeOffset now)
    {
        if (!config.Enabled || config.Validate() is not null || kind == ResultRunKind.Production) return false;
        var shift = ShiftStart(config, now.LocalDateTime);
        if (_shift != shift || (_qualified is not null && IsDue(config, now)))
        {
            _shift = shift;
            _qualified = null;
            _good = _bad = false;
        }

        var expected = kind == ResultRunKind.GoodReference ? ResultRating.Pass : ResultRating.Fail;
        var passed = !status.OperatorAbort && status.Rating == expected;
        if (kind == ResultRunKind.GoodReference) _good = passed;
        else _bad = passed;
        if (_good && _bad && _qualified is null)
        {
            _qualified = now;
            _productionCount = 0;
        }
        if (!passed) _qualified = null;
        return passed;
    }

    private static DateTime ShiftStart(ReferenceCheckConfig config, DateTime now)
    {
        var shifts = config.ReadShifts();
        var start = shifts.LastOrDefault(time => time.ToTimeSpan() <= now.TimeOfDay, shifts[^1]);
        return now.Date.AddDays(start.ToTimeSpan() > now.TimeOfDay ? -1 : 0).Add(start.ToTimeSpan());
    }
}
