using System.Text.Json.Serialization;

namespace Testumgebung.Presentation.Settings;

public sealed class AlarmSettings
{
    public int ErrorStreak { get; set; }
    public int OutboxThreshold { get; set; }
    public bool ResultFolder { get; set; }
    public bool Calibration { get; set; }

    public bool SmtpEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpStartTls { get; set; } = true;
    public string SmtpUsername { get; set; } = "";
    public string SmtpSender { get; set; } = "";
    public string SmtpRecipient { get; set; } = "";
    [JsonIgnore] public string? SmtpPassword { get; set; }
    public string? ProtectedSmtpPassword { get; set; }

    public bool TeamsEnabled { get; set; }
    [JsonIgnore] public string? TeamsUrl { get; set; }
    public string? ProtectedTeamsUrl { get; set; }
}
