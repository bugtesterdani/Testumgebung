using System.Diagnostics;

namespace Testumgebung.Persistence.Programs;

/// <summary>ArgumentList statt Shell; nicht interaktiv, begrenzte Laufzeit und vollständiger Abbruch des Clientprozesses.</summary>
internal static class SourceCommand
{
    public static async Task<string> Run(string executable, IEnumerable<string> arguments, CancellationToken ct, ProgramSource? source = null, SourceCredentials? credentials = null)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        var input = source is null ? null : SourceAuthentication.Configure(start, source, credentials ?? new(SourceLoginMode.External));
        using var svnConfiguration = SourceAuthentication.IsolateSvnConfiguration(start, credentials);
        using var process = Start(start);

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (input is not null) await process.StandardInput.WriteLineAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        var text = await output.ConfigureAwait(false); var diagnostic = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            diagnostic = credentials?.Redact(diagnostic) ?? diagnostic;
            if (source is not null && SourceAuthentication.IsAuthenticationFailure(diagnostic))
                throw new SourceAuthenticationException(source, "Anmeldung am Repository fehlgeschlagen. Lokale Zugangsmethode und Leseberechtigung prüfen.");
            throw new IOException($"Versionsverwaltungsclient meldet Fehler {process.ExitCode}: {diagnostic[..Math.Min(diagnostic.Length, 1500)]}");
        }
        return text.Trim();
    }
    private static Process Start(ProcessStartInfo start)
    {
        try { return Process.Start(start) ?? throw new IOException("Versionsverwaltungsclient konnte nicht gestartet werden."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException($"Client '{start.FileName}' konnte nicht gestartet werden. Lokale Git-/SVN-Clientpfade prüfen: {ex.Message}", ex); }
    }

}
