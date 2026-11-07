using Testumgebung.Instruments;
using static Testumgebung.Engine.Tests.DaqIoTests;

namespace Testumgebung.Engine.Tests;

public sealed class UlDaqSessionTests
{
    [Fact]
    public void Ul_inventory_selects_exact_identifier_and_discovers_actual_channels_without_output_changes()
    {
        var api = new Ul();
        using var session = Session(api);
        Assert.Equal(7, session.Channels.Count); Assert.Empty(api.Writes);
        Assert.Equal(2, session.Channels.Count(channel => channel.Name.StartsWith("AI",StringComparison.Ordinal)));
        Assert.Equal(0, session.Channels.Single(channel=>channel.Name=="AO0").Min);
        Assert.Equal(10, session.Channels.Single(channel=>channel.Name=="AO0").Max);
        Assert.Equal(2.5, session.Read("AI0"));
        session.Write("AO0", 3); Assert.Contains("AO:0:100:3", api.Writes);
        session.Read("DIO0"); session.Write("DIO0", 1); session.Write("DIO1", 0);
        Assert.Equal(1, api.Bits & 1); Assert.Equal(0, api.Bits & 2); Assert.Equal(12, api.Bits & 12);
        Assert.Contains("BIT:1:0:True", api.Writes);
        session.Dispose(); session.Dispose(); Assert.Equal(1, api.Closed);
    }
    [Fact]
    public void Ul_supports_further_native_models_and_explicit_ethernet_addresses()
    {
        var api = new Ul { Devices=[] };
        using var network = new UlIoSession(DaqModels.Get(DaqModels.RedLab,DaqModels.AutoEthernet),"Ethernet","192.168.0.7",Options(),api);
        Assert.Equal(("192.168.0.7",54211,5000), api.Network);
        Assert.Contains("Ethernet",network.Identity);
        api.Devices=[new("USB-FUTURE-MODEL","ABC","USB")];
        using var any = Session(api); Assert.Contains("FUTURE",any.Identity);
        Assert.Throws<InstrumentException>(()=>new UlIoSession(DaqModels.Get(DaqModels.RedLab,"RedLab 3101"),"USB","ABC",Options(),api));
        api.Devices.Add(api.Devices[0]); Assert.Throws<InstrumentException>(()=>Session(api));
        api.Devices=[]; Assert.Throws<InstrumentException>(()=>Session(api));
    }
    [Fact]
    public void Ul_does_not_reconfigure_whole_ports_implicitly_and_uses_correct_8255_bit_offsets()
    {
        var api = new Ul { PortType=12, InputMask=0, OutputMask=0 };
        using var session = Session(api);
        Assert.Throws<InstrumentException>(()=>session.Write("DIO0",1)); Assert.Empty(api.Writes);
        using var configured = Session(api,Options(("Port-Richtungen","PORT12=Ausgang")));
        configured.Write("DIO0",1); configured.Write("DIO1",1); configured.Read("DIO0");
        Assert.Single(api.Writes,write=>write.StartsWith("PORT:",StringComparison.Ordinal));
        Assert.Contains("OUT:10:16:1",api.Writes); Assert.Contains("OUT:10:17:1",api.Writes);
        using var input = Session(api,Options(("Port-Richtungen","PORT12=Eingang")));
        Assert.All(input.Channels.Where(channel=>channel.Name.StartsWith("DIO",StringComparison.Ordinal)),channel=>Assert.Equal(ChannelAccess.Read,channel.Access));
    }
    [Fact]
    public void Ul_handles_temperature_models_and_closes_when_capabilities_are_invalid()
    {
        var api = new Ul { Devices=[new("USB-TEMP","ABC","USB")], AnalogOutputs=0 };
        using var temperature = new UlIoSession(DaqModels.Get(DaqModels.RedLab,"RedLab TEMP"),"USB","ABC",Options(),api);
        Assert.Equal("°C",temperature.Channels[0].Unit); Assert.Equal(22,temperature.Read("TI0"));
        api.AnalogInputs=5000; Assert.Throws<InstrumentException>(()=>Session(api)); Assert.Equal(1,api.Closed);
        api.AnalogInputs=0; api.PortBits=33; Assert.Throws<InstrumentException>(()=>Session(api));
        api.PortBits=4; Assert.Throws<InstrumentException>(()=>Session(api,Options(("Port-Richtungen","PORT1=invalid"))));
    }
    [Fact]
    public void Ul_requires_known_or_explicit_analog_ranges_instead_of_inventing_device_limits()
    {
        var api = new Ul { InputRange=-1 };
        Assert.Throws<InstrumentException>(()=>Session(api));
        using var selected = Session(api,Options(("AI-Bereich","21")));
        Assert.Equal(-15,selected.Channels[0].Min); Assert.Equal(15,selected.Channels[0].Max);
        Assert.Throws<InstrumentException>(()=>Session(api,Options(("AI-Bereich","invalid"))));
        Assert.Throws<InstrumentException>(()=>Session(api,Options(("AI-Bereich","9999"))));
    }
    [Theory]
    [InlineData("Port", "1.5")] [InlineData("Port", "abc")] [InlineData("Port", "65536")]
    [InlineData("Erkennungszeit [ms]", "0")] [InlineData("Erkennungszeit [ms]", "NaN")]
    public void Ul_rejects_invalid_network_options_before_open(string key, string value)
    {
        var api = new Ul { Devices = [] };
        Assert.Throws<InstrumentException>(() => new UlIoSession(DaqModels.Get(DaqModels.RedLab, DaqModels.AutoEthernet), "Ethernet", "host", Options((key,value)), api));
        Assert.Equal(default, api.Network);
    }
    [Fact]
    public void Ul_output_readback_does_not_switch_a_whole_port_to_input()
    {
        var api = new Ul { PortType = 12, InputMask = 0, OutputMask = 15 };
        using var session = Session(api);
        session.Read("DIO0");
        Assert.Empty(api.Writes);
    }
    [Fact]
    public void Ul_hybrid_and_configurable_channels_keep_voltage_and_temperature_separate()
    {
        var api = new Ul { Devices = [new("USB-TEMP-AI", "ABC", "USB")], AnalogInputs = 8 };
        using var mixed = Session(api);
        Assert.Equal(4, mixed.Channels.Count(channel => channel.Unit == "°C"));
        Assert.Equal("V", mixed.Channels.Single(channel => channel.Name == "AI4").Unit);
        using var selected = Session(api, Options(("Kanalarten", "AI4=Temperatur;AI0=Spannung")));
        Assert.Equal("°C", selected.Channels.Single(channel => channel.Name == "TI4").Unit);
        Assert.Equal("V", selected.Channels.Single(channel => channel.Name == "AI0").Unit);
        Assert.Throws<InstrumentException>(() => Session(api, Options(("Kanalarten", "AI0=Strom"))));
    }
    [Theory]
    [InlineData(0,5000)] [InlineData(65536,5000)] [InlineData(54211,0)] [InlineData(54211,120001)]
    public void Native_ul_rejects_invalid_network_arguments_without_loading_driver(int port,int timeout)
    {
        Assert.Throws<InstrumentException>(() => NativeUlApi.Instance.NetworkDevice("host",port,timeout));
    }
    private static UlIoSession Session(Ul api,InstrumentOptions? options=null)=>new(DaqModels.Get(DaqModels.RedLab,DaqModels.AutoUsb),"USB","ABC",options??Options(),api);
    private sealed class Ul : IUlApi
    {
        public List<UlDevice> Devices=[new("USB-1608G","ABC","USB")];
        public int AnalogInputs=2,AnalogOutputs=1,InputRange=1,PortType=1,PortBits=4,InputMask=15,OutputMask,Closed,Bits=15;
        public (string,int,int) Network; public List<string> Writes=[];
        public IReadOnlyList<UlDevice> Inventory(string connection)=>Devices;
        public UlDevice NetworkDevice(string host,int port,int timeoutMs) { Network=(host,port,timeoutMs);return new("E-1608",host,"Ethernet"); }
        public int Open(UlDevice device)=>3;
        public void Close(int board)=>Closed++;
        public int Config(int board,int type,int device,int item)=>type==2 ? item switch {7=>AnalogInputs,13=>AnalogOutputs,9=>1,6=>InputRange,114=>100,_=>0} : item switch {2=>PortType,6=>PortBits,8=>InputMask,9=>OutputMask,_=>0};
        public double AnalogIn(int board,int channel,int range)=>2.5;
        public double TemperatureIn(int board,int channel)=>22;
        public void AnalogOut(int board,int channel,int range,double value)=>Writes.Add($"AO:{channel}:{range}:{value}");
        public int DigitalIn(int board,int port,int bit)=>(Bits>>bit)&1;
        public void DigitalOut(int board,int port,int bit,int value) { Writes.Add($"OUT:{port}:{bit}:{value}");Bits=value==1 ? Bits|(1<<bit) : Bits&~(1<<bit); }
        public void ConfigureBit(int board,int port,int bit,bool output)=>Writes.Add($"BIT:{port}:{bit}:{output}");
        public void ConfigurePort(int board,int port,bool output)=>Writes.Add($"PORT:{port}:{output}");
    }
}
