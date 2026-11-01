using System.Windows.Controls;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Definitions;
using Testumgebung.Presentation.ViewModels;
namespace Testumgebung.Desktop.UiTests;
public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyNodeOptions(ShellViewModel shell, EditorView page, System.Windows.Threading.Dispatcher dispatcher)
    {
        var node=shell.Editor.AllNodes().First(n=>n.Step is not null);
        shell.Editor.SelectedNode=node;page.UpdateLayout();Sta.Flush(dispatcher);
        var enabled=Descendants(page).OfType<CheckBox>().Single(c=>c.Content?.ToString()=="Element aktiv");
        enabled.IsChecked=false;Sta.Flush(dispatcher);Assert.True(node.Definition.Disabled);Assert.True(node.IsInactive);
        enabled.IsChecked=true;Sta.Flush(dispatcher);Assert.False(node.Definition.Disabled);
        var expander=Descendants(page).OfType<Expander>().Single(e=>e.Header?.ToString()=="Ablauf-Haltepunkt");
        expander.IsExpanded=true;page.UpdateLayout();Sta.Flush(dispatcher);
        var point=Descendants(expander).OfType<CheckBox>().Single(c=>c.Content?.ToString()=="Haltepunkt aktiv");
        point.IsChecked=true;Sta.Flush(dispatcher);Assert.True(shell.Editor.Breakpoints!.HasBreakpoint(node.Id));
        var modes=Descendants(expander).OfType<ComboBox>().Single();modes.SelectedItem=BreakpointMode.FailOrError;Sta.Flush(dispatcher);
        Assert.Equal(BreakpointMode.FailOrError,node.Definition.BreakpointRule.Mode);
        point.IsChecked=false;Sta.Flush(dispatcher);Assert.False(shell.Editor.Breakpoints.HasBreakpoint(node.Id));
        modes.SelectedItem=BreakpointMode.Always;Sta.Flush(dispatcher);Assert.Equal(BreakpointRule.Default,node.Definition.BreakpointRule);
    }
}
