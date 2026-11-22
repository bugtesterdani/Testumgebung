using System.Text;
using System.Text.RegularExpressions;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Presentation.ViewModels;

internal static class CabBarcodeGs1
{
    internal static string Payload(string text)
    {
        if (!text.StartsWith('(')) return text;
        _ = Gs1LabelData.Cab(text);
        var matches = Regex.Matches(text, @"\((\d{2,4})\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var payload = new StringBuilder();
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index]; var end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            var id = match.Groups[1].Value;
            payload.Append(id); payload.Append(text.AsSpan(match.Index + match.Length, end - match.Index - match.Length));
            if (index + 1 < matches.Count && Gs1LabelData.Identifiers.Single(i => i.Id == id).Variable) payload.Append('\u001d');
        }
        return payload.ToString();
    }
}
