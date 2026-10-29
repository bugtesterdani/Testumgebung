/** @file @brief Wertet vollständige Logzeilen mit begrenztem Regex-Zeitbudget aus. */
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Testumgebung.Engine.Blocks;

/// <summary>Textzuordnungen haben Vorrang vor dem Zahlenmuster; unverstandene Zeilen ändern den Prozentwert nicht.</summary>
public sealed class LiveProgressParser
{
    public sealed record Rule(string? Text = null, string? Regex = null, double Percent = 0, string? Status = null);
    private readonly List<(Rule Rule, Regex? Expression)> _rules;
    private readonly Regex? _numbers, _end;
    private readonly string _status;
    private readonly StringBuilder _pending = new();
    public double? Percent { get; private set; }
    public string? Status { get; private set; }
    public bool Finished { get; private set; }
    private static Regex Expression(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public LiveProgressParser(string rules, string numbers = "", string status = "{current} / {total}", string end = "")
    {
        var parsed = JsonSerializer.Deserialize<List<Rule>>(rules, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Textregeln müssen eine JSON-Liste sein.");
        if (parsed.Count > 100) throw new ArgumentException("Maximal 100 Textregeln erlaubt.");
        _rules = parsed.Select(rule => (Validate(rule), string.IsNullOrEmpty(rule.Regex) ? null : Expression(rule.Regex))).ToList();
        _numbers = string.IsNullOrEmpty(numbers) ? null : Expression(numbers);
        if (_numbers is not null && (!_numbers.GetGroupNames().Contains("current") || !_numbers.GetGroupNames().Contains("total")))
            throw new ArgumentException("Zahlen-Regex benötigt benannte Gruppen (?<current>...) und (?<total>...).");
        _end = string.IsNullOrEmpty(end) ? null : Expression(end);
        _status = status;
    }
    private static Rule Validate(Rule rule)
    {
        if (rule is null) throw new ArgumentException("Textregeln dürfen keine null-Einträge enthalten.");
        if (string.IsNullOrEmpty(rule.Text) == string.IsNullOrEmpty(rule.Regex)) throw new ArgumentException("Jede Regel benötigt genau text oder regex.");
        if (!double.IsFinite(rule.Percent) || rule.Percent < 0 || rule.Percent > 100) throw new ArgumentException("Regel-Prozent muss zwischen 0 und 100 liegen.");
        if (rule.Status?.Length > 2048) throw new ArgumentException("Regelstatus zu lang.");
        return rule;
    }
    /// <summary>CR und LF schließen eine Statuszeile ab, auch bei Konsolen-Prozentanzeigen mit Wagenrücklauf.</summary>
    public void Push(string chunk, bool final = false)
    {
        foreach (var c in chunk)
        {
            if (c is '\r' or '\n') { ApplyPending(); continue; }
            if (_pending.Length >= 65536) throw new InvalidOperationException("Logzeile überschreitet 65536 Zeichen.");
            _pending.Append(c);
        }
        // An end marker may be emitted without a newline; retain incomplete numbers until the line ends.
        if (_end?.IsMatch(_pending.ToString()) == true) { Finished = true; ApplyPending(); }
        if (final) ApplyPending();
    }
    /// <summary>Nach Dateikürzung darf eine halbe alte Zeile nicht mit neuen Daten verschmelzen.</summary>
    public void DiscardPartialLine() => _pending.Clear();

    private void ApplyPending()
    {
        if (_pending.Length == 0) return;
        var line = _pending.ToString(); _pending.Clear();
        if (_end?.IsMatch(line) == true) Finished = true;
        Status = line.Length > 2048 ? line[^2048..] : line;
        if (!ApplyRule(line)) ApplyNumbers(line);
    }
    private bool ApplyRule(string line)
    {
        foreach (var (rule, expression) in _rules)
        {
            var match = expression?.Match(line);
            var found = expression is null ? line.Contains(rule.Text!, StringComparison.Ordinal) : match!.Success;
            if (!found) continue;
            Percent = rule.Percent;
            if (rule.Status is { } status) Status = match is null ? status : match.Result(status);
            return true;
        }
        return false;
    }
    private void ApplyNumbers(string line)
    {
        if (_numbers?.Match(line) is not { Success: true } match) return;
        if (!Number(match.Groups["current"].Value, out var current) || !Number(match.Groups["total"].Value, out var total)) return;
        if (current < 0 || total <= 0) return;
        var percent = current / total * 100;
        if (!double.IsFinite(percent)) return;
        Percent = Math.Clamp(percent, 0, 100);
        Status = _status.Replace("{current}", match.Groups["current"].Value).Replace("{total}", match.Groups["total"].Value);
    }
    private static bool Number(string text, out double value)
        => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
