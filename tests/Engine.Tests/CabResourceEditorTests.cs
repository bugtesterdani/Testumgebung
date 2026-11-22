using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabResourceEditorTests
{
    [Theory]
    [InlineData("d TMP;data\r\u001b.old\u001b?data\u001b.\rJ\rA 1\r")]
    [InlineData("d TMP;data\r\u001b.unclosed")]
    [InlineData("d TMP;data\r\u001b:unclosed")]
    [InlineData("d TMP;data\r<BASE64>unclosed")]
    public void File_exchange_rejects_ambiguous_boundaries_or_embedded_controls(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var editor = new CabSourceEditor(bytes);
        var resource = Assert.Single(CabResourceEditor.Resources(editor));
        Assert.Throws<InvalidDataException>(() => CabResourceEditor.Replace(editor, resource, [1, 2]));
        Assert.Equal(bytes, editor.Document.Write());
    }

    [Fact]
    public void A_closed_base64_frame_with_invalid_old_data_can_be_repaired_without_deleting_neighbours()
    {
        var editor = new CabSourceEditor("d TMP;data\r<BASE64>???</BASE64>\r;keep\r"u8.ToArray());
        var next = CabResourceEditor.Replace(editor, Assert.Single(CabResourceEditor.Resources(editor)), [1, 2]);
        Assert.Equal("d TMP;data\r<BASE64>AQI=</BASE64>\r;keep\r"u8.ToArray(), next.Document.Write());
    }

    [Theory]
    [InlineData("JS-2.3")]
    [InlineData("JS-2.4")]
    public void Standalone_binary_commands_start_empty_and_accept_real_file_data(string id)
    {
        var editor = new CabSourceEditor(CabPropertyTemplates.Invocation(id));
        var resource = Assert.Single(CabResourceEditor.Resources(editor));
        Assert.Empty(CabResourceEditor.Read(editor, resource));
        var changed = CabResourceEditor.Replace(editor, resource, [0, 255, 27]);
        Assert.Equal(new byte[] { 0, 255, 27 }, CabResourceEditor.Read(changed, Assert.Single(CabResourceEditor.Resources(changed))));
        Assert.Throws<InvalidOperationException>(() => CabPropertyTemplates.Invocation("JS-3.3"));
    }

    [Fact]
    public void Every_catalog_download_type_can_be_created_and_exchanged()
    {
        Assert.NotEmpty(CabResourceEditor.Types);
        foreach (var type in CabResourceEditor.Types)
        {
            var editor = CabResourceEditor.Add(new CabSourceEditor([]), type.Value, "asset", [0, 27, 255]);
            var resource = Assert.Single(CabResourceEditor.Resources(editor));
            Assert.Equal(type.Value, resource.Type);
            Assert.Equal("asset", resource.Name);
            Assert.Equal(new byte[] { 0, 27, 255 }, CabResourceEditor.Read(editor, resource));
            var changed = CabResourceEditor.Replace(editor, resource, [3, 4]);
            Assert.Equal(new byte[] { 3, 4 }, CabResourceEditor.Read(changed, Assert.Single(CabResourceEditor.Resources(changed))));
        }
    }

    [Theory]
    [InlineData("ESC.")]
    [InlineData("ESC:")]
    [InlineData("<BASE64>")]
    public void Replacement_roundtrips_every_byte_and_preserves_neighbouring_source(string framing)
    {
        var before = Encoding.ASCII.GetBytes(";before\rd PNG;picture\r");
        var after = Encoding.ASCII.GetBytes("\r;after\rJ\rA 1\r");
        var original = new byte[] { 1, 2, 3 };
        var editor = new CabSourceEditor(before.Concat(CabResourceEditor.Frame(original, framing)).Concat(after).ToArray());
        var resource = Assert.Single(CabResourceEditor.Resources(editor));
        Assert.Equal(original, CabResourceEditor.Read(editor, resource));
        var replacement = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var next = CabResourceEditor.Replace(editor, resource, replacement);
        Assert.Equal(replacement, CabResourceEditor.Read(next, Assert.Single(CabResourceEditor.Resources(next))));
        Assert.True(next.Document.Write().AsSpan().StartsWith(before));
        Assert.True(next.Document.Write().AsSpan().EndsWith(after));
    }

    [Fact]
    public void Raw_terminator_inside_file_switches_to_escaped_framing_without_losing_data()
    {
        var payload = Encoding.ASCII.GetBytes("one\u001bend-of-data\u001b.two");
        var bytes = Encoding.ASCII.GetBytes("d TMP;data\r").Concat(CabResourceEditor.Frame(payload, "ESC:")).ToArray();
        var editor = new CabSourceEditor(bytes);
        var resource = Assert.Single(CabResourceEditor.Resources(editor));
        Assert.Equal("ESC.", resource.Block.Command);
        Assert.Equal(payload, CabResourceEditor.Read(editor, resource));
    }
}
