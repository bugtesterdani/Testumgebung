using System.Reflection;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class OperatorPromptBoundaryTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0d, null, null)]
    [InlineData(null, 10d, "V")]
    [InlineData(0d, 10d, "V")]
    public async Task Numeric_prompt_range_and_confirmation_preserve_value(double? min, double? max, string? unit)
    {
        var prompt = new OperatorPrompt(OperatorPromptKind.Input, "Voltage", VarType.Double, true, 1.25, Min: min, Max: max, Unit: unit, ImagePath: "image.png") { DisplayId = "id", StepName = "Input" };
        var page = new OperatorContentViewModel(prompt); Assert.Equal("1,25", page.Input); Assert.True(page.HasImage); Assert.True(page.IsInput); Assert.False(page.IsChoice); Assert.False(page.IsConfirmation);
        Assert.Equal("id", page.DisplayId); Assert.Equal("Input", page.Title); Assert.Equal("Voltage", page.Text); Assert.Equal("image.png", page.ImagePath); Assert.True(page.Required);
        Assert.Equal(min is null && max is null, page.RangeText.Length == 0); Assert.Equal("Antwort bestätigen", page.ConfirmText);
        page.Confirm(); Assert.False(page.IsAnswered);
        var answer = new TaskCompletionSource<object?>(); typeof(OperatorContentViewModel).GetMethod("Await", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [answer]);
        page.Input = "bad"; page.Confirm(); Assert.NotNull(page.Error); Assert.False(answer.Task.IsCompleted);
        page.Input = "1.25"; Assert.Null(page.Error); page.ConfirmCommand.Execute(null); Assert.Equal(1.25, await answer.Task); Assert.True(page.IsAnswered); Assert.False(page.IsAwaiting);
        Assert.Equal("1,25" + (unit is null ? "" : " " + unit), page.AnswerText);
        foreach (var outside in new[] { -1d, 11d })
        {
            var result = prompt.Validate(outside); Assert.Equal((min is not null && outside < min) || (max is not null && outside > max), result.Error is not null);
            if (result.Error is not null) Assert.Contains("Zulässiger Bereich", result.Error);
        }
    }

    [Theory]
    [InlineData(OperatorPromptKind.Input)]
    [InlineData(OperatorPromptKind.Choice)]
    [InlineData(OperatorPromptKind.Instruction)]
    [InlineData(OperatorPromptKind.ImageConfirmation)]
    public async Task Optional_answers_choices_and_cancellation_have_defined_results(OperatorPromptKind kind)
    {
        var prompt = new OperatorPrompt(kind, "Prompt", AllowedValues: ["yes"], DefaultValue: "yes");
        Assert.Null(prompt.Validate(null).Error); Assert.Null(prompt.Validate(" ").Error);
        var page = new OperatorContentViewModel(prompt); Assert.False(page.HasImage); Assert.Equal(new[] { "yes" }, page.Choices);
        var answer = new TaskCompletionSource<object?>(); typeof(OperatorContentViewModel).GetMethod("Await", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [answer]);
        if (kind is OperatorPromptKind.Input or OperatorPromptKind.Choice)
        {
            page.Input = "no"; page.SelectedChoice = "no"; page.Confirm(); Assert.Equal("Wert ist nicht erlaubt", page.Error);
            page.Input = "yes"; page.SelectedChoice = "yes"; page.Confirm(); Assert.Equal("yes", await answer.Task); Assert.Equal("yes", page.AnswerText);
        }
        else { page.Confirm(); Assert.Equal(true, await answer.Task); Assert.Equal("bestätigt", page.AnswerText); Assert.Equal("Bestätigen", page.ConfirmText); }
        var cancelled = new OperatorContentViewModel(prompt); var pending = new TaskCompletionSource<object?>(); typeof(OperatorContentViewModel).GetMethod("Await", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cancelled, [pending]);
        typeof(OperatorContentViewModel).GetMethod("Cancel", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cancelled, null); Assert.True(pending.Task.IsCanceled); Assert.True(cancelled.IsAnswered);
    }

    [Fact]
    public async Task Empty_optional_value_and_unrestricted_choice_are_accepted()
    {
        var prompt = new OperatorPrompt(OperatorPromptKind.Input, "optional"); var page = new OperatorContentViewModel(prompt);
        var answer = new TaskCompletionSource<object?>(); typeof(OperatorContentViewModel).GetMethod("Await", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [answer]); page.Confirm(); Assert.Null(await answer.Task); Assert.Equal("", page.AnswerText);
        Assert.Equal("custom", new OperatorPrompt(OperatorPromptKind.Choice, "choice").Validate("custom").Value);
        Assert.NotNull(new OperatorPrompt(OperatorPromptKind.Input, "required", Required: true).Validate(null).Error);
        Assert.NotNull(new OperatorPrompt(OperatorPromptKind.Input, "typed", VarType.Int).Validate(true).Error);
    }
}
