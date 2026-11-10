using System.Diagnostics;
using System.Text;

namespace Testumgebung.Persistence.Programs;

public enum SourceLoginMode { External, GitAccessToken, GitDeployToken, SvnWindows, SvnPassword }

/// <summary>Local, transient authentication material. Never serialize this object into a source or release.</summary>
public sealed class SourceCredentials(SourceLoginMode mode, string username = "", string secret = "")
{
    public SourceLoginMode Mode { get; } = mode;
    public string Username { get; } = username;
    public string Secret { get; } = secret;
    public override string ToString() => Mode.ToString();
    public string Redact(string text)
    {
        if (Secret.Length == 0) return text;
        foreach (var value in new[] { Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Secret)), Uri.EscapeDataString(Secret), Secret })
            text = text.Replace(value, "[ausgeblendet]", StringComparison.Ordinal);
        return text;
    }
    public void Validate(ProgramSource source)
    {
        if (Mode == SourceLoginMode.External) return;
        var valid = source.Provider switch
        {
            "Git" => Mode is SourceLoginMode.GitAccessToken or SourceLoginMode.GitDeployToken,
            "SVN" => Mode is SourceLoginMode.SvnWindows or SourceLoginMode.SvnPassword,
            _ => false,
        };
        if (!valid) throw new ArgumentException("Zugangsmethode passt nicht zur Versionsverwaltung.");
        if (new Uri(source.Repository).Scheme != "https") throw new ArgumentException("Diese Zugangsmethode benötigt eine HTTPS-Repository-URL. Für SSH die vorhandene Client-Anmeldung verwenden.");
        if (Mode == SourceLoginMode.SvnWindows) return;
        if (string.IsNullOrWhiteSpace(Username) || Secret.Length == 0) throw new ArgumentException("Benutzername und Token/Passwort eingeben.");
        if (Username.Contains(':') || (Username + Secret).Any(character => character is '\r' or '\n' or '\0')) throw new ArgumentException("Zugangsdaten enthalten unzulässige Steuerzeichen oder einen Doppelpunkt im Benutzernamen.");
    }
}

public sealed class SourceAuthenticationException(ProgramSource source, string message) : IOException(message)
{
    public ProgramSource ProgramSource { get; } = source;
}

/// <summary>Credentials use per-child environment (Git) or stdin (SVN), never command-line secrets or repository config.</summary>
public static class SourceAuthentication
{
    public static string? Configure(ProcessStartInfo start, ProgramSource source, SourceCredentials credentials)
    {
        credentials.Validate(source);
        return credentials.Mode switch
        {
            SourceLoginMode.GitAccessToken or SourceLoginMode.GitDeployToken => ConfigureGit(start, source, credentials),
            SourceLoginMode.SvnPassword => ConfigureSvnPassword(start, credentials),
            SourceLoginMode.SvnWindows => ConfigureWindows(start),
            _ => null,
        };
    }
    private static string? ConfigureGit(ProcessStartInfo start, ProgramSource source, SourceCredentials credentials)
    {
        // Disable credential-helper fallback and redirects so an explicit token cannot be silently replaced or forwarded.
        var header = "http." + source.Repository.TrimEnd('/') + "/.extraHeader";
        var values = new[] { ("credential.helper", ""), (header, ""),
            (header, "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.Username + ":" + credentials.Secret))),
            ("http.followRedirects", "false") };
        start.Environment.Remove("GIT_CONFIG_PARAMETERS");
        start.Environment["GIT_CONFIG_COUNT"] = values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < values.Length; index++)
        {
            start.Environment["GIT_CONFIG_KEY_" + index] = values[index].Item1;
            start.Environment["GIT_CONFIG_VALUE_" + index] = values[index].Item2;
        }
        return null;
    }
    private static string ConfigureSvnPassword(ProcessStartInfo start, SourceCredentials credentials)
    {
        start.ArgumentList.Add("--no-auth-cache");
        start.ArgumentList.Add("--username"); start.ArgumentList.Add(credentials.Username);
        start.ArgumentList.Add("--password-from-stdin");
        // Do not reuse another account or integrated credentials in explicit-password mode.
        start.ArgumentList.Add("--config-option"); start.ArgumentList.Add("servers:global:http-auth-types=basic;digest");
        return credentials.Secret;
    }
    private static string? ConfigureWindows(ProcessStartInfo start)
    {
        start.ArgumentList.Add("--no-auth-cache");
        start.ArgumentList.Add("--config-option"); start.ArgumentList.Add("servers:global:http-auth-types=negotiate");
        return null;
    }
    internal static IDisposable? IsolateSvnConfiguration(ProcessStartInfo start, SourceCredentials? credentials)
    {
        if (credentials?.Mode is not (SourceLoginMode.SvnPassword or SourceLoginMode.SvnWindows)) return null;
        // Existing server-group rules and cached identities must not override an explicit login choice.
        var directory = new ConfigurationDirectory();
        start.ArgumentList.Add("--config-dir"); start.ArgumentList.Add(directory.Path);
        return directory;
    }
    private sealed class ConfigurationDirectory : IDisposable
    {
        public string Path { get; } = SourcePaths.Temporary();
        public void Dispose() => SourcePaths.Remove(Path);
    }
    public static bool IsAuthenticationFailure(string diagnostic)
        => new[] { "authentication failed", "authentication error", "authorization failed", "could not read Username", "could not read Password", "terminal prompts disabled", "HTTP 401", "error: 401", "E170001", "E215004", "E215003" }
            .Any(value => diagnostic.Contains(value, StringComparison.OrdinalIgnoreCase));
}