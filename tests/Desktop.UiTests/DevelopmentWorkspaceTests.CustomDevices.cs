using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Definitions;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyCustomDevices(Dispatcher dispatcher, string output)
    {
        var project = new TestProject(new TestDefinition("Gerätebeispiel", "0.1", new("start", "Start"), new("loop", "Loop"), new("end", "End")));
        var shell = new ShellViewModel(project: project); var vm = shell.StationsPage;
        vm.AddProfileCommand.Execute(null); vm.AddProfileChannelCommand.Execute(null);
        var channel = vm.ProfileChannels[0]; channel.Name = "Spannung"; channel.Unit = "V"; channel.Min = 0; channel.Max = 30;
        channel.ReadQuery = "MEAS:VOLT?"; channel.WriteCommand = "VOLT {value}";
        vm.SelectedProfile!.Name = "Eigenes Netzgerät"; vm.SelectedProfile.ProbeQuery = "*IDN?"; vm.SelectedProfile.SafeCommands = "OUTP OFF";
        var view = new StationsView { DataContext = shell, Width = 1280, Height = 900, Background = (Brush)Application.Current.FindResource("BackgroundBrush") };
        view.Measure(new(1280, 900)); view.Arrange(new(0, 0, 1280, 900)); view.UpdateLayout(); Sta.Flush(dispatcher);
        var edit = Descendants(view).OfType<TextBox>().Single(b => ReferenceEquals(b.DataContext, vm.SelectedProfile)
            && b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Name");
        edit.Text = "Eigenes Netzgerät (Beispiel)"; edit.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Sta.Flush(dispatcher);
        Assert.Equal(edit.Text, vm.SelectedProfile.Name);
        var add = Descendants(view).OfType<Button>().Single(b => b.Content?.ToString() == "Als Gerät hinzufügen"); add.Command.Execute(null);
        Sta.Flush(dispatcher); Assert.Equal(vm.SelectedProfile.Adapter, vm.SelectedDevice!.Adapter); Assert.Single(project.DeviceProfiles);
        var grids = Descendants(view).OfType<DataGrid>(); Assert.Contains(grids, g => ReferenceEquals(g.ItemsSource, vm.ProfileChannels));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, "eigene-geraete.png")); encoder.Save(stream);
    }
}
