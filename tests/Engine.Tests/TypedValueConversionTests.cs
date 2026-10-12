using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class TypedValueConversionTests
{
    public static IEnumerable<object?[]> Values()
    {
        foreach (var value in new object?[] { null, false, true, 12, 12L, 12f, 12d, "12", new DateTime(2026, 10, 11), 12m, new object() })
            foreach (var target in Enum.GetValues<VarType>().Append((VarType)99)) yield return [value, target];
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Coercion_accepts_only_matching_types_and_numeric_widening(object? value, VarType target)
    {
        var source = TypedValue.TypeOf(value);
        var accepted = source == target || target == VarType.Double && source == VarType.Int;
        if (accepted)
        {
            var result = TypedValue.Coerce(value, target);
            Assert.Equal(target, TypedValue.TypeOf(result));
            if (target == VarType.Double) Assert.Equal(12d, result);
            else if (target == VarType.Int) Assert.Equal(12L, result);
            else Assert.Equal(value, result);
        }
        else Assert.Contains("passt nicht zu Typ", Assert.Throws<TypeMismatchException>(() => TypedValue.Coerce(value, target)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Can_signals_validate_length_and_data_boundaries_for_both_byte_orders(bool motorola)
    {
        foreach (var length in new[] { 0, 65 }) Assert.Throws<ArgumentException>(() => new CanSignal(0, length, motorola).Raw(new byte[8]));
        foreach (var length in new[] { 1, 31, 33, 63 }) Assert.Throws<ArgumentException>(() => new CanSignal(0, length, motorola, IsFloat: true).Value(new byte[8]));
        Assert.Throws<ArgumentException>(() => new CanSignal(motorola ? 7 : 0, 9, motorola).Raw([255]));
        Assert.Throws<ArgumentException>(() => new CanSignal(motorola ? 7 : 0, 9, motorola).WriteRaw(new byte[1], 1));
        var signed = new CanSignal(motorola ? 7 : 0, 8, motorola, Signed: true);
        Assert.Equal(-128, signed.Raw([128])); Assert.Equal(127, signed.Raw([127])); Assert.Equal((-128d, 127d), signed.RawRange);
        var unsigned = signed with { Signed = false }; Assert.Equal(128, unsigned.Raw([128])); Assert.Equal((0d, 255d), unsigned.RawRange);
        Assert.Equal(-1, new CanSignal(motorola ? 7 : 0, 64, motorola, true).Raw(Enumerable.Repeat((byte)255, 8).ToArray()));
        foreach (var length in new[] { 32, 64 })
        {
            var floating = new CanSignal(motorola ? 7 : 0, length, motorola, IsFloat: true, Factor: 2, Offset: 1);
            var data = new byte[8]; floating.Write(data, 6); Assert.Equal(6, floating.Value(data));
            Assert.Equal((double.MinValue, double.MaxValue), floating.RawRange);
        }
    }
}
