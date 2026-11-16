using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Curves;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class CurvesViewModel
{
    // ------------------------------------------------------------------ Geräteeignung und Testausgabe

    public ObservableCollection<string> Outputs { get; } = [];

    public string? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!SetProperty(ref _selectedOutput, value)) return;
            ResetTestOutput();
            CheckSuitability();
        }
    }

    public ObservableCollection<SuitabilityIssue> Suitability { get; } = [];
    public bool IsSuitable => Suitability.Count > 0 && Suitability.All(issue => !issue.IsError);
    public string CapabilitiesText => _testOutput is { } output
        ? string.Create(CultureInfo.InvariantCulture, $"{output.Name}: {output.Unit} {output.Min:0.##}…{output.Max:0.##}, Kanäle {output.Channels}, Abtastraten {output.SampleRates} Hz{(output.ExplicitTimes ? ", explizite Zeit" : "")}, max. {output.MaxSamples} Samples")
        : "";

    public ObservableCollection<string> OutputLog { get; } = [];

    public string OutputStateText
    {
        get => _outputStateText;
        private set => SetProperty(ref _outputStateText, value);
    }

    public RelayCommand LoadCommand { get; }
    public RelayCommand TransferCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    private CurveDefinition? Current => _working ?? _selected?.Curve;
    private bool CanOutput => Current is not null && _testOutput is not null && !_outputBusy;

    private void RefreshOutputs()
    {
        var keep = _selectedOutput;
        Outputs.Clear();
        foreach (var output in _editor.Project.SimulationOutputs) Outputs.Add(output.Name);
        _selectedOutput = Outputs.Contains(keep ?? "") ? keep : Outputs.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedOutput));
        ResetTestOutput();
        CheckSuitability();
    }

    private void ResetTestOutput()
    {
        _testSim = null;
        _testOutput = _editor.Project.SimulationOutputs.FirstOrDefault(output => output.Name == _selectedOutput);
        OutputLog.Clear();
        OutputStateText = _testOutput is null ? "Kein Ausgabegerät im Projekt" : "Leerlauf";
        OnPropertyChanged(nameof(CapabilitiesText));
        RaiseOutput();
    }

    private void RaiseOutput()
    {
        foreach (var relayCommand in new[] { LoadCommand, TransferCommand, StartCommand, StopCommand }) relayCommand.RaiseCanExecuteChanged();
    }

    private void CheckSuitability()
    {
        Suitability.Clear();
        if (Current is { } curve && _testOutput is not null)
        {
            var sim = EnsureSim();
            foreach (var issue in OutputSuitability.Check(sim.GetOutputCapabilities(_testOutput.Channel), curve.ToOutput())) Suitability.Add(issue);
        }
        OnPropertyChanged(nameof(IsSuitable));
        RaiseOutput();
    }

    private SimulationAdapter EnsureSim()
        => _testSim ??= SampleProjects.CreateSimulation(_editor.Project, seed: 7).Adapter;

    private async Task OutputAsync(CurveCommand command)
    {
        if (_testOutput is null) return;
        var sim = EnsureSim();
        var channel = _testOutput.Channel;
        _outputBusy = true;
        RaiseOutput();
        try
        {
            switch (command)
            {
                case CurveCommand.Load:
                    await sim.LoadAsync(channel, Current!.ToOutput(), CancellationToken.None).ConfigureAwait(true);
                    Log($"Geladen: {Current!.Key}");
                    break;
                case CurveCommand.Transfer:
                    await sim.TransferAsync(channel, CancellationToken.None).ConfigureAwait(true);
                    Log($"Übertragen: {sim.LoadedCurve(channel)}");
                    break;
                case CurveCommand.Start:
                    await sim.StartAsync(channel, CancellationToken.None).ConfigureAwait(true);
                    Log($"Gestartet: {sim.LoadedCurve(channel)}");
                    await FeedbackAsync(sim).ConfigureAwait(true);
                    break;
                case CurveCommand.Stop:
                    await sim.StopAsync(channel, CancellationToken.None).ConfigureAwait(true);
                    Log("Gestoppt");
                    break;
            }
            Error = null;
        }
        catch (Exception ex) when (ex is OutputSequenceException or InvalidOperationException or ArgumentException)
        {
            Log("Abgelehnt: " + ex.Message);
            Error = ex.Message;
        }
        finally
        {
            _outputBusy = false;
            OutputStateText = StateText(sim.GetOutputState(channel), sim.LoadedCurve(channel));
            RaiseOutput();
        }
    }

    /// <summary>Rückmessung über die zugeordneten Messkanäle (Simulation): Effektivwert je Phase.</summary>
    private async Task FeedbackAsync(SimulationAdapter sim)
    {
        if (_testOutput is null || Current is not { SampleRate: > 0 } curve) return;
        var pairs = _testOutput.Feedback.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries)).Where(parts => parts.Length == 2).ToList();
        if (pairs.Count == 0) return;
        var set = await sim.CaptureAsync(pairs.Select(parts => parts[1]).ToList(), curve.SampleRate, Math.Max(1, curve.Count), CancellationToken.None).ConfigureAwait(true);
        Log("Rückmessung: " + string.Join(", ", set.Channels.Select((waveform, index) =>
            string.Create(CultureInfo.InvariantCulture, $"{pairs[index][0]}→{waveform.Channel} {waveform.Rms():0.##} {_testOutput.Unit} eff."))));
    }

    private static string StateText(OutputState state, string? key) => state switch
    {
        OutputState.Idle => "Leerlauf",
        OutputState.Loaded => $"Geladen ({key}) – nächster Schritt: Übertragen",
        OutputState.Transferred => $"Im Gerät ({key}) – bereit zum Starten",
        OutputState.Running => $"Ausgabe läuft ({key})",
        OutputState.Stopped => $"Gestoppt ({key}) – erneut starten möglich",
        _ => state.ToString(),
    };

    private void Log(string text) => OutputLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
}
