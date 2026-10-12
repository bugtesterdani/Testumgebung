using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Tests;

public sealed class CSharpDebuggerContractTests
{
    private sealed class Host : ICodeHost
    {
        public List<CodeDebugEvent> Stops { get; } = [];
        public object Get(string path) => throw new NotSupportedException();
        public void Set(string path, object? value) => throw new NotSupportedException();
        public Task<double> ReadAsync(string resource) => throw new NotSupportedException();
        public Task WriteAsync(string resource, double value) => throw new NotSupportedException();
        public Task<object?> AskAsync(OperatorPrompt prompt) => throw new NotSupportedException();
        public void Log(string message) { }
        public Task<CodeDebugStop> DebugAsync(CodeDebugEvent e)
        {
            Stops.Add(e);
            return Task.FromResult(new CodeDebugStop("continue", [], true));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Debugger_instruments_declarations_assignments_and_nested_blocks(bool stopOnEntry)
    {
        using var runtime = new CodeRuntime(new CodeRuntimeOptions { WorkerDirectory = Path.Combine(AppContext.BaseDirectory, "workers") });
        var host = new Host();
        const string source = "long a = 1, b = 2;\nlong c;\nc = a + b;\nif (c > 0) {\n c += 1;\n}\nvar values = new[] { c };\nvalues[0] = 5;\nreturn Pass(values[0]);";
        var result = await runtime.InvokeAsync(new(CodeLanguage.CSharp, source, [], Debug: new(true, [], stopOnEntry, true)),
            new Dictionary<string, object?>(), host, TimeSpan.FromSeconds(30), default).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ResultRating.Pass, result.Rating);
        Assert.Equal(5, Convert.ToInt32(result.Value));
        Assert.Contains(host.Stops, e => e.Line == 1 && e.Kind == "line");
        Assert.Contains(host.Stops, e => e.Line == 5 && e.Kind == "line");
        Assert.Contains(host.Stops, e => e.Line == 9 && e.Kind == "line");
    }

    [Fact]
    public async Task User_exception_is_reported_and_worker_can_execute_again()
    {
        using var runtime = new CodeRuntime(new CodeRuntimeOptions { WorkerDirectory = Path.Combine(AppContext.BaseDirectory, "workers") });
        var host = new Host();
        var failed = await Assert.ThrowsAsync<CodeExecutionException>(() => runtime.InvokeAsync(new(CodeLanguage.CSharp, "throw new InvalidOperationException(\"failure\");", []),
            new Dictionary<string, object?>(), host, TimeSpan.FromSeconds(30), default).WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Contains("failure", failed.Message);
        var recovered = await runtime.InvokeAsync(new(CodeLanguage.CSharp, "return Pass(42);", []),
            new Dictionary<string, object?>(), host, TimeSpan.FromSeconds(30), default).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ResultRating.Pass, recovered.Rating);
        Assert.Equal(42, Convert.ToInt32(recovered.Value));
    }
}
