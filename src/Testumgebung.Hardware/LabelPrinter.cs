using System.Net.Sockets;
using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

/// <summary>Versendet ZPL unverändert über einen TCP-Druckeranschluss.</summary>
public sealed partial class LabelPrinter : ILabelPrinter
{
    private static async Task SendTcpAsync(LabelPrintRequest request, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(request.Destination, request.Port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(request.Zpl, cancellationToken).ConfigureAwait(false);
    }

    public Task SendAsync(LabelPrintRequest request, CancellationToken cancellationToken)
        => SendTcpAsync(request, cancellationToken);
}
