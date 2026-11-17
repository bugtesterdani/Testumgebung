namespace Testumgebung.Contracts;

public enum LabelPrinterKind { Tcp, Windows }

public sealed record LabelPrintRequest(LabelPrinterKind Kind, string Destination, int Port, byte[] Zpl, TimeSpan Timeout)
{
    /// <summary>Unveränderte Druckdaten für die RAW-Ausgabe, auch für Binärdateien.</summary>
    public byte[] Data => Zpl;
}

public interface ILabelPrinter
{
    Task SendAsync(LabelPrintRequest request, CancellationToken cancellationToken);
}
