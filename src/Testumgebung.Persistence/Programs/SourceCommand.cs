using System.Diagnostics;

namespace Testumgebung.Persistence.Programs;

/// <summary>ArgumentList statt Shell; nicht interaktiv, begrenzte Laufzeit und vollständiger Abbruch des Clientprozesses.</summary>
internal static class SourceCommand
{
    public static async Task<string> Run(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        using var process = Start(start);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        var text = await output.ConfigureAwait(false); var diagnostic = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException($"Versionsverwaltungsclient meldet Fehler {process.ExitCode}: {diagnostic[..Math.Min(diagnostic.Length, 1500)]}");
        return text.Trim();
    }
    private static Process Start(ProcessStartInfo start)
    {
        try { return Process.Start(start) ?? throw new IOException("Versionsverwaltungsclient konnte nicht gestartet werden."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException($"Client '{start.FileName}' konnte nicht gestartet werden. Lokale Git-/SVN-Clientpfade prüfen: {ex.Message}", ex); }
    }

}
