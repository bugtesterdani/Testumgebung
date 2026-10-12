using System.Windows;
using System.Windows.Threading;

namespace Testumgebung.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // OPC-UA-Meldeweg über den OPC-Foundation-Stack (nur in der Windows-Anwendung verfügbar)
        Testumgebung.Presentation.Settings.ResultServices.OpcUaFactory = options => new OpcUaNotifier(options);
        // Realer OPC-UA-Client für Geräte vom Typ „OPC UA“ (Lesen/Schreiben/Methoden/Abonnements)
        Testumgebung.Instruments.Communication.OpcUaDevice.ClientFactory = (resource, options) => new OpcUaClient(resource, options);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = FaultLog.Write(e.Exception);
        e.Handled = true;
        // Nach einem unbekannten UI-Fehler keinen weiteren Prüfablauf mehr zulassen.
        if (Current.MainWindow is MainWindow window) window.CloseAfterFault();
        else Current.Shutdown(-1);
        MessageBox.Show(e.Exception.Message + "\n\nDie Anwendung wird kontrolliert beendet."
            + (path is null ? "\nFehlerprotokoll konnte nicht gespeichert werden." : "\nFehlerprotokoll: " + path),
            "Unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
