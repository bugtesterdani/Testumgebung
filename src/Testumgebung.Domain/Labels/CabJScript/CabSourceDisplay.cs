using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabSourceDisplay
{
    internal static string Token(string text) => text switch
    {
        "\r" or "\r\n" => "\n",
        "\u001b" => "␛e",
        "␛" => "␛u",
        _ => text,
    };

    internal static string Source(string text, string lineEnding)
    {
        text = NormalizeLines(text).Replace("\n", lineEnding, StringComparison.Ordinal);
        var output = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '␛') { output.Append(text[index]); continue; }
            if (++index == text.Length) throw new FormatException("Unvollständige ESC-Darstellung: ␛e für ESC oder ␛u für das wörtliche Zeichen verwenden.");
            output.Append(text[index] switch
            {
                'e' => '\u001b', 'u' => '␛',
                _ => throw new FormatException("Unbekannte ESC-Darstellung: ␛e für ESC oder ␛u für das wörtliche Zeichen verwenden."),
            });
        }
        return output.ToString();
    }

    internal static string NormalizeLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
