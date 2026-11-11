namespace Testumgebung.Instruments;

internal readonly record struct CustomCommand(string Text, string? Ending);

internal static class CustomCommandSequence
{
    private static string[] Commands(string? text, bool split, bool multiline)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (multiline)
            return text.Split(split ? ["; ", "\r", "\n"] : ["\r", "\n"],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return split ? text.Split("; ", StringSplitOptions.None) : [text];
    }

    private static string[] Endings(string? endings) => string.IsNullOrWhiteSpace(endings)
        ? [] : endings.Split(',', StringSplitOptions.TrimEntries);

    private static string? Suffix(string ending) => ending.ToUpperInvariant() switch
    {
        "LF" => "\n",
        "CR" => "\r",
        "CRLF" => "\r\n",
        "KEINS" or "NONE" => "",
        "STANDARD" => null,
        _ => throw new ArgumentException("Ungültiges Zeilenende.", nameof(ending))
    };

    internal static void Validate(string? text, string? endings, bool split, bool multiline, string label, List<string> errors)
    {
        var commands = Commands(text, split, multiline);
        var values = Endings(endings);
        if (commands.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{label}: Die Befehlsfolge enthält einen leeren Befehl.");
        if (values.Length != 0 && values.Length != commands.Length)
            errors.Add($"{label}: Je Befehl ein Zeilenende angeben.");
        if (values.Any(value => value.ToUpperInvariant() is not ("LF" or "CR" or "CRLF" or "KEINS" or "NONE" or "STANDARD")))
            errors.Add($"{label}: Erlaubte Zeilenenden sind LF, CR, CRLF, KEINS und STANDARD.");
    }

    internal static CustomCommand[] Parse(string text, string? endings, bool split, bool multiline = false)
    {
        var values = Endings(endings);
        return Commands(text, split, multiline)
            .Select((command, index) => new CustomCommand(command, values.Length == 0 ? null : Suffix(values[index])))
            .ToArray();
    }
}

