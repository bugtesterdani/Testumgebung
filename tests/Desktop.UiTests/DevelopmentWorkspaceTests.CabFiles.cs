using System.IO;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyCabFileIntegration(Dispatcher dispatcher)
    {
        var folder = Path.Combine(Path.GetTempPath(), "cab-file-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var legacy = new LabelLayout { Elements = [new() { Value = "legacy-{Kennung}" }] };
        var legacyBytes = CabLabelLayout.Render(legacy);
        var step = BlockCatalog.Default.Create(LabelPrintingBlocks.Print, "label", parameters: new Dictionary<string, string>
        {
            [LabelPrintingBlocks.FormatParameter] = LabelPrintingBlocks.Cab,
            [LabelPrintingBlocks.CabDataParameter] = Convert.ToBase64String(legacyBytes),
            [CabLabelLayout.Parameter] = legacy.Save(),
            [LabelPrintingBlocks.CabTextOffsetParameter] = "12",
        });
        var project = new TestProject(new TestDefinition("cab files", "1", new("init", "Init"), new GroupDefinition("loop", "Loop").Add(step), new("end", "Ende")));
        var editor = new EditorViewModel(project);
        var properties = Assert.IsType<StepPropertiesViewModel>(editor.AllNodes().Single(n => n.Id == "label").InlineProperties);
        var row = properties.Parameters.Single(p => p.IsCabLabel);
        step.Parameters[CabLabelLayout.Parameter] = "null";
        Assert.Equal(legacyBytes, LabelDesignerWindow.ReadLayout(row, true).CabSource!.Data);
        Assert.Equal("null", step.Parameters[CabLabelLayout.Parameter]);
        step.Parameters[CabLabelLayout.Parameter] = legacy.Save();
        var serializer = new ProjectSerializer(); var before = serializer.Serialize(project);
        var path = Path.Combine(folder, "original.LBL"); var exported = Path.Combine(folder, "export.lbl");
        var imported = "J\r\n; unchanged comment\nT 1,8,0,5,3;new-{Kennung}\rA 1\r"u8.ToArray();
        File.WriteAllBytes(path, imported);
        var source = new Border(); var owner = Sta.ShowHost(source);
        try
        {
            RunCabDialog(dispatcher, owner, source, row, dialog =>
            {
                Assert.True(dialog.AllowDrop);
                var work = Assert.IsType<LabelLayout>(dialog.DataContext);
                Assert.NotNull(work.CabSource); Assert.Equal(legacyBytes, CabLabelLayout.Render(work));
                Assert.False(dialog.DropCabFiles([Path.Combine(folder, "wrong.png")]));
                Assert.False(dialog.DropCabFiles([path, path]));
                var over = RaiseFileDrag(dialog, DragDrop.PreviewDragOverEvent, new DataObject(DataFormats.FileDrop, new[] { path }));
                Assert.True(over.Handled); Assert.Equal(DragDropEffects.Copy, over.Effects);
                var invalid = RaiseFileDrag(dialog, DragDrop.PreviewDragOverEvent, new DataObject(DataFormats.FileDrop, new[] { path, path }));
                Assert.Equal(DragDropEffects.None, invalid.Effects);
                Assert.False(RaiseFileDrag(dialog, DragDrop.PreviewDragOverEvent, new DataObject(DataFormats.Text, "text")).Handled);
                Assert.False(RaiseFileDrag(dialog, DragDrop.PreviewDropEvent, new DataObject(DataFormats.Text, "text")).Handled);
                Assert.True(RaiseFileDrag(dialog, DragDrop.PreviewDropEvent, new DataObject(DataFormats.FileDrop, new[] { path })).Handled);
                dialog.SaveCabFile(exported);
                Assert.Equal(imported, File.ReadAllBytes(exported));
                Assert.Equal(before, serializer.Serialize(project));
            });
            Assert.Equal(before, serializer.Serialize(project));
            RunCabDialog(dispatcher, owner, source, row, dialog =>
            {
                Assert.True(dialog.DropCabFiles([path]));
                Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "Vorlage übernehmen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.Equal(imported, Convert.FromBase64String(step.Parameters[LabelPrintingBlocks.CabDataParameter]));
            Assert.DoesNotContain(LabelPrintingBlocks.CabTextOffsetParameter, step.Parameters.Keys);
            Assert.Equal(imported, LabelLayout.Load(step.Parameters[CabLabelLayout.Parameter]).CabSource!.Data);
            var zpl = new LabelDesignerWindow(new()); Assert.False(zpl.AllowDrop); zpl.Close();
        }
        finally { owner.Close(); Directory.Delete(folder, true); }
    }

    private static void RunCabDialog(Dispatcher dispatcher, Window owner, FrameworkElement source, ParameterRowViewModel row, Action<LabelDesignerWindow> action)
    {
        Exception? failure = null; var seen = false;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var dialog = owner.OwnedWindows.OfType<LabelDesignerWindow>().Single(); seen = true;
            try { action(dialog); }
            catch (Exception error) { failure = error; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        LabelDesignerWindow.Edit(source, row);
        Assert.True(seen);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static DragEventArgs RaiseFileDrag(Window window, RoutedEvent routedEvent, IDataObject data)
    {
        // WPF normally creates these arguments at its OLE boundary; exercise the actual routed handlers.
        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var args = (DragEventArgs)constructor.Invoke([data, 0, DragDropEffects.Copy, window, new Point(0, 0)]);
        args.RoutedEvent = routedEvent; window.RaiseEvent(args); return args;
    }
}
