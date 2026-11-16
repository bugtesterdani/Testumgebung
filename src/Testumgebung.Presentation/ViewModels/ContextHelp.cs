using System.Text;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Presentation.ViewModels;

public sealed record HandbookTopic(string FileName, string Heading);
public sealed record HandbookContent(HandbookTopic Topic, string Section, BlockDescriptor? Block);

public static class ContextHelp
{
    private const string UserHandbook = "benutzerhandbuch.md";
    private const string DeveloperHandbook = "entwickler.md";

    private static readonly IReadOnlyDictionary<ShellPage, HandbookTopic> Pages = new Dictionary<ShellPage, HandbookTopic>
    {
        [ShellPage.Operator] = new(UserHandbook, "4. Prüfen als Werker"),
        [ShellPage.Protocol] = new(UserHandbook, "4.2 Ergebnisse verstehen"),
        [ShellPage.Editor] = new(UserHandbook, "5. Prüfprogramm im Ablaufeditor erstellen"),
        [ShellPage.Library] = new(UserHandbook, "6. CodeBlocks und Bibliotheksbausteine"),
        [ShellPage.Selection] = new(UserHandbook, "8. Auftragsauswahl vor dem Prüfprogramm"),
        [ShellPage.Stations] = new(UserHandbook, "9. Prüfplätze und Geräte einrichten"),
        [ShellPage.Debug] = new(UserHandbook, "11. Simulation und Debugger"),
        [ShellPage.Python] = new(UserHandbook, "11. Simulation und Debugger"),
        [ShellPage.Releases] = new(UserHandbook, "12. Testprogramme freigeben"),
        [ShellPage.Results] = new(UserHandbook, "13. Ergebnisse und Berichte"),
        [ShellPage.Reports] = new(UserHandbook, "13. Ergebnisse und Berichte"),
        [ShellPage.Curves] = new(UserHandbook, "14. Kurven verwenden"),
        [ShellPage.Settings] = new(UserHandbook, "15. Einstellungen, Sicherung und Betrieb"),
        [ShellPage.Notifications] = new(UserHandbook, "15. Einstellungen, Sicherung und Betrieb"),
        [ShellPage.System] = new(UserHandbook, "15.1 Systemprüfung und Sicherung"),
        [ShellPage.Access] = new(UserHandbook, "3. Orientierung und Anmeldung"),
    };

    public static HandbookContent Load(string folder, ShellPage page, BlockDescriptor? block)
    {
        var topic = SelectTopic(page, block);
        return new(topic, ReadSection(File.ReadLines(Path.Combine(folder, topic.FileName)), topic.Heading),
            page == ShellPage.Editor ? block : null);
    }

    public static HandbookTopic SelectTopic(ShellPage page, BlockDescriptor? block)
        => page == ShellPage.Editor && block is not null ? BlockTopic(block) : Pages[page];

    private static readonly HandbookTopic CommunicationTopic = new(UserHandbook, "10. COM und Netzwerkkommunikation");

    private static readonly IReadOnlyDictionary<string, HandbookTopic> BlockTopics = new Dictionary<string, HandbookTopic>(StringComparer.Ordinal)
    {
        [BlockCatalog.Code] = Pages[ShellPage.Library],
        [BlockCatalog.LibraryBlock] = Pages[ShellPage.Library],
        [BlockCatalog.GroupBlock] = Pages[ShellPage.Library],
        ["auswahl"] = Pages[ShellPage.Selection],
        ["etikett"] = new(DeveloperHandbook, "Etikett drucken und gestalten"),
        ["verlauf"] = new(UserHandbook, "Messwertverläufe mit Grenzbandprüfung"),
        ["kurve"] = Pages[ShellPage.Curves],
        ["komm"] = CommunicationTopic,
        ["netz"] = CommunicationTopic,
        ["bus"] = CommunicationTopic,
        ["can"] = CommunicationTopic,
        ["lin"] = CommunicationTopic,
        ["prozess"] = new(DeveloperHandbook, "Externe Programme"),
        ["bibliothek"] = Pages[ShellPage.Library],
    };

    private static readonly HandbookTopic DefaultBlockTopic = new(UserHandbook, "5.3 Schritte einfügen und verschieben");

    private static HandbookTopic BlockTopic(BlockDescriptor block)
        => BlockTopics.GetValueOrDefault(block.Type.Split('.', 2)[0], DefaultBlockTopic);

    public static string ReadSection(IEnumerable<string> lines, string heading)
    {
        var section = new StringBuilder();
        int? sectionLevel = null;
        var fenced = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) fenced = !fenced;
            var title = fenced ? null : Heading(line);
            if (sectionLevel is { } level)
            {
                if (title is { } next && next.Level <= level) break;
                section.AppendLine(line);
            }
            else if (title is { } start && start.Title == heading)
            {
                sectionLevel = start.Level;
            }
        }
        return section.ToString().Trim();
    }

    private static (int Level, string Title)? Heading(string line)
    {
        var level = line.Length - line.TrimStart('#').Length;
        return level is > 0 and <= 6 && level < line.Length && line[level] == ' '
            ? (level, line[(level + 1)..].Trim()) : null;
    }
}
