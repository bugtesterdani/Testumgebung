using Testumgebung.Contracts;

namespace Testumgebung.Presentation.Settings;

public static class RemoteStationConfiguration
{
    public static RemoteStationSettings Read(AppSettings settings) => new(settings.ShowPairedStations,
        settings.FirstPairedStation ?? "", settings.SecondPairedStation ?? "", settings.Alarms.ErrorStreak,
        settings.Alarms.OutboxThreshold, settings.Alarms.ResultFolder, settings.Alarms.Calibration);

    public static void Write(AppSettings settings, RemoteStationSettings desired)
    {
        settings.ShowPairedStations = desired.ShowPairedStations;
        settings.FirstPairedStation = desired.FirstPairedStation;
        settings.SecondPairedStation = desired.SecondPairedStation;
        settings.Alarms.ErrorStreak = desired.ErrorStreak;
        settings.Alarms.OutboxThreshold = desired.OutboxThreshold;
        settings.Alarms.ResultFolder = desired.ResultFolderAlarm;
        settings.Alarms.Calibration = desired.CalibrationAlarm;
    }

}
