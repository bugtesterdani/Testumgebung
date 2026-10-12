using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Curves;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class EditorValueBoundaryTests
{
    public static IEnumerable<object[]> Inputs()
    {
        foreach (var type in Enum.GetValues<VarType>())
            foreach (var name in new[] { "Auftrag", "_Wert", "Wert_2", "ä", "", " ", "1Wert", "A/B", "A B", "A-B" })
                foreach (var value in new[] { "", "A123", "0", "1.25", "true", "false", "invalid" })
                    yield return [type, name, value];
    }

    [Theory, MemberData(nameof(Inputs))]
    public void Port_editing_validates_names_and_typed_values_and_recovers(VarType type, string name, string value)
    {
        var changes = 0;
        var row = new PortRowViewModel(() => changes++, new CodePort("Initial", VarType.String, PortDirection.Input));
        row.Name = name; row.Type = type; row.TestValue = value;
        row.Direction = PortDirection.Output; row.Unit = "V"; row.Description = "Beschreibung";
        var trimmed = name.Trim();
        var validName = trimmed.Length > 0 && (char.IsLetter(trimmed[0]) || trimmed[0] == '_') && trimmed.All(c => char.IsLetterOrDigit(c) || c == '_');
        object? expected = null;
        var validValue = true;
        try { if (value.Length > 0) expected = TypedValue.Parse(value, type); }
        catch (TypeMismatchException) { validValue = false; }
        var port = row.ToPort();
        if (validName && validValue)
        {
            Assert.NotNull(port); Assert.Null(row.Error);
            Assert.Equal(trimmed, port.Name); Assert.Equal(expected, port.TestValue);
            Assert.Equal(type, port.Type); Assert.Equal(PortDirection.Output, port.Direction);
            Assert.Equal("V", port.Unit); Assert.Equal("Beschreibung", port.Description);
        }
        else { Assert.Null(port); Assert.NotNull(row.Error); }
        row.Name = "Auftrag"; row.Type = VarType.String; row.TestValue = "A123";
        row.Unit = ""; row.Description = "";
        var repaired = Assert.IsType<CodePort>(row.ToPort());
        Assert.Null(row.Error); Assert.Equal("A123", repaired.TestValue);
        Assert.Null(repaired.Unit); Assert.Null(repaired.Description);
        Assert.True(changes > 0);
        var before = changes; row.Name = row.Name; row.Type = row.Type; row.Direction = row.Direction;
        row.Unit = row.Unit; row.Description = row.Description; row.TestValue = row.TestValue;
        Assert.Equal(before, changes);
    }

    [Theory, MemberData(nameof(Inputs))]
    public void Variable_editing_preserves_invalid_input_and_can_be_repaired(VarType type, string name, string value)
    {
        var changes = 0;
        var row = new VariableRowViewModel(new VariableDefinition("Initial", VarType.String, "A123"), () => changes++);
        row.Name = name; row.Type = type; row.Initial = value;
        var validName = !string.IsNullOrWhiteSpace(name) && !name.Trim().Contains('/') && !name.Trim().Contains(' ');
        var validValue = true;
        object? expected = null;
        try { if (value.Length > 0) expected = TypedValue.Parse(value, type); }
        catch (TypeMismatchException) { validValue = false; }
        var definition = row.ToDefinition();
        if (validName && validValue)
        { Assert.NotNull(definition); Assert.Equal(expected, definition.Initial); Assert.Null(row.Error); }
        else { Assert.Null(definition); Assert.NotNull(row.Error); }
        Assert.Equal(value, row.Initial);
        row.Name = "Seriennummer"; row.Type = VarType.String; row.Initial = "SN123";
        Assert.Equal("SN123", row.ToDefinition()!.Initial); Assert.Null(row.Error);
        var before = changes; row.Name = row.Name; row.Type = row.Type; row.Initial = row.Initial;
        Assert.Equal(before, changes);
    }

    public static IEnumerable<object[]> FormulaCases()
    {
        foreach (var name in new[] { "sin", "cos", "tan", "asin", "acos", "atan", "sqrt", "abs", "exp", "ln", "log10", "floor", "ceil", "round", "sign", "step" })
            foreach (var value in new[] { -0.5, 0.0, 0.5, 1.0 }) yield return [name, value];
    }

    [Theory, MemberData(nameof(FormulaCases))]
    public void Curve_functions_match_the_mathematical_contract(string name, double value)
    {
        var expected = name switch
        {
            "sin" => Math.Sin(value), "cos" => Math.Cos(value), "tan" => Math.Tan(value),
            "asin" => Math.Asin(value), "acos" => Math.Acos(value), "atan" => Math.Atan(value),
            "sqrt" => Math.Sqrt(value), "abs" => Math.Abs(value), "exp" => Math.Exp(value),
            "ln" => Math.Log(value), "log10" => Math.Log10(value), "floor" => Math.Floor(value),
            "ceil" => Math.Ceiling(value), "round" => Math.Round(value, MidpointRounding.AwayFromZero),
            "sign" => Math.Sign(value), _ => value < 0 ? 0 : 1,
        };
        Assert.Equal(expected, CurveFormula.Compile($"{name}(t)")(value));
        Assert.Throws<CurveFormulaException>(() => CurveFormula.Compile($"{name}()"));
        Assert.Throws<CurveFormulaException>(() => CurveFormula.Compile($"{name}(1,2)"));
    }
}
