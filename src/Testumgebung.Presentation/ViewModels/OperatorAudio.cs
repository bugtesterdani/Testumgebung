/**
 * @file
 * @brief Abstraktion für optionale, externe Wartemusik ohne Speicherung von Mediendaten.
 */
namespace Testumgebung.Presentation.ViewModels;

/// <summary>Startet eine unabhängige Wiedergabe; Dispose beendet auch einen noch laufenden Ladevorgang.</summary>
public interface IOperatorAudioPlayer
{
    IDisposable PlayLoop(string source, Action<string> onError);
}

/// <summary>Erlaubt ausschließlich externe Datei- und Webreferenzen, keine eingebetteten Audiodaten.</summary>
public static class OperatorAudioSource
{
    public static Uri Resolve(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            throw new ArgumentException("Audiodatei benötigt einen absoluten Datei-/UNC-Pfad oder eine HTTP/HTTPS-Adresse.");
        if (uri.Scheme is "http" or "https") return uri;
        if (uri.IsFile && Path.GetExtension(uri.LocalPath).ToLowerInvariant() is ".mp3" or ".wav") return uri;
        throw new ArgumentException("Audio unterstützt externe MP3-/WAV-Dateien und HTTP/HTTPS-Adressen.");
    }
}
