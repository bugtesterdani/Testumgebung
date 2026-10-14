/**
 * @file
 * @brief Startet lokale simulierte Gegenstellen für Entwicklungsverbindungen und verwaltet deren Lebensdauer.
 */

using Testumgebung.Instruments;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>
/// Laborsimulatoren der Anwendung (eine Instanz): feste Ports 50251–50255 auf localhost, damit gespeicherte
/// Konfigurationen beim nächsten Start wieder passen; belegte Ports → freie Ports.
/// </summary>
public static class SimulatedLabHost
{
    private static readonly object Gate = new();
    private static SimulatedLab? _lab;

    public static SimulatedLab? Current
    {
        get { lock (Gate) return _lab; }
    }

    public static SimulatedLab Start()
    {
        lock (Gate)
        {
            if (_lab is not null) return _lab;
            try
            {
                _lab = new SimulatedLab(basePort: 50250);
            }
            catch (System.Net.Sockets.SocketException)
            {
                _lab = new SimulatedLab();
            }
            return _lab;
        }
    }

    public static async Task StopAsync()
    {
        SimulatedLab? lab;
        lock (Gate)
        {
            lab = _lab;
            _lab = null;
        }
        if (lab is not null) await lab.DisposeAsync().ConfigureAwait(false);
    }
}
