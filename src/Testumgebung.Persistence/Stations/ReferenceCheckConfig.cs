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
}
