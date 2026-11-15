namespace Testumgebung.Contracts;

public enum LabelPrinterKind { Tcp, Windows }

public sealed record LabelPrintRequest(LabelPrinterKind Kind, string Destination, int Port, byte[] Zpl);

public interface ILabelPrinter
{
    Task SendAsync(LabelPrintRequest request, CancellationToken cancellationToken);
}
