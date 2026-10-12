using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Editor-Komfort: Rückgängig/Wiederholen, Kopieren/Einfügen/Duplizieren mit neuen IDs, Suche; Versionsvergleich.</summary>
public sealed class EditorComfortTests
{
    private static EditorViewModel Create() => new(SampleProjects.AcPruefung());

    private static PaletteItem Item(EditorViewModel e, string? type) => e.Palette.SelectMany(c => c.Items).First(i => i.BlockType == type);

    [Fact]
    public void Rueckgaengig_und_Wiederholen_ueber_Struktur_und_Eigenschaften()
    {
        var e = Create();
        Assert.False(e.UndoCommand.CanExecute(null));
        var count = e.Project.Definition.AllNodes().Count();

        e.SelectedNode = e.FindNode("pruefling");
        var inserted = e.Insert(Item(e, BlockCatalog.Wait));
        Assert.Equal(count + 1, e.Project.Definition.AllNodes().Count());
        inserted.Name = "Pause A";
        e.MarkChanged();                       // wie die Eigenschaftenseite bei jeder Eingabe
        inserted.Name = "Pause AB";            // Tippfolge am selben Element → ein Schritt
        e.MarkChanged();
        e.Delete(e.FindNode("sicht"));
        Assert.Null(e.Project.Definition.Find("sicht"));

        e.Undo();                              // Löschen zurück
        Assert.NotNull(e.Project.Definition.Find("sicht"));
        Assert.Equal("Pause AB", e.Project.Definition.Find(inserted.Id)!.Name);
        e.Undo();                              // Umbenennen zurück
        Assert.Equal("Warten", e.Project.Definition.Find(inserted.Id)!.Name);
        e.Undo();                              // Einfügen zurück
        Assert.Null(e.Project.Definition.Find(inserted.Id));
        Assert.Equal(count, e.Project.Definition.AllNodes().Count());
        Assert.False(e.UndoCommand.CanExecute(null));
        Assert.True(e.IsDirty);

        e.Redo();
        e.Redo();
        Assert.Equal("Pause AB", e.Project.Definition.Find(inserted.Id)!.Name);
        Assert.NotNull(e.FindNode(inserted.Id));          // Kartenliste neu aufgebaut
        e.Redo();
        Assert.Null(e.Project.Definition.Find("sicht"));
        Assert.False(e.RedoCommand.CanExecute(null));

        // Neue Änderung verwirft Wiederholen
        e.Undo();
        e.SelectedNode = e.FindNode("temperatur");
        e.MoveBy(e.SelectedNode, -1);
        Assert.False(e.RedoCommand.CanExecute(null));

        // Laden setzt den Verlauf zurück
        e.Load(SampleProjects.AcPruefung());
        Assert.False(e.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void Kopieren_Einfuegen_Duplizieren_mit_neuen_IDs()
    {
        var e = Create();
        var group = e.FindNode("belastung")!;               // Gruppe mit Reservierung, Kindern und Exit
        e.Copy(group);
        Assert.True(e.PasteCommand.CanExecute(null));
        e.SelectedNode = e.FindNode("bewertung");
        var pasted = e.Paste()!;
        Assert.NotEqual("belastung", pasted.Id);
        var copy = (GroupDefinition)pasted.Definition;
        var original = (GroupDefinition)group.Definition;
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.Resources, copy.Resources);
        Assert.Equal(original.Children.Count, copy.Children.Count);
        Assert.NotNull(copy.ExitGroup);
        var ids = e.Project.Definition.AllNodes().Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());     // keine doppelten IDs
        Assert.Empty(e.Diagnostics);
        var parent = (GroupDefinition)e.FindNode("pruefling")!.Definition;
        Assert.Equal(parent.Children.FindIndex(n => n.Id == "bewertung") + 1, parent.Children.IndexOf(copy));

        var step = e.FindNode("spannung")!;
        var dup = e.Duplicate(step)!;
        Assert.Equal(step.Step!.Parameters["Ressource"], dup.Step!.Parameters["Ressource"]);
        Assert.NotEqual(step.Id, dup.Id);

        e.Cut(e.FindNode("anschluss"));
        Assert.Null(e.Project.Definition.Find("anschluss"));
        e.SelectedNode = e.FindNode("kennung");
        var back = e.Paste()!;
        Assert.Equal("Prüfling anschließen", back.Name);
        e.Undo();
        Assert.Null(e.Project.Definition.Find(back.Id));
    }

    [Fact]
    public void Suche_ueber_Namen_Parameter_Variablen_und_Reservierung()
    {
        var e = Create();
        e.SearchText = "AC_Quelle";
        Assert.Contains(e.SearchHits, h => h.Node.Id == "belastung" && h.Where.Contains("Reservierung"));
        Assert.Contains(e.SearchHits, h => h.Node.Id == "belastung.ein" && h.Where.Contains("Ressource = AC_Quelle"));
        e.FindNext(+1);
        Assert.Equal(e.SearchHits[0].Node.Id, e.SelectedNode!.Id);
        Assert.StartsWith("Treffer 1 von", e.SearchStatus);
        e.FindNext(-1);
        Assert.Equal(e.SearchHits[^1].Node.Id, e.SelectedNode!.Id);
        Assert.True(e.SelectedNode.Parent!.IsExpanded);

        e.SearchText = "grenzwertmin";
        Assert.Contains(e.SearchHits, h => h.Node.Id == "pruefling" && h.Where.Contains("Variable GrenzwertMin"));
        Assert.Contains(e.SearchHits, h => h.Node.Id == "spannung");
        e.SearchText = "Werkerinteraktion";
        Assert.Contains(e.SearchHits, h => h.Node.Id == "kennung" && h.Where.Contains("Baustein"));
        e.SearchText = "gibtesnicht";
        Assert.Equal("Keine Treffer", e.SearchStatus);
        e.SearchText = "";
        Assert.Null(e.SearchStatus);
    }

    [Fact]
    public void Versionsvergleich_findet_Aenderungen_ueber_stabile_IDs()
    {
        var serializer = new ProjectSerializer();
        var before = SampleProjects.AcPruefung();
        var after = serializer.Clone(before);
        var def = after.Definition;
        ((StepDefinition)def.Find("spannung")!).Parameters["Untergrenze"] = "215";
        def.Find("sicht")!.Name = "Sichtkontrolle";
        var pruefling = (GroupDefinition)def.Find("pruefling")!;
        pruefling.Variables.RemoveAll(v => v.Name == "GrenzwertMax");
        pruefling.Variables.Add(new VariableDefinition("Seriennummer", VarType.String, "x"));
        pruefling.Children.Remove(def.Find("bewertung")!);
        pruefling.Children.Insert(0, BlockCatalog.Default.Create(BlockCatalog.Wait, "neu1", "Vorlauf"));
        var temp = pruefling.Children.First(n => n.Id == "temperatur");
        pruefling.Children.Remove(temp);
        pruefling.Children.Add(temp);
        ((GroupDefinition)def.Find("belastung")!).Repetition = Repetition.Times(5);
        def.Layout = new OperatorLayout { ShowProtocol = false };
        after.SimulationComm.Add(new SimulatedComm("Bus", "b", Testumgebung.Hardware.CommKind.Can, "zyklisch 0x100 01"));

        var diff = ProjectDiff.Compare(before, after);
        Assert.Contains(diff, d => d.Kind == DiffKind.Changed && d.Detail == "Untergrenze: GrenzwertMin → 215");
        Assert.Contains(diff, d => d.Kind == DiffKind.Changed && d.Detail == "Name: Sichtprüfung → Sichtkontrolle");
        Assert.Contains(diff, d => d.Kind == DiffKind.Removed && d.Area == "Variable" && d.Item.EndsWith("GrenzwertMax"));
        Assert.Contains(diff, d => d.Kind == DiffKind.Added && d.Area == "Variable" && d.Item.EndsWith("Seriennummer"));
        Assert.Contains(diff, d => d.Kind == DiffKind.Removed && d.Item.EndsWith("Bewertung Temperatur"));
        Assert.Contains(diff, d => d.Kind == DiffKind.Added && d.Item.EndsWith("Vorlauf"));
        Assert.Contains(diff, d => d.Kind == DiffKind.Moved && d.Item.EndsWith("Gehäusetemperatur erfassen"));
        Assert.Contains(diff, d => d.Detail == "Wiederholung: 3× → 5×");
        Assert.Contains(diff, d => d.Area == "Werkeransicht");
        Assert.Contains(diff, d => d.Area == "Simulation" && d.Item == "Bus" && d.Kind == DiffKind.Added);
        Assert.Empty(ProjectDiff.Compare(before, serializer.Clone(before)));
    }
}
