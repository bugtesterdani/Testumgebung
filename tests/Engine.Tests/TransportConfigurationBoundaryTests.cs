using Testumgebung.Instruments;
using Testumgebung.Contracts;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class TransportConfigurationBoundaryTests
{
    [Theory]
    [InlineData("host", "host", 502)]
    [InlineData(" host:1234 ", "host", 1234)]
    [InlineData("[::1]:1234", "::1", 1234)]
    [InlineData("[::1]", "::1", 502)]
    [InlineData("host:not-a-number", "host:not-a-number", 502)]
    [InlineData("TCPIP::host::1234::SOCKET", "host", 1234)]
    [InlineData("tcpip::host::invalid::SOCKET", "host", 502)]
    [InlineData("TCPIP::host", "host", 502)]
    [InlineData("TCPIP", "TCPIP", 502)]
    public void Endpoint_notations_preserve_host_and_default_port(string text, string host, int port)
        => Assert.Equal((host, port), Endpoints.Parse(text, 502));

    [Fact]
    public void Blank_endpoint_has_actionable_error()
        => Assert.Contains("Adresse fehlt", Assert.Throws<CommunicationException>(() => Endpoints.Parse("  ", 502)).Message);

    [Theory]
    [InlineData("Parität", "K", 'N')]
    [InlineData("Paritaet", "G", 'E')]
    [InlineData("Parität", "U", 'O')]
    [InlineData("Parität", "m", 'M')]
    [InlineData("Parität", "s", 'S')]
    public void German_parity_names_and_aliases_are_normalized(string key, string value, char parity)
    {
        var settings = SerialSettings.From(" com9 ", new InstrumentOptions(new Dictionary<string, string> { [key] = value }));
        Assert.Equal("COM9", settings.Port); Assert.Equal(parity, settings.Parity);
        Assert.Equal(TimeSpan.FromSeconds((1 + 8 + (parity == 'N' ? 0 : 1) + 1) / 9600d), settings.CharTime);
    }

    [Theory]
    [InlineData("Parität", "X", "Parität")]
    [InlineData("Datenbits", "4", "Datenbits")]
    [InlineData("Datenbits", "9", "Datenbits")]
    [InlineData("Stoppbits", "0", "Stoppbits")]
    [InlineData("Stoppbits", "3", "Stoppbits")]
    public void Unsupported_serial_values_are_rejected(string key, string value, string message)
        => Assert.Contains(message, Assert.Throws<InstrumentException>(() => SerialSettings.From("COM9", new InstrumentOptions(new Dictionary<string, string> { [key] = value }))).Message);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tls_options_preserve_hostname_and_certificate_policy(bool disabledVerification)
    {
        var options = new InstrumentOptions(new Dictionary<string, string> { ["TLS"] = "ja", ["TLS-Prüfung"] = disabledVerification ? "AUS" : "ein", ["Firmen-CA"] = "ca.pem", ["TLS-Servername"] = "server" });
        var tls = TlsSettings.From("host", options);
        Assert.True(tls.Enabled); Assert.Equal("server", tls.ServerName); Assert.Equal("ca.pem", tls.CaCertificatePath); Assert.Equal(!disabledVerification, tls.VerifyCertificate);
        var defaults = TlsSettings.From("host", new InstrumentOptions());
        Assert.False(defaults.Enabled); Assert.True(defaults.VerifyCertificate); Assert.Equal("host", defaults.ServerName); Assert.Null(defaults.CaCertificatePath);
    }
}
