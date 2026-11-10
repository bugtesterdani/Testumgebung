using System.Windows;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class LocalPasswordWindow : Window
{
    private readonly RightsEditRow _row;
    public LocalPasswordWindow(RightsEditRow row)
    {
        InitializeComponent(); _row = row; Account.Text = row.Upn;
        Loaded += (_, _) => NewPassword.Focus();
        Closed += (_, _) => { NewPassword.Clear(); Confirmation.Clear(); };
    }
    private void OnApply(object sender, RoutedEventArgs eventArgs)
    {
        if (NewPassword.Password.Length == 0 || NewPassword.Password != Confirmation.Password)
        { Error.Text = "Passwort eingeben und identisch wiederholen."; return; }
        _row.SetPassword(NewPassword.Password); DialogResult = true;
    }
}
