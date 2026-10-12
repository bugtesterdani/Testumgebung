using System.Reflection;
using System.Text.Json.Nodes;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Tests;

public sealed class WorkerParserContractTests
{
    private static readonly Type Instrument = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "workers", "native", "Testumgebung.Workers.Native.dll"))
        .GetType("Testumgebung.Workers.Native.CppInstrument", true)!;

    [Theory]
    [InlineData("double value = 1;", "value")]
    [InlineData(" value = (1 + 2);", "value")]
    [InlineData("auto text = \"a=b;\\\"c\";", "text")]
    [InlineData("int value /* comment */ = 1;", "value")]
    [InlineData("value = 1 // no terminator", null)]
    [InlineData("value = 1", null)]
    [InlineData("value == 1;", null)]
    [InlineData("value != 1;", null)]
    [InlineData("value <= 1;", null)]
    [InlineData("value >= 1;", null)]
    [InlineData("value += 1;", null)]
    [InlineData("value -= 1;", null)]
    [InlineData("value *= 1;", null)]
    [InlineData("value /= 1;", null)]
    [InlineData("value %= 1;", null)]
    [InlineData("value &= 1;", null)]
    [InlineData("value |= 1;", null)]
    [InlineData("value ^= 1;", null)]
    [InlineData("obj.member = 1;", null)]
    [InlineData("ptr->member = 1;", null)]
    [InlineData("ns::member = 1;", null)]
    [InlineData("array[0] = 1;", null)]
    [InlineData("call(value = 1);", null)]
    [InlineData("// value = 1;", null)]
    [InlineData("/* value = 1; */", null)]
    [InlineData("; value = 1;", null)]
    [InlineData("= 1;", null)]
    [InlineData("", null)]
    public void Debugger_only_captures_simple_local_assignments(string source, string? variable)
    {
        var assigned = Instrument.GetMethod("AssignedName", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(variable, assigned.Invoke(null, new object[] { source, false }));
        Assert.Null(assigned.Invoke(null, new object[] { source, true }));
    }

    [Theory]
    [InlineData("// comment\nint value = 1;", 2)]
    [InlineData("/* comment\ncontinued */\nint value = 1;", 3)]
    [InlineData("#define X 1\nint value = X;", 2)]
    [InlineData("#define X \\\n 1\nint value = X;", 3)]
    [InlineData("auto text = \"\\\"/*not comment*/\";\nint value = 1;", 2)]
    [InlineData("char text = '\\'';\nint value = 1;", 2)]
    public void Instrumentation_keeps_comments_and_escapes_and_original_line_numbers(string source, int line)
    {
        var instrumented = (string)Instrument.GetMethod("Instrument")!.Invoke(null, new object[] { source })!;
        Assert.Contains($"__tu_dbg_line({line}); int value", instrumented);
        Assert.Contains("__tu_dbg_var(\"value\", value)", instrumented);
        Assert.Equal(source.Split('\n').Length, instrumented.Split('\n').Length);
    }

    [Theory]
    [InlineData("else")]
    [InlineData("catch")]
    [InlineData("case")]
    [InlineData("default")]
    [InlineData("public")]
    [InlineData("private")]
    [InlineData("protected")]
    [InlineData("while")]
    public void Continuation_keywords_are_not_confused_with_variable_name_prefixes(string keyword)
    {
        var continuation = Instrument.GetMethod("IsContinuation", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)continuation.Invoke(null, new object[] { keyword })!);
        Assert.True((bool)continuation.Invoke(null, new object[] { keyword + " (condition)" })!);
        Assert.False((bool)continuation.Invoke(null, new object[] { keyword + "Value = 1;" })!);
        Assert.False((bool)continuation.Invoke(null, new object[] { keyword + "_value = 1;" })!);
    }

    private static JsonObject Parse(string text)
    {
        var type = typeof(CodeRuntime).Assembly.GetType("Testumgebung.Workers.GdbInspector+MiReader", true)!;
        var reader = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { text }, null)!;
        return (JsonObject)type.GetMethod("Fields")!.Invoke(reader, new object[] { '\0' })!;
    }

    [Fact]
    public void Gdb_machine_interface_preserves_nested_fields_arrays_and_escaped_values()
    {
        var parsed = Parse("text=\"line\\n\\r\\t\\101\\7\\\\\\\"\",frames=[frame={level=\"0\",args=[\"a\",\"b\"]}],empty=[],tuple={}");
        Assert.Equal("line\n\r\tA\a\\\"", parsed["text"]!.GetValue<string>());
        Assert.Equal("0", parsed["frames"]![0]!["level"]!.GetValue<string>());
        Assert.Equal("b", parsed["frames"]![0]!["args"]![1]!.GetValue<string>());
        Assert.Empty(parsed["empty"]!.AsArray()); Assert.Empty(parsed["tuple"]!.AsObject());
        Assert.Empty(Parse(""));
        Assert.Equal("x", Parse("items=[\"x\"]")["items"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("field")]
    [InlineData("field=")]
    [InlineData("field=1")]
    [InlineData("field=\"unterminated")]
    [InlineData("field=\"escape\\")]
    [InlineData("field=[item]")]
    [InlineData("field=[\"x\"")]
    [InlineData("field={x=\"value\"")]
    public void Invalid_machine_interface_records_report_format_error(string text)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => Parse(text));
        Assert.IsType<FormatException>(exception.InnerException);
    }
}
