using System.Text;
using System.Text.RegularExpressions;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

/// <summary>Ergebnis der Abhängigkeitsanalyse eines CodeBlocks.</summary>
public sealed record CodeDependencies(IReadOnlyList<CodePort> Ports, IReadOnlyList<string> Warnings);

/// <summary>
/// Wandelt einen CodeBlock im Ablauf in einen wiederverwendbaren Baustein um (Abschnitt 3):
/// externe Abhängigkeiten (verwendete Ordnervariablen) werden explizite Ein-/Ausgänge.
/// </summary>
public static partial class CodeBlockConverter
{
    /// <summary>
    /// Ermittelt verwendete Variablen: nur gelesen → Eingang, nur geschrieben → Ausgang, beides → Ein-/Ausgang.
    /// Zeichenketten und Kommentare werden ignoriert.
    /// </summary>
    public static CodeDependencies Analyze(CodeLanguage language, string source, IEnumerable<(string Name, VarType Type)> visible)
    {
        var code = StripStringsAndComments(source, language);
        var ports = new List<CodePort>();
        foreach (var (name, type) in visible)
        {
            if (!IsIdentifier(name)) continue;
            var pattern = $@"(?<![\w.]){Regex.Escape(name)}(?!\w)";
            var matches = Regex.Matches(code, pattern);
            if (matches.Count == 0) continue;

            bool read = false, write = false;
            foreach (Match m in matches)
            {
                var before = code[..m.Index].TrimEnd();
                var after = code[(m.Index + m.Length)..].TrimStart();
                var compound = Regex.IsMatch(after, @"^(\+=|-=|\*=|/=|%=|\+\+|--)") || before.EndsWith("++", StringComparison.Ordinal) || before.EndsWith("--", StringComparison.Ordinal);
                var assign = Regex.IsMatch(after, @"^=(?!=)");
                var declaration = Regex.IsMatch(before, @"(\bvar|\bdouble|\bint|\blong|\bbool|\bstring)$");
                if (declaration) continue; // lokale Neudeklaration gleichen Namens
                if (compound)
                {
                    read = true;
                    write = true;
                }
                else if (assign) write = true;
                else read = true;
            }
            var direction = read && write ? PortDirection.InOut : write ? PortDirection.Output : PortDirection.Input;
            ports.Add(new CodePort(name, type, direction));
        }

        var warnings = new List<string>();
        foreach (Match m in VarCall().Matches(source))
            warnings.Add($"Dynamischer Variablenzugriff {m.Value} – im Baustein nicht erlaubt, bitte als Port anlegen.");
        return new CodeDependencies(ports, warnings);
    }

    /// <summary>
    /// Erzeugt den Bibliotheksbaustein und ersetzt den CodeBlock-Schritt durch einen Baustein-Schritt
    /// mit gleichwertigen Bindungen (Eingänge = gleichnamige Variable, Ausgänge → gleichnamige Variable).
    /// </summary>
    public static (CodeBlockDefinition Block, StepDefinition Step, IReadOnlyList<string> Warnings) Convert(
        StepDefinition codeStep,
        IEnumerable<(string Name, VarType Type)> visible,
        string blockId,
        BlockCatalog catalog)
    {
        if (codeStep.BlockType != BlockCatalog.Code) throw new ArgumentException("Nur CodeBlocks können umgewandelt werden.", nameof(codeStep));
        var language = BlockCatalog.ParseLanguage(codeStep.Parameters.GetValueOrDefault("Sprache") ?? "C#");
        var source = codeStep.Parameters.GetValueOrDefault("Quelltext") ?? "";
        var deps = Analyze(language, source, visible);

        var block = new CodeBlockDefinition(blockId, codeStep.Name)
        {
            Language = language,
            Source = source,
            Version = "0.1",
            Description = codeStep.Description ?? $"Aus CodeBlock „{codeStep.Name}“ erstellt.",
        };
        block.Ports.AddRange(deps.Ports);

        var parameters = new Dictionary<string, string>
        {
            ["Baustein"] = blockId,
            ["Timeout"] = codeStep.Parameters.GetValueOrDefault("Timeout") ?? "5",
        };
        foreach (var p in deps.Ports)
        {
            if (p.IsInput) parameters[BlockCatalog.InputBindingPrefix + p.Name] = p.Name;
            if (p.Direction == PortDirection.Output) parameters[BlockCatalog.OutputBindingPrefix + p.Name] = p.Name;
        }
        var step = catalog.Create(BlockCatalog.LibraryBlock, codeStep.Id, codeStep.Name, parameters);
        step.Description = codeStep.Description;
        return (block, step, deps.Warnings);
    }

    /// <summary>Statisch sichtbare Variablen an einer Stelle des Ablaufs (innere Definition verdeckt äußere).</summary>
    public static IReadOnlyList<(string Name, VarType Type)> VisibleVariables(TestDefinition definition, string nodeId)
    {
        var result = new List<(string, VarType)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in definition.AncestorsOf(nodeId).Reverse())
        {
            foreach (var v in group.Variables)
                if (seen.Add(v.Name)) result.Add((v.Name, v.Type));
            if (group.Resources.Count > 0 && seen.Add(TestRun.ResourcesAcquiredVariable))
                result.Add((TestRun.ResourcesAcquiredVariable, VarType.Bool));
            if (group.IsInterfaceBoundary) break;
        }
        // Im Körper eines grafischen Bausteins: Ports als äußerste Variablen
        if (definition.OwningGroupBlock(nodeId) is { } block)
            foreach (var port in block.Ports)
                if (seen.Add(port.Name)) result.Add((port.Name, port.Type));
        return result;
    }

    private static bool IsIdentifier(string name)
        => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string StripStringsAndComments(string source, CodeLanguage language)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (SkipComment(source, ref i, language)) continue;
            if (c is '"' or '\'')
            {
                SkipQuotedText(source, ref i, c);
                sb.Append(" \"\" ");
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool SkipComment(string source, ref int i, CodeLanguage language)
    {
        if (language == CodeLanguage.Python)
        {
            if (source[i] != '#') return false;
            while (i < source.Length && source[i] != '\n') i++;
            return true;
        }
        if (source[i] != '/' || i + 1 >= source.Length) return false;
        if (source[i + 1] == '/')
        {
            while (i < source.Length && source[i] != '\n') i++;
            return true;
        }
        if (source[i + 1] != '*') return false;
        var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
        i = end < 0 ? source.Length : end + 2;
        return true;
    }

    private static void SkipQuotedText(string source, ref int i, char quote)
    {
        i++;
        while (i < source.Length && source[i] != quote && source[i] != '\n')
        {
            if (source[i] == '\\') i++;
            i++;
        }
        i++;
    }

    [GeneratedRegex(@"\b(Var<[^>]+>|VarText|Var|SetVar|var|set_var)\s*\(\s*""[^""]*""")]
    private static partial Regex VarCall();
}
