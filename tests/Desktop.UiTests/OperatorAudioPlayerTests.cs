using System.IO;
using Testumgebung.Desktop.Audio;

namespace Testumgebung.Desktop.UiTests;

public sealed class OperatorAudioPlayerTests
{
    [Fact]
    public void Playback_repeats_until_disposed_and_late_events_cannot_restart_it()
    {
        var media = new FakeMedia();
        var errors = new List<string>();
        var player = new OperatorAudioPlayer(() => media);
        var session = player.PlayLoop("https://example.invalid/music.mp3", errors.Add);
        Assert.Equal("https", media.Source!.Scheme);
        Assert.Equal(new[] { false }, media.Plays);
        media.End(); media.End();
        Assert.Equal(new[] { false, true, true }, media.Plays);
        session.Dispose(); session.Dispose();
        media.End(); media.Fail();
        Assert.Equal(1, media.Closes);
        Assert.Equal(3, media.Plays.Count);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("play")]
    [InlineData("repeat")]
    [InlineData("async")]
    [InlineData("sync")]
    [InlineData("close")]
    public void Failed_open_play_repeat_and_close_release_the_media(string mode)
    {
        var media = new FakeMedia { Failure = mode };
        var errors = new List<string>();
        var player = new OperatorAudioPlayer(() => media);
        if (mode is "open" or "play")
            Assert.Throws<IOException>(() => player.PlayLoop(@"C:\Music\wait.wav", errors.Add));
        else
        {
            var session = player.PlayLoop(@"C:\Music\wait.wav", errors.Add);
            if (mode == "repeat") media.End();
            if (mode == "async") media.Fail();
            session.Dispose();
            Assert.Single(errors);
        }
        Assert.Equal(1, media.Closes);
        if (mode == "sync") Assert.Empty(media.Plays);
    }

    [Fact]
    public void Invalid_reference_does_not_allocate_a_media_player()
    {
        var player = new OperatorAudioPlayer(() => throw new InvalidOperationException("must not allocate"));
        Assert.Throws<ArgumentException>(() => player.PlayLoop("data:audio/mp3;base64,AAAA", _ => { }));
    }

    private sealed class FakeMedia : IAudioMedia
    {
        public string Failure { get; init; } = "";
        public Uri? Source { get; private set; }
        public List<bool> Plays { get; } = [];
        public int Closes { get; private set; }
        public event EventHandler? Ended;
        public event Action<Exception>? Failed;
        public void Open(Uri source)
        {
            Source = source;
            if (Failure == "open") throw new IOException("cannot open");
            if (Failure == "sync") Fail();
        }
        public void Play(bool restart)
        {
            if (Failure == "play" || (Failure == "repeat" && restart)) throw new IOException("cannot play");
            Plays.Add(restart);
        }
        public void Close()
        {
            Closes++;
            if (Failure == "close") throw new IOException("cannot close");
        }
        public void End() => Ended?.Invoke(this, EventArgs.Empty);
        public void Fail() => Failed?.Invoke(new IOException("network failed"));
    }
}
