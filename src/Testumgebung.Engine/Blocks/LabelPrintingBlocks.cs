using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

public static class LabelPrintingBlocks
{
    public const string Print = "etikett.drucken";
    private const string Simulated = "Simulation", Tcp = "TCP", Windows = "Windows";

    public static void Register(BlockCatalog catalog)
    {
        catalog.Register(new(Print, "Etikett drucken", "Werkerinteraktion", "arrow",
            "Rendert eine ZPL-Vorlage und übergibt das Etikett an einen TCP-Drucker, eine Windows-Druckwarteschlange oder die Simulation.",
            [
                new("Druckart", "Druckart", ParameterKind.Choice, Simulated, Choices: [Simulated, Tcp, Windows]),
                new("ZplVorlage", "ZPL-Vorlage", ParameterKind.Text, "^XA\n^CI28\n^FO30,30^FD{Kennung}^FS\n^XZ", Required: true,
                    Hint: "Variablen als {Kennung} einsetzen; {{ und }} ergeben geschweifte Klammern."),
                new("Host", "Druckername / IP-Adresse", ParameterKind.Text, Required: true, VisibleWhen: new("Druckart", Tcp)),
                new("Port", "TCP-Port", ParameterKind.Number, "9100", VisibleWhen: new("Druckart", Tcp)),
                new("Zeitlimit", "TCP-Zeitlimit [ms]", ParameterKind.Number, "5000", VisibleWhen: new("Druckart", Tcp)),
                new("Warteschlange", "Windows-Druckwarteschlange", ParameterKind.Text, Required: true, VisibleWhen: new("Druckart", Windows)),
            ], Create));
    }

    private static IStepAction Create(BlockParameters parameters)
    {
        var template = parameters.Text("ZplVorlage");
        return parameters.Choice("Druckart") switch
        {
            Tcp => CreateTcp(parameters, template),
            Windows => new PrintLabelAction(template, LabelPrinterKind.Windows, parameters.Text("Warteschlange").Trim(), 0, TimeSpan.Zero),
            _ => new PrintLabelAction(template, null, "", 0, TimeSpan.Zero),
        };
    }

    private static PrintLabelAction CreateTcp(BlockParameters parameters, string template)
    {
        var port = parameters.Number("Port") ?? 9100;
        if (!double.IsFinite(port) || port < 1 || port > 65535 || port != Math.Truncate(port))
            throw new BlockConfigurationException("TCP-Port muss eine ganze Zahl von 1 bis 65535 sein.");
        var timeout = parameters.Number("Zeitlimit") ?? 5000;
        if (!double.IsFinite(timeout) || timeout < 1 || timeout > int.MaxValue)
            throw new BlockConfigurationException("TCP-Zeitlimit muss zwischen 1 und 2147483647 ms liegen.");
        return new(template, LabelPrinterKind.Tcp, parameters.Text("Host").Trim(), (int)port, TimeSpan.FromMilliseconds(timeout));
    }

    private sealed class PrintLabelAction(string template, LabelPrinterKind? kind, string destination, int port, TimeSpan timeout) : IStepAction
    {
        public async Task<StepOutcome> ExecuteAsync(IStepContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var zpl = CommunicationBlocks.Interpolate(template, context, FormatLabelValue);
            var bytes = Encoding.UTF8.GetBytes(zpl);

            if (kind is { } target && await context.PrintLabelAsync(new(target, destination, port, bytes, timeout)).ConfigureAwait(false))
                return StepOutcome.Unrated((long)bytes.Length, "ZPL an Druckziel übergeben.");
            return StepOutcome.Unrated(zpl, "Etikett simuliert.");
        }

        private static string FormatLabelValue(object value)
        {
            var text = CommunicationBlocks.Text(value);
            if (text.Any(character => character is '^' or '~' || char.IsControl(character)))
                throw new FormatException("Etikettvariable enthält ein ZPL-Steuerzeichen.");
            return text;
        }
    }
}
