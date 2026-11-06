using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Instruments;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyDaqDevices(Dispatcher dispatcher, string output)
    {
        var shell = new ShellViewModel(); var vm = shell.StationsPage;
        vm.AddDeviceCommand.Execute(null); vm.SelectedDevice = vm.Devices.Last();
        var row = vm.SelectedDevice!; row.Adapter = DaqModels.LabJack; row.Model = "T7-Pro";
        var view = new StationsView { DataContext = shell, Width = 1280, Height = 900 };
        view.Measure(new(1280,900)); view.Arrange(new(0,0,1280,900)); view.UpdateLayout(); Sta.Flush(dispatcher);
        var connection = Descendants(view).OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == "Geräteverbindung");
        Assert.Equal(new[] { "USB", "Ethernet", "WLAN" }, connection.Items.Cast<string>());
        connection.SelectedItem = "WLAN"; Sta.Flush(dispatcher); Assert.Equal("WLAN", row.Connection);
        row.Model = "U3-LV"; Sta.Flush(dispatcher);
        Assert.Equal(new[] { "USB" }, connection.Items.Cast<string>()); Assert.Equal("USB", row.Connection);
        row.Adapter = DaqModels.RedLab; row.Model = "RedLab E-1608"; Sta.Flush(dispatcher);
        Assert.Equal(new[] { "Ethernet" }, connection.Items.Cast<string>()); Assert.Equal("Ethernet", row.Connection);
        row.Model = "RedLab 1608G"; Sta.Flush(dispatcher);
        Assert.Equal(new[] { "USB" }, connection.Items.Cast<string>());
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280,900,96,96,PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output,"daq-verbindungen.png")); encoder.Save(stream);
    }
}
