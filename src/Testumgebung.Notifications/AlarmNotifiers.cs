using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Notifications;

public sealed record SmtpAlarmOptions(string Host, int Port, bool StartTls, string? Username, string? Password,
    string Sender, string Recipient)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Host) && Port is >= 1 and <= 65535
        && MailAddress.TryCreate(Sender, out _) && MailAddress.TryCreate(Recipient, out _);
}

public sealed class SmtpAlarmNotifier(SmtpAlarmOptions options) : QueuedNotifier(TimeSpan.FromSeconds(15))
{
    public override string Channel => "SMTP";
    public override string TargetFor(RunNotification n) => options.Recipient;

    protected override async Task SendAsync(RunNotification n, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.StartTls };
        if (!string.IsNullOrWhiteSpace(options.Username))
            client.Credentials = new NetworkCredential(options.Username, options.Password);
        using var message = new MailMessage(options.Sender, options.Recipient, "Testumgebung – Alarm", n.Message);
        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TeamsAlarmNotifier(Uri endpoint) : QueuedNotifier(TimeSpan.FromSeconds(15))
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    public override string Channel => "Teams";
    public override string TargetFor(RunNotification n) => "Teams-Workflow";

    protected override async Task SendAsync(RunNotification n, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.PostAsJsonAsync(endpoint, Card(n.Message), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new IOException($"Teams-Workflow antwortet mit HTTP {(int)response.StatusCode}.");
        }
        catch (HttpRequestException)
        {
            // Die aufrufbare URL enthält den Zugriffsschlüssel des Workflows.
            throw new IOException("Teams-Workflow konnte nicht erreicht werden.");
        }
    }

    private static object Card(string message) => new
    {
        type = "message",
        attachments = new[]
        {
            new
            {
                contentType = "application/vnd.microsoft.card.adaptive",
                content = new
                {
                    type = "AdaptiveCard",
                    version = "1.2",
                    body = new[] { new { type = "TextBlock", text = message, wrap = true } },
                },
            },
        },
    };
}
