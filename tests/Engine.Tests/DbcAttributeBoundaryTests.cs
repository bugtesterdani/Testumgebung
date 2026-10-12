using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class DbcAttributeBoundaryTests
{
    private const string Header = "BO_ 100 Status: 8 ECU\n SG_ Value : 0|8@1+ (1,0) [0|255] \"V\" Receiver\n";
    public static IEnumerable<object[]> IgnoredAttributes()
    {
        foreach (var name in new[] { "ProtocolType", "Baudrate", "BusSpeed", "GenMsgCycleTime", "VFrameFormat", "GenSigStartValue", "VendorSpecific" })
            foreach (var value in new[] { "", "BO_", "SG_", "BO_ 100", "SG_ 100 Value", "XX_ 100 1" })
                yield return [name, value];
    }

    [Theory, MemberData(nameof(IgnoredAttributes))]
    public void Inapplicable_optional_attributes_do_not_destroy_messages(string attribute, string value)
    {
        var db = BusDatabase.Parse(Header + $"BA_ \"{attribute}\" {value};", BusDatabaseFormat.Dbc);
        var message = Assert.Single(db.Messages);
        Assert.Equal("Status", message.Name);
        Assert.Equal("Value", Assert.Single(message.Signals).Name);
    }

    [Theory]
    [InlineData("Baudrate", "500", 500000)]
    [InlineData("BusSpeed", "1000000", 1000000)]
    [InlineData("Baudrate", "invalid", null)]
    public void Bitrate_attributes_accept_both_units_and_ignore_non_numeric_values(string attribute, string value, int? expected)
    {
        var db = BusDatabase.Parse(Header + $"BA_ \"{attribute}\" {value};", BusDatabaseFormat.Dbc);
        Assert.Equal(expected, db.Bitrate);
    }

    [Theory]
    [InlineData(100, "Value", true)]
    [InlineData(999, "Value", false)]
    [InlineData(100, "Unknown", false)]
    public void Attributes_only_apply_to_existing_messages_and_signals(int id, string signal, bool matches)
    {
        var db = BusDatabase.Parse(Header + $"BA_ \"GenMsgCycleTime\" BO_ {id} 20;\nBA_ \"VFrameFormat\" BO_ {id} 14;\nBA_ \"GenSigStartValue\" SG_ {id} {signal} 42;", BusDatabaseFormat.Dbc);
        var message = Assert.Single(db.Messages);
        Assert.Equal(id == 100, message.Fd);
        Assert.Equal(matches ? 42ul : 0ul, Assert.Single(message.Signals).InitialRaw);
    }
}
