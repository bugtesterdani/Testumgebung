using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

public static class LabelPrintingBlocks
{
    public const string Print = "etikett.drucken";
    public const string FormatParameter = "Vorlagenformat", CabDataParameter = "CabLblDaten";
    public const string Zpl = "ZPL", Cab = "cab LBL";
    public const string CabTextOffsetParameter = "CabLblTextOffset";

    private const string Simulated = "Simulation", Tcp = "TCP", Windows = "Windows";

    public static void Register(BlockCatalog catalog)
    {
        catalog.Register(new(Print, "Etikett drucken", "Werkerinteraktion", "arrow",
            "Übergibt eine gerenderte ZPL-Vorlage oder eine eingebettete cab-LBL-Druckdatei an einen TCP-Drucker, eine Windows-Druckwarteschlange oder die Simulation.",
            [
                new("Druckart", "Druckart", ParameterKind.Choice, Simulated, Choices: [Simulated, Tcp, Windows]),
                new(FormatParameter, "Vorlagenformat", ParameterKind.Choice, Zpl, Choices: [Zpl, Cab]),
                new("ZplVorlage", "ZPL-Vorlage", ParameterKind.Text, "^XA\n^CI28\n^FO30,30^FD{Kennung}^FS\n^XZ", Required: true,
                    Hint: "Variablen als {Kennung} einsetzen; {{ und }} ergeben geschweifte Klammern.", VisibleWhen: new(FormatParameter, Zpl)),
                new(CabDataParameter, "cab-LBL-Druckdatei", ParameterKind.Text, Required: true,
                    Hint: "Druckfertiger JScript-Export aus cablabel. Eingebettete Grafiken werden übernommen.", VisibleWhen: new(FormatParameter, Cab)),
                new("Host", "Druckername / IP-Adresse", ParameterKind.Text, Required: true, VisibleWhen: new("Druckart", Tcp)),
                new("Port", "TCP-Port", ParameterKind.Number, "9100", VisibleWhen: new("Druckart", Tcp)),
                new("Zeitlimit", "TCP-Zeitlimit [ms]", ParameterKind.Number, "5000", VisibleWhen: new("Druckart", Tcp)),
                new("Warteschlange", "Windows-Druckwarteschlange", ParameterKind.Text, Required: true, VisibleWhen: new("Druckart", Windows)),
            ], Create));
    }

    private static IStepAction Create(BlockParameters parameters)
    {
        var format = parameters.Choice(FormatParameter);
        var render = format == Cab ? CabRenderer(parameters.Text(CabDataParameter), parameters.Raw.GetValueOrDefault(CabTextOffsetParameter)) : ZplRenderer(parameters.Text("ZplVorlage"), parameters.Raw.ContainsKey("ZplLayout"));
        return parameters.Choice("Druckart") switch
        {
            Tcp => CreateTcp(parameters, render, format),
            Windows => new PrintLabelAction(render, format, LabelPrinterKind.Windows, parameters.Text("Warteschlange").Trim(), 0, TimeSpan.Zero),
            _ => new PrintLabelAction(render, format, null, "", 0, TimeSpan.Zero),
        };
    }

    private static PrintLabelAction CreateTcp(BlockParameters parameters, RenderLabel render, string format)
    {
        var port = parameters.Number("Port") ?? 9100;
        if (!double.IsFinite(port) || port < 1 || port > 65535 || port != Math.Truncate(port))
            throw new BlockConfigurationException("TCP-Port muss eine ganze Zahl von 1 bis 65535 sein.");
        var timeout = parameters.Number("Zeitlimit") ?? 5000;
        if (!double.IsFinite(timeout) || timeout < 1 || timeout > int.MaxValue)
            throw new BlockConfigurationException("TCP-Zeitlimit muss zwischen 1 und 2147483647 ms liegen.");
        return new(render, format, LabelPrinterKind.Tcp, parameters.Text("Host").Trim(), (int)port, TimeSpan.FromMilliseconds(timeout));
    }

    private delegate (byte[] Bytes, object Preview) RenderLabel(IStepContext context);

    private static RenderLabel ZplRenderer(string template, bool fromDesigner) => context =>
    {
        var zpl = CommunicationBlocks.Interpolate(template, context, FormatLabelValue);
        if (fromDesigner) zpl = Gs1LabelData.RenderZpl(zpl);
        return (Encoding.UTF8.GetBytes(zpl), zpl);
    };

    private static RenderLabel CabRenderer(string data, string? textOffset)
    {

        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { throw new BlockConfigurationException("Die eingebettete cab-LBL-Druckdatei enthält ungültige Base64-Daten."); }
        if (bytes.Length == 0)
            throw new BlockConfigurationException("Die eingebettete cab-LBL-Druckdatei ist leer.");
        if (textOffset is null) return _ => (bytes, (long)bytes.Length);
        if (!int.TryParse(textOffset, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset)
            || offset >= bytes.Length || !bytes.AsSpan(offset).StartsWith("J\r"u8))
            throw new BlockConfigurationException("Ungültiger Textabschnitt in der cab-Druckvorlage.");
        return CabTemplateRenderer(bytes, offset);
    }

    private static RenderLabel CabTemplateRenderer(byte[] bytes, int offset)
    {
        string[] commands;
        try { commands = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset).Split('\r'); }
        catch (DecoderFallbackException) { throw new BlockConfigurationException("Textabschnitt der cab-Druckvorlage ist kein gültiges UTF-8."); }
        return context =>
        {
            var text = string.Join('\r', commands.Select(command => RenderCabCommand(command, context)));
            var suffix = Encoding.UTF8.GetBytes(text);
            var rendered = new byte[offset + suffix.Length];
            bytes.AsSpan(0, offset).CopyTo(rendered);
            suffix.CopyTo(rendered.AsSpan(offset));
            return (rendered, (long)rendered.Length);
        };
    }

    private static string RenderCabCommand(string command, IStepContext context)
    {
        if (!command.StartsWith("T ", StringComparison.Ordinal) && !command.StartsWith("B ", StringComparison.Ordinal))
            return command;
        var separator = command.IndexOf(';');
        if (separator < 0) return command;
        var header = command[..separator];
        var content = CommunicationBlocks.Interpolate(command[(separator + 1)..], context);
        if (content.Any(c => char.IsControl(c) || c is '[' or ']'))
            throw new FormatException("cab-Etikettvariable enthält ein Steuerzeichen oder eine eckige Klammer.");
        if (header.Contains(",CODE128,", StringComparison.Ordinal) && (content.Length == 0 || content.Any(c => !char.IsAscii(c))))
            throw new FormatException("cab Code 128 benötigt druckbare ASCII-Zeichen.");
        if (header.Contains(",GS1", StringComparison.Ordinal)) content = Gs1LabelData.Cab(content);
        return header + ";" + content;
    }

    private sealed class PrintLabelAction(RenderLabel render, string format, LabelPrinterKind? kind, string destination, int port, TimeSpan timeout) : IStepAction
    {
        public async Task<StepOutcome> ExecuteAsync(IStepContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var (bytes, preview) = render(context);

            if (kind is { } target && await context.PrintLabelAsync(new(target, destination, port, bytes, timeout)).ConfigureAwait(false))
                return StepOutcome.Unrated((long)bytes.Length, $"{format} an Druckziel übergeben.");
            return StepOutcome.Unrated(preview, "Etikett simuliert.");
        }
    }

    private static string FormatLabelValue(object value)
    {
        var text = CommunicationBlocks.Text(value);
        if (text.Any(character => character is '^' or '~' || char.IsControl(character)))
            throw new FormatException("Etikettvariable enthält ein ZPL-Steuerzeichen.");
        return text;
    }
}
