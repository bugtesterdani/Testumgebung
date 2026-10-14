/**
 * @file
 * @brief Bündelt SSH-, SFTP-, HTTP- und weitere entfernte Operationen hinter einer Geräteverbindung.
 */

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments.Communication;

public sealed class RemoteDevice(string adapter, string resource, InstrumentOptions options)
    : CommDevice(adapter, adapter, resource, options), IRemoteConnection, IConfigurableConnection
{
    public const string SshAdapter = "SSH / SFTP";
    public const string HttpAdapter = "HTTP / HTTPS";
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public override IReadOnlyList<InstrumentChannel> Channels { get; } = [new("Daten", "", 0, 0, ChannelAccess.ReadWrite, "Netzwerkoperationen")];
    public Task ConfigureAsync(string endpoint, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        => LockedAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(endpoint)) throw new CommunicationException("Verbindungsadresse fehlt.");
            Resource = endpoint;
            if (values.Count > 0) Options = new InstrumentOptions(values);
            return Task.FromResult(0);
        }, cancellationToken);
    public override Task SafeStateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
        => Task.FromResult(new InstrumentProbe(false, "Verbindung mit einem HTTP-, SSH- oder SFTP-Prüfschritt testen.", [], Log));

    public Task<RemoteResponse> ExecuteAsync(RemoteRequest request, CancellationToken cancellationToken)
        => LockedAsync(async () =>
        {
            var timeout = request.Timeout ?? TimeSpan.FromSeconds(30);
            if (timeout <= TimeSpan.Zero) throw new CommunicationException("Zeitlimit muss positiv sein.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                if (request.Operation == "http" && Adapter == HttpAdapter) return await HttpAsync(request, deadline.Token).ConfigureAwait(false);
                if (Adapter != SshAdapter) throw new CommunicationException("Operation passt nicht zum Adapter.");
                var fingerprint = Options.Text("HostKeySHA256")?.Replace("SHA256:", "", StringComparison.Ordinal).TrimEnd('=')
                    ?? throw new CommunicationException("SSH: HostKeySHA256 fehlt. Fingerabdruck der Gegenstelle hinterlegen.");
                var (host, port) = Endpoints.Parse(Resource, 22);
                var user = Options.Text("Benutzer") ?? throw new CommunicationException("SSH: Benutzer fehlt.");
                using var key = Options.Text("Schlüsseldatei") is { } keyPath
                    ? new PrivateKeyFile(keyPath, Options.Text("Schlüsselpasswort") ?? "") : null;
                AuthenticationMethod auth = key is not null ? new PrivateKeyAuthenticationMethod(user, key)
                    : new PasswordAuthenticationMethod(user, Options.Text("Passwort") ?? "");
                var info = new ConnectionInfo(host, port, user, auth) { Timeout = timeout };
                void CheckKey(object? sender, Renci.SshNet.Common.HostKeyEventArgs e)
                    => e.CanTrust = string.Equals(Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('='), fingerprint, StringComparison.Ordinal);
                if (request.Operation == "ssh")
                {
                    using var client = new SshClient(info);
                    client.HostKeyReceived += CheckKey;
                    await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    using var command = client.CreateCommand(request.Argument);
                    command.CommandTimeout = timeout;
                    await command.ExecuteAsync(deadline.Token).ConfigureAwait(false);
                    return new RemoteResponse(command.Result, command.ExitStatus ?? -1, command.Error);
                }
                if (request.Operation is not ("sftp.upload" or "sftp.download")) throw new CommunicationException("Unbekannte SSH/SFTP-Operation.");
                using var sftp = new SftpClient(info) { OperationTimeout = timeout };
                sftp.HostKeyReceived += CheckKey;
                await sftp.ConnectAsync(deadline.Token).ConfigureAwait(false);
                if (request.Operation == "sftp.upload")
                {
                    await using var file = File.OpenRead(request.LocalPath);
                    await sftp.UploadFileAsync(file, request.Argument, request.Overwrite, cancellationToken: deadline.Token).ConfigureAwait(false);
                    return new RemoteResponse(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                }
                // Erst temporär laden: Abbruch oder Fehler lässt eine bestehende Zieldatei unverändert.
                var destination = Path.GetFullPath(request.LocalPath);
                if (!request.Overwrite && File.Exists(destination)) throw new IOException("SFTP-Zieldatei existiert bereits.");
                var temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    long length;
                    await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await sftp.DownloadFileAsync(request.Argument, file, deadline.Token).ConfigureAwait(false);
                        length = file.Length;
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    File.Move(temp, destination, request.Overwrite);
                    return new RemoteResponse(length.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new CommunicationTimeoutException("Netzwerkoperation: Zeitlimit überschritten.", []); }
        }, cancellationToken);

    private async Task<RemoteResponse> HttpAsync(RemoteRequest request, CancellationToken token)
    {
        var uri = new Uri(new Uri(Resource.TrimEnd('/') + "/"), request.Argument);
        if (uri.Scheme is not ("http" or "https")) throw new CommunicationException("HTTP-Adresse muss http:// oder https:// verwenden.");
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        if (request.Body.Length > 0) message.Content = new StringContent(request.Body, Encoding.UTF8);
        foreach (var line in request.Headers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = line.IndexOf(':');
            if (split <= 0) throw new CommunicationException("HTTP-Header braucht Name: Wert.");
            var name = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content ??= new StringContent("");
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
            }
            else message.Headers.Add(name, value);
        }
        using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(1024 * 1024, token).ConfigureAwait(false);
        return new RemoteResponse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false), (int)response.StatusCode);
    }
}
