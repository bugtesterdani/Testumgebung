/**
 * @file
 * @brief Spielt externe Wartemusik asynchron mit dem Windows-Medienplayer ab.
 */
using System.Windows;
using System.Windows.Media;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Audio;

internal interface IAudioMedia
{
    event EventHandler? Ended;
    event Action<Exception>? Failed;
    void Open(Uri source);
    void Play(bool restart);
    void Close();
}

/// <summary>Jede Aufforderung besitzt eine eigene Wiedergabe; kein Audio wird in ein Paket oder die Datenbank kopiert.</summary>
internal sealed class OperatorAudioPlayer : IOperatorAudioPlayer
{
    private readonly Func<IAudioMedia> _create;
    public OperatorAudioPlayer() : this(() => new WpfAudioMedia()) { }
    internal OperatorAudioPlayer(Func<IAudioMedia> create) => _create = create;

    public IDisposable PlayLoop(string source, Action<string> onError)
    {
        var uri = OperatorAudioSource.Resolve(source);
        return new Session(_create(), uri, onError);
    }

    private sealed class Session : IDisposable
    {
        private readonly IAudioMedia _media;
        private readonly Action<string> _onError;
        private bool _closed;

        public Session(IAudioMedia media, Uri source, Action<string> onError)
        {
            _media = media;
            _onError = onError;
            _media.Ended += OnEnded;
            _media.Failed += OnFailed;
            try
            {
                _media.Open(source);
                if (!_closed) _media.Play(restart: false);
            }
            catch { Dispose(); throw; }
        }

        private void OnEnded(object? sender, EventArgs args)
        {
            if (_closed) return;
            try { _media.Play(restart: true); }
            catch (Exception ex) { OnFailed(ex); }
        }

        private void OnFailed(Exception exception)
        {
            if (_closed) return;
            Dispose();
            _onError(exception.Message);
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _media.Ended -= OnEnded;
            _media.Failed -= OnFailed;
            try { _media.Close(); }
            catch (Exception ex) { _onError(ex.Message); }
        }
    }

    private sealed class WpfAudioMedia : IAudioMedia
    {
        private readonly MediaPlayer _player = new();
        public WpfAudioMedia()
        {
            _player.MediaEnded += OnEnded;
            _player.MediaFailed += OnFailed;
        }
        public event EventHandler? Ended;
        public event Action<Exception>? Failed;
        private void OnEnded(object? sender, EventArgs args) => Ended?.Invoke(this, args);
        private void OnFailed(object? sender, ExceptionEventArgs args) => Failed?.Invoke(args.ErrorException);
        public void Open(Uri source) => _player.Open(source);
        public void Play(bool restart)
        {
            if (restart) _player.Position = TimeSpan.Zero;
            _player.Play();
        }
        public void Close()
        {
            _player.MediaEnded -= OnEnded;
            _player.MediaFailed -= OnFailed;
            _player.Close();
        }
    }
}
