using System.IO;
using System.Windows;

namespace Testumgebung.Desktop.Views;

public partial class LabelDesignerWindow
{
    private void InitializeCabFiles()
    {
        if (!_cab) return;
        AllowDrop = true;
        PreviewDragOver += (_, args) =>
        {
            if (!args.Data.GetDataPresent(DataFormats.FileDrop)) return;
            args.Effects = AcceptedCabFiles(args.Data) is not null ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        };
        PreviewDrop += (_, args) =>
        {
            if (AcceptedCabFiles(args.Data) is not { } files) return;
            DropCabFiles(files); args.Handled = true;
        };
    }

    private static string[]? AcceptedCabFiles(IDataObject data)
        => data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && IsCabFile(files) ? files : null;

    private static bool IsCabFile(string[] files) => files.Length == 1 && string.Equals(Path.GetExtension(files[0]), ".lbl", StringComparison.OrdinalIgnoreCase);

    internal bool DropCabFiles(string[] files)
    {
        if (!_cab || !IsCabFile(files)) return false;
        OpenCabFile(files[0]); return true;
    }
}
