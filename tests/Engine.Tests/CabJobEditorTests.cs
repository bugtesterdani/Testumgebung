using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabJobEditorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Moving_a_line_without_final_newline_keeps_commands_separate(int index)
    {
        var editor = new CabSourceEditor(";one\r;two"u8.ToArray());
        var rows = CabJobEditor.Items(editor);
        var next = CabJobEditor.MoveBefore(editor, rows[index], index == 1 ? rows[0] : null);
        Assert.Equal(";two\r;one\r"u8.ToArray(), next.Document.Write());
        Assert.Equal(2, CabJobEditor.Items(next).Count);
    }

    [Fact]
    public void Overview_includes_controls_comments_and_groups_downloads_with_opaque_payloads()
    {
        var editor = new CabSourceEditor("d BMP;logo\r\x1b.\0data\x1b.\r; comment\rJ\rS e;0,0,40,40,60\rT 0,0,0,5,3;a\x1b?b\rA 1\r"u8.ToArray());
        var rows = CabJobEditor.Items(editor);
        var download = Assert.Single(rows, r => !r.IsEmbedded && r.Command?.Syntax.Command == "d");
        Assert.Contains((byte)0, editor.Document.Bytes(download.Range));
        Assert.Contains(rows, r => r.Syntax.Kind == CabNodeKind.Comment);
        var embedded = Assert.Single(rows, r => r.IsEmbedded && r.Command?.Syntax.Command == "\x1b?");
        Assert.Throws<InvalidOperationException>(() => CabJobEditor.Remove(editor, embedded));
        Assert.DoesNotContain(CabJobEditor.Remove(editor, download).Document.Write(), b => b == 0);
    }

    [Fact]
    public void Reordering_preserves_bytes_but_rejects_new_job_order_errors()
    {
        var editor = new CabSourceEditor("J\rS e;0,0,40,40,60\rT 0,0,0,5,3;one\rT 0,10,0,5,3;two\rA 1\r"u8.ToArray());
        var rows = CabJobEditor.Items(editor).Where(r => !r.IsEmbedded).ToArray();
        var moved = CabJobEditor.MoveBefore(editor, rows[3], rows[2]);
        Assert.Equal("J\rS e;0,0,40,40,60\rT 0,10,0,5,3;two\rT 0,0,0,5,3;one\rA 1\r"u8.ToArray(), moved.Document.Write());
        Assert.Throws<InvalidOperationException>(() => CabJobEditor.MoveBefore(editor, rows[^1], rows[2]));
        Assert.Throws<InvalidOperationException>(() => CabJobEditor.MoveBefore(editor, rows[0], rows[2]));
        Assert.Throws<InvalidOperationException>(() => CabJobEditor.Remove(editor, rows[0]));
    }

    [Fact]
    public void Existing_partial_sources_stay_editable_and_loaded_labels_supply_job_context()
    {
        var partial = new CabSourceEditor("T 0,0,0,5,3;partial\r"u8.ToArray());
        Assert.Single(CabJobEditor.OrderIssues(partial));
        Assert.Empty(CabJobEditor.Remove(partial, Assert.Single(CabJobEditor.Items(partial))).Document.Write());
        var loaded = new CabSourceEditor("M l LBL;sample\rR Data;value\rA 1\r"u8.ToArray());
        Assert.Empty(CabJobEditor.OrderIssues(loaded));
        var complete = new CabSourceEditor("J\rA 1\r"u8.ToArray());
        var print = CabJobEditor.Items(complete).Last();
        var added = CabJobEditor.AddCommand(complete, "JS-4.16", print);
        Assert.Contains(added.Validation.Commands, c => c.Syntax.Command == "T");
        Assert.Empty(CabJobEditor.OrderIssues(added));
    }
}
