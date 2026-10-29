using System.Text.Json;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.ViewModels;
namespace Testumgebung.Engine.Tests;

public sealed class LiveProgressTests
{
    [Fact]
    public void Rules_ratio_partial_lines_and_carriage_returns_keep_progress_consistent()
    {
        var parser = new LiveProgressParser("""[{"text":"START","percent":5,"status":"Vorbereitung"},{"regex":"PHASE (.+)","percent":80,"status":"Jetzt $1"}]""", @"(?<current>\d+[,.]?\d*) von (?<total>\d+)", "{current} von {total}", "^FERTIG$");
        parser.Push("START\r\n"); Assert.Equal(5, parser.Percent); Assert.Equal("Vorbereitung", parser.Status);
        parser.Push("2,5 von 1"); Assert.Equal(5, parser.Percent);
        parser.Push("0\r"); Assert.Equal(25, parser.Percent); Assert.Equal("2,5 von 10", parser.Status);
        parser.Push("PHASE Schreiben\n"); Assert.Equal(80, parser.Percent); Assert.Equal("Jetzt Schreiben", parser.Status);
        parser.Push("kein Treffer\n"); Assert.Equal(80, parser.Percent); Assert.Equal("kein Treffer", parser.Status);
        parser.Push("FERTIG"); Assert.True(parser.Finished);
    }
    [Theory]
    [InlineData("0", "0", null)] [InlineData("-1", "10", null)] [InlineData("1", "-1", null)]
    [InlineData("NaN", "10", null)] [InlineData("Infinity", "10", null)]
    [InlineData("12", "10", 100.0)] [InlineData("1", "4", 25.0)] [InlineData("0", "4", 0.0)]
    public void Invalid_counts_do_not_divide_by_zero_or_publish_nonfinite_progress(string current, string total, double? expected)
    {
        var parser = new LiveProgressParser("[]", @"(?<current>\S+) / (?<total>\S+)");
        parser.Push(current + " / " + total, final: true); Assert.Equal(expected, parser.Percent);
    }
    [Theory]
    [InlineData("[null]", "")] [InlineData("null", "")] [InlineData("[{}]", "")] [InlineData("[{\"text\":\"x\",\"regex\":\"x\"}]", "")]
    [InlineData("[{\"text\":\"x\",\"percent\":-1}]", "")] [InlineData("[{\"text\":\"x\",\"percent\":101}]", "")]
    [InlineData("[]", "(x)")] [InlineData("[]", "[")]
    public void Invalid_configuration_is_rejected(string rules, string numbers)
        => Assert.ThrowsAny<ArgumentException>(() => new LiveProgressParser(rules, numbers));
    [Fact]
    public void Rule_count_and_pending_line_are_bounded()
    {
        Assert.Throws<ArgumentException>(() => new LiveProgressParser(JsonSerializer.Serialize(Enumerable.Repeat(new LiveProgressParser.Rule(Text:"x"),101))));
        Assert.Throws<ArgumentException>(() => new LiveProgressParser(JsonSerializer.Serialize(new[]{new LiveProgressParser.Rule(Text:"x",Status:new string('x',2049))})));
        var parser = new LiveProgressParser("[]");
        parser.Push(new string('x',65536));
        Assert.Throws<InvalidOperationException>(() => parser.Push("x"));
        parser.Push("",true); Assert.Equal(2048, parser.Status!.Length);
    }
    [Fact]
    public void Cards_are_separate_bounded_resettable_and_do_not_invent_completion()
    {
        var cards = new LiveOutputCollection(); Assert.False(cards.HasContent);
        cards.Apply("a","Flash",new("one\n",25,"Schreiben"));
        cards.Apply("b","Prüfen",new("other"));
        cards.Apply("a","Flash",new(new string('x',25000)));
        var a=cards.Items[0]; Assert.Equal(20000,a.Text.Length); Assert.Equal(25,a.Value); Assert.False(a.Indeterminate);
        Assert.Contains("25",a.PercentText); Assert.Equal("Schreiben",a.Status); Assert.True(cards.Items[1].Indeterminate);
        cards.Apply("a","Flash",new("new",Status:"Neu",Reset:true)); Assert.Null(a.Percent); Assert.Equal("new",a.Text);
        cards.Complete("b");Assert.False(cards.Items[1].Indeterminate);
        for(var i=0;i<10;i++) cards.Apply(i.ToString(),"test",new("data"));
        Assert.Equal(8,cards.Items.Count); cards.Clear(); Assert.False(cards.HasContent);
    }
    [Theory]
    [InlineData(-1)] [InlineData(101)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void Direct_updates_reject_invalid_percent(double value) => Assert.Throws<ArgumentOutOfRangeException>(()=>new LiveOutputUpdate("",value).Validate());
    [Fact]
    public void Direct_updates_validate_payload_bounds()
    {
        new LiveOutputUpdate("ok",0,"status").Validate(); new LiveOutputUpdate("",100).Validate();
        Assert.Throws<ArgumentException>(()=>new LiveOutputUpdate("",Channel:"").Validate());
        Assert.Throws<ArgumentException>(()=>new LiveOutputUpdate("",Channel:new string('x',129)).Validate());
        Assert.Throws<ArgumentException>(()=>new LiveOutputUpdate(new string('x',65537)).Validate());
        Assert.Throws<ArgumentException>(()=>new LiveOutputUpdate("",Status:new string('x',2049)).Validate());
    }
}
