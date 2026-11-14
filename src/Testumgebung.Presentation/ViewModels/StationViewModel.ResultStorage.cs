using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Domain.Storage;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Hardware;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationViewModel
{
    // ------------------------------------------------------------------ Ergebnisdatei und Datenbank

    private async Task StoreResultAsync(ResultRun recorded, StorageResolver resolver)
    {
        StorageReport report;
        try
        {
            report = await _results!.ProcessAsync(recorded, resolver).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report = new StorageReport(recorded.RunId, recorded.DutId, new FileSaveResult(null, ex.Message, false), null);
        }
        _ui.Post(() => ApplyStorageReport(report));
    }

    private void ApplyStorageReport(StorageReport report)
    {
        StorageReports.Add(report);
        while (StorageReports.Count > 50) StorageReports.RemoveAt(0);
        var dut = report.DutId == "" ? "ohne Kennung" : report.DutId;
        var now = _clock.Now;
        if (report.File is { } file)
        {
            if (file.Success)
            {
                LastResultFile = file.Path;
                StorageError = null;
                AddProtocol(new ProtocolEntry(now, ProtocolKind.Info, $"Ergebnisdatei gespeichert ({dut})", "—", "", "", null, Path.GetFileName(file.Path!)));
            }
            else
            {
                StorageError = file.Recovered
                    ? $"{file.Error} Lokal gesichert: {file.Path}"
                    : file.Error;
                AddProtocol(new ProtocolEntry(now, ProtocolKind.Error, $"Ergebnisdatei für {dut} nicht gespeichert", "—", "", "", ResultRating.Error, StorageError ?? ""));
            }
        }
        if (report.Database is { Stored: false } db)
        {
            var warning = new StorageWarning(report.RunId, dut,
                $"Datenbank-Speicherung für Prüfling {dut} gescheitert. Die Daten liegen sicher in der lokalen Outbox und werden automatisch nachgereicht.",
                db.Error ?? "");
            StorageWarnings.Add(warning);
            AddProtocol(new ProtocolEntry(now, ProtocolKind.Error, $"DB-Speicherung für {dut} gescheitert – Outbox", "—", "", "", null, db.Error ?? ""));
            _ = HideWarningLater(warning);
        }
    }

    private async Task HideWarningLater(StorageWarning warning)
    {
        try { await WarningDelay(WarningDuration).ConfigureAwait(false); }
        catch (Exception) { /* Anzeige-Timer */ }
        _ui.Post(() => StorageWarnings.Remove(warning));
    }
}
