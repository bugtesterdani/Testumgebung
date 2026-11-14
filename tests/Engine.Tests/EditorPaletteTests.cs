using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class EditorPaletteTests
{
    private static EditorViewModel Editor() => new(new TestProject(Kit.Test(new GroupDefinition("loop", "Prüfen"))));

    [Fact]
    public void Normal_selection_has_one_group_and_every_real_block_once()
    {
        var editor = Editor();
        var items = editor.Palette.SelectMany(category => category.Items).ToList();
        Assert.DoesNotContain(editor.Palette, category => category.Name == "Steuerung");
        Assert.Single(items, item => item.BlockType is null);
        Assert.DoesNotContain(items, item => item.Preset is not null);
        Assert.Equal(editor.Catalog.All.Where(block => block.Type is not (BlockCatalog.SetRating or BlockCatalog.RaiseError or NetworkBlocks.Download)
            && block.Type is not (BlockCatalog.CurveLoad or BlockCatalog.CurveTransfer or BlockCatalog.CurveStart or BlockCatalog.CurveStop)
            && block.Type is not (ProcessBlocks.Send or ProcessBlocks.WaitOutput or ProcessBlocks.Wait or ProcessBlocks.Status or ProcessBlocks.Stop)).Select(block => block.Type).Order(), items.Where(item => item.BlockType is not null).Select(item => item.BlockType!).Order());
        Assert.All(editor.Palette.Where(category => category.Name == "Kommunikation"), category => Assert.False(category.IsExpanded));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Visual_modes_keep_all_control_variants_without_changing_the_project(bool graph)
    {
        var editor = Editor(); var serializer = new ProjectSerializer();
        var before = serializer.Serialize(editor.Project);
        var changes = new List<string?>(); editor.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        editor.PaletteSearch = "does not exist";
        Assert.Empty(editor.Palette);
        if (graph) editor.GraphView = true; else editor.VisualBlocks = true;
        Assert.Contains(nameof(editor.Palette), changes);
        var controls = editor.Palette.Single(category => category.Name == "Steuerung").Items;
        Assert.Equal(new[] { "ifelse", "parallel", "repeat", "until", "when", "while" }, controls.Select(item => item.Preset).Order());
        Assert.Equal(before, serializer.Serialize(editor.Project));
        Assert.False(editor.IsDirty); Assert.False(editor.UndoCommand.CanExecute(null));
        changes.Clear();
        if (graph) editor.GraphView = false; else editor.VisualBlocks = false;
        Assert.Contains(nameof(editor.Palette), changes);
        Assert.Empty(editor.Palette);
        editor.PaletteSearch = "";
        Assert.DoesNotContain(editor.Palette, category => category.Name == "Steuerung");
    }

    [Theory]
    [InlineData("MODBUS lesen", "modbus.lesen")]
    [InlineData("kommunikation modbus", "modbus.schreiben")]
    [InlineData("analog.messen", BlockCatalog.Measure)]
    public void Search_finds_specialized_blocks_and_opens_matching_categories(string search, string type)
    {
        var editor = Editor(); editor.PaletteSearch = search;
        Assert.Contains(editor.Palette.SelectMany(category => category.Items), item => item.BlockType == type);
        Assert.All(editor.Palette, category => Assert.True(category.IsExpanded));
        Assert.All(editor.Palette, category => Assert.NotEmpty(category.Items));
        editor.PaletteSearch = "not-a-block-at-all";
        Assert.Empty(editor.Palette);
        editor.PaletteSearch = "  ";
        Assert.Contains(editor.Palette.SelectMany(category => category.Items), item => item.BlockType == BlockCatalog.Code);
    }

    [Fact]
    public void Existing_if_else_template_survives_normal_mode_editing_and_roundtrip()
    {
        var editor = Editor(); editor.GraphView = true;
        var item = editor.Palette.SelectMany(category => category.Items).Single(item => item.Preset == "ifelse");
        var group = editor.InsertAt(item, editor.FindNode("loop")!, 0);
        var id = group.Id; var serializer = new ProjectSerializer(); var before = serializer.Serialize(editor.Project);
        editor.GraphView = false;
        Assert.Equal(before, serializer.Serialize(serializer.Clone(editor.Project)));
        Assert.NotNull(editor.FindNode(id));
        Assert.Equal(3, editor.FindNode(id)!.Group!.Children.Count);
        editor.UndoCommand.Execute(null); Assert.Null(editor.FindNode(id));
        editor.RedoCommand.Execute(null); Assert.Equal(before, serializer.Serialize(editor.Project));
    }
}
