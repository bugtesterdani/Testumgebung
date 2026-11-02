using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class LiveGroupResultTests
{
    [Fact]
    public void Computed_group_result_is_live_scoped_and_read_only()
    {
        var outer = Scope.CreateFor(Group("outer"), null);
        var inner = Scope.CreateFor(Group("inner"), outer);

        Assert.Equal("NRESULT", inner.Get("GruppenErgebnis"));
        Assert.Equal("NRESULT", inner.Get("../GruppenErgebnis"));
        Assert.Contains(inner.VisibleVariables(), v => v.Name == "GruppenErgebnis" && v.Type == VarType.String);

        outer.GroupResult.Merge(ResultRating.Pass);
        Assert.Equal("PASS", inner.Get("../GruppenErgebnis"));
        Assert.Equal("NRESULT", inner.Get("GruppenErgebnis"));
        inner.GroupResult.Merge(ResultRating.Fail);
        Assert.Equal("FAIL", inner.Get("GruppenErgebnis"));
        Assert.Equal("FAIL", inner.Snapshot().Variables.Single(v => v.Name == Scope.GroupResultVariable).Value);
        Assert.Equal("PASS", outer.Get("GruppenErgebnis"));
        Assert.True(Expression.Parse("GruppenErgebnis == \"FAIL\" && ../GruppenErgebnis == \"PASS\"").EvaluateBool(inner));

        Assert.Throws<InvalidOperationException>(() => inner.Set("GruppenErgebnis", "PASS"));
        Assert.Throws<InvalidOperationException>(() => inner.Set("../GruppenErgebnis", "ERROR"));

        var isolated = Scope.CreateFor(new GroupDefinition("isolated", "isolated") { IsInterfaceBoundary = true }, outer);
        Assert.Throws<VariableNotFoundException>(() => isolated.Get("../GruppenErgebnis"));
        Assert.Equal("NRESULT", isolated.Get("GruppenErgebnis"));
    }

    [Fact]
    public void Reserved_name_is_reported_by_validation()
    {
        var definition = Test(Group("loop").Var("GruppenErgebnis", VarType.String));
        Assert.Contains(definition.Validate(), diagnostic => diagnostic.Contains("GruppenErgebnis") && diagnostic.Contains("Systemvariable"));
    }

    [Fact]
    public async Task Running_groups_return_partial_results_and_parent_rating()
    {
        var observed = new List<string>();
        var nested = Group("nested").Add(
            Steps.Code("readParent", "read parent", ctx =>
            {
                observed.Add($"{ctx.Get("GruppenErgebnis")}|{ctx.Get("../GruppenErgebnis")}");
                return StepOutcome.Unrated();
            }),
            Steps.Rate("nestedPass", ResultRating.Pass));
        var group = Group("g").Add(
            Steps.Code("initial", "initial", ctx => { observed.Add((string)ctx.Get("GruppenErgebnis")); return StepOutcome.Unrated(); }),
            Steps.Rate("pass", ResultRating.Pass),
            nested,
            Steps.Code("afterPass", "after pass", ctx => { observed.Add((string)ctx.Get("GruppenErgebnis")); return StepOutcome.Unrated(); }),
            Steps.Rate("fail", ResultRating.Fail),
            Steps.Code("afterFail", "after fail", ctx => { observed.Add((string)ctx.Get("GruppenErgebnis")); return StepOutcome.Unrated(); }),
            Steps.Rate("error", ResultRating.Error),
            Steps.Code("afterError", "after error", ctx => { observed.Add((string)ctx.Get("GruppenErgebnis")); return StepOutcome.Unrated(); }));

        var run = new TestRun(Test(Group("loop").Add(group)));
        var result = await run.RunAsync().Within();

        Assert.Equal(new[] { "NRESULT", "NRESULT|PASS", "PASS", "FAIL", "ERROR" }, observed);
        Assert.Equal(ResultRating.Error, run.GroupResult("g").Rating);
        Assert.Equal(ResultRating.Error, result.Rating);
    }

    [Theory]
    [InlineData(RepeatResultPolicy.Keep, "FAIL", ResultRating.Fail)]
    [InlineData(RepeatResultPolicy.ResetToNResult, "NRESULT", ResultRating.Pass)]
    public async Task Live_result_follows_repeat_reset_policy(RepeatResultPolicy policy, string secondIterationRating, ResultRating final)
    {
        var observed = new List<string>();
        var iteration = 0;
        var group = Group("g").Add(
            Steps.Code("read", "read", ctx => { observed.Add((string)ctx.Get("GruppenErgebnis")); return StepOutcome.Unrated(); }),
            Steps.Code("rate", "rate", _ => ++iteration == 1 ? StepOutcome.Fail() : StepOutcome.Pass()));
        group.Repetition = Repetition.Times(2);
        group.ResultBeforeRepeat = policy;

        var run = new TestRun(Test(Group("loop").Add(group)));
        await run.RunAsync().Within();

        Assert.Equal(new[] { "NRESULT", secondIterationRating }, observed);
        Assert.Equal(final, run.GroupResult("g").Rating);
    }

    [Fact]
    public async Task Group_entry_condition_can_use_parent_rating_without_storing_it()
    {
        var visited = false;
        var condition = new GroupDefinition("conditional", "conditional")
        {
            EntryCondition = "GruppenErgebnis == \"NRESULT\" && ../GruppenErgebnis == \"PASS\"",
        }.Add(Steps.Code("conditional-step", "step", _ => { visited = true; return StepOutcome.Pass(); }));
        var run = new TestRun(Test(Group("loop").Add(Group("g").Add(Steps.Rate("pass", ResultRating.Pass), condition))));

        await run.RunAsync().Within();
        Assert.True(visited);
    }

    [Fact]
    public async Task Parallel_children_see_already_committed_rating()
    {
        var seen = "";
        var group = new GroupDefinition("parallel", "parallel") { Parallel = true }.Add(
            Steps.Rate("fail", ResultRating.Fail),
            Steps.Code("reader", "reader", async ctx =>
            {
                using var timeout = new CancellationTokenSource(Kit.Timeout);
                while ((string)ctx.Get("GruppenErgebnis") != "FAIL")
                    await Task.Delay(1, timeout.Token);
                seen = (string)ctx.Get("GruppenErgebnis");
                return StepOutcome.Unrated();
            }));

        var run = new TestRun(Test(Group("loop").Add(group)));
        await run.RunAsync().Within();
        Assert.Equal("FAIL", seen);
        Assert.Equal(ResultRating.Fail, run.GroupResult("parallel").Rating);
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    [InlineData("C++")]
    public async Task Codeblocks_read_live_result_and_parent_path(string language)
    {
        static StepDefinition Code(string id, string lang, string csharp, string python)
            => BlockCatalog.Default.Create(BlockCatalog.Code, id, id, new Dictionary<string, string>
            {
                ["Sprache"] = lang,
                ["Quelltext"] = lang == "Python" ? python : lang == "C++" ? csharp.Replace("Var<string>", "VarText", StringComparison.Ordinal) : csharp,
                ["Timeout"] = "10",
            });

        var group = Group("g").Add(
            Code("first", language,
                "return GruppenErgebnis == \"NRESULT\" ? Pass() : Fail();",
                "return Pass() if GruppenErgebnis == \"NRESULT\" else Fail()"),
            Code("second", language,
                "return GruppenErgebnis == \"PASS\" && Var<string>(\"../GruppenErgebnis\") == \"NRESULT\" ? Pass() : Fail();",
                "return Pass() if GruppenErgebnis == \"PASS\" and var(\"../GruppenErgebnis\") == \"NRESULT\" else Fail()"));

        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions
        {
            Clock = new VirtualClock(),
            CodeRuntime = CodeBlockTests.SharedRuntime.Value,
        });
        await run.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("first")).Rating);
        Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("second")).Rating);
        Assert.Equal(ResultRating.Pass, run.GroupResult("g").Rating);
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    [InlineData("C++")]
    public async Task Codeblocks_cannot_overwrite_system_rating(string language)
    {
        var block = BlockCatalog.Default.Create(BlockCatalog.Code, "overwrite", "overwrite", new Dictionary<string, string>
        {
            ["Sprache"] = language,
            ["Quelltext"] = language != "Python"
                ? "SetVar(\"GruppenErgebnis\", \"PASS\"); return Pass();"
                : "set_var(\"GruppenErgebnis\", \"PASS\")\nreturn Pass()",
            ["Timeout"] = "10",
        });
        var run = new TestRun(Test(Group("loop").Add(Group("g").Add(block))), new TestRunOptions
        {
            Clock = new VirtualClock(),
            CodeRuntime = CodeBlockTests.SharedRuntime.Value,
        });
        await run.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ResultRating.Error, Assert.Single(run.Steps("overwrite")).Rating);
    }
}
