using System.Reflection;
using System.Text;
using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class ScpiBlockBoundaryTests
{
    private static bool Complete(string text, bool end)
    {
        try
        {
            return (bool)typeof(ScpiSession).GetMethod("Complete", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [Encoding.ASCII.GetBytes(text).ToList(), end])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InstrumentException error) { throw error; }
    }

    [Theory]
    [InlineData("", false, false)]
    [InlineData("", true, true)]
    [InlineData("text", false, false)]
    [InlineData("text\n", false, true)]
    [InlineData("text", true, true)]
    [InlineData("#", false, false)]
    [InlineData("#0abc", false, false)]
    [InlineData("#0abc\n", false, true)]
    [InlineData("#0abc", true, true)]
    [InlineData("#2", false, false)]
    [InlineData("#203ab", false, false)]
    [InlineData("#203abc", false, false)]
    [InlineData("#203abc", true, true)]
    [InlineData("#203abc\n", false, true)]
    [InlineData("#203abcX", false, false)]
    [InlineData("#100", false, false)]
    public void Completion_distinguishes_payload_length_line_ending_and_end_marker(string text, bool end, bool expected)
        => Assert.Equal(expected, Complete(text, end));

    [Theory]
    [InlineData("#", true)]
    [InlineData("#2", true)]
    [InlineData("#203ab", true)]
    [InlineData("#/", false)]
    [InlineData("#:", false)]
    [InlineData("#2xx", false)]
    [InlineData("#2-1", false)]
    public void Invalid_or_ended_truncated_blocks_are_diagnosed(string text, bool end)
        => Assert.Throws<InstrumentException>(() => Complete(text, end));

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("text")]
    [InlineData("#/")]
    [InlineData("#:")]
    public void Non_block_replies_have_no_block_bounds(string text)
        => Assert.Null(ScpiSession.BlockBounds(Encoding.ASCII.GetBytes(text)));

    [Theory]
    [InlineData("#0", 2, 0)]
    [InlineData("#0abc", 2, 3)]
    [InlineData("#0abc\n", 2, 3)]
    [InlineData("#203abc", 4, 3)]
    [InlineData("#203abc\n", 4, 3)]
    public void Bounds_extract_only_payload(string text, int offset, int length)
        => Assert.Equal((offset, length), ScpiSession.BlockBounds(Encoding.ASCII.GetBytes(text)));

    [Theory]
    [InlineData("#2")]
    [InlineData("#203ab")]
    [InlineData("#2xx")]
    [InlineData("#2-1")]
    public void Truncated_or_invalid_bounds_are_diagnosed(string text)
        => Assert.Throws<InstrumentException>(() => ScpiSession.BlockBounds(Encoding.ASCII.GetBytes(text)));
}
