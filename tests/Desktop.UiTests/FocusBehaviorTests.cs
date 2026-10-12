using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Testumgebung.Desktop.Behaviors;

namespace Testumgebung.Desktop.UiTests;

/// <summary>
/// UI-Tests der Attached-Behavior <see cref="FocusBehavior"/> (Scannerbetrieb der Werkeransicht): ein Eingabefeld
/// erhält automatisch den Fokus, sobald es sichtbar und bedienbar wird. Geprüft wird der logische Fokus über
/// <see cref="FocusManager.GetFocusedElement(DependencyObject)"/> – unabhängig davon, ob das Fenster im Vordergrund
/// steht (Tastaturfokus würde den OS-Vordergrund erfordern und wäre in Testläufen unzuverlässig).
/// Nur unter Windows (WPF/STA).
/// </summary>
public sealed class FocusBehaviorTests
{
    [Fact]
    public void Fokussiert_Feld_sobald_das_Panel_erscheint()
    {
        Sta.Run(dispatcher =>
        {
            var panel = new Border { Visibility = Visibility.Collapsed };
            var scan = new TextBox();
            FocusBehavior.SetAutoFocus(scan, true);
            panel.Child = scan;
            var window = Sta.ShowHost(panel);
            try
            {
                Sta.Flush(dispatcher);
                // Panel noch ausgeblendet → kein Fokus.
                Assert.NotSame(scan, FocusManager.GetFocusedElement(window));

                panel.Visibility = Visibility.Visible;     // „Prüfling erfassen“ erscheint
                Sta.Flush(dispatcher);
                Assert.Same(scan, FocusManager.GetFocusedElement(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Fokus_kehrt_nach_jedem_Pruefling_zurueck()
    {
        Sta.Run(dispatcher =>
        {
            var panel = new Border { Visibility = Visibility.Visible };
            var scan = new TextBox();
            FocusBehavior.SetAutoFocus(scan, true);
            panel.Child = scan;
            var other = new TextBox();
            var root = new StackPanel();
            root.Children.Add(panel);
            root.Children.Add(other);
            var window = Sta.ShowHost(root);
            try
            {
                Sta.Flush(dispatcher);
                Assert.Same(scan, FocusManager.GetFocusedElement(window));

                // Prüfung läuft: Panel ausblenden (Werkerinteraktion bekäme den Fokus).
                other.Focus();
                panel.Visibility = Visibility.Collapsed;
                Sta.Flush(dispatcher);

                // Nächster Prüfling: Panel wieder einblenden → Scanfeld bekommt erneut den Fokus.
                panel.Visibility = Visibility.Visible;
                Sta.Flush(dispatcher);
                Assert.Same(scan, FocusManager.GetFocusedElement(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Wartet_bis_das_Feld_aktiv_ist()
    {
        // Entspricht dem Werker-Eingabefeld, das erst bei aktiver Abfrage (IsAwaiting) bedienbar ist.
        Sta.Run(dispatcher =>
        {
            var input = new TextBox { IsEnabled = false };
            FocusBehavior.SetAutoFocus(input, true);
            var window = Sta.ShowHost(input);
            try
            {
                Sta.Flush(dispatcher);
                Assert.NotSame(input, FocusManager.GetFocusedElement(window)); // gesperrt → kein Fokus

                input.IsEnabled = true;                    // Abfrage wird aktiv
                Sta.Flush(dispatcher);
                Assert.Same(input, FocusManager.GetFocusedElement(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Markiert_vorhandenen_Text_fuer_Ueberschreiben_beim_Scannen()
    {
        Sta.Run(dispatcher =>
        {
            var scan = new TextBox { Text = "SN-ALT" };
            FocusBehavior.SetAutoFocus(scan, true);
            var window = Sta.ShowHost(scan);
            try
            {
                Sta.Flush(dispatcher);
                Assert.Same(scan, FocusManager.GetFocusedElement(window));
                Assert.Equal(scan.Text.Length, scan.SelectionLength); // komplett markiert → neuer Scan überschreibt
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Zieht_den_Fokus_nicht_weg_wenn_das_Feld_ausgeblendet_wird()
    {
        Sta.Run(dispatcher =>
        {
            var panel = new Border { Visibility = Visibility.Visible };
            var scan = new TextBox();
            FocusBehavior.SetAutoFocus(scan, true);
            panel.Child = scan;
            var other = new TextBox();
            var root = new StackPanel();
            root.Children.Add(panel);
            root.Children.Add(other);
            var window = Sta.ShowHost(root);
            try
            {
                Sta.Flush(dispatcher);
                // Werker klickt bewusst in ein anderes Feld.
                other.Focus();
                Sta.Flush(dispatcher);
                Assert.Same(other, FocusManager.GetFocusedElement(window));

                // Ausblenden darf den Fokus NICHT zurückreißen (nur der Übergang „wird sichtbar+aktiv“ fokussiert).
                panel.Visibility = Visibility.Collapsed;
                Sta.Flush(dispatcher);
                Assert.Same(other, FocusManager.GetFocusedElement(window));
            }
            finally { window.Close(); }
        });
    }
}
