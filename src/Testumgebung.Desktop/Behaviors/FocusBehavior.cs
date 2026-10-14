/**
 * @file
 * @brief Verbindet Sichtbarkeit und Bedienbarkeit mit dem Tastaturfokus für den Scannerbetrieb.
 */

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Testumgebung.Desktop.Behaviors;

/// <summary>
/// Legt den Tastaturfokus automatisch auf ein Eingabefeld, sobald es sichtbar und bedienbar wird.
/// Für den Scannerbetrieb: der Werker scannt, ohne vorher ins Feld zu klicken. Greift bei jedem
/// Übergang „unsichtbar/gesperrt → sichtbar + aktiv“ – also auch für den nächsten Prüfling und für
/// jede neue Werkerabfrage (Eingabe), die während eines Ablaufs erscheint.
/// </summary>
public static class FocusBehavior
{
    public static readonly DependencyProperty AutoFocusProperty =
        DependencyProperty.RegisterAttached(
            "AutoFocus", typeof(bool), typeof(FocusBehavior),
            new PropertyMetadata(false, OnAutoFocusChanged));

    public static bool GetAutoFocus(DependencyObject obj) => (bool)obj.GetValue(AutoFocusProperty);

    public static void SetAutoFocus(DependencyObject obj, bool value) => obj.SetValue(AutoFocusProperty, value);

    private static void OnAutoFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.IsVisibleChanged += OnConditionChanged;
            fe.IsEnabledChanged += OnConditionChanged;
            fe.Loaded += OnLoaded;
            if (fe.IsLoaded) TryFocus(fe);
        }
        else
        {
            fe.IsVisibleChanged -= OnConditionChanged;
            fe.IsEnabledChanged -= OnConditionChanged;
            fe.Loaded -= OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => TryFocus((FrameworkElement)sender);

    private static void OnConditionChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Nur auf den Übergang „jetzt verfügbar“ reagieren, nicht beim Wegblenden oder Sperren –
        // sonst würde ein Klick des Werkers in ein anderes Feld sofort wieder weggezogen.
        if (e.NewValue is false) return;
        TryFocus((FrameworkElement)sender);
    }

    private static void TryFocus(FrameworkElement fe)
    {
        if (!fe.IsVisible || !fe.IsEnabled) return;
        // Nach dem Layout setzen: beim Erscheinen ist das Element oft noch nicht fokussierbar.
        fe.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!fe.IsVisible || !fe.IsEnabled) return;
            fe.Focus();
            Keyboard.Focus(fe);
            if (fe is TextBox box) box.SelectAll();
        }));
    }
}
