using System.Reflection;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class JournalValueBoundaryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData('b', "1")]
    [InlineData('b', "0")]
    [InlineData('l', "42")]
    [InlineData('d', "2.5")]
    [InlineData('t', "2026-10-11T12:00:00.0000000Z")]
    [InlineData('s', "text")]
    [InlineData('?', "unknown")]
    public void Journal_restores_typed_values_and_marks_unpersisted_waveforms(char? type, string? text)
    {
        var journalType = typeof(RunJournal).Assembly.GetType("Testumgebung.Persistence.Results.JournalEvent")!;
        foreach (var waves in new int?[] { null, 0, 2 }) foreach (var message in new string?[] { null, "details" })
        {
            var journal = Activator.CreateInstance(journalType)!;
            void Set(string name, object? value) => journalType.GetProperty(name)!.SetValue(journal, value);
            Set("Vt", type); Set("V", text); Set("W", waves); Set("M", message);
            var result = (ResultEvent)journalType.GetMethod("ToEvent")!.Invoke(journal, null)!;
            if (waves > 0) Assert.Equal((message is null ? "" : "details · ") + "2 Kurve(n) nicht zwischengespeichert", result.Message);
            else Assert.Equal(message, result.Message);
            switch (type)
            {
                case 'b': Assert.Equal(text == "1", result.Value); break;
                case 'l': Assert.Equal(42L, result.Value); break;
                case 'd': Assert.Equal(2.5, result.Value); break;
                case 't': Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(result.Value).Kind); break;
                case 's': Assert.Equal(text, result.Value); break;
                default: Assert.Null(result.Value); break;
            }
        }
    }
}
