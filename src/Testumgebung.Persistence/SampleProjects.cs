/**
 * @file
 * @brief Erzeugt lokale Beispielprüfprogramme aus denselben Definitionen und Bausteinen wie Benutzerprojekte.
 */

using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;

namespace Testumgebung.Persistence;

/// <summary>Beispielprojekt „AC-Prüfung“ wie in den Entwürfen – vollständig aus Katalogbausteinen.</summary>
public static class SampleProjects
{
    public static TestProject AcPruefung(BlockCatalog? catalog = null)
    {
        var blockCatalog = catalog ?? BlockCatalog.Default;
        StepDefinition S(string type, string id, string name, params (string Key, string Value)[] entry)
            => blockCatalog.Create(type, id, name, entry.ToDictionary(entry2 => entry2.Key, entry2 => entry2.Value));

        var init = new GroupDefinition("init", "Test Start / Init")
            .Add(
                S(BlockCatalog.SetValue, "init.auftrag", "Auftrag übernehmen", ("Ziel", "Auftrag"), ("Ausdruck", "Auftrag")),
                S(BlockCatalog.DigitalWrite, "init.grundzustand", "Ausgänge in Grundzustand", ("Ressource", "Digital_Start"), ("Wert", "false")),
                S(BlockCatalog.CurveOutput, "init.kurve.laden", "AC-Profil laden", (CurveOutputBlocks.ActionParameter, CurveOutputBlocks.Load), ("Ressource", "AC_Kurvenquelle"), ("Kurve", "AC_Profil"), ("Version", "0.4")),
                S(BlockCatalog.CurveOutput, "init.kurve.uebertragen", "AC-Profil übertragen", (CurveOutputBlocks.ActionParameter, CurveOutputBlocks.Transfer), ("Ressource", "AC_Kurvenquelle")),
                S(BlockCatalog.CurveOutput, "init.kurve.starten", "Kurvenausgabe starten", (CurveOutputBlocks.ActionParameter, CurveOutputBlocks.Start), ("Ressource", "AC_Kurvenquelle")));

        var belastung = new GroupDefinition("belastung", "Messgruppe – Belastung")
        {
            Repetition = Repetition.Times(3),
            ResultBeforeRepeat = RepeatResultPolicy.ResetToNResult,
            ResourceTimeout = TimeSpan.FromSeconds(30),
        };
        belastung.Resources.Add("AC_Quelle");
        belastung.Var("Versuch", VarType.Int, 0).Var("StromLast", VarType.Double, 0.0).Add(
            S(BlockCatalog.SetValue, "belastung.versuch", "Versuch zählen", ("Ziel", "Versuch"), ("Ausdruck", "Versuch + 1")),
            S(BlockCatalog.Output, "belastung.ein", "AC-Ausgabe einschalten", ("Ressource", "AC_Quelle"), ("Wert", "Nennspannung")),
            S(BlockCatalog.Wait, "belastung.warten", "Einschwingen", ("Dauer", "400")),
            S(BlockCatalog.Measure, "belastung.messung", "Messung unter Last",
                ("Ressource", "Strom_Pruefling"), ("Ziel", "StromLast"), ("Untergrenze", "0"), ("Obergrenze", "4.5"), ("Einheit", "A")));
        belastung.ExitGroup = new GroupDefinition("belastung.exit", "Exit – Abschalten").Add(
            new GroupDefinition("belastung.exit.erhalten", "Reservierte Quelle abschalten")
            { EntryCondition = TestRun.ResourcesAcquiredVariable }.Add(
                S(BlockCatalog.Output, "belastung.aus", "AC-Ausgabe stoppen", ("Ressource", "AC_Quelle"), ("Wert", "0"))));

        var pruefling = new GroupDefinition("pruefling", "Prüfling") { IsDutRun = true }
            .Var("Kennung", VarType.String)
            .Var("Sichtpruefung", VarType.String)
            .Var("Temperatur", VarType.Double, 22.0)
            .Var("Spannung", VarType.Double)
            .Var("GrenzwertMin", VarType.Double, 220.0)
            .Var("GrenzwertMax", VarType.Double, 240.0)
            .Add(
                S(BlockCatalog.Operator, "kennung", "Kennung erfassen",
                    ("Art", "Eingabe"), ("Text", "Seriennummer des Prüflings scannen oder eingeben"), ("Datentyp", "Text"),
                    ("Pflicht", "true"), ("Ziel", "Kennung")),
                S(BlockCatalog.Operator, "sicht", "Sichtprüfung",
                    ("Art", "Auswahl"), ("Text", "Gehäuse, Stecker und Kennzeichnung prüfen"), ("ErlaubteWerte", "i. O.;n. i. O."),
                    ("Pflicht", "true"), ("Ziel", "Sichtpruefung")),
                S(BlockCatalog.Evaluate, "sicht.bewertung", "Bewertung Sichtprüfung",
                    ("Bedingung", "Sichtpruefung == \"i. O.\""), ("Wert", "Sichtpruefung"), ("Meldung", "Sichtprüfung n. i. O.")),
                S(BlockCatalog.Operator, "temperatur", "Gehäusetemperatur erfassen",
                    ("Art", "Eingabe"), ("Text", "Gehäusetemperatur erfassen"), ("Datentyp", "Zahl"), ("Standardwert", "22"),
                    ("Min", "0"), ("Max", "50"), ("Einheit", "°C"), ("Ziel", "Temperatur")),
                S(BlockCatalog.Operator, "anschluss", "Prüfling anschließen",
                    ("Art", "Anweisung"), ("Text", "Prüfling an Prüfadapter anschließen und bestätigen")),
                S(BlockCatalog.Measure, "spannung", "Messung Spannung",
                    ("Ressource", "Spannung_Pruefling"), ("Ziel", "Spannung"), ("Untergrenze", "GrenzwertMin"), ("Obergrenze", "GrenzwertMax"), ("Einheit", "V")),
                S(BlockCatalog.Capture, "netzkurven", "Netzkurven aufnehmen",
                    ("Ressourcen", "Spannung_L1, Spannung_L2, Spannung_L3"), ("Abtastrate", "5000"), ("Dauer", "40"),
                    ("Untergrenze", "GrenzwertMin"), ("Obergrenze", "GrenzwertMax"), ("Einheit", "V")),
                belastung,
                S(BlockCatalog.Evaluate, "bewertung", "Bewertung Temperatur",
                    ("Bedingung", "Temperatur <= 45"), ("Wert", "Temperatur"), ("Einheit", "°C"), ("Meldung", "Gehäuse zu warm")));
        pruefling.ExitGroup = new GroupDefinition("pruefling.exit", "Exit Prüfling").Add(
            S(BlockCatalog.DigitalWrite, "pruefling.sicher", "Sicheren Zustand herstellen", ("Ressource", "Digital_Start"), ("Wert", "false")));

        var loop = new GroupDefinition("loop", "Test Loop / Testing") { Repetition = Repetition.Endless }.Add(pruefling);

        var fin = new GroupDefinition("fin", "Test End / Finalization").Add(
            S(BlockCatalog.CurveOutput, "fin.kurve.stoppen", "Kurvenausgabe stoppen", (CurveOutputBlocks.ActionParameter, CurveOutputBlocks.Stop), ("Ressource", "AC_Kurvenquelle")),
            S(BlockCatalog.DigitalWrite, "fin.reset", "Ausgänge zurücksetzen", ("Ressource", "Digital_Start"), ("Wert", "false")));

        var definition = new TestDefinition("AC-Prüfung", "1.5", init, loop, fin);
        // Ausgabekurve AC_Profil: dreiphasig 230 V eff. (325 V Scheitel), 50 Hz, 20 kHz, zwei Perioden (nahtlos wiederholbar)
        definition.Curves.Add(CurveGenerator.Generate(new GeneratorSettings
        {
            Kind = GeneratorKind.Sine, Amplitude = 325, Frequency = 50, Phases = [0, 120, 240], SampleRate = 20000, Duration = 0.04,
        }, "AC_Profil", "AC_Profil", "0.4", "Beispielprojekt", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2))));
        // Gemeinsame Auftragsdaten im Hauptablauf: Init ermittelt sie, der Loop liest sie.
        definition.Root.Var("Auftrag", VarType.String, "FA-2038").Var("Nennspannung", VarType.Double, 230.0);
        var project = new TestProject(definition)
        {
            Description = "Beispielprojekt: Prüfling erfassen, Sichtprüfung, Spannung, Netzkurven L1–L3 und Strom unter Last.",
        };
        project.Simulation.AddRange(
        [
            new SimulatedResource("Spannung_Pruefling", "ai0", "V", 230.1, Noise: 0.6),
            new SimulatedResource("Strom_Pruefling", "ai1", "A", 4.2, Noise: 0.1),
            new SimulatedResource("AC_Quelle", "L1/L2/L3", "V", 0, RequiresLock: true, Min: 0, Max: 300),
            new SimulatedResource("Digital_Start", "do0", "bool", 0, Min: 0, Max: 1),
            new SimulatedResource("Spannung_L1", "u1", "V", 230.0, Frequency: 50, Phase: 0),
            new SimulatedResource("Spannung_L2", "u2", "V", 229.5, Frequency: 50, Phase: 120),
            new SimulatedResource("Spannung_L3", "u3", "V", 230.4, Frequency: 50, Phase: 240),
        ]);
        project.SimulationOutputs.Add(new SimulatedOutput("AC_Kurvenquelle", "ac_out", "V", -400, 400, "20000;10000", "L1;L2;L3",
            Feedback: "L1=u1;L2=u2;L3=u3"));
        return project;
    }

    /// <summary>Simulationsadapter und Prüfplatzzuordnung für ein Projekt erzeugen.</summary>
    public static (SimulationAdapter Adapter, HardwareRegistry Registry) CreateSimulation(TestProject project, int seed = 1)
    {
        var random = new Random(seed);
        var adapter = new SimulationAdapter();
        var registry = new HardwareRegistry();
        foreach (var resource in project.Simulation)
        {
            adapter.Channel(resource.Channel, new ChannelCapabilities(resource.Unit, resource.Min, resource.Max));
            if (resource.Noise > 0)
            {
                var value = resource.Value;
                var noise = resource.Noise;
                adapter.SetInput(resource.Channel, () =>
                {
                    lock (random) return Math.Round(value + (random.NextDouble() * 2 - 1) * noise, 3);
                });
            }
            else if (resource.Value != 0)
            {
                adapter.SetInput(resource.Channel, resource.Value);
            }
            if (resource.Frequency > 0) adapter.SetSine(resource.Channel, resource.Value, resource.Frequency, resource.Phase);
            registry.Bind(resource.Name, adapter, resource.Channel, resource.RequiresLock);
        }
        foreach (var output in project.SimulationOutputs)
        {
            var rates = output.SampleRates.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => double.Parse(entry, CultureInfo.InvariantCulture)).ToList();
            var feedback = output.Feedback.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries)).Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1]);
            adapter.ConfigureOutput(output.Channel, new OutputCapabilities($"{output.Name} (Simulation)", output.Unit, output.Min, output.Max,
                output.Channels.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), rates, output.ExplicitTimes, output.MaxSamples), feedback);
            registry.Bind(output.Name, adapter, output.Channel, output.RequiresLock);
        }
        foreach (var communication in project.SimulationComm)
        {
            adapter.ConfigureCommunication(communication.Channel, Testumgebung.Hardware.CommSimulation.Create(communication.Kind, communication.Rules, seed));
            registry.Bind(communication.Name, adapter, communication.Channel, communication.RequiresLock);
        }
        return (adapter, registry);
    }
}
