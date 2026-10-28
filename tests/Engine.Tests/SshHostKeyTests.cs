using System.Security.Cryptography;
using Testumgebung.Contracts;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class SshHostKeyTests
{
    private static readonly byte[] First = [1, 2, 3];
    private static readonly byte[] Replacement = [4, 5, 6];
    private static string Fingerprint(byte[] key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');

    [Theory]
    [InlineData(null)]
    [InlineData("Pruefen")]
    [InlineData("Prüfen")]
    public void Standardmodus_vergleicht_auch_nach_einem_Geraetewechsel(string? mode)
    {
        var values = new Dictionary<string, string> { ["HostKeySHA256"] = Fingerprint(First) + "=" };
        if (mode is not null) values["HostKeyPruefung"] = mode;
        var policy = new SshHostKeyPolicy(new InstrumentOptions(values));
        Assert.True(policy.Check(First));
        Assert.False(policy.Check(Replacement));
        Assert.Equal(Fingerprint(Replacement), policy.Fingerprint);
        Assert.Equal(Convert.ToBase64String(Replacement), policy.PublicKey);
    }

    [Theory]
    [InlineData("Akzeptieren", false)]
    [InlineData("akzeptieren", true)]
    public void Explizites_Akzeptieren_erfasst_jeden_Schluessel_auch_mit_altem_Pin(string mode, bool oldPin)
    {
        var values = new Dictionary<string, string> { ["HostKeyPruefung"] = mode };
        if (oldPin) values["HostKeySHA256"] = "SHA256:old";
        var policy = new SshHostKeyPolicy(new InstrumentOptions(values));
        Assert.True(policy.Check(First));
        Assert.True(policy.Check(Replacement));
        var response = policy.Attach(new RemoteResponse("OK", 0));
        Assert.Equal(Fingerprint(Replacement), response.HostKeySHA256);
        Assert.Equal(Convert.ToBase64String(Replacement), response.HostKeyBase64);
        Assert.Equal("OK", response.Text);
        Assert.Equal(0, response.Code);
    }

    [Theory]
    [InlineData("Akzeptierne")]
    [InlineData("false")]
    [InlineData("Aus")]
    [InlineData("Pruefen")]
    [InlineData(null)]
    public void Fehlende_Pruefkonfiguration_und_Tippfehler_sind_keine_Akzeptanz(string? mode)
    {
        var values = new Dictionary<string, string>();
        if (mode is not null) values["HostKeyPruefung"] = mode;
        Assert.Throws<CommunicationException>(() => new SshHostKeyPolicy(new InstrumentOptions(values)));
    }
}
