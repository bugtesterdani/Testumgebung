using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyCurvePointTable(Dispatcher dispatcher)
    {
        var editor = new EditorViewModel(SampleProjects.AcPruefung());
        var curves = new CurvesViewModel(editor, null, () => "Tester", () => null, ImmediateDispatcher.Instance);
        var view = new CurvesView();
        var open = (Button)view.FindName("OpenPointTableButton");
        open.DataContext = curves;
        Click(open);

        curves.SelectedCurve = Assert.Single(curves.Curves);
        curves.NewFromSelectedCommand.Execute(null);
        var original = Assert.IsType<Testumgebung.Domain.Curves.CurveDefinition>(curves.Working);
        EditPointTable(dispatcher, open, apply: false);
        Assert.Same(original, curves.Working);

        EditPointTable(dispatcher, open, apply: true);
        Assert.Equal(42, curves.Working!.Channels[0].Samples[0]);
        Assert.Same(original.Channels[1], curves.Working.Channels[1]);
        curves.UndoCommand.Execute(null);
        Assert.Same(original, curves.Working);
    }

    private static void EditPointTable(Dispatcher dispatcher, Button open, bool apply)
    {
        var interaction = dispatcher.InvokeAsync(() =>
        {
            var dialog = Assert.Single(Application.Current.Windows.OfType<CurvePointTableWindow>());
            try
            {
                var points = (DataGrid)dialog.FindName("Points");
                var pageInput = (TextBox)dialog.FindName("PageInput");
                var jump = (Button)dialog.FindName("Jump");
                pageInput.Text = "0";
                Click(jump);
                Assert.NotEmpty(((TextBlock)dialog.FindName("Error")).Text);
                pageInput.Text = "1";
                Click(jump);

                var rows = Assert.IsAssignableFrom<IEnumerable<CurvePointTable.Row>>(points.ItemsSource);
                rows.First().Value = "42";
                if (apply) Click((Button)dialog.FindName("ApplyChanges"));
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
            }
        }, DispatcherPriority.Background);
        Click(open);
        interaction.Task.GetAwaiter().GetResult();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
