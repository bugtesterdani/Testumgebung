using System.Windows;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class ContextHelpWindow : Window
{
    public ContextHelpWindow(HandbookContent help)
    {
        InitializeComponent();
        DataContext = help;
    }
}
