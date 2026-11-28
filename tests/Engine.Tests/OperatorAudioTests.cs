using System.IO.Compression;
using System.Reflection;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class OperatorAudioTests
{
    private static void Await(OperatorContentViewModel vm, TaskCompletionSource<object?> answer)
        => typeof(OperatorContentViewModel).GetMethod("Await", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [answer]);
    private static void Cancel(OperatorContentViewModel vm)
        => typeof(OperatorContentViewModel).GetMethod("Cancel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);

    [Theory]
    [InlineData(OperatorPromptKind.Instruction)]
    [InlineData(OperatorPromptKind.ImageConfirmation)]
    [InlineData(OperatorPromptKind.Input)]
    [InlineData(OperatorPromptKind.Choice)]
    public async Task Audio_lasts_until_a_valid_answer_and_invalid_input_keeps_it_running(OperatorPromptKind kind)
    {
        var player = new FakePlayer();
        var prompt = new OperatorPrompt(kind, "Wait", InputType: VarType.String, Required: true,
            AllowedValues: ["ok"], AudioPath: "https://example.invalid/wait.mp3");
        var vm = new OperatorContentViewModel(prompt, player);
        Assert.Empty(player.Sessions);
        var answer = new TaskCompletionSource<object?>();
        Await(vm, answer);
        var session = Assert.Single(player.Sessions);
        Assert.Equal(prompt.AudioPath, Assert.Single(player.Sources));
        if (prompt.ExpectsValue)
        {
            vm.Confirm();
            Assert.NotNull(vm.Error);
            Assert.False(answer.Task.IsCompleted);
            Assert.Equal(0, session.Disposals);
            vm.Input = "ok";
            vm.SelectedChoice = "ok";
        }
        vm.Confirm();
        Assert.True(answer.Task.IsCompletedSuccessfully);
        await answer.Task;
        Assert.Equal(1, session.Disposals);
        Cancel(vm);
        Assert.Equal(1, session.Disposals);
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    [InlineData("music.wav", false, false)]
    [InlineData("music.wav", true, true)]
    public void Empty_nonwaiting_and_already_cancelled_prompts_never_start_audio(string? source, bool wait, bool cancelled)
    {
        var player = new FakePlayer();
        var vm = new OperatorContentViewModel(new OperatorPrompt(OperatorPromptKind.Instruction, "Wait", WaitForAnswer: wait, AudioPath: source), player);
        var answer = new TaskCompletionSource<object?>();
        if (cancelled) answer.SetCanceled();
        Await(vm, answer);
        Assert.Empty(player.Sessions);
        Cancel(vm);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("playback")]
    [InlineData("stop")]
    public void Optional_audio_errors_never_prevent_confirmation(string failure)
    {
        var player = new FakePlayer { FailStart = failure == "start", FailStop = failure == "stop" };
        var vm = new OperatorContentViewModel(new OperatorPrompt(OperatorPromptKind.Instruction, "Wait", AudioPath: "music.wav"), player);
        var answer = new TaskCompletionSource<object?>();
        Await(vm, answer);
        if (failure == "playback") player.Error!("server unavailable");
        vm.Confirm();
        Assert.True(answer.Task.IsCompletedSuccessfully);
        Assert.NotNull(vm.AudioError);
    }

    [Fact]
    public async Task Cancellation_stops_audio_even_before_the_prompt_is_removed()
    {
        var ui = new QueueDispatcher();
        var player = new FakePlayer();
        var project = new TestProject(Test(Group("loop")));
        var station = new StationViewModel("Test", () => project, new ResourceManager(), ui, operatorAudio: player);
        using var cancellation = new CancellationTokenSource();
        var pending = ((IOperatorInterface)station).AskAsync("run", new OperatorPrompt(OperatorPromptKind.Instruction, "Wait", AudioPath: "music.wav") { DisplayId = "one" }, cancellation.Token);
        ui.Pump();
        Assert.Single(player.Sessions);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await ui.Until(() => player.Sessions[0].Disposals == 1, "audio stopped");
        Assert.True(Assert.Single(station.Contents).IsAnswered);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_never_starts_late_audio()
    {
        var ui = new QueueDispatcher();
        var player = new FakePlayer();
        var project = new TestProject(Test(Group("loop")));
        var station = new StationViewModel("Test", () => project, new ResourceManager(), ui, operatorAudio: player);
        using var cancellation = new CancellationTokenSource();
        var pending = ((IOperatorInterface)station).AskAsync("run", new OperatorPrompt(OperatorPromptKind.Instruction, "Wait", AudioPath: "music.wav") { DisplayId = "one" }, cancellation.Token);
        cancellation.Cancel();
        ui.Pump();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(player.Sessions);
        Assert.Empty(station.Contents);
    }

    [Fact]
    public void Independent_prompts_do_not_stop_each_others_audio()
    {
        var player = new FakePlayer();
        var prompt = new OperatorPrompt(OperatorPromptKind.Instruction, "Wait", AudioPath: "music.wav");
        var a = new OperatorContentViewModel(prompt, player);
        var b = new OperatorContentViewModel(prompt, player);
        Await(a, new TaskCompletionSource<object?>());
        Await(b, new TaskCompletionSource<object?>());
        a.Confirm();
        Assert.Equal(1, player.Sessions[0].Disposals);
        Assert.Equal(0, player.Sessions[1].Disposals);
        Cancel(b);
        Assert.Equal(1, player.Sessions[1].Disposals);
    }

    [Theory]
    [InlineData(@"C:\Music\wait.mp3")]
    [InlineData(@"\\server\share\wait.wav")]
    [InlineData("https://example.invalid/download?id=123")]
    [InlineData("http://example.invalid/wait.wav")]
    public void Only_external_file_and_web_sources_are_supported(string source)
        => Assert.True(OperatorAudioSource.Resolve(source).IsAbsoluteUri);

    [Theory]
    [InlineData("relative.wav")]
    [InlineData("data:audio/wav;base64,AAAA")]
    [InlineData("ftp://example.invalid/music.wav")]
    [InlineData(@"C:\Music\video.exe")]
    public void Embedded_and_unsupported_sources_are_rejected(string source)
        => Assert.Throws<ArgumentException>(() => OperatorAudioSource.Resolve(source));

    [Fact]
    public async Task Catalog_interpolates_audio_reference_without_mutating_the_project()
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Operator, "ask", "Ask", new Dictionary<string, string>
        { ["Audio"] = "https://example.invalid/{File}", ["Warten"] = "false" });
        var run = new TestRun(Test(Group("loop").Var("File", VarType.String, "wait.mp3").Add(step)), new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within();
        Assert.Equal("https://example.invalid/wait.mp3", Assert.Single(run.Events.OfType<OperatorContentShown>()).Prompt.AudioPath);
        Assert.Equal("https://example.invalid/{File}", step.Parameters["Audio"]);
    }

    [Fact]
    public void Project_and_release_keep_the_reference_without_loading_or_embedding_the_audio_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tu-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "wait.wav");
            File.WriteAllText(audio, "AUDIO_CONTENT_MUST_NEVER_BE_EMBEDDED_7c74e");
            var step = BlockCatalog.Default.Create(BlockCatalog.Operator, "ask", "Ask", new Dictionary<string, string> { ["Audio"] = audio });
            var project = new TestProject(Test(Group("loop").Add(step)));
            var serializer = new ProjectSerializer();
            var json = serializer.Serialize(project);
            Assert.DoesNotContain("AUDIO_CONTENT_MUST_NEVER_BE_EMBEDDED", json);
            Assert.Equal(audio, serializer.Clone(project).Definition.AllNodes().OfType<StepDefinition>().Single().Parameters["Audio"]);
            var time = DateTimeOffset.UtcNow;
            var (before, _) = ReleasePackage.Build(project, "tester", time);
            File.Delete(audio);
            var (after, _) = ReleasePackage.Build(project, "tester", time);
            Assert.Equal(before, after);
            using var zip = new ZipArchive(new MemoryStream(after));
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            Assert.True(ReleasePackage.Open(after).IsIntact);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_station_run_stops_and_cleans_audio_on_answer_or_abort(bool abort)
    {
        var ui = new QueueDispatcher();
        var player = new FakePlayer();
        var step = BlockCatalog.Default.Create(BlockCatalog.Operator, "ask", "Ask", new Dictionary<string, string> { ["Audio"] = "https://example.invalid/wait.mp3" });
        var project = new TestProject(Test(Group("loop").Add(step)));
        var station = new StationViewModel("Test", () => project, new ResourceManager(), ui, clock: new VirtualClock(), operatorAudio: player);
        var run = station.StartAsync();
        await ui.Until(() => player.Sessions.Count == 1, "audio started");
        if (abort) station.RequestAbort();
        else station.ActiveContent!.Confirm();
        await ui.Until(() => run.IsCompleted, "run finished");
        await run;
        ui.Pump();
        Assert.Equal(1, player.Sessions[0].Disposals);
        Assert.Empty(station.Contents);
    }

    [Fact]
    public async Task Clearing_a_group_cancels_its_pending_audio_and_answer()
    {
        var ui = new QueueDispatcher();
        var player = new FakePlayer();
        var project = new TestProject(Test(Group("loop")));
        var station = new StationViewModel("Test", () => project, new ResourceManager(), ui, operatorAudio: player);
        var pending = ((IOperatorInterface)station).AskAsync("run", new OperatorPrompt(OperatorPromptKind.Instruction, "Wait", AudioPath: "music.wav")
            { DisplayId = "one", GroupScopeId = "scope" }, CancellationToken.None);
        ui.Pump();
        typeof(StationViewModel).GetMethod("HandleOperatorContentCleared", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(station, [new OperatorContentCleared("scope") { Time = DateTimeOffset.UtcNow, RunId = "run", NodeId = "loop", Path = "loop" }]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(station.Contents);
        Assert.Equal(1, Assert.Single(player.Sessions).Disposals);
    }

    private sealed class FakePlayer : IOperatorAudioPlayer
    {
        public bool FailStart { get; init; }
        public bool FailStop { get; init; }
        public Action<string>? Error { get; private set; }
        public List<string> Sources { get; } = [];
        public List<FakeSession> Sessions { get; } = [];
        public IDisposable PlayLoop(string source, Action<string> onError)
        {
            Error = onError;
            if (FailStart) throw new IOException("missing file");
            Sources.Add(source);
            var session = new FakeSession(FailStop);
            Sessions.Add(session);
            return session;
        }
    }
    private sealed class FakeSession(bool failStop) : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose()
        {
            Disposals++;
            if (failStop) throw new IOException("device unavailable");
        }
    }
}
