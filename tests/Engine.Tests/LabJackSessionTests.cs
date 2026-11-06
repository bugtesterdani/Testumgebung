using Testumgebung.Instruments;
using static Testumgebung.Engine.Tests.DaqIoTests;

namespace Testumgebung.Engine.Tests;

public sealed class LabJackSessionTests
{
    [Theory]
    [InlineData("T4", "USB", 4, "USB")]
    [InlineData("T7-Pro", "WLAN", 7, "WIFI")]
    [InlineData("T8", "Ethernet", 8, "ETHERNET")]
    public void Ljm_identifies_model_without_setting_outputs_and_reads_writes_requested_channel(string model, string connection, int type, string nativeConnection)
    {
        var api = new Ljm { Type = type };
        using var session = new LjmIoSession(DaqModels.Get(DaqModels.LabJack, model), connection, "470010001", Options(), api);
        Assert.Equal(nativeConnection, api.Connection); Assert.Empty(api.Writes);
        Assert.Equal(3.2, session.Read("AIN0"));
        session.Write("DAC1", 2); Assert.Equal(("DAC1", 2d), Assert.Single(api.Writes));
        Assert.Contains("470010001", session.Identity);
        session.Dispose(); session.Dispose(); Assert.Equal(1, api.Closed);
    }
    [Fact]
    public void Ljm_rejects_wrong_device_and_invalid_range_and_closes_failed_open()
    {
        var api = new Ljm { Type = 4 };
        Assert.Throws<InstrumentException>(() => new LjmIoSession(DaqModels.Get(DaqModels.LabJack, "T7"), "USB", "1", Options(), api));
        Assert.Equal(1, api.Closed);
        api.Type = 7;
        using var session = new LjmIoSession(DaqModels.Get(DaqModels.LabJack, "T7"), "USB", "1", Options(("Bereich", "1")), api);
        session.Read("AIN1"); Assert.Equal(("AIN1_RANGE", 1d), api.Writes.Last());
        using var invalid = new LjmIoSession(DaqModels.Get(DaqModels.LabJack, "T7"), "USB", "1", Options(("Bereich", "-1")), api);
        Assert.Throws<InstrumentException>(() => invalid.Read("AIN0"));
    }
    [Theory]
    [InlineData("T4", "1")] [InlineData("T8", "12")] [InlineData("T7", "abc")]
    public void Ljm_does_not_write_unsupported_or_malformed_ranges(string model, string range)
    {
        var api = new Ljm { Type = int.Parse(model[1..]) };
        using var session = new LjmIoSession(DaqModels.Get(DaqModels.LabJack, model), "USB", "1", Options(("Bereich", range)), api);
        Assert.Throws<InstrumentException>(() => session.Read("AIN0"));
        Assert.Empty(api.Writes);
    }
    [Theory]
    [InlineData("U3-LV", false, 3, 1)] [InlineData("U3-HV", true, 3, 1)]
    [InlineData("U6-Pro", false, 6, 1)] [InlineData("UE9", false, 9, 2)]
    public void Ud_uses_correct_family_and_configures_only_accessed_io(string model, bool hv, int type, int connection)
    {
        var api = new Ud { Hv = hv };
        using var session = new UdIoSession(DaqModels.Get(DaqModels.LabJack, model), connection == 1 ? "USB" : "Ethernet", "123456", Options(), api);
        Assert.Equal(type, api.Type); Assert.Equal(connection, api.Connection); Assert.Empty(api.Puts);
        session.Read("AIN4"); Assert.Contains(api.Gets, call => call == (10,4));
        session.Write("DAC1", 3); Assert.Contains((20,1,3d), api.Puts);
        session.Read("DIO4"); session.Write("DIO4", 1);
        Assert.Contains((40,4,1d), api.Puts);
        if (type == 3) Assert.Contains((2013,4,0d), api.Puts);
        session.Dispose(); session.Dispose(); Assert.Equal(1, api.Released);
    }
    [Fact]
    public void Ud_validates_voltage_variant_and_model_specific_ranges()
    {
        var api = new Ud { Hv = true };
        Assert.Throws<InstrumentException>(() => new UdIoSession(DaqModels.Get(DaqModels.LabJack, "U3-LV"), "USB", "1", Options(), api));
        Assert.Equal(1, api.Released);
        foreach (var range in new[] { "10", "1", "0.1", "0.01" })
        {
            using var session = new UdIoSession(DaqModels.Get(DaqModels.LabJack, "U6"), "USB", "1", Options(("Bereich",range)), api);
            session.Read("AIN0");
        }
        Assert.Throws<InstrumentException>(() => new UdIoSession(DaqModels.Get(DaqModels.LabJack, "U6"), "USB", "1", Options(("Bereich","2")), api));
        Assert.Throws<InstrumentException>(() => new UdIoSession(DaqModels.Get(DaqModels.LabJack, "U3-HV"), "USB", "1", Options(("Bereich","10")), api));
        Assert.Throws<InstrumentException>(() => new UdIoSession(DaqModels.Get(DaqModels.LabJack, "UE9"), "USB", "1", Options(("Bereich","10")), api));
        using var hv = new UdIoSession(DaqModels.Get(DaqModels.LabJack, "U3-HV"), "USB", "1", Options(), api);
        var before = api.Puts.Count; hv.Read("AIN0"); Assert.Equal(before, api.Puts.Count);
    }
    [Fact]
    public void U12_preserves_other_analog_output_only_with_explicit_known_start_and_maps_both_digital_banks()
    {
        var api = new U12();
        using var unknown = new U12IoSession("123456", Options(), api);
        Assert.Throws<InstrumentException>(() => unknown.Write("DAC0", 1)); Assert.Empty(api.Outputs);
        using var session = new U12IoSession("123456", Options(("AO-Paar-Startwerte", "DAC0=0;DAC1=2")), api);
        Assert.Empty(api.Outputs); Assert.Equal(1.2, session.Read("AIN3"));
        session.Write("DAC0", 1); session.Write("DAC1", 3);
        Assert.Equal(new[] { (1d,2d), (1d,3d) }, api.Outputs);
        session.Read("DIO0"); Assert.Equal((0,true), api.Digital);
        session.Read("DIO19"); Assert.Equal((3,false), api.Digital);
        session.Write("DIO15", 1); Assert.Equal((15,true), api.Digital);
        session.Write("DIO16", 0); Assert.Equal((0,false), api.Digital);
        Assert.Throws<InstrumentException>(() => new U12IoSession("not-a-serial", Options(), api));
        Assert.Throws<InstrumentException>(() => new U12IoSession("1", Options(("AO-Paar-Startwerte", "DAC2=0")), api));
        Assert.Throws<InstrumentException>(() => new U12IoSession("1", Options(("AO-Paar-Startwerte", "DAC0=NaN")), api));
        api.Found = false; Assert.Throws<InstrumentException>(() => new U12IoSession("1", Options(), api));
    }
    [Fact]
    public void T8_exposes_its_own_input_output_ranges_and_programmable_gain()
    {
        var api = new Ljm { Type = 8 };
        using var session = new LjmIoSession(DaqModels.Get(DaqModels.LabJack,"T8"),"USB","1",Options(("Bereich","9.6")),api);
        session.Read("AIN0"); Assert.Contains(("AIN0_RANGE",9.6),api.Writes);
        var input = session.Channels.Single(channel => channel.Name == "AIN0");
        Assert.Equal(-11,input.Min); Assert.Equal(11,input.Max);
        Assert.Equal(-0.1,session.Channels.Single(channel => channel.Name == "DAC0").Min);
    }
    private sealed class Ljm : ILjmApi
    {
        public int Type = 7; public string Connection = ""; public int Closed;
        public List<(string,double)> Writes = [];
        public int Open(string model,string connection,string resource) { Connection=connection; return 1; }
        public int DeviceType(int handle) => Type;
        public double Read(int handle,string channel) => 3.2;
        public void Write(int handle,string channel,double value) => Writes.Add((channel,value));
        public void Close(int handle) => Closed++;
    }
    private sealed class Ud : IUdApi
    {
        public bool Hv; public int Type,Connection,Released;
        public List<(int,int)> Gets=[]; public List<(int,int,double)> Puts=[];
        public int Open(int type,int connection,string resource) { Type=type;Connection=connection;return 1; }
        public double Get(int handle,int operation,int channel) { Gets.Add((operation,channel));return operation==1000 ? Hv ? 1 : 0 : 2; }
        public void Put(int handle,int operation,int channel,double value)=>Puts.Add((operation,channel,value));
        public void Release()=>Released++;
    }
    private sealed class U12 : IU12Api
    {
        public bool Found=true; public (int,bool) Digital;
        public List<(double,double)> Outputs=[];
        public float Firmware(ref int identifier)=>Found ? 1.22f : 0;
        public double AnalogIn(ref int identifier,int channel)=>1.2;
        public double DigitalIn(ref int identifier,int channel,bool dPort) { Digital=(channel,dPort);return 1; }
        public void AnalogOut(ref int identifier,double first,double second)=>Outputs.Add((first,second));
        public void DigitalOut(ref int identifier,int channel,bool dPort,bool state)=>Digital=(channel,dPort);
    }
}
