/**
 * @file
 * @brief Ergebnisse der Kommandozeile: Textausgabe und JUnit-XML für CI bzw. KI-Agenten.
 */

using System.Xml.Linq;
using System.Text;

namespace Testumgebung.Cli;

/// <summary>Ein Prüfpunkt (JUnit-Testfall): bestanden, fehlgeschlagen oder übersprungen.</summary>
public sealed record CheckResult(string Suite, string Name, CheckOutcome Outcome, string? Message = null, string? Output = null);

public enum CheckOutcome { Passed, Failed, Skipped }

public sealed class Report
{
    private readonly List<CheckResult> _results = [];

    public IReadOnlyList<CheckResult> Results => _results;

    public int Failed => _results.Count(r => r.Outcome == CheckOutcome.Failed);

    public void Add(CheckResult result)
    {
        _results.Add(result);
        var mark = result.Outcome switch
        {
            CheckOutcome.Passed => "OK  ",
            CheckOutcome.Failed => "FEHL",
            _ => "--  ",
        };
        Console.WriteLine($"[{mark}] {result.Suite} › {result.Name}{(result.Message is null ? "" : ": " + result.Message)}");
        if (result.Outcome == CheckOutcome.Failed && result.Output is { Length: > 0 } output)
        {
            // Bei Fehlern das Wichtigste gleich mit ausgeben (Katalogauszug bzw. Ende des Ablaufprotokolls).
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 60) Console.WriteLine($"       … {lines.Length - 60} Zeilen davor (vollständig im JUnit-Bericht)");
            foreach (var line in lines.TakeLast(60)) Console.WriteLine("       " + line.TrimEnd('\r'));
        }
    }

    public void Pass(string suite, string name, string? message = null, string? output = null)
        => Add(new CheckResult(suite, name, CheckOutcome.Passed, message, output));

    public void Fail(string suite, string name, string message, string? output = null)
        => Add(new CheckResult(suite, name, CheckOutcome.Failed, message, output));

    public void Skip(string suite, string name, string message)
        => Add(new CheckResult(suite, name, CheckOutcome.Skipped, message));

    public void WriteSummary()
    {
        var passed = _results.Count(r => r.Outcome == CheckOutcome.Passed);
        var skipped = _results.Count(r => r.Outcome == CheckOutcome.Skipped);
        Console.WriteLine();
        Console.WriteLine(Failed == 0
            ? $"Ergebnis: alles bestanden ({passed} bestanden{(skipped > 0 ? $", {skipped} übersprungen" : "")})."
            : $"Ergebnis: {Failed} fehlgeschlagen, {passed} bestanden{(skipped > 0 ? $", {skipped} übersprungen" : "")}.");
    }

    /// <summary>JUnit-XML (testsuites/testsuite/testcase), eine Suite je Projekt bzw. Szenario.</summary>
    public void WriteJUnit(string path)
    {
        if (Directory.Exists(path)) path = Path.Combine(path, "testumgebung.xml");
        var suites = new XElement("testsuites", new XAttribute("name", "Testumgebung"),
            new XAttribute("tests", _results.Count), new XAttribute("failures", Failed));
        foreach (var group in _results.GroupBy(r => r.Suite))
        {
            var suite = new XElement("testsuite", new XAttribute("name", XmlText(group.Key)),
                new XAttribute("tests", group.Count()),
                new XAttribute("failures", group.Count(r => r.Outcome == CheckOutcome.Failed)),
                new XAttribute("skipped", group.Count(r => r.Outcome == CheckOutcome.Skipped)));
            foreach (var r in group)
            {
                var testcase = new XElement("testcase", new XAttribute("classname", XmlText(group.Key)), new XAttribute("name", XmlText(r.Name)));
                if (r.Outcome == CheckOutcome.Failed) testcase.Add(new XElement("failure", new XAttribute("message", XmlText(Clip(r.Message ?? ""))), XmlText(r.Message ?? "")));
                if (r.Outcome == CheckOutcome.Skipped) testcase.Add(new XElement("skipped", new XAttribute("message", XmlText(Clip(r.Message ?? "")))));
                if (r.Output is { Length: > 0 }) testcase.Add(new XElement("system-out", XmlText(r.Output)));
                suite.Add(testcase);
            }
            suites.Add(suite);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        new XDocument(new XDeclaration("1.0", "utf-8", null), suites).Save(path);
        Console.WriteLine($"JUnit-Bericht: {path}");
    }

    // Worker output can contain terminal control characters, which XML 1.0 cannot represent directly.
    private static string XmlText(string text) => string.Concat(text.EnumerateRunes().Select(r =>
        r.Value is 9 or 10 or 13 or >= 32 and <= 0xD7FF or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF
            ? r.ToString() : $"\\u{r.Value:x4}"));

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + " …";
}
