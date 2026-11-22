using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabOptionalParameterTests
{
    [Theory]
    [InlineData("T 0,0,0,5,3;data", "name", "Label")]
    [InlineData("T 0,0,0,5,3;data", "effects.fr", "1")]
    [InlineData("B 0,0,0,RMQR,.3;ABC", "EL", "M")]
    [InlineData("E RFID;T:Auto,R:1,C:UTF-8,P:0,E:10", "wpower", "0")]
    [InlineData("S e;0,0,40,40,60", "dx", "1")]
    [InlineData("C 1", "disp1", "1")]
    [InlineData("W 0,0,0,20,10,3,3;data", "line-spacing", "1")]
    [InlineData("I 0,0,0;logo", "mx", "2")]
    [InlineData("H 100,0", "P.x", "1")]
    [InlineData("A 1", "preview_index", "ALL")]
    public void Missing_optional_parameters_are_inserted_only_after_binding_proves_their_position(string line, string name, string value)
    {
        var editor = new CabSourceEditor(Encoding.UTF8.GetBytes("J\r" + line + "\rA 1\r"));
        var command = editor.Validation.Commands.First(c => c.Syntax.Command != "J");
        var property = Assert.Single(CabPropertyBindings.ForCommand(editor, command), p => p.Definition.Name == name);
        Assert.False(property.IsPresent);
        var next = CabOptionalParameter.Add(editor, command, property.Definition, value);
        var updated = next.Validation.Commands.First(c => c.Syntax.Range.Offset == command.Syntax.Range.Offset);
        Assert.Equal(value, updated.Parameter(name)!.Value);
        foreach (var old in command.Parameters.Where(p => p.IsPresent && p.Value is not null && p.Rule.ChoiceMode == CabChoiceMode.None && p.Rule.Parameter.Name is not ("param" or "ao") && !name.StartsWith(p.Rule.Parameter.Name + ".", StringComparison.Ordinal)))
            Assert.Equal(old.Value, updated.Parameter(old.Rule.Parameter.Name)?.Value);
        Assert.EndsWith("\rA 1\r", Encoding.UTF8.GetString(next.Document.Write()), StringComparison.Ordinal);
    }
}
