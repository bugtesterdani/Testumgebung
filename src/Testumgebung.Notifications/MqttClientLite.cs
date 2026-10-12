using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Testumgebung.Notifications;

public sealed record MqttOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 8883;
    public bool UseTls { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string ClientId { get; init; } = "testumgebung-" + Environment.MachineName.ToLowerInvariant();

    /// <summary>Platzhalter: {station}, {event}, {runId}, {dutId}.</summary>
    public string Topic { get; init; } = "testumgebung/{station}/runs";

    /// <summary>Prüfplatzrechner in Topic und Nachricht.</summary>
    public string Station { get; init; } = Environment.MachineName;

    /// <summary>Firmen-CA (PEM oder DER), wenn der Broker kein öffentlich vertrauenswürdiges Zertifikat hat.</summary>
    public string? CaCertificatePath { get; init; }

    /// <summary>Zeitlimit für Verbinden + Senden (begrenzter Sendeversuch).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

public sealed class MqttException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Schlanker MQTT-3.1.1-Client: CONNECT → CONNACK → PUBLISH (QoS 0, nicht retained) → DISCONNECT.
/// Je Meldung eine eigene kurze Verbindung – kein Reconnect-Puffer, keine Sitzungsdaten, keine Wiederholung.
/// </summary>
public static class MqttClientLite
{
    public static async Task PublishAsync(MqttOptions options, string topic, byte[] payload, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured) throw new MqttException("Kein MQTT-Broker konfiguriert.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.Timeout);
        var ct = cts.Token;
        using var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(options.Host, options.Port, ct).ConfigureAwait(false);
            Stream stream = tcp.GetStream();
            if (options.UseTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = options.Host,
                    RemoteCertificateValidationCallback = Validator(options.CaCertificatePath),
                }, ct).ConfigureAwait(false);
                stream = ssl;
            }
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(Connect(options), ct).ConfigureAwait(false);
                var ack = new byte[4];
                await stream.ReadExactlyAsync(ack, ct).ConfigureAwait(false);
                if (ack[0] != 0x20 || ack[1] != 0x02) throw new MqttException("Unerwartete Antwort des Brokers (kein CONNACK).");
                if (ack[3] != 0) throw new MqttException(ConnackText(ack[3]));
                await stream.WriteAsync(Publish(topic, payload), ct).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 0xE0, 0x00 }, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MqttException($"Zeitüberschreitung nach {options.Timeout.TotalSeconds:0.#} s ({options.Host}:{options.Port}).");
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or EndOfStreamException)
        {
            throw new MqttException($"{options.Host}:{options.Port} – {ex.Message}", ex);
        }
    }

    internal static byte[] Connect(MqttOptions o)
    {
        var body = new MemoryStream();
        WriteString(body, "MQTT");
        body.WriteByte(4); // Protokollstufe 3.1.1
        byte flags = 0x02; // Clean Session
        if (!string.IsNullOrEmpty(o.Username)) flags |= 0x80;
        if (!string.IsNullOrEmpty(o.Username) && o.Password is not null) flags |= 0x40;
        body.WriteByte(flags);
        body.WriteByte(0);
        body.WriteByte(30); // Keep-Alive 30 s (Verbindung lebt nur wenige Millisekunden)
        WriteString(body, o.ClientId);
        if (!string.IsNullOrEmpty(o.Username))
        {
            WriteString(body, o.Username);
            if (o.Password is not null) WriteString(body, o.Password);
        }
        return Packet(0x10, body.ToArray());
    }

    /// <summary>PUBLISH-Paket (QoS 0, nicht retained).</summary>
    public static byte[] Publish(string topic, byte[] payload)
    {
        if (topic.Length == 0 || topic.Contains('+') || topic.Contains('#')) throw new MqttException($"Ungültiges Topic '{topic}'.");
        var body = new MemoryStream();
        WriteString(body, topic);
        body.Write(payload);
        return Packet(0x30, body.ToArray()); // QoS 0, DUP 0, RETAIN 0
    }

    private static byte[] Packet(byte header, byte[] body)
    {
        var p = new MemoryStream();
        p.WriteByte(header);
        var len = body.Length;
        do
        {
            var b = (byte)(len % 128);
            len /= 128;
            if (len > 0) b |= 0x80;
            p.WriteByte(b);
        } while (len > 0);
        p.Write(body);
        return p.ToArray();
    }

    private static void WriteString(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > ushort.MaxValue) throw new MqttException("Text zu lang für MQTT.");
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)bytes.Length);
        s.Write(len);
        s.Write(bytes);
    }

    private static string ConnackText(byte rc) => rc switch
    {
        1 => "Broker lehnt Protokollversion 3.1.1 ab.",
        2 => "Broker lehnt Client-ID ab.",
        3 => "Broker nicht verfügbar.",
        4 => "Benutzername oder Passwort falsch.",
        5 => "Nicht berechtigt.",
        _ => $"Verbindung abgelehnt (Code {rc}).",
    };

    /// <summary>Standardprüfung; mit Firmen-CA zusätzlich Kettenprüfung gegen diese Wurzel (Hostname muss trotzdem passen).</summary>
    internal static RemoteCertificateValidationCallback Validator(string? caPath)
    {
        X509Certificate2? ca = null;
        if (!string.IsNullOrWhiteSpace(caPath))
        {
            var bytes = File.ReadAllBytes(caPath);
            var text = Encoding.ASCII.GetString(bytes);
            ca = text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(bytes);
        }
        return (_, certificate, chain, errors) =>
        {
            if (errors == SslPolicyErrors.None) return true;
            if (ca is null || certificate is null) return false;
            if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.Add(ca);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (chain is not null)
                foreach (var element in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            return custom.Build(certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
        };
    }
}
