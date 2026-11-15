using System.Net.Sockets;
using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

/// <summary>Versendet ZPL unverändert über einen TCP-Druckeranschluss.</summary>
public sealed partial class LabelPrinter : ILabelPrinter
{
    private static async Task SendTcpAsync(LabelPrintRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        try { await TransmitTcpAsync(request, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Zeitlimit beim TCP-Etikettendruck überschritten.");
        }
    }

    private static async Task TransmitTcpAsync(LabelPrintRequest request, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(request.Destination, request.Port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(request.Zpl, cancellationToken).ConfigureAwait(false);
    }

    public Task SendAsync(LabelPrintRequest request, CancellationToken cancellationToken)
        => request.Kind switch
        {
            LabelPrinterKind.Tcp => SendTcpAsync(request, cancellationToken),
            LabelPrinterKind.Windows => Task.Run(() => SendToWindows(request, cancellationToken), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
}
