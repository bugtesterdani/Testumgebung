namespace Testumgebung.Presentation.ViewModels;

public sealed record ProjectTemplate(string Title, string Description, string? Path);

public static class ProjectTemplates
{
    public static IReadOnlyList<ProjectTemplate> InFolder(string folder)
    {
        ProjectTemplate[] entries =
        [
            new("Leeres Prüfprogramm", "Start, Prüfschleife und Abschluss für einen eigenen Ablauf.", null),
            new("Spannungsprüfung", "Ein einfaches Messprogramm mit simuliertem Gerät und Grenzwerten.", Path.Combine(folder, "programm-a.tproj.json")),
            new("Grenzbandprüfung", "Messwerte gegen ein Grenzband prüfen und als Verlauf anzeigen.", Path.Combine(folder, "grenzband-demo.tproj.json")),
            new("Werkerkurve mit Sofortabbruch", "Kurvenaufzeichnung mit einem vorzeitigen Sprung in die Exit-Gruppe.", Path.Combine(folder, "werkerkurve-direkter-exit.tproj.json")),
            new("Werkerkurve mit Messzeitpause", "Eine laufende Aufzeichnung pausieren und anschließend fortsetzen.", Path.Combine(folder, "werkerkurve-messzeitpause.tproj.json")),
        ];
        return entries.Where(template => template.Path is null || File.Exists(template.Path)).ToArray();
    }
}
