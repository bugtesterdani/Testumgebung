using System.Text;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class VisaDiscoveryTests
{
    private sealed class Peer(string mode) : IVisaDiscoveryApi
    {
        public List<uint> Closed = [];
        public string? Expression;
        private int _next;
        public bool IsAvailable => mode != "unavailable";
        public int OpenDefaultRM(out uint rm) { rm = 1; return mode == "open-error" ? -1 : 0; }
        public int FindResources(uint rm, string expression, out uint list, out uint count, byte[] description)
        {
            Assert.Equal(1u, rm); Expression = expression; list = 2; count = mode == "single" ? 1u : 3u;
            Encoding.ASCII.GetBytes("USB0::LONG_FIRST::INSTR").CopyTo(description, 0); return mode == "find-error" ? -1 : 0;
        }
        public int FindNext(uint list, byte[] description)
        {
            Assert.Equal(2u, list); Assert.All(description, b => Assert.Equal(0, b));
            if (mode == "next-throw") throw new IOException("fixture discovery failure");
            if (mode == "next-error") return -1;
            Encoding.ASCII.GetBytes(++_next == 1 ? "GPIB0::1::INSTR" : "TCPIP0::host::INSTR").CopyTo(description, 0); return 0;
        }
        public int Close(uint handle) { Closed.Add(handle); return 0; }
    }
    [Theory]
    [InlineData("unavailable")]
    [InlineData("open-error")]
    [InlineData("find-error")]
    [InlineData("next-error")]
    [InlineData("next-throw")]
    [InlineData("single")]
    [InlineData("multiple")]
    public void Visa_discovery_enumerates_resources_and_closes_owned_handles_even_on_exception(string mode)
    {
        var peer = new Peer(mode);
        if (mode == "next-throw") Assert.Contains("fixture", Assert.Throws<IOException>(() => Visa.Find("?*INSTR", peer)).Message);
        else
        {
            var found = Visa.Find("?*INSTR", peer);
            Assert.Equal(mode is "single" or "next-error" ? 1 : mode == "multiple" ? 3 : 0, found.Count);
            if (found.Count > 0) Assert.Equal("USB0::LONG_FIRST::INSTR", found[0]);
            if (found.Count == 3) Assert.Equal(new[] { "GPIB0::1::INSTR", "TCPIP0::host::INSTR" }, found.Skip(1));
        }
        Assert.Equal(mode is "unavailable" or "open-error" ? Array.Empty<uint>() : mode == "find-error" ? new uint[] { 1 } : new uint[] { 2, 1 }, peer.Closed);
        if (mode is not ("unavailable" or "open-error")) Assert.Equal("?*INSTR", peer.Expression);
    }

    [Fact]
    public void Serial_port_inventory_contains_unique_nonempty_registry_values()
    {
        var ports = SerialTransport.Ports(); Assert.All(ports, port => Assert.False(string.IsNullOrWhiteSpace(port)));
        Assert.Equal(ports.Count, ports.Distinct().Count());
        Assert.Equal(ports.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal), ports);
    }
}
