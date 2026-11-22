using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabSourceHistoryTests
{
    [Fact]
    public void A_new_opaque_block_can_be_undone_and_redone_byte_exactly()
    {
        var initial = new CabSourceEditor("J\rT 0,0,0,5,3;data\rA 1\r"u8.ToArray());
        var history = new CabSourceHistory(initial);
        Assert.Same(initial, history.Undo()); Assert.Same(initial, history.Redo());
        Assert.False(history.Record(initial));
        var opaque = initial.Edit(0, "␛e." + initial.Sections[0].Editable!.Text);
        Assert.True(history.Record(opaque)); Assert.Contains(history.Current.Sections, s => s.IsProtected);
        Assert.Equal(initial.Document.Write(), history.Undo().Document.Write());
        Assert.True(history.CanRedo); Assert.Equal(opaque.Document.Write(), history.Redo().Document.Write());
    }

    [Fact]
    public void New_edits_discard_redo_and_the_bounded_history_keeps_the_latest_changes()
    {
        var initial = new CabSourceEditor("T 0,0,0,5,3;0\r"u8.ToArray());
        var history = new CabSourceHistory(initial, 2);
        foreach (var value in new[] { "1", "2", "3" })
            history.Record(history.Current.Edit(0, "T 0,0,0,5,3;" + value + "\n"));
        history.Undo(); history.Undo();
        Assert.False(history.CanUndo); Assert.Contains(";1", history.Current.Sections[0].Editable!.Text);
        history.Record(history.Current.Edit(0, "T 0,0,0,5,3;new\n"));
        Assert.False(history.CanRedo);
        Assert.Throws<ArgumentException>(() => history.Record(new("T 0,0,0,5,3;other\r"u8.ToArray(), "ISO-8859-1")));
    }
}
