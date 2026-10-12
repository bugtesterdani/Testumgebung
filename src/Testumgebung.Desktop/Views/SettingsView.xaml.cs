using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class SettingsView : UserControl
{
    private bool _syncing;
    private SettingsViewModel? _attached;

    public SettingsView()
    {
        InitializeComponent();
        // PasswordBox lässt sich nicht binden: aus dem ViewModel füllen (Anzeigen, Verwerfen) und Änderungen zurückschreiben.
        IsVisibleChanged += (_, _) => SyncPassword();
        DataContextChanged += (_, _) => Attach();
    }

    private SettingsViewModel? Settings => (DataContext as ShellViewModel)?.SettingsPage;

    private void Attach()
    {
        if (_attached is not null) _attached.PropertyChanged -= OnSettingsChanged;
        _attached = Settings;
        if (_attached is not null) _attached.PropertyChanged += OnSettingsChanged;
        SyncPassword();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(SettingsViewModel.MqttPassword) or nameof(SettingsViewModel.HttpHeaderValue)
            or nameof(SettingsViewModel.OpcUaPassword)) SyncPassword();
    }

    private void SyncPassword()
    {
        if (_syncing || Settings is not { } vm) return;
        _syncing = true;
        if (MqttPasswordBox.Password != vm.MqttPassword) MqttPasswordBox.Password = vm.MqttPassword;
        if (HttpHeaderBox.Password != vm.HttpHeaderValue) HttpHeaderBox.Password = vm.HttpHeaderValue;
        if (OpcUaPasswordBox.Password != vm.OpcUaPassword) OpcUaPasswordBox.Password = vm.OpcUaPassword;
        _syncing = false;
    }

    private void MqttPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || Settings is not { } vm) return;
        _syncing = true;
        vm.MqttPassword = MqttPasswordBox.Password;
        _syncing = false;
    }

    private void HttpHeaderChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || Settings is not { } vm) return;
        _syncing = true;
        vm.HttpHeaderValue = HttpHeaderBox.Password;
        _syncing = false;
    }

    private void OpcUaPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || Settings is not { } vm) return;
        _syncing = true;
        vm.OpcUaPassword = OpcUaPasswordBox.Password;
        _syncing = false;
    }
}
