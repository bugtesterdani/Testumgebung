/** @file @brief Liest anwachsende Dateien mit Schreibfreigabe sowie nicht konsumierende Prozessausgabe. */
using System.Runtime.CompilerServices;
using System.Text;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Processes;
namespace Testumgebung.Engine.Blocks;

internal sealed record LiveLogChunk(string Text, bool ResetLine = false);

internal static class LiveLogSources
{
    public static async IAsyncEnumerable<LiveLogChunk> Process(IStepContext ctx, string handle, ProcessOutputStream stream, [EnumeratorCancellation] CancellationToken token)
    {
        long offset = 0;
        while (true)
        {
            var chunk = await ctx.Processes.ReadOutputAsync(handle, stream, offset, token).ConfigureAwait(false);
            offset = chunk.NextOffset;
            if (chunk.Text.Length > 0) yield return new(chunk.Text);
            if (chunk.Completed) yield break;
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }
    public static async IAsyncEnumerable<LiveLogChunk> File(string path, string encoding, bool fromEnd, [EnumeratorCancellation] CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Live-Log benötigt einen absoluten Dateipfad.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codec = Encoding.GetEncoding(encoding);
        await using var file = await OpenAsync(path, token).ConfigureAwait(false);
        if (fromEnd) file.Seek(0, SeekOrigin.End);
        yield return new(""); // Source is open; observers can distinguish readiness from the initial reset.
        var decoder = codec.GetDecoder();
        var first = !fromEnd;
        var bytes = new byte[8192];
        var chars = new char[codec.GetMaxCharCount(bytes.Length)];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (file.Length < file.Position)
            { file.Position = 0; decoder.Reset(); first = true; yield return new("", ResetLine: true); }
            var count = await file.ReadAsync(bytes, token).ConfigureAwait(false);
            if (count == 0) { await Task.Delay(100, token).ConfigureAwait(false); continue; }
            var decoded = decoder.GetChars(bytes, 0, count, chars, 0, flush: false);
            if (decoded > 0)
            {
                var text = new string(chars, 0, decoded);
                if (first) { first = false; text = text.TrimStart('\uFEFF'); }
                yield return new(text);
                // Bound UI-dispatch traffic even when an existing log is much larger than the visible tail.
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }
    private static async Task<FileStream> OpenAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous); }
            catch (FileNotFoundException) { await Task.Delay(100, token).ConfigureAwait(false); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            { throw new IOException("Logdatei ist exklusiv gegen Lesen gesperrt. Das schreibende Programm muss Lesefreigabe erlauben.", ex); }
        }
    }
}
