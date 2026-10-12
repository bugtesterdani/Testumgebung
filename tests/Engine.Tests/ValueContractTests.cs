using System.Reflection;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

/// <summary>Public value DTOs are used in dictionaries, diffs and result transport.
/// Verify equality, hash consistency and independent changes to each init property.</summary>
public sealed class ValueContractTests
{
    private static readonly NullabilityInfoContext Nullability = new();
    public static IEnumerable<object[]> Records()
    {
        var assemblies = new[]
        {
            typeof(ResultRating).Assembly, typeof(ResultRun).Assembly,
            typeof(Testumgebung.Domain.Definitions.TestDefinition).Assembly,
            typeof(Testumgebung.Engine.TestRun).Assembly,
            typeof(Testumgebung.Instruments.InstrumentCatalog).Assembly,
            typeof(Testumgebung.Presentation.ViewModels.ShellViewModel).Assembly,
            typeof(Testumgebung.Identity.SessionService).Assembly,
            typeof(Testumgebung.Hardware.HardwareRegistry).Assembly,
            typeof(Testumgebung.Workers.CodeRuntime).Assembly,
            Assembly.Load("Testumgebung.DatabaseAdapters"),
            Assembly.Load("Testumgebung.Notifications"),
        };
        foreach (var type in assemblies.Distinct().SelectMany(a => a.GetTypes()).Where(t => t.IsPublic && !t.IsAbstract && !t.ContainsGenericParameters))
        {
            if (type.GetMethod("<Clone>$") is null) continue;
            foreach (var ctor in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
            {
                if (ctor.GetParameters().All(p => CanCreate(p.ParameterType)))
                {
                    yield return [type, ctor, false];
                    if (ctor.GetParameters().Any(p => Nullability.Create(p).ReadState == NullabilityState.Nullable))
                        yield return [type, ctor, true];
                    break;
                }
            }
        }
    }

    private static bool CanCreate(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return CanCreate(inner);
        return type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
            || type == typeof(Guid) || type == typeof(object) || type.IsArray
            // Empty collection fixtures do not require constructing their elements.
            || (type.IsGenericType
                && type.GetGenericTypeDefinition() is var generic
                && new[] { typeof(List<>), typeof(IReadOnlyList<>), typeof(IEnumerable<>), typeof(IReadOnlyDictionary<,>), typeof(Dictionary<,>) }.Contains(generic));
    }

    private static object? Create(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return Create(inner);
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        if (type == typeof(string)) return "Testwert";
        if (type == typeof(bool)) return false;
        if (type == typeof(char)) return 'A';
        if (type == typeof(Guid)) return Guid.Parse("11111111-1111-1111-1111-111111111111");
        if (type == typeof(DateTime)) return new DateTime(2026, 10, 10);
        if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        if (type == typeof(TimeSpan)) return TimeSpan.FromSeconds(1);
        if (type == typeof(object)) return "Testwert";
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            return Activator.CreateInstance((args.Length == 2 ? typeof(Dictionary<,>) : typeof(List<>)).MakeGenericType(args));
        }
        return Convert.ChangeType(1, type, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory, MemberData(nameof(Records))]
    public void Records_have_consistent_value_equality_and_hashes(Type type, ConstructorInfo ctor, bool nullableInputs)
    {
        var original = ctor.Invoke(ctor.GetParameters().Select(p => nullableInputs && Nullability.Create(p).ReadState == NullabilityState.Nullable ? null : Create(p.ParameterType)).ToArray());
        var cloneMethod = type.GetMethod("<Clone>$")!;
        var clone = cloneMethod.Invoke(original, null)!;
        Assert.NotSame(original, clone);
        Assert.True(original.Equals(original));
        Assert.True(original.Equals(clone));
        Assert.True(clone.Equals(original));
        Assert.Equal(original.GetHashCode(), clone.GetHashCode());
        Assert.False(original.Equals(null));
        Assert.False(original.Equals(new object()));
        Assert.Equal(original.ToString(), clone.ToString());
        var equal = type.GetMethod("op_Equality", BindingFlags.Static | BindingFlags.Public)!;
        var unequal = type.GetMethod("op_Inequality", BindingFlags.Static | BindingFlags.Public)!;
        Assert.True((bool)equal.Invoke(null, [original, clone])!);
        Assert.False((bool)unequal.Invoke(null, [original, clone])!);
        Assert.True((bool)equal.Invoke(null, [null, null])!);
        Assert.False((bool)equal.Invoke(null, [original, null])!);
        Assert.False((bool)equal.Invoke(null, [null, original])!);

        foreach (var property in type.GetProperties().Where(p => type.GetField("<" + p.Name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) is not null
            && p.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true))
        {
            var changed = cloneMethod.Invoke(original, null)!;
            var current = property.GetValue(original);
            object? next = null;
            if (current is not null && !property.PropertyType.IsValueType)
            {
                if (!CanCreate(property.PropertyType)) continue;
                next = property.PropertyType == typeof(string) ? "Anderer Wert" : Create(property.PropertyType);
            }
            if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
            {
                next = Activator.CreateInstance(property.PropertyType);
                if (Equals(next, current))
                {
                    if (property.PropertyType == typeof(bool)) next = true;
                    else if (property.PropertyType.IsEnum) next = Enum.GetValues(property.PropertyType).Cast<object>().FirstOrDefault(value => !Equals(value, current));
                    else if (CanCreate(property.PropertyType)) next = Create(property.PropertyType);
                }
            }
            else if (current is null && CanCreate(property.PropertyType)) next = Create(property.PropertyType);
            if (Equals(current, next)) continue;
            property.SetValue(changed, next);
            Assert.False(original.Equals(changed), type.Name + "." + property.Name);
            Assert.False(changed.Equals(original), type.Name + "." + property.Name);
            Assert.True((bool)unequal.Invoke(null, [original, changed])!);
            _ = changed.GetHashCode();
            _ = changed.ToString();
        }
    }
}
