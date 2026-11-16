namespace Testumgebung.Contracts;

/// <summary>Editable, non-secret settings exchanged with the remote administration service.</summary>
public sealed record RemoteStationSettings(bool ShowPairedStations, string FirstPairedStation, string SecondPairedStation,
    int ErrorStreak, int OutboxThreshold, bool ResultFolderAlarm, bool CalibrationAlarm)
{
    public bool IsValid => FirstPairedStation is { Length: <= 120 } && SecondPairedStation is { Length: <= 120 }
        && ErrorStreak >= 0 && OutboxThreshold >= 0
        && (!ShowPairedStations || !string.IsNullOrWhiteSpace(FirstPairedStation)
            && !string.IsNullOrWhiteSpace(SecondPairedStation) && FirstPairedStation != SecondPairedStation);
}

public sealed record RemoteSettingsReport(RemoteStationSettings Current, string[] Stations, long? Revision = null, string? Status = null);

public sealed record RemoteSettingsChange(long Revision, RemoteStationSettings Expected, RemoteStationSettings Desired);
