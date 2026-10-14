/**
 * @file
 * @brief Ermittelt lokale Betriebsbereitschaft durch ersetzbare Prozess-, Laufzeit- und Treiberprüfungen.
 */

using System.Diagnostics;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Identity;
using Testumgebung.Instruments;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Presentation.Pilot;

public enum CheckLevel
{
    Ok,
    Info,
    Warning,
    Error,
}

public sealed record SystemCheckItem(string Area, string Name, CheckLevel Level, string Detail)
{
    public string LevelText => Level switch
    {
        CheckLevel.Ok => "OK",
        CheckLevel.Info => "Hinweis",
        CheckLevel.Warning => "Warnung",
        _ => "Fehler",
    };
}

/// <summary>Was die Systemprüfung untersucht; alles optional (Tests ersetzen Prozessaufrufe und Bibliotheksprüfungen).</summary>
public sealed class SystemCheckContext
{
    public ResultServices? Services { get; init; }
    public StationCoordinator? Coordinator { get; init; }
    public StationSetupStore? StationStore { get; init; }
    public AccessConfigStore? AccessStore { get; init; }
    public string? PythonExecutable { get; init; }

    /// <summary>Reale Geräte ansprechen (*IDN?, DAQmx-Selbsttest) – dauert je Gerät bis zum Zeitlimit.</summary>
    public bool ProbeDevices { get; init; }

    public long MinimumFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Programm starten und erste Ausgabezeile liefern; null = nicht startbar.</summary>
    public Func<string, string, string?> RunProcess { get; init; } = SystemCheck.FirstOutputLine;

    public Func<bool> VisaAvailable { get; init; } = () => VisaIo.IsAvailable;
    public Func<bool> DaqmxAvailable { get; init; } = () => NativeDaqmxApi.Instance.IsAvailable;
}

/// <summary>
/// Systemprüfung vor dem Pilotbetrieb: Ordner und Rechte, Speicherplatz, Prüfplatzkonfiguration und Geräte, Zugang,
/// Datenbank/Outbox, unvollständige Läufe, Python, C++-Toolchain, VISA und NI-DAQmx. Verändert nichts (außer Testdateien).
/// </summary>
public static class SystemCheck
{
    public static async Task<IReadOnlyList<SystemCheckItem>> RunAsync(SystemCheckContext ctx, CancellationToken cancellationToken = default)
    {
        var items = new List<SystemCheckItem>();
        Folders(ctx, items);
        Stations(ctx, items);
        if (ctx.ProbeDevices) await ProbeAsync(ctx, items, cancellationToken).ConfigureAwait(false);
        Access(ctx, items);
        await DatabaseAsync(ctx, items, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => Runtimes(ctx, items), cancellationToken).ConfigureAwait(false);
        Drivers(ctx, items);
        return items;
    }

    // ------------------------------------------------------------------ Ordner und Speicherplatz

    private static void Folders(SystemCheckContext ctx, List<SystemCheckItem> items)
    {
        const string area = "Ordner";
        var pipeline = ctx.Services?.Pipeline;
        if (pipeline?.Files is { } files)
        {
            items.Add(Writable(area, "Ergebnisordner", files.Folder, CheckLevel.Error));
            FreeSpace(files.Folder, ctx.MinimumFreeBytes, items);
        }
        else
        {
            items.Add(new SystemCheckItem(area, "Ergebnisordner", CheckLevel.Warning, "Dateispeicherung nicht konfiguriert – Ergebnisse nur in der Datenbank."));
        }
        if (ctx.Services is { } s)
        {
            items.Add(Writable(area, "Wiederherstellungsordner", s.RecoveryFolder, CheckLevel.Error));
            items.Add(Writable(area, "Outbox-Ordner", s.Outbox.Folder, CheckLevel.Error));
        }
        if (pipeline?.Journal is { } journal)
            items.Add(Writable(area, "Zwischenaufzeichnung", journal.Folder, CheckLevel.Error));
        if (ctx.StationStore is { } store)
        {
            var dir = Path.GetDirectoryName(store.Path)!;
            var w = Writable(area, "Rechnerkonfiguration (ProgramData)", dir, CheckLevel.Info);
            items.Add(w.Level == CheckLevel.Ok ? w with { Detail = w.Detail + " – dieses Konto darf Prüfplätze und Zugang ändern." }
                : w with { Detail = $"{dir}: nur lesbar – erwartet für Werkerkonten; Änderungen nur durch Administratoren." });
        }
    }

    private static SystemCheckItem Writable(string area, string name, string folder, CheckLevel failLevel)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".schreibtest-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new SystemCheckItem(area, name, CheckLevel.Ok, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SystemCheckItem(area, name, failLevel, $"{folder}: nicht beschreibbar ({ex.Message})");
        }
    }

    private static void FreeSpace(string folder, long minimum, List<SystemCheckItem> items)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            var free = drive.AvailableFreeSpace;
            var gb = free / 1024.0 / 1024 / 1024;
            items.Add(new SystemCheckItem("Ordner", "Freier Speicher", free < minimum ? CheckLevel.Warning : CheckLevel.Ok,
                $"{gb:0.0} GB frei auf {root}{(free < minimum ? " – knapp für Kurvendaten" : "")}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            items.Add(new SystemCheckItem("Ordner", "Freier Speicher", CheckLevel.Info, "nicht ermittelbar: " + ex.Message));
        }
    }

    // ------------------------------------------------------------------ Prüfplätze und Geräte

    private static void Stations(SystemCheckContext ctx, List<SystemCheckItem> items)
    {
        const string area = "Prüfplätze";
        if (ctx.Coordinator is not { } c) return;
        if (c.LoadError is { } error)
        {
            items.Add(new SystemCheckItem(area, "Prüfplatzkonfiguration", CheckLevel.Error, error));
            return;
        }
        var setup = c.Setup;
        var real = setup.Devices.Where(d => !d.IsSimulation).ToList();
        items.Add(c.IsSuggestion
            ? new SystemCheckItem(area, "Prüfplatzkonfiguration", CheckLevel.Warning, "Nur Vorschlag aus dem Projekt – unter „Prüfplatzkonfiguration“ prüfen und speichern.")
            : new SystemCheckItem(area, "Prüfplatzkonfiguration", CheckLevel.Ok,
                $"{setup.Stations.Count} Prüfplatz/-plätze, {setup.Devices.Count} Gerät(e), davon {real.Count} real"));
        if (real.Count == 0)
            items.Add(new SystemCheckItem(area, "Geräte", CheckLevel.Info, "Nur Simulation – für den Pilot reale Geräte zuordnen."));
        foreach (var d in real)
        {
            if (string.IsNullOrWhiteSpace(d.Resource))
                items.Add(new SystemCheckItem(area, d.Name, CheckLevel.Error, $"{d.Adapter}: keine Ressource (VISA-Adresse, LAN-Socket oder DAQmx-Gerät)."));
            else
                items.Add(new SystemCheckItem(area, d.Name, CheckLevel.Info, $"{d.Adapter} {d.Model} · {d.Resource}"));
        }
        var unused = setup.Stations.Where(s => s.Selection is { Enabled: true } sel && sel.UseRules && sel.Rules.Count == 0 && !sel.UseManual && !sel.UseCustom).ToList();
        foreach (var s in unused)
            items.Add(new SystemCheckItem(area, s.Name + " · Auswahl", CheckLevel.Error, "Nur Kennungsregeln aktiv, aber keine Regel – kein Start möglich."));
    }

    private static async Task ProbeAsync(SystemCheckContext ctx, List<SystemCheckItem> items, CancellationToken cancellationToken)
    {
        if (ctx.Coordinator is not { LoadError: null } c) return;
        foreach (var d in c.Setup.Devices.Where(d => !d.IsSimulation && !string.IsNullOrWhiteSpace(d.Resource)))
        {
            try
            {
                var probe = await StationCoordinator.ProbeAsync(d, cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                items.Add(new SystemCheckItem("Geräte", d.Name, probe.Ok ? CheckLevel.Ok : CheckLevel.Error,
                    probe.Identity + (probe.Details.Count > 0 ? " · " + string.Join("; ", probe.Details) : "")));
            }
            catch (TimeoutException)
            {
                items.Add(new SystemCheckItem("Geräte", d.Name, CheckLevel.Error, "keine Antwort innerhalb von 20 s"));
            }
        }
    }

    // ------------------------------------------------------------------ Zugang, Datenbank, Outbox

    private static void Access(SystemCheckContext ctx, List<SystemCheckItem> items)
    {
        const string area = "Zugang";
        if (ctx.AccessStore is not { } store) return;
        var config = store.Load();
        if (config is null)
            items.Add(new SystemCheckItem(area, "Anmeldung", CheckLevel.Warning,
                $"Keine {Path.GetFileName(store.Path)} – Entwicklungsmodus mit simulierter Anmeldung. Für den Pilot Entra-App und Rechte eintragen."));
        else if (config.LoadError is { } error)
            items.Add(new SystemCheckItem(area, "Anmeldung", CheckLevel.Error, "Zugangsdatei beschädigt (niemand hat erweiterte Rechte): " + error));
        else
            items.Add(new SystemCheckItem(area, "Anmeldung", config.UsesEntra ? CheckLevel.Ok : CheckLevel.Warning,
                config.UsesEntra ? $"Entra ID, {config.Users.Count} Konto/Konten mit Rechten" : "Ohne Entra-App (simulierte Anmeldung)."));
    }

    private static async Task DatabaseAsync(SystemCheckContext ctx, List<SystemCheckItem> items, CancellationToken cancellationToken)
    {
        const string area = "Datenbank";
        if (ctx.Services is not { } s) return;
        if (s.ApplyWarning is { } warning) items.Add(new SystemCheckItem(area, "Einstellungen", CheckLevel.Warning, warning));
        switch (s.Database.Inner)
        {
            case null:
                items.Add(new SystemCheckItem(area, "Prüfdatenbank", CheckLevel.Info, "Keine Datenbank – Ergebnisse nur als Datei; Freigaben lokal."));
                break;
            case SqlRunDatabase sql:
                try
                {
                    var text = await sql.TestConnectionAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                    items.Add(new SystemCheckItem(area, "Prüfdatenbank", CheckLevel.Ok, text));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    items.Add(new SystemCheckItem(area, "Prüfdatenbank", CheckLevel.Error, $"{sql.Name}: {ex.Message} – Ergebnisse bleiben in der Outbox."));
                }
                break;
            case { } other:
                items.Add(new SystemCheckItem(area, "Prüfdatenbank", CheckLevel.Warning, $"{other.Name} – nur für Vorführung/Test."));
                break;
        }
        var pending = s.Outbox.Pending.Count;
        items.Add(new SystemCheckItem(area, "Outbox", pending == 0 ? CheckLevel.Ok : CheckLevel.Warning,
            pending == 0 ? "leer" : $"{pending} Run(s) noch nicht übertragen"));
        if (s.Current.Mqtt.Enabled)
            items.Add(new SystemCheckItem(area, "MQTT", CheckLevel.Info, $"{s.Current.Mqtt.Host}:{s.Current.Mqtt.Port} – Verbindungstest unter „Einstellungen“."));
        if (s.Pipeline.Journal is { } journal && journal.FindIncomplete() is { Count: > 0 } incomplete)
            items.Add(new SystemCheckItem(area, "Unvollständige Prüfläufe", CheckLevel.Warning,
                $"{incomplete.Count} aus früheren Sitzungen – unter „Ergebnisse“ als abgebrochen speichern oder verwerfen."));
    }

    // ------------------------------------------------------------------ Laufzeiten und Treiber

    private static void Runtimes(SystemCheckContext ctx, List<SystemCheckItem> items)
    {
        const string area = "Codebausteine";
        var python = ctx.PythonExecutable ?? Testumgebung.Workers.CodeRuntimeOptions.DefaultPython;
        items.Add(ctx.RunProcess(python, "--version") is { } pv
            ? new SystemCheckItem(area, "Python", CheckLevel.Ok, $"{pv} ({python})")
            : new SystemCheckItem(area, "Python", CheckLevel.Warning, $"„{python}“ nicht startbar – Python-Bausteine laufen nicht (TESTUMGEBUNG_PYTHON setzen)."));

        var configured = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CXX");
        string? cxx = null;
        if (!string.IsNullOrWhiteSpace(configured))
            cxx = ctx.RunProcess(configured, Path.GetFileNameWithoutExtension(configured).Equals("cl", StringComparison.OrdinalIgnoreCase) ? "" : "--version") is { } v
                ? $"{v} (TESTUMGEBUNG_CXX)" : null;
        if (cxx is null && OperatingSystem.IsWindows())
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vswhere = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere)
                && ctx.RunProcess(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath") is { Length: > 0 } path)
                cxx = "MSVC Build Tools · " + path;
        }
        cxx ??= ctx.RunProcess("clang++", "--version") ?? ctx.RunProcess("g++", "--version");
        items.Add(cxx is not null
            ? new SystemCheckItem(area, "C++-Toolchain", CheckLevel.Ok, cxx)
            : new SystemCheckItem(area, "C++-Toolchain", CheckLevel.Warning, "Keine gefunden (MSVC Build Tools, clang++ oder g++) – C++-/LabVIEW-Bausteine werden nicht gebaut."));
    }

    private static void Drivers(SystemCheckContext ctx, List<SystemCheckItem> items)
    {
        const string area = "Gerätetreiber";
        var devices = ctx.Coordinator?.Setup.Devices.Where(d => !d.IsSimulation).ToList() ?? [];
        var needsVisa = devices.Any(d => d.Resource is { } r && !r.Contains("SOCKET", StringComparison.OrdinalIgnoreCase)
                                                                 && r.Contains("::", StringComparison.Ordinal) && !IsDaqmx(d));
        var needsDaqmx = devices.Any(IsDaqmx);
        var visa = ctx.VisaAvailable();
        items.Add(new SystemCheckItem(area, "VISA", visa ? CheckLevel.Ok : needsVisa ? CheckLevel.Error : CheckLevel.Info,
            visa ? "VISA-Bibliothek gefunden (Keysight IO Libraries / NI-VISA)"
                : needsVisa ? "VISA fehlt, aber USB/GPIB-Geräte sind zugeordnet – Keysight IO Libraries Suite oder NI-VISA installieren."
                : "Keine VISA-Bibliothek – LAN-Socket-Geräte (TCPIP::…::SOCKET) funktionieren ohne."));
        var daq = ctx.DaqmxAvailable();
        items.Add(new SystemCheckItem(area, "NI-DAQmx", daq ? CheckLevel.Ok : needsDaqmx ? CheckLevel.Error : CheckLevel.Info,
            daq ? "nicaiu.dll gefunden" : needsDaqmx ? "NI-DAQmx fehlt, aber DAQ-Geräte sind zugeordnet." : "Nicht installiert – nur nötig für NI-DAQ-Module."));
    }

    private static bool IsDaqmx(DeviceConfig d) => d.Adapter.Contains("DAQ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Programm mit Zeitlimit starten; erste nicht leere Zeile von stdout/stderr (python --version schreibt teils auf stderr).</summary>
    public static string? FirstOutputLine(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return null;
            }
            var text = stdout.Result + "\n" + stderr.Result;
            return text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? (p.ExitCode == 0 ? "" : null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
