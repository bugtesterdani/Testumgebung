using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Curves;
using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class QualityBoundaryTests
{
    public static IEnumerable<object[]> ModbusAliases()
    {
        var groups = new (ModbusDataType Type, string[] Names)[]
        {
            (ModbusDataType.UInt16, ["", "uint16", "u16", "word"]),
            (ModbusDataType.Int16, ["int16", "i16", "s16"]),
            (ModbusDataType.UInt32, ["uint32", "u32", "dword"]),
            (ModbusDataType.Int32, ["int32", "i32", "s32"]),
            (ModbusDataType.Float32, ["float32", "f32", "float", "real"]),
            (ModbusDataType.Float64, ["float64", "f64", "double"]),
            (ModbusDataType.Bool, ["bool", "bit"]),
            (ModbusDataType.Text, ["text", "string", "ascii"]),
        };
        foreach (var (type, names) in groups)
            foreach (var name in names) yield return [name, type];
    }

    [Theory, MemberData(nameof(ModbusAliases))]
    public void Modbus_aliases_support_case_and_whitespace(string name, ModbusDataType expected)
    {
        Assert.Equal(expected, ModbusCodec.ParseType(name));
        Assert.Equal(expected, ModbusCodec.ParseType(" " + name.ToUpperInvariant() + " "));
    }

    [Fact]
    public void Modbus_invalid_type_is_diagnostic_and_null_has_default()
    {
        Assert.Equal(ModbusDataType.UInt16, ModbusCodec.ParseType(null));
        Assert.Contains("unknown", Assert.Throws<FormatException>(() => ModbusCodec.ParseType("unknown")).Message);
    }

    [Theory]
    [InlineData(ModbusDataType.UInt16, 0d)]
    [InlineData(ModbusDataType.UInt16, 65535d)]
    [InlineData(ModbusDataType.Int16, -32768d)]
    [InlineData(ModbusDataType.Int16, 32767d)]
    [InlineData(ModbusDataType.UInt32, 4294967295d)]
    [InlineData(ModbusDataType.Int32, -2147483648d)]
    [InlineData(ModbusDataType.Int32, 2147483647d)]
    [InlineData(ModbusDataType.Float32, -1.25d)]
    [InlineData(ModbusDataType.Float64, 1e100)]
    public void Modbus_numeric_limits_roundtrip_in_both_word_orders(ModbusDataType type, double value)
    {
        foreach (var lowFirst in new[] { false, true })
        {
            var encoded = ModbusCodec.Encode(value, type, lowFirst);
            Assert.Equal(ModbusCodec.Registers(type), encoded.Length);
            Assert.Equal(value, Convert.ToDouble(ModbusCodec.Decode(encoded, type, lowFirst), CultureInfo.InvariantCulture));
        }
    }

    private sealed class Resolver : IValueResolver
    {
        public object Resolve(string name) => name switch
        {
            "integer" => 4L, "number" => 4d, "text" => "abc", "flag" => true,
            "date" => new DateTime(2026, 10, 10), "later" => new DateTime(2026, 10, 11),
            _ => throw new InvalidOperationException(name),
        };
    }

    [Theory]
    [InlineData("integer == number", true)]
    [InlineData("integer != number", false)]
    [InlineData("text == \"abc\"", true)]
    [InlineData("text != \"abc\"", false)]
    [InlineData("flag == true", true)]
    [InlineData("date < later", true)]
    [InlineData("date <= later", true)]
    [InlineData("date > later", false)]
    [InlineData("date >= later", false)]
    [InlineData("\"abc\" < \"def\"", true)]
    [InlineData("\"abc\" >= \"abc\"", true)]
    [InlineData("false && missing", false)]
    [InlineData("true || missing", true)]
    [InlineData("true && flag", true)]
    [InlineData("false || flag", true)]
    public void Expressions_compare_types_and_short_circuit(string source, bool expected)
        => Assert.Equal(expected, Expression.Parse(source).EvaluateBool(new Resolver()));

    [Theory]
    [InlineData("integer + 2", 6d)]
    [InlineData("integer - 2", 2d)]
    [InlineData("integer * 2", 8d)]
    [InlineData("integer / 2", 2d)]
    [InlineData("integer % 3", 1d)]
    [InlineData("number + 2.5", 6.5d)]
    [InlineData("number - 2.5", 1.5d)]
    [InlineData("number * 2.5", 10d)]
    [InlineData("number / 2.5", 1.6d)]
    [InlineData("number % 2.5", 1.5d)]
    [InlineData("-integer", -4d)]
    [InlineData("-number", -4d)]
    [InlineData("1e2", 100d)]
    public void Expressions_preserve_integer_and_float_arithmetic(string source, double expected)
        => Assert.Equal(expected, Convert.ToDouble(Expression.Parse(source).Evaluate(new Resolver()), CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("integer == text")]
    [InlineData("integer != flag")]
    [InlineData("flag < text")]
    [InlineData("text - 1")]
    [InlineData("1 / 0")]
    [InlineData("1 % 0")]
    [InlineData("integer && true")]
    [InlineData("true && integer")]
    [InlineData("integer || false")]
    [InlineData("false || integer")]
    [InlineData("!integer")]
    [InlineData("-flag")]
    public void Expressions_reject_invalid_operand_types(string source)
        => Assert.Throws<ExpressionException>(() => Expression.Parse(source).Evaluate(new Resolver()));

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("../")]
    [InlineData("../123")]
    [InlineData("$")]
    [InlineData("1e")]
    [InlineData("999999999999999999999999999")]
    [InlineData("(true")]
    [InlineData("true false")]
    [InlineData("")]
    public void Expressions_reject_malformed_tokens(string source)
        => Assert.Throws<ExpressionException>(() => Expression.Parse(source));

    [Theory]
    [InlineData("1e-2", 0.01)]
    [InlineData("1e+2", 100)]
    [InlineData("(2 + 3)*4", 20)]
    [InlineData("sin(pi / 2)", 1)]
    [InlineData("max(1,2)", 2)]
    [InlineData("clamp(3,0,2)", 2)]
    public void Curve_formula_numeric_and_named_tokens(string source, double expected)
        => Assert.Equal(expected, CurveFormula.Compile(source)(0), 8);

    [Theory]
    [InlineData(".")]
    [InlineData("1e")]
    [InlineData("unknown")]
    [InlineData("unknown(1)")]
    [InlineData("sin()")]
    [InlineData("min(1)")]
    [InlineData("(1")]
    [InlineData("1 2")]
    [InlineData("")]
    public void Curve_formula_invalid_tokens_are_diagnostic(string source)
        => Assert.Throws<CurveFormulaException>(() => CurveFormula.Compile(source));

    [Fact]
    public void Scpi_command_aliases_change_the_same_output_and_errors_keep_last_valid_value()
    {
        var bench = new SimulatedBench();
        var supply = new SupplySimulator("E36313A", bench);
        foreach (var command in new[] { "VOLT", "VOLTAGE", "SOUR:VOLT" })
        {
            Assert.Null(supply.Execute(command + " 5,(@1)", null));
            Assert.Equal(5, bench.DcVolt[0]);
        }
        foreach (var command in new[] { "CURR", "CURRENT", "SOUR:CURR" })
        {
            supply.Execute(command + " 1,(@1:3)", null);
            Assert.All(bench.DcCurrentLimit, value => Assert.Equal(1, value));
        }
        supply.Execute("OUTPUT ON,(@1,2)", null);
        Assert.True(bench.DcOn[0]); Assert.True(bench.DcOn[1]); Assert.False(bench.DcOn[2]);
        supply.Execute("VOLT 1000,(@1)", null);
        Assert.Equal(5, bench.DcVolt[0]); Assert.StartsWith("-222", supply.NextError());
        supply.Execute("VOLT invalid", null); Assert.StartsWith("-104", supply.NextError());
        Assert.Equal("0\n", System.Text.Encoding.ASCII.GetString(supply.Execute("UNKNOWN?", null)!));
        Assert.StartsWith("-113", supply.NextError());
        supply.Execute("*RST", null); Assert.All(bench.DcOn, value => Assert.False(value));
    }
}
