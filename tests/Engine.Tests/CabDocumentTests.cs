using System.Text;
using System.Text.Json;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabDocumentTests
{
    [Theory]
    [InlineData("")]
    [InlineData("; comment\r\nJ\rS l1;0,0,30,32,50\nT:a;1,2,0,5,3; x,;[DATE]  \rA 3\r\nJ\nA 1\n")]
    [InlineData("unknown\u001ba tail\r\n<ABC>\rJ\n\u001boBAD;\r</ABC>\n")]
    [InlineData("d BMP;image\r\u001b.a\u001b\u001b.b\u001ba\u001b.\r")]
    [InlineData("d DBF;db\n\u001b:a\u001ba\r\u001bend-of-data\n")]
    [InlineData("<BASE64>\r\nYWJj\n</BASE64>\r<ENCRYPTED JOB>J\rA 1</ENCRYPTED JOB>")]
    [InlineData("<ENCRYPTED LABEL: 012345678901>\r\u001ba\0\nJ\nA 1")]
    [InlineData("W 0,0,0,10,20,3,4;<HTML>\n<p>x</p>\r\n</HTML>\rA 1\r")]
    public void Original_bytes_and_all_root_ranges_are_preserved(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var document = CabParser.Parse(bytes);
        Assert.Equal(bytes, document.Write());
        Assert.Equal(bytes, document.Root.Children.SelectMany(n => document.Bytes(n.Range)).ToArray());
        CheckRanges(document.Root);
    }

    [Fact]
    public void Arbitrary_bytes_never_throw_and_remain_identical()
    {
        var random = new Random(255);
        for (var sample = 0; sample < 2000; sample++)
        {
            var bytes = new byte[random.Next(513)];
            random.NextBytes(bytes);
            var document = CabParser.Parse(bytes);
            Assert.Equal(bytes, document.Write());
            CheckRanges(document.Root);
        }
    }

    [Fact]
    public void Random_catalog_sequences_preserve_bytes()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs/cab/jscript-befehlskatalog.json")));
        var syntax = catalog.RootElement.GetProperty("eintraege").EnumerateArray()
            .SelectMany(e => e.GetProperty("syntax").EnumerateArray()).Select(s => s.GetString()!).ToArray();
        var random = new Random(237);
        for (var run = 0; run < 100; run++)
        {
            var text = string.Join("\r\n", Enumerable.Range(0, 30).Select(_ => syntax[random.Next(syntax.Length)]))
                .Replace("ESC", "\u001b", StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(text);
            Assert.Equal(bytes, CabParser.Parse(bytes).Write());
        }
    }

    [Fact]
    public void Every_lossless_fixture_round_trips_through_parser_and_existing_import_entry_point()
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "tests/Fixtures/CabJScript"), "*.lbl");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            Assert.Equal(bytes, CabParser.Parse(bytes).Write());
            Assert.Equal(bytes, Testumgebung.Presentation.ViewModels.CabLabelLayout.Render(
                Testumgebung.Presentation.ViewModels.CabLabelImport.Read(bytes)));
        }
    }

    [Fact]
    public void All_escape_commands_are_framed_even_inside_lines()
    {
        var commands = new[] { "\u001b\u001b", "\u001b!\u001b!", "\u001b?", "\u001ba", "\u001bb", "\u001bc", "\u001bend-of-data", "\u001bf", "\u001bg", "\u001bi", "\u001bj", "\u001bl", "\u001boUTF-8;", "\u001bp0", "\u001bp1", "\u001br", "\u001bs", "\u001bt", "\u001bv0", "\u001bxin0;", "\u001bxout", "\u001by", "\u001bz" };
        var document = Parse("T 0,0" + string.Join("x", commands) + ";data\r");
        var actual = document.Root.Children.Where(n => n.Kind == CabNodeKind.EscapeCommand)
            .Select(n => Encoding.ASCII.GetString(document.Bytes(n.Range))).ToArray();
        Assert.Equal(commands, actual);
        var binary = Parse("\u001b.a\u001b\u001b.b\u001b?c\u001b.");
        var node = Assert.Single(binary.Root.Children);
        Assert.Equal(CabNodeKind.BinaryBlock, node.Kind);
        Assert.Equal("\u001b?", Assert.Single(node.Children).Command);
    }

    [Fact]
    public void Block_names_in_comments_and_text_are_not_opaque_blocks_and_html_does_not_hide_escape_commands()
    {
        var document = Parse("; literal <ABC>\nT 0,0,0,5,3;<ENCRYPTED JOB>\nW 0;<HTML>x\u001ba y</HTML>\nA 1\n");
        Assert.DoesNotContain(document.Root.Children, n => n.Kind is CabNodeKind.Block or CabNodeKind.BinaryBlock);
        Assert.Contains(document.Root.Children, n => n.Kind == CabNodeKind.Comment && document.Decode(n) == "; literal <ABC>");
        Assert.Contains(document.Root.Children, n => n.Kind == CabNodeKind.EscapeCommand && n.Command == "\u001ba");
        Assert.Equal("\n", Parse("W 0;<HTML>\nx\ny\n</HTML>\r").LineEnding);
    }

    [Fact]
    public void Encoding_and_units_are_tracked_at_each_position()
    {
        var prefix = Encoding.ASCII.GetBytes("m i\rJ\rT 0,0,0,5,3;");
        var bytes = prefix.Concat(new byte[] { 0xe4 }).Concat(Encoding.ASCII.GetBytes("\rA 1\r\u001boUTF-8;\rm m\rJ\rT 0,0,0,5,3;"))
            .Concat(Encoding.UTF8.GetBytes("ä\rA 1\r")).ToArray();
        var document = CabParser.Parse(bytes, new("windows-1252"));
        var texts = Flatten(document.Root).Where(n => n.Kind == CabNodeKind.LabelCommand && n.Command == "T").ToArray();
        Assert.Equal(2, texts.Length);
        Assert.Equal(CabUnit.Inches, texts[0].Unit);
        Assert.Equal("windows-1252", texts[0].CodePage);
        Assert.EndsWith("ä", document.Decode(texts[0]));
        Assert.Equal(CabUnit.Millimetres, texts[1].Unit);
        Assert.Equal("UTF-8", texts[1].CodePage);
        Assert.EndsWith("ä", document.Decode(texts[1]));
        Assert.Equal(bytes, document.Write());
    }

    [Fact]
    public void Control_parameters_inside_escaped_downloads_are_recognized_but_raw_downloads_disable_them()
    {
        foreach (var escaped in new[] { true, false })
        {
            var source = (escaped ? "\u001b." : "\u001b:") + "\u001bowindows-1252;"
                + (escaped ? "\u001b." : "\u001bend-of-data") + "\rT 0,0,0,5,3;";
            var bytes = Encoding.ASCII.GetBytes(source).Concat(new byte[] { 0xe4 }).ToArray();
            var document = CabParser.Parse(bytes);
            var block = document.Root.Children[0];
            var text = document.Root.Children[^1];
            Assert.Equal(escaped ? "windows-1252" : "UTF-8", text.CodePage);
            Assert.Equal(escaped ? "T 0,0,0,5,3;ä" : null, document.Decode(text));
            if (escaped)
            {
                var control = Assert.Single(block.Children);
                Assert.Equal("windows-1252", document.Decode(Assert.Single(control.Children)));
            }
            else Assert.Empty(block.Children);
            Assert.Equal(bytes, document.Write());
        }
    }

    [Fact]
    public void Invalid_encodings_and_frames_produce_diagnostics_without_losing_data()
    {
        foreach (var text in new[] { "\u001b", "\u001boUTF-8\r", "\u001b.a", "\u001b:a", "<ABC>x", "<BASE64>x", "<ENCRYPTED JOB>x", "m ?\r", "?raw\r", "W 0;<HTML>x\r" })
        {
            var document = Parse(text);
            Assert.NotEmpty(document.Diagnostics);
            Assert.Equal(Encoding.UTF8.GetBytes(text), document.Write());
        }
        var invalid = CabParser.Parse([0xff, 0xfe, 10]);
        Assert.Null(invalid.Decode(invalid.Root.Children[0]));
        Assert.NotEmpty(invalid.Diagnostics);
        var unknownPage = CabParser.Parse("T x"u8, new("unknown-page"));
        Assert.Null(unknownPage.Decode(unknownPage.Root.Children[0]));
    }

    [Fact]
    public void Parameters_keep_empty_slots_data_delimiters_and_content_field_ranges()
    {
        var document = Parse("T:Name;1,,0,5,3;A,;[+:1,2][unknown]\r");
        var command = Assert.Single(document.Root.Children, n => n.Kind == CabNodeKind.LabelCommand);
        Assert.Equal(new[] { "Name", "1", "", "0", "5", "3", "A,;[+:1,2][unknown]" }, command.Children.Select(document.Decode));
        var fields = command.Children[^1].Children;
        Assert.Equal(new[] { "[+:1,2]", "[unknown]" }, fields.Select(document.Decode));
        var output = document.Write([document.Replace(command.Children[2], "2.5"), document.Replace(fields[0], "[+:4,8]")]);
        Assert.Equal("T:Name;1,2.5,0,5,3;A,;[+:4,8][unknown]\r", Encoding.UTF8.GetString(output));
        Assert.Equal("T:Name;1,,0,5,3;A,;[+:1,2][unknown]\r", Encoding.UTF8.GetString(document.Write()));
    }

    [Theory]
    [InlineData("", "\r\n")]
    [InlineData("a\na\r\na\n", "\n")]
    [InlineData("a\r\na\n", "\r\n")]
    [InlineData("a\ra\r\na\r", "\r")]
    public void Appended_lines_use_predominant_ending_with_first_seen_tiebreak(string text, string ending)
    {
        var document = Parse(text);
        Assert.Equal(ending, document.LineEnding);
        Assert.Equal(text + "A 1" + ending, Encoding.UTF8.GetString(document.AppendLine("A 1")));
        Assert.Equal("tail\r\nA 1\r\n", Encoding.UTF8.GetString(Parse("tail").AppendLine("A 1")));
    }

    [Fact]
    public void Writer_rejects_overlapping_or_out_of_document_edits_and_copies_input()
    {
        var source = "abc"u8.ToArray();
        var document = CabParser.Parse(source);
        source[0] = 0;
        Assert.Equal("abc"u8.ToArray(), document.Write());
        Assert.Throws<ArgumentException>(() => document.Write([new(new(0, 2), []), new(new(1, 1), [])]));
        foreach (var range in new[] { new CabSourceRange(-1, 1), new(0, -1), new(2, 2), new(int.MaxValue, 1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => document.Bytes(range));
        Assert.Equal("aXc", Encoding.UTF8.GetString(document.Write([new(new(1, 1), "X"u8.ToArray())])));
    }

    private static CabDocument Parse(string text) => CabParser.Parse(Encoding.UTF8.GetBytes(text));
    private static IEnumerable<CabSyntaxNode> Flatten(CabSyntaxNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
    private static void CheckRanges(CabSyntaxNode parent)
    {
        foreach (var node in parent.Children)
        {
            Assert.InRange(node.Range.Offset, parent.Range.Offset, parent.Range.End);
            Assert.InRange(node.Range.End, node.Range.Offset, parent.Range.End);
            CheckRanges(node);
        }
    }
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory.Parent is not null && !File.Exists(Path.Combine(directory.FullName, "Testumgebung.sln"))) directory = directory.Parent;
        return directory.FullName;
    }
}
