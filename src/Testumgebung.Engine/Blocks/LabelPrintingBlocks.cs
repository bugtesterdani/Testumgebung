using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

public static class LabelPrintingBlocks
{
    public const string Print = "etikett.drucken";
    public const string FormatParameter = "Vorlagenformat", CabDataParameter = "CabLblDaten";
    public const string Zpl = "ZPL", Cab = "cab LBL";

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
        var render = format == Cab ? CabRenderer(parameters.Text(CabDataParameter)) : ZplRenderer(parameters.Text("ZplVorlage"));
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

    private static RenderLabel ZplRenderer(string template) => context =>
    {
        var zpl = CommunicationBlocks.Interpolate(template, context, FormatLabelValue);
        return (Encoding.UTF8.GetBytes(zpl), zpl);
    };

    private static RenderLabel CabRenderer(string data)
    {

        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { throw new BlockConfigurationException("Die eingebettete cab-LBL-Druckdatei enthält ungültige Base64-Daten."); }
        if (bytes.Length == 0)
            throw new BlockConfigurationException("Die eingebettete cab-LBL-Druckdatei ist leer.");
        return _ => (bytes, (long)bytes.Length);
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
