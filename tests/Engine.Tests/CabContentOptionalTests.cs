using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabContentOptionalTests
{
    [Theory]
    [InlineData("[SER:1]", "inc", "2", "[SER:1,2]")]
    [InlineData("[SER:1]", "freq", "2", "[SER:1,,2]")]
    [InlineData("[SER:1,2]", "freq", "3", "[SER:1,2,3]")]
    public void Missing_content_arguments_are_inserted_without_rewriting_other_fields(string field, string parameter, string value, string expected)
    {
        var text = "J\rT 0,0,0,5,3;prefix" + field + "[DAY]suffix\rA 1\r";
        var editor = new CabSourceEditor(Encoding.UTF8.GetBytes(text));
        var content = Assert.Single(CabContentValidation.Bind(editor.Document, editor.Validation.Commands));
        var syntax = content.Syntax.Fields[0];
        var property = Assert.Single(CabPropertyBindings.ForContent(editor, content, syntax), p => p.Definition.Name == parameter);
        Assert.False(property.IsPresent);
        Assert.Throws<ArgumentException>(() => CabContentOptional.Add(editor, content, syntax, property.Definition, "not-a-number"));
        var next = CabContentOptional.Add(editor, content, syntax, property.Definition, value);
        Assert.Equal(Encoding.UTF8.GetBytes(text.Replace(field, expected, StringComparison.Ordinal)), next.Document.Write());
    }
}
