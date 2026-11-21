namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabAmountOptions
{
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
        { "NO", "NOPRINT", "R", "REPEAT", "SAVE", "PREVIEW" };

    internal static bool IsBuiltin(string name) => Builtins.Contains(name);
    internal static bool IsBuiltinToken(string token)
        => token.Length > 2 && token[0] == '[' && token[^1] == ']' && IsBuiltin(token[1..^1]);
}
