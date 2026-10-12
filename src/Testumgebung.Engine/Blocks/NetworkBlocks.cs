using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

public static class NetworkBlocks
{
    public const string Configure = "komm.verbindung";
    public const string Http = "netz.http";
    public const string Ssh = "netz.ssh";
    public const string Upload = "netz.sftp.upload";
    public const string Download = "netz.sftp.download";

    public static IEnumerable<ParameterDescriptor> ConnectionParameters() =>
    [
        new("Adresse", "Adresse / COM-Port", ParameterKind.Text, Hint: "Optional: COM3 oder {ComPort}, {Host}:{Port}, https://{Host}. Zur Laufzeit ausgewertet; leer = aktuelle Geräteadresse."),
        new("Verbindungsoptionen", "Verbindungsoptionen", ParameterKind.Text, Hint: "Optional: eine Zeile Schlüssel=Wert, Platzhalter {Variable}; ersetzt die Optionen. Z.B. Baud=115200 oder Benutzer={Benutzer}. Geheimnisse nicht als feste Werte speichern."),
    ];
    private static IEnumerable<ParameterDescriptor> Common() =>
    [
        new("Ressource", "Ressource", ParameterKind.Resource, "Pruefling_Netzwerk", Required: true),
        .. ConnectionParameters(),
        new("Zeitlimit", "Zeitlimit [ms]", ParameterKind.Number, "30000", Required: true),
        new("Ziel", "Antwort / Byteanzahl → Variable", ParameterKind.Variable),
        new("StatusZiel", "Status / Exitcode → Variable", ParameterKind.Variable),
        new("FehlerZiel", "SSH-Fehlerausgabe → Variable", ParameterKind.Variable),
        new("ErwarteterStatus", "Erwarteter Status / Exitcode", ParameterKind.Text, Hint: "HTTP leer = 200–299, SSH/SFTP leer = 0; alternativ z.B. 201 oder 200-204."),
    ];
    public static void Register(BlockCatalog c)
    {
        c.Register(new(Configure, "Verbindung konfigurieren", CommunicationBlocks.Category, "arrow",
            "Ändert COM-Port oder Netzwerkadresse und Optionen aus aktuellen Variablen. Eine bestehende Streamverbindung wird geschlossen.",
            [new("Ressource", "Ressource", ParameterKind.Resource, "Pruefling_Seriell", Required: true), .. ConnectionParameters()],
            p => new ConfigureAction(p.Text("Ressource"), p.Optional("Adresse"), p.Optional("Verbindungsoptionen"))));
        c.Register(new(Http, "HTTP-Anfrage (GET / POST / …)", CommunicationBlocks.Category, "arrow",
            "HTTP/HTTPS direkt ohne curl-Installation. Antwort und Statuscode als Variablen; maximal 1 MiB Antwort.",
            [.. Common(), new("Methode", "Methode", ParameterKind.Choice, "GET", Choices: ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"]),
                new("Pfad", "Pfad / URL", ParameterKind.Text, "/", Required: true),
                new("Body", "Request-Body", ParameterKind.Text, Hint: "Platzhalter {Variable}; JSON-Klammern als {{ und }} schreiben."),
                new("Header", "Header", ParameterKind.Text, Hint: "Eine Zeile Name: Wert; z.B. Content-Type: application/json oder Authorization: Bearer {Token}. Nicht protokolliert.")],
            p => Create(p, "http", "Pfad")));
        c.Register(new(Ssh, "SSH-Befehl", CommunicationBlocks.Category, "arrow",
            "Führt einen Befehl über SSH aus. Passwort oder Schlüsseldatei und HostKeySHA256 in Verbindungsoptionen. Pro Schritt eigene SSH-Verbindung.",
            [.. Common(), new("Befehl", "Befehl", ParameterKind.Text, "uname -a", Required: true)], p => Create(p, "ssh", "Befehl")));
        foreach (var upload in new[] { true, false })
            c.Register(new(upload ? Upload : Download, upload ? "SFTP-Datei hochladen" : "SFTP-Datei herunterladen", CommunicationBlocks.Category, "arrow",
                "Überträgt eine Datei über SSH/SFTP. Pfade unterstützen Variablen. Überschreiben ist ausdrücklich wählbar.",
                [.. Common(), new("Lokal", "Lokale Datei", ParameterKind.Text, Required: true), new("Remote", "Datei auf Gegenstelle", ParameterKind.Text, Required: true),
                    new("Ueberschreiben", "Bestehende Datei überschreiben", ParameterKind.Bool, "false")],
                p => Create(p, upload ? "sftp.upload" : "sftp.download", "Remote")));
    }
    private static RemoteAction Create(BlockParameters p, string operation, string argument)
    {
        var timeout = p.Number("Zeitlimit") ?? 30000;
        if (!double.IsFinite(timeout) || timeout <= 0) throw new BlockConfigurationException("Zeitlimit muss positiv sein.");
        return new(p.Text("Ressource"), p.Optional("Adresse"), p.Optional("Verbindungsoptionen"),
            new RemoteRequest(operation, p.Text(argument), operation == "http" ? p.Choice("Methode") : "GET",
                operation == "http" ? p.Text("Body") : "", operation == "http" ? p.Text("Header") : "",
                operation.StartsWith("sftp", StringComparison.Ordinal) ? p.Text("Lokal") : "", TimeSpan.FromMilliseconds(timeout),
                operation.StartsWith("sftp", StringComparison.Ordinal) && p.Bool("Ueberschreiben")),
            p.Optional("Ziel"), p.Optional("StatusZiel"), p.Optional("FehlerZiel"), p.Optional("ErwarteterStatus"));
    }
    internal static async Task ConfigureAsync(IHardwareAdapter adapter, string? endpoint, string? options, IStepContext ctx, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            if (!string.IsNullOrWhiteSpace(options)) throw new CommunicationException("Verbindungsoptionen brauchen eine Adresse.");
            return;
        }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (options ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new CommunicationException("Verbindungsoption braucht Schlüssel=Wert.");
            values.Add(line[..equals].Trim(), CommunicationBlocks.Interpolate(line[(equals + 1)..], ctx));
        }
        await CommunicationBlocks.As<IConfigurableConnection>(adapter, "konfigurierbare Verbindung")
            .ConfigureAsync(CommunicationBlocks.Interpolate(endpoint, ctx), values, token).ConfigureAwait(false);
    }
    private sealed class ConfigureAction(string resource, string? endpoint, string? options) : IStepAction
    {
        public async Task<StepOutcome> ExecuteAsync(IStepContext context)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new CommunicationException("Adresse / COM-Port fehlt.");
            await context.UseDeviceAsync(resource, "Verbindung konfigurieren", async (a, _, ct) =>
            { await ConfigureAsync(a, endpoint, options, context, ct).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
            return StepOutcome.Unrated("Verbindung konfiguriert");
        }
    }
    private sealed class RemoteAction(string resource, string? endpoint, string? options, RemoteRequest request,
        string? target, string? statusTarget, string? errorTarget, string? expected) : IStepAction
    {
        public async Task<StepOutcome> ExecuteAsync(IStepContext context)
        {
            string Expand(string text) => CommunicationBlocks.Interpolate(text, context);
            var resolved = request with { Argument = Expand(request.Argument), Body = Expand(request.Body), Headers = Expand(request.Headers), LocalPath = Expand(request.LocalPath) };
            var response = await context.UseDeviceAsync(resource, request.Operation, async (a, _, ct) =>
            {
                await ConfigureAsync(a, endpoint, options, context, ct).ConfigureAwait(false);
                return await CommunicationBlocks.As<IRemoteConnection>(a, "SSH/SFTP/HTTP").ExecuteAsync(resolved, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (target is not null) CommunicationBlocks.SetTarget(context, target, response.Text);
            if (statusTarget is not null) CommunicationBlocks.SetTarget(context, statusTarget, response.Code);
            if (errorTarget is not null) CommunicationBlocks.SetTarget(context, errorTarget, response.Error);
            var range = (expected is null ? (request.Operation == "http" ? "200-299" : "0") : Expand(expected)).Split('-', 2);
            var low = int.Parse(range[0], CultureInfo.InvariantCulture);
            var high = range.Length == 1 ? low : int.Parse(range[1], CultureInfo.InvariantCulture);
            return new StepOutcome(response.Code >= low && response.Code <= high ? ResultRating.Pass : ResultRating.Fail,
                response.Text, Message: $"{request.Operation}: Status {response.Code}");
        }
    }
}
