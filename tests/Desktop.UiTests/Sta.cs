using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Testumgebung.Desktop.UiTests;

/// <summary>
/// Minimaler STA-/Dispatcher-Rahmen für WPF-Tests ohne Zusatzpaket: führt den Testkörper auf einem eigenen
/// STA-Thread mit eigenem Dispatcher aus und pumpt die Dispatcher-Warteschlange auf Wunsch ab (FocusBehavior
/// setzt den Fokus über <c>BeginInvoke(DispatcherPriority.Input)</c>, muss also abgepumpt werden).
/// </summary>
internal static class Sta
{
    public static void Run(Action<Dispatcher> body)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try { body(dispatcher); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { dispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    /// <summary>Alle anstehenden Dispatcher-Arbeiten bis einschließlich Priorität <c>Input</c> abarbeiten.</summary>
    public static void Flush(Dispatcher dispatcher)
    {
        // Ein Rahmen, der sich selbst bei Background-Priorität beendet: alles Höhere (u. a. Input) läuft vorher.
        for (var i = 0; i < 2; i++)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>
    /// Ein unsichtbares (abseits des Bildschirms liegendes) Fenster mit dem übergebenen Inhalt zeigen – ohne den
    /// Vordergrund zu stehlen. Der Rückgabewert ist das Fenster; Aufrufer schließen es am Ende.
    /// </summary>
    public static Window ShowHost(UIElement content)
    {
        var window = new Window
        {
            Content = content,
            Width = 200,
            Height = 120,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        return window;
    }
}
