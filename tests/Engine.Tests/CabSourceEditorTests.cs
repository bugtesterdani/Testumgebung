using System.Text;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class CabSourceEditorTests
{
    [Theory]
    [InlineData(8, 1)]
    [InlineData(9, 1)]
    [InlineData(8, 2)]
    public void Renaming_a_download_refreshes_dependent_images_without_rewriting_their_commands(int width, int height)
    {
        var bitmap = Enumerable.Repeat((byte)0x80, LabelLayout.BytesPerRow(width) * height).ToArray();
        var original = new LabelLayout { Elements =
        [
            new() { Kind = LabelElementKind.Image, ImageWidth = 8, ImageHeight = 1, Bitmap = [0] },
            new() { Kind = LabelElementKind.Image, X = 20, ImageWidth = width, ImageHeight = height, Bitmap = bitmap },
            new() { Kind = LabelElementKind.Text, Value = "unaffected" },
        ] };
        var generated = CabParser.Parse(CabLabelLayout.Render(original));
        var secondImage = CabValidator.Validate(generated).Commands.Where(c => c.Syntax.Command == "I").Last();
        var bytes = generated.Write([new(secondImage.Syntax.Range, [])]);
        var layout = CabLabelImport.Read(bytes); var oldImage = Assert.Single(layout.Elements, e => e.Kind == LabelElementKind.Image);
        var text = Assert.Single(layout.Elements, e => e.Kind == LabelElementKind.Text);
        var before = new CabSourceEditor(bytes);
        var downloads = before.Validation.Commands.Where(c => c.Syntax.Command == "d").ToArray();
        Assert.Equal(2, downloads.Length);
        var firstName = downloads[0].Parameter("name")!.Value!; var secondName = downloads[1].Parameter("name")!.Value!;
        var index = Enumerable.Range(0, before.Sections.Count).Single(i => before.Sections[i].Editable?.Text.Contains("d BMP;" + secondName, StringComparison.Ordinal) == true);
        var after = before.Edit(index, before.Sections[index].Editable!.Text.Replace(secondName, firstName, StringComparison.Ordinal));
        CabSourceReconcile.Apply(layout, before, after);
        var image = Assert.Single(layout.Elements, e => e.Kind == LabelElementKind.Image);
        Assert.NotSame(oldImage, image); Assert.Equal(bitmap, image.Bitmap);
        Assert.Equal(width, image.ImageWidth); Assert.Equal(height, image.ImageHeight);
        Assert.Same(text, Assert.Single(layout.Elements, e => e.Kind == LabelElementKind.Text));
        Assert.Equal(after.Document.Write(), CabLabelLayout.Render(layout));
        var firstCommand = Assert.Single(before.Validation.Commands, c => c.Syntax.Command == "I");
        var nextCommand = Assert.Single(after.Validation.Commands, c => c.Syntax.Command == "I");
        Assert.Equal(before.Document.Bytes(firstCommand.Syntax.Range), after.Document.Bytes(nextCommand.Syntax.Range));
    }

    [Fact]
    public void Unicode_separators_literal_escape_symbols_and_adjacent_controls_remain_distinct()
    {
        const string source = "J\rT 0,0,0,5,3;α␛β\u2028γ\u2029δ\x1b\x1bZ\rA 1\r";
        var bytes = Encoding.UTF8.GetBytes(source); var editor = new CabSourceEditor(bytes);
        var display = Assert.Single(editor.Sections).Editable!.Text;
        Assert.Contains("␛u", display); Assert.Contains("␛e␛e", display);
        Assert.Equal(bytes, editor.Edit(0, display).Document.Write());
        var after = editor.Edit(0, display.Replace("α", "changed", StringComparison.Ordinal).Replace("Z", "last", StringComparison.Ordinal));
        Assert.Equal(Encoding.UTF8.GetBytes(source.Replace("α", "changed", StringComparison.Ordinal).Replace("Z", "last", StringComparison.Ordinal)), after.Document.Write());
    }

    [Fact]
    public void Byte_navigation_points_to_the_start_of_surrogates_and_display_escapes()
    {
        const string source = "J\rT 0,0,0,5,3;😀␛[DAY]\rA 1\r";
        var editor = new CabSourceEditor(Encoding.UTF8.GetBytes(source)); var text = Assert.Single(editor.Sections).Editable!;
        var start = Encoding.UTF8.GetByteCount(source[..source.IndexOf("😀", StringComparison.Ordinal)]);
        var character = text.Text.IndexOf("😀", StringComparison.Ordinal);
        Assert.Equal(character, text.CharacterOffset(start)); Assert.Equal(character, text.CharacterOffset(start + 1));
        Assert.Equal(start, text.ByteOffset(character + 1));
        Assert.Equal(character + 2, text.CharacterOffset(start + 4));
    }

    [Theory]
    [InlineData("␛")]
    [InlineData("␛x")]
    public void Incomplete_or_unknown_escape_notation_does_not_change_the_source(string notation)
    {
        var editor = new CabSourceEditor("T 0,0,0,5,3;text\r"u8.ToArray()); var bytes = editor.Document.Write();
        Assert.Throws<FormatException>(() => editor.Edit(0, editor.Sections[0].Editable!.Text + notation));
        Assert.Equal(bytes, editor.Document.Write());
    }

    [Fact]
    public void Content_completion_inserts_recognized_functions_instead_of_catalog_ellipsis()
    {
        var items = CabSourceCatalog.Complete(CabPrinterGeneration.X4);
        foreach (var entry in CabContentParser.Definitions)
        {
            var item = Assert.Single(items, i => i.CatalogId == entry.Id && i.Kind == CabCompletionKind.Command);
            var syntax = CabContentParser.Parse(item.Insertion, new HashSet<string>(StringComparer.Ordinal) { "name" });
            Assert.Equal(entry.Id, Assert.Single(syntax.Fields).Definition.Id);
        }
        Assert.Equal("[H12]", Assert.Single(items, i => i.CatalogId == "JS-5.1" && i.Kind == CabCompletionKind.Command).Insertion);
    }

    [Fact]
    public void Mixed_codepage_source_edits_use_the_original_strict_encoding()
    {
        var bytes = "\x1boISO-8859-1;J\rT 0,0,0,5,3;"u8.ToArray().Concat(new byte[] { 0xe4, 13 }).Concat("A 1\r"u8.ToArray()).ToArray();
        var editor = new CabSourceEditor(bytes);
        var index = Enumerable.Range(0, editor.Sections.Count).Single(i => editor.Sections[i].Editable?.Text.Contains('ä') == true);
        var text = editor.Sections[index].Editable!.Text;
        var after = editor.Edit(index, text.Replace('ä', 'ö'));
        Assert.Equal(bytes.Select(b => b == 0xe4 ? (byte)0xf6 : b), after.Document.Write());
        Assert.Throws<EncoderFallbackException>(() => editor.Edit(index, text.Replace("ä", "😀", StringComparison.Ordinal)));
        Assert.Equal(bytes, editor.Document.Write());
    }

    [Fact]
    public void Unreadable_fragments_and_encrypted_data_are_protected_without_loss()
    {
        byte[][] cases = [new byte[] { 0xff }, "<ENCRYPTED JOB>\rsecret\0\r</ENCRYPTED JOB>"u8.ToArray(),
            "<ENCRYPTED LABEL: 123456789012>\rsecret"u8.ToArray()];
        foreach (var bytes in cases)
        {
            var editor = new CabSourceEditor(bytes);
            Assert.Contains(editor.Sections, s => s.IsProtected);
            Assert.Equal(bytes, editor.Document.Write());
        }
        Assert.Equal(Array.Empty<byte>(), new CabSourceEditor([]).Edit(0, "").Document.Write());
    }

    [Fact]
    public void Removing_a_command_preserves_the_remaining_object_and_rebases_its_binding()
    {
        var source = "J\rT 0,0,0,5,3;one\rT 0,10,0,5,3;two\rA 1\r";
        var layout = CabLabelImport.Read(Encoding.UTF8.GetBytes(source)); var remaining = layout.Elements[1];
        var before = new CabSourceEditor(Encoding.UTF8.GetBytes(source));
        var after = before.Edit(0, before.Sections[0].Editable!.Text.Replace("T 0,0,0,5,3;one\n", "", StringComparison.Ordinal));
        CabSourceReconcile.Apply(layout, before, after);
        Assert.Same(remaining, Assert.Single(layout.Elements)); Assert.Equal(after.Document.Write(), CabLabelLayout.Render(layout));
    }

    public static IEnumerable<object[]> CatalogFiles() => CabJScriptCatalogCoverageTests.Entries().Select(r => new[] { r[0], r[1] });

    [Theory]
    [MemberData(nameof(CatalogFiles))]
    public void Every_catalog_fixture_opens_in_source_view_and_saves_byte_identically(string id, string file)
    {
        var bytes = File.ReadAllBytes(CabJScriptCatalogCoverageTests.Fixture(file));
        var editor = new CabSourceEditor(bytes);
        Assert.Equal(bytes, editor.Document.Write());
        Assert.Single(CabSourceCatalog.Complete(CabPrinterGeneration.X4), i => i.CatalogId == id && i.Kind == CabCompletionKind.Command);
        var offset = 0;
        foreach (var section in editor.Sections)
        {
            Assert.Equal(offset, section.Range.Offset); offset = section.Range.End;
            if (section.Editable is null) continue;
            var index = editor.Sections.ToList().IndexOf(section);
            Assert.Equal(bytes, editor.Edit(index, section.Editable.Text).Document.Write());
        }
        Assert.Equal(bytes.Length, offset);
    }

    [Theory]
    [InlineData(CabPrinterGeneration.Ax)]
    [InlineData(CabPrinterGeneration.X2)]
    [InlineData(CabPrinterGeneration.X3)]
    [InlineData(CabPrinterGeneration.X4)]
    public void Every_entry_parameter_and_value_is_reachable_with_manual_help(CabPrinterGeneration generation)
    {
        var items = CabSourceCatalog.Complete(generation);
        Assert.Equal(237, items.Count(i => i.Kind == CabCompletionKind.Command));
        Assert.Equal(1416, items.Count(i => i.Kind == CabCompletionKind.Parameter));
        Assert.Equal(1801, items.Count(i => i.Kind == CabCompletionKind.Value));
        foreach (var entry in CabCatalog.Default.Entries)
        {
            var own = items.Where(i => i.CatalogId == entry.Id).ToArray();
            Assert.All(own, item => { Assert.Contains("Handbuch S.", item.Tooltip); Assert.Contains(entry.Id, item.Tooltip); });
            Assert.Equal(entry.Parameters.Select(p => p.Name), own.Where(i => i.Kind == CabCompletionKind.Parameter).Select(i => i.Parameter));
            foreach (var parameter in entry.Parameters)
                Assert.Equal(parameter.Values.Select(v => v.Value), own.Where(i => i.Kind == CabCompletionKind.Value && i.Parameter == parameter.Name).Select(i => i.Text));
        }
        Assert.NotEmpty(CabSourceCatalog.Complete(generation, "JS-4.2.42"));
        Assert.Empty(CabSourceCatalog.Complete(generation, "no-such-catalog-value"));
    }

    [Theory]
    [InlineData("m m\rJ\nT 00,0,0,3,3;ä😀\r\nA 1\r", "ä😀", "longer😀")]
    [InlineData("J\rT 0,0,0,3,3;old\rA 1\r", "old", "new")]
    [InlineData("J\rT 0,0,0,3,3;😀\rA 1\r", "😀", "😃")]
    public void Editing_changes_only_the_selected_text_bytes(string source, string old, string replacement)
    {
        var bytes = Encoding.UTF8.GetBytes(source); var editor = new CabSourceEditor(bytes);
        Assert.Equal(bytes, editor.Document.Write());
        var section = Assert.Single(editor.Sections);
        var next = editor.Edit(0, section.Editable!.Text.Replace(old, replacement, StringComparison.Ordinal));
        Assert.Equal(Encoding.UTF8.GetBytes(source.Replace(old, replacement, StringComparison.Ordinal)), next.Document.Write());
        Assert.Equal(next.Document.Write(), next.Edit(0, next.Sections[0].Editable!.Text).Document.Write());
    }

    [Fact]
    public void Opaque_bytes_are_folded_and_cannot_be_edited_as_text()
    {
        var bytes = "d BMP;logo\r\x1b.\0\xff\x1b.\rJ\rT 0,0,0,3,3;old\rA 1\r"u8.ToArray();
        var editor = new CabSourceEditor(bytes);
        var protectedIndex = Enumerable.Range(0, editor.Sections.Count).Single(i => editor.Sections[i].IsProtected);
        Assert.Contains("Bytes", editor.Sections[protectedIndex].Summary);
        Assert.Throws<InvalidOperationException>(() => editor.Edit(protectedIndex, "changed"));
        var textIndex = Enumerable.Range(0, editor.Sections.Count).Single(i => editor.Sections[i].Editable?.Text.Contains("old", StringComparison.Ordinal) == true);
        var next = editor.Edit(textIndex, editor.Sections[textIndex].Editable!.Text.Replace("old", "new", StringComparison.Ordinal));
        Assert.Equal(bytes.AsSpan(0, 20).ToArray(), next.Document.Write().AsSpan(0, 20).ToArray());
        Assert.Equal(editor.Document.Bytes(editor.Sections[protectedIndex].Range), next.Document.Bytes(next.Sections[protectedIndex].Range));
    }

    [Fact]
    public void Invalid_values_have_source_navigation_and_manual_page_evidence()
    {
        var editor = new CabSourceEditor("J\rB 0,0,45,CODE128,10,.3;ABC\rA 1\r"u8.ToArray());
        var diagnostic = Assert.Single(editor.Validation.Diagnostics, d => d.Code == "parameter-value");
        Assert.Equal(2, diagnostic.Line); Assert.True(diagnostic.FirstPage > 0);
        var section = Assert.Single(editor.Sections).Editable!;
        var character = section.CharacterOffset(diagnostic.Range.Offset);
        Assert.Equal(diagnostic.Range.Offset, section.ByteOffset(character));
        Assert.Contains("45", section.Text[character..]);
        Assert.Contains(CabSourceHighlights.Read(editor), h => h.Style == CabSourceStyle.Parameter);
    }

    [Fact]
    public void Source_changes_reuse_unaffected_models_even_when_offsets_shift()
    {
        var bytes = "J\rT 0,0,0,5,3;one\rT 0,10,0,5,3;two\rT 0,20,0,5,3;three\rA 1\r"u8.ToArray();
        var layout = CabLabelImport.Read(bytes); var originalList = layout.Elements;
        var second = layout.Elements[1]; var third = layout.Elements[2];
        var before = new CabSourceEditor(bytes);
        var after = before.Edit(0, before.Sections[0].Editable!.Text.Replace("one", "much longer one", StringComparison.Ordinal));
        CabSourceReconcile.Apply(layout, before, after);
        Assert.Same(originalList, layout.Elements); Assert.Same(second, layout.Elements[1]); Assert.Same(third, layout.Elements[2]);
        Assert.NotSame(second, third); Assert.Equal(after.Document.Write(), CabLabelLayout.Render(layout));
    }

    [Fact]
    public void Content_field_highlights_use_the_actual_disjoint_byte_ranges()
    {
        var editor = new CabSourceEditor("J\rT 0,0,0,3,3;[DAY] [U:$1F600]\rA 1\r"u8.ToArray());
        var fields = CabSourceHighlights.Read(editor).Where(h => h.Style == CabSourceStyle.ContentField).ToArray();
        Assert.Equal(2, fields.Length);
        Assert.Equal("[DAY]", Encoding.UTF8.GetString(editor.Document.Bytes(fields[0].Range)));
        Assert.Contains("JS-5.", fields[0].Tooltip);
    }
}
