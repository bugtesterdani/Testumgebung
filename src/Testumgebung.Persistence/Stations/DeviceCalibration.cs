namespace Testumgebung.Persistence.Stations;

public sealed record DeviceCalibration(string? SerialNumber = null, DateOnly? CalibratedOn = null,
    int IntervalDays = 0, int WarningDays = 30, bool BlockWhenOverdue = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DateOnly? DueOn => CalibratedOn is { } date && IntervalDays > 0 &&
        IntervalDays <= DateOnly.MaxValue.DayNumber - date.DayNumber ? date.AddDays(IntervalDays) : null;

    public bool IsOverdue(DateOnly today) => DueOn is { } due && today > due;

    public bool IsDueSoon(DateOnly today) => DueOn is { } due && today <= due &&
        due.DayNumber - today.DayNumber <= WarningDays;
}
