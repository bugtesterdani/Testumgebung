using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class LdfSyntaxBoundaryTests
{
    [Theory]
    [InlineData("// trailing comment")]
    [InlineData("/* unfinished optional comment")]
    [InlineData("Vendor = {0,{1,2}};")]
    [InlineData("Vendor, {0,{1,2}};")]
    [InlineData("Vendor { Field: 1 }")]
    [InlineData("Vendor { ; }")]
    [InlineData("Vendor = value")]
    [InlineData("Vendor / token;")]
    public void Optional_vendor_syntax_does_not_change_known_lin_messages(string suffix)
    {
        var db = BusDatabase.Parse(BusTests.Ldf + suffix, BusDatabaseFormat.Ldf);
        Assert.Contains(db.Messages, m => m.Name == "FH_Status"); Assert.Contains(db.Messages, m => m.Name == "FH_Befehl");
    }
    [Theory]
    [InlineData("\"")]
    [InlineData("}")]
    [InlineData("Vendor {")]
    public void Malformed_quotes_and_block_delimiters_are_diagnosed(string text)
        => Assert.StartsWith("LDF:", Assert.Throws<BusDatabaseException>(() => BusDatabase.Parse(text, BusDatabaseFormat.Ldf)).Message);
}
