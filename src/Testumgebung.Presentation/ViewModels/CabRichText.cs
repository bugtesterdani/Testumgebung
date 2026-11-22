using System.Net;
using System.Text.RegularExpressions;

namespace Testumgebung.Presentation.ViewModels;

public sealed record CabRichStyle(bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false,
    double Scale = 1, int Baseline = 0, string Alignment = "left");
public sealed record CabRichRun(string Text, CabRichStyle Style, bool Break = false, bool Rule = false, bool Paragraph = false);

/// <summary>Offline subset of the catalogued rich-text tags. Never loads scripts, links, CSS or external resources.</summary>
public static class CabRichText
{
    public static IReadOnlyList<CabRichRun> Parse(string text)
    {
        var runs = new List<CabRichRun>();
        var stack = new List<(string Name, CabRichStyle Style)>();
        var style = new CabRichStyle();
        var html = Regex.Replace(text, @"<!--[\s\S]*?-->", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (Match token in Regex.Matches(html, @"<[^>]*>|[^<]+|<", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            var tag = Regex.Match(token.Value, @"^<\s*(?<close>/)?\s*(?<name>[A-Za-z0-9]+)(?<attributes>[^>]*)>$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!tag.Success) { runs.Add(new(WebUtility.HtmlDecode(token.Value), style)); continue; }
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            if (name == "br") { runs.Add(new("", style, Break: true)); continue; }
            if (name == "hr") { runs.Add(new("", style, Break: true, Rule: true, Paragraph: true)); continue; }
            if (tag.Groups["close"].Success) { Close(name, stack, ref style); if (Block(name)) runs.Add(new("", style, Break: true, Paragraph: true)); continue; }
            if (Block(name) && runs.Count > 0 && !runs[^1].Break) runs.Add(new("", style, Break: true, Paragraph: true));
            stack.Add((name, style));
            style = Change(StyleName(name), tag.Groups["attributes"].Value, style);
        }
        return runs;
    }

    private static bool Block(string name) => name is "p" or "h1" or "h2" or "h3" or "h4" or "h5";
    private static void Close(string name, List<(string Name, CabRichStyle Style)> stack, ref CabRichStyle style)
    {
        var index = stack.FindLastIndex(item => item.Name == name);
        if (index < 0) return;
        style = stack[index].Style; stack.RemoveRange(index, stack.Count - index);
    }
    private static CabRichStyle Change(string name, string attributes, CabRichStyle style)
        => name switch
        {
            "b" => style with { Bold = true }, "i" => style with { Italic = true },
            "u" => style with { Underline = true }, "s" => style with { Strike = true },
            "sup" => style with { Scale = style.Scale * .67, Baseline = 1 }, "sub" => style with { Scale = style.Scale * .67, Baseline = -1 },
            "big" => style with { Scale = style.Scale * 1.2 }, "small" => style with { Scale = style.Scale / 1.2 },
            "p" => style with { Alignment = Alignment(attributes) }, _ => Heading(name, style),
        };
    private static string StyleName(string name) => name switch { "strong" => "b", "em" => "i", "strike" => "s", _ => name };
    private static CabRichStyle Heading(string name, CabRichStyle style)
    {
        var scale = name switch { "h1" => 2, "h2" => 1.7, "h3" => 1.5, "h4" => 1.3, "h5" => 1.1, _ => 0 };
        return scale > 0 ? style with { Bold = true, Scale = style.Scale * scale } : style;
    }
    private static string Alignment(string attributes)
    {
        var match = Regex.Match(attributes, "\\balign\\s*=\\s*['\"]?(?<align>left|center|right|justify)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["align"].Value.ToLowerInvariant() : "left";
    }
}
