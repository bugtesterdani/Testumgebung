using Testumgebung.Contracts;
using Testumgebung.Domain.Expressions;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class VisualExpressionTests
{
    private static readonly IReadOnlyList<ExpressionVariable> Variables =
    [new("X", VarType.Double, "Gruppe"), new("N", VarType.Int, "Gruppe"), new("Bereit", VarType.Bool, "Gruppe"),
     new("Text", VarType.String, "Gruppe"), new("A", VarType.DateTime, "Gruppe"), new("B", VarType.DateTime, "Gruppe"), new("../N", VarType.Int, "Eltern")];
    private sealed class Values : IValueResolver
    {
        public object Resolve(string name) => name switch
        { "X" => 2.5, "N" => 4L, "Bereit" => true, "Text" => "abc", "A" => new DateTime(2026, 1, 1), "B" => new DateTime(2026, 2, 1), "../N" => 6L, _ => throw new ExpressionException(name) };
    }
    public static IEnumerable<object[]> Roundtrips()
    {
        foreach (var text in new[] { "0", "1.0", "2e3", "2e+3", "-2", "-(X + 3)", "true", "false", "!Bereit", "\"\"", "\"a\\b\\\"c\"", "\"erste\nzweite\"",
            "X + N * 2", "8 - (3 - 2)", "3 / 2", "3.0 / 2", "9 % 4", "X >= 2 && Bereit", "Bereit || (1 / 0 > 1)", "false && (1 / 0 > 1)",
            "Text + \"def\"", "A < B", "../N == 6", "N != X", "N <= 4", "N > 3", "\"a\" >= \"b\"", "Bereit == true" }) yield return [text];
    }
    [Theory, MemberData(nameof(Roundtrips))]
    public void Existing_expression_keeps_types_precedence_literals_and_short_circuiting(string source)
    {
        var model = new ExpressionBuilderViewModel(source, Variables);
        Assert.True(model.CanApply, model.Error);
        var before = Expression.Parse(source).Evaluate(new Values());
        var after = Expression.Parse(model.Source).Evaluate(new Values());
        Assert.Equal(before.GetType(), after.GetType()); Assert.Equal(before, after);
        Assert.NotEmpty(model.Root.Color); Assert.NotEmpty(model.Root.Label); Assert.NotEmpty(model.Root.TypeText);
    }
    [Theory]
    [InlineData("\"abc\" + 1")][InlineData("true * 2")][InlineData("1 && Bereit")][InlineData("!X")]
    [InlineData("-Text")][InlineData("A + 1")][InlineData("Bereit < false")][InlineData("Text == N")]
    [InlineData("Missing")][InlineData("1 +")][InlineData("1e309")][InlineData("9223372036854775808")]
    public void Invalid_or_incompatible_blocks_cannot_be_applied_and_original_is_preserved(string source)
    {
        var model = new ExpressionBuilderViewModel(source, Variables);
        Assert.False(model.CanApply); Assert.NotEmpty(model.Error); Assert.Equal(source, model.Original);
    }
    [Fact]
    public void Typed_targets_and_widening_distinguish_inputs_from_write_targets()
    {
        Assert.False(new ExpressionBuilderViewModel("1", Variables, VarType.Bool).CanApply);
        Assert.True(new ExpressionBuilderViewModel("1", Variables, VarType.Double).CanApply);
        Assert.False(new ExpressionBuilderViewModel("N", Variables, VarType.Double, variablesOnly: true).CanApply);
        Assert.True(new ExpressionBuilderViewModel("X", Variables, VarType.Double, variablesOnly: true).CanApply);
        var target = new ExpressionBuilderViewModel("X", Variables, variablesOnly: true);
        Assert.All(target.Palette.SelectMany(g => g.Items), t => Assert.Equal(VisualExpressionKind.Variable, t.Kind));
        target.Insert(new("Zahl", VisualExpressionKind.Number, "5", "Werte")); Assert.Equal("X", target.Source);
        var empty = new ExpressionBuilderViewModel("", []); empty.Insert(new("Variable", VisualExpressionKind.Variable, "", "Werte"));
        Assert.False(empty.CanApply); Assert.Contains("keine passende Variable", empty.Notice);
    }
    private static ExpressionTile Tile(ExpressionBuilderViewModel model, string value, VisualExpressionKind kind)
        => model.Palette.SelectMany(g => g.Items).Single(t => t.Kind == kind && t.Value == value);
    [Fact]
    public void Slots_wrapping_values_and_local_undo_redo_build_a_real_expression()
    {
        var model = new ExpressionBuilderViewModel("", Variables);
        model.Insert(Tile(model, "+", VisualExpressionKind.Binary)); Assert.False(model.CanApply);
        model.Insert(Tile(model, "0", VisualExpressionKind.Number)); model.Root.Left!.Value = "2,5";
        model.Selected = model.Root.Right!; model.Insert(Tile(model, "", VisualExpressionKind.Variable));
        model.Root.Right!.VariableChoice = Variables.Single(v => v.Path == "X");
        Assert.True(model.CanApply, model.Error); Assert.Equal(5d, Expression.Parse(model.Source).Evaluate(new Values()));
        var previous = model.Source;
        model.Selected = model.Root; model.Insert(Tile(model, ">", VisualExpressionKind.Binary), wrap: true);
        model.Selected = model.Root.Right!; model.Insert(Tile(model, "0", VisualExpressionKind.Number));
        model.Root.Right!.Value = "4"; Assert.Equal(true, Expression.Parse(model.Source).Evaluate(new Values()));
        Assert.Equal("Bool", model.ResultType);
        model.UndoCommand.Execute(null); Assert.Equal("0", model.Root.Right!.Value);
        model.RedoCommand.Execute(null); Assert.Equal("4", model.Root.Right!.Value);
        model.Selected = model.Root.Right!; model.DeleteCommand.Execute(null); Assert.False(model.CanApply);
        model.UndoCommand.Execute(null); Assert.True(model.CanApply); Assert.NotEqual(previous, model.Source);
    }
    [Fact]
    public void Dragging_and_copying_do_not_alias_or_create_cycles_and_stale_slots_are_rejected()
    {
        var model = new ExpressionBuilderViewModel("1 + 2", Variables);
        var oldLeft = model.Root.Left!;
        Assert.False(model.Connect(model.Root, oldLeft)); Assert.False(model.Connect(oldLeft, oldLeft));
        Assert.False(model.Connect(new ExpressionBuilderViewModel("3", Variables).Root, model.Root.Right!));
        Assert.True(model.Connect(oldLeft, model.Root.Right!, copy: true)); Assert.NotSame(model.Root.Left, model.Root.Right);
        model.Root.Right!.Value = "4"; Assert.Equal("1", model.Root.Left!.Value);
        model.UndoCommand.Execute(null); model.UndoCommand.Execute(null); Assert.Equal("2", model.Root.Right!.Value);
        Assert.False(model.Connect(oldLeft, model.Root.Right!));
        var current = model.Source; oldLeft.Value = "999"; Assert.Equal(current, model.Source); Assert.True(model.RedoCommand.CanExecute(null));
        Assert.True(model.Connect(model.Root.Left!, model.Root.Right!)); Assert.False(model.CanApply);
        Assert.Equal(VisualExpressionKind.Empty, model.Root.Left!.Kind);
        model.UndoCommand.Execute(null); Assert.True(model.CanApply);
    }
    [Fact]
    public void Oversized_imports_are_preserved_and_deep_wrapping_is_bounded()
    {
        foreach (var source in new[] { new string('(', 40) + "1" + new string(')', 40), new string('!', 1000) + "true", new string('x', 33000) })
        {
            var model = new ExpressionBuilderViewModel(source, Variables);
            Assert.False(model.CanApply); Assert.Equal(source, model.Original);
        }
        var block = new ExpressionBuilderViewModel("true", Variables);
        for (int i = 0; i < 40; i++) { block.Selected = block.Root; block.Insert(Tile(block, "!", VisualExpressionKind.Unary), wrap: true); }
        Assert.Contains("verschachtelte", block.Notice); Assert.True(block.CanApply);
    }
    [Fact]
    public void Leaf_values_and_history_stay_usable_without_gui()
    {
        var number = new ExpressionBuilderViewModel("1", Variables);
        number.Root.Value = "+2"; Assert.Equal("2", number.Source);
        number.Root.Value = "keine Zahl"; Assert.False(number.CanApply); number.UndoCommand.Execute(null); Assert.True(number.CanApply);
        var boolean = new ExpressionBuilderViewModel("false", Variables); boolean.Root.BooleanValue = true; Assert.Equal("true", boolean.Source);
        var variable = new ExpressionBuilderViewModel("X", Variables); variable.Root.VariableChoice = null; Assert.Equal("X", variable.Source);
        for (int i = 0; i < 105; i++) number.Root.Value = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int count = 0; while (number.UndoCommand.CanExecute(null)) { number.UndoCommand.Execute(null); count++; }
        Assert.Equal(100, count);
    }
    [Fact]
    public void Replacing_large_subtrees_uses_the_resulting_tree_size()
    {
        string Balanced(int depth) => depth == 0 ? "1" : "(" + Balanced(depth - 1) + " + " + Balanced(depth - 1) + ")";
        var model = new ExpressionBuilderViewModel(Balanced(7), Variables);
        Assert.True(model.CanApply, model.Error);
        var source = model.Root.Right!.Right!.Right!.Right!.Right!.Right!;
        Assert.True(model.Connect(source, model.Root.Left!, copy: true));
        Assert.True(model.CanApply, model.Error);
        model.Selected = model.Root; model.Insert(Tile(model, "-", VisualExpressionKind.Unary), wrap: true);
        Assert.True(model.CanApply, model.Error);
    }

    [Fact]
    public void Changing_an_operator_preserves_connected_operands()
    {
        var model = new ExpressionBuilderViewModel("X + 2", Variables);
        model.Insert(Tile(model, "*", VisualExpressionKind.Binary));
        Assert.True(model.CanApply, model.Error);
        Assert.Equal(5d, Expression.Parse(model.Source).Evaluate(new Values()));
        Assert.Equal("X", model.Root.Left!.Value); Assert.Equal("2", model.Root.Right!.Value);
        model.UndoCommand.Execute(null); Assert.Contains("+", model.Source);
        var unary = new ExpressionBuilderViewModel("!Bereit", Variables);
        unary.Insert(Tile(unary, "!", VisualExpressionKind.Unary)); Assert.True(unary.CanApply);
    }

    [Fact]
    public void Bounded_parser_uses_real_tokens_not_operator_characters_inside_strings_or_parent_paths()
    {
        Assert.Equal(ExpressionSyntaxKind.Variable, Expression.Parse("../../N", 2, 1).ToSyntax().Kind);
        Assert.Equal(ExpressionSyntaxKind.Text, Expression.Parse("\"&& || ( )\"", 2, 1).ToSyntax().Kind);
        Assert.Throws<ExpressionException>(() => Expression.Parse("1 + 2", 2, 2));
        Assert.Throws<ExpressionException>(() => Expression.Parse("((true))", 20, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Expression.Parse("1", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Expression.Parse("1", 2, 0));
        string Balanced(int depth) => depth == 0 ? "true" : "(" + Balanced(depth - 1) + " && " + Balanced(depth - 1) + ")";
        var model = new ExpressionBuilderViewModel(Balanced(7), []);
        Assert.True(model.CanApply, model.Error);
        var text = new ExpressionBuilderViewModel("\"text\"", []); text.Root.Value = new string('x', 33000);
        Assert.False(text.CanApply); Assert.Contains("zu lang", text.Error);
    }

    [Fact]
    public void Default_variable_prefers_user_values_of_the_slot_type_over_internal_status()
    {
        ExpressionVariable[] variables = [new("GruppenErgebnis", VarType.String, "Gruppe"), new("_BlockZweig_x", VarType.Bool, "Vorlage"), new("X", VarType.Double, "Gruppe"), new("Bereit", VarType.Bool, "Gruppe")];
        var model = new ExpressionBuilderViewModel("", variables, VarType.Bool);
        model.Insert(Tile(model, "", VisualExpressionKind.Variable)); Assert.Equal("Bereit", model.Root.Value); Assert.True(model.CanApply);
        model.Selected = model.Root; model.Insert(Tile(model, ">=", VisualExpressionKind.Binary));
        model.Insert(Tile(model, "", VisualExpressionKind.Variable)); Assert.Equal("X", model.Root.Left!.Value);
    }

}