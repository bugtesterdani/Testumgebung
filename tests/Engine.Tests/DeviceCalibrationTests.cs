using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Stations;

namespace Testumgebung.Engine.Tests;

public sealed class DeviceCalibrationTests
{
    [Fact]
    public void Calibration_dates_include_the_due_day_in_the_warning_period()
    {
        var date = new DateOnly(2026, 1, 1);
        var calibration = new DeviceCalibration("SN-1", date, 10, 2, true);
        Assert.Equal(date.AddDays(10), calibration.DueOn);
        Assert.False(calibration.IsDueSoon(date.AddDays(7)));
        Assert.True(calibration.IsDueSoon(date.AddDays(8)));
        Assert.True(calibration.IsDueSoon(date.AddDays(10)));
        Assert.True(calibration.IsOverdue(date.AddDays(11)));
        Assert.Null(new DeviceCalibration(CalibratedOn: DateOnly.MaxValue, IntervalDays: 1).DueOn);

        var device = new DeviceConfig { Adapter = "Real", Name = "Multimeter", Calibration = calibration };
        Assert.Contains("abgelaufen", Assert.Single(StationHardware.CalibrationWarnings([device], date.AddDays(11))));
        Assert.Contains("fällig", Assert.Single(StationHardware.CalibrationWarnings([device], date.AddDays(8))));
        Assert.Null(StationHardware.CalibrationRefusal([device], date.AddDays(10), developmentRun: false));
        Assert.Contains("Start gesperrt", StationHardware.CalibrationRefusal([device], date.AddDays(11), developmentRun: false));
        Assert.Null(StationHardware.CalibrationRefusal([device], date.AddDays(11), developmentRun: true));
        device.Calibration = calibration with { BlockWhenOverdue = false };
        Assert.Null(StationHardware.CalibrationRefusal([device], date.AddDays(11), developmentRun: false));
    }

    [Fact]
    public void Older_device_configuration_and_result_files_keep_optional_calibration()
    {
        var older = System.Text.Json.JsonSerializer.Deserialize<DeviceConfig>("{}")!;
        Assert.Null(older.Calibration);
        var oldResult = new ResultRun { RunId = "old" };
        Assert.Empty(ResultFile.Read(ResultFile.ToBytes(oldResult)).Run.Devices);

        var device = new ResultDevice("Multimeter", "SN-1", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        var result = new ResultRun { RunId = "new", Devices = [device] };
        var filtered = result.CopyWith("Ergebnisdatei", [], [], []);
        var loaded = ResultFile.Read(ResultFile.ToBytes(filtered));
        Assert.Empty(loaded.Problems);
        Assert.Equal(device, Assert.Single(loaded.Run.Devices));
        Assert.NotNull(Reports.TestReport(loaded.Run));
    }
}
