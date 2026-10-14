/**
 * @file
 * @brief Verknüpft simulierte Auftragsbeispiele mit passenden Prüfprogrammen für die lokale Entwicklung.
 */

namespace Testumgebung.Persistence.Stations;

/// <summary>Lokales Auftragsbeispiel mit ausschließlich simulierten Geräten, ohne Änderung der Prüfplatzkonfiguration.</summary>
public static class OrderExample
{
    public static (TestProject Project, StationSetup Setup) Load(string examplesDirectory)
    {
        var project = new ProjectSerializer().Load(Path.Combine(examplesDirectory, "programm-a.tproj.json"));
        var setup = StationSetup.Default(project, 1);
        setup.ExampleProjects = new[] { "a", "b", "c", "d" }
            .Select(letter => new ProjectSerializer().Load(Path.Combine(examplesDirectory, $"programm-{letter}.tproj.json"))).ToList();
        foreach (var example in setup.ExampleProjects)
            setup.Stations[0].Resources.AddRange(StationSetup.Suggest(example, setup.Stations[0].Resources));
        setup.Stations[0].Selection = new SelectionConfig
        {
            OrderSession = true, UseCustom = true, UseRules = false, UseManual = false,
            CustomProject = Path.Combine(examplesDirectory, "auswahl-auftrag.tproj.json"),
        };
        return (project, setup);
    }
}
