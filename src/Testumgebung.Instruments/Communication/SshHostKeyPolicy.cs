/**
 * @file
 * @brief Prüft oder ausdrücklich akzeptiert SSH-Hostschlüssel und erfasst deren öffentliche Identität je Verbindung.
 */
using System.Security.Cryptography;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments.Communication;

/// <summary>Verbindungsbezogener Zustand: kein gemeinsamer Cache und kein Vertrauen auf Basis einer früheren IP.</summary>
internal sealed class SshHostKeyPolicy
{
    private readonly bool _accept;
    private readonly string? _expected;
    public string Fingerprint { get; private set; } = "";
    public string PublicKey { get; private set; } = "";

    public SshHostKeyPolicy(InstrumentOptions options)
    {
        _accept = (options.Text("HostKeyPruefung") ?? "Pruefen").ToUpperInvariant() switch
        {
            "AKZEPTIEREN" => true,
            "PRUEFEN" or "PRÜFEN" => false,
            _ => throw new CommunicationException("SSH: HostKeyPruefung muss Pruefen oder Akzeptieren sein."),
        };
        _expected = options.Text("HostKeySHA256")?.Replace("SHA256:", "", StringComparison.Ordinal).TrimEnd('=');
        if (!_accept && string.IsNullOrWhiteSpace(_expected))
            throw new CommunicationException("SSH: HostKeySHA256 fehlt. Fingerabdruck hinterlegen oder HostKeyPruefung=Akzeptieren ausdrücklich wählen.");
    }

    /// <summary>Erfasst den präsentierten Schlüssel auch bei abgelehntem Vergleich; nur Akzeptieren überspringt den Abgleich.</summary>
    public bool Check(byte[] key)
    {
        var digest = Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
        Fingerprint = "SHA256:" + digest;
        PublicKey = Convert.ToBase64String(key);
        return _accept || string.Equals(digest, _expected, StringComparison.Ordinal);
    }

    public RemoteResponse Attach(RemoteResponse response)
        => response with { HostKeySHA256 = Fingerprint, HostKeyBase64 = PublicKey };
}
