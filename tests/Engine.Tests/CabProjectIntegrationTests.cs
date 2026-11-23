using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class CabProjectIntegrationTests
{
    private const string WarningSource = "J\rS e;0,0,10,10,10\rT 100,100,0,5,3;ok\rA 1\r";
    private const string ErrorSource = "J\rB 1,2,0,CODE128,10,.3;ä\rA 1\r";

    internal static TestProject Project(string source)
    {
        var step = BlockCatalog.Default.Create(LabelPrintingBlocks.Print, "label", parameters: new Dictionary<string, string>
        {
            [LabelPrintingBlocks.FormatParameter] = LabelPrintingBlocks.Cab,
            [LabelPrintingBlocks.CabDataParameter] = Convert.ToBase64String(Encoding.UTF8.GetBytes(source)),
        });
        return new(new TestDefinition("cab", "1", new("init", "Init"), new GroupDefinition("loop", "Loop").Add(step), new("end", "Ende")));
    }

    [Theory]
    [InlineData(WarningSource, false)]
    [InlineData(ErrorSource, true)]
    public void Project_diagnostics_keep_error_and_warning_severity_without_mutating_parameters(string source, bool error)
    {
        var project = Project(source);
        var serializer = new ProjectSerializer(); var before = serializer.Serialize(project);
        var issues = CabProjectDiagnostics.Check(project.Definition);
        Assert.NotEmpty(issues); Assert.Equal(error, issues.Any(i => i.IsError));
        Assert.All(issues, i => Assert.Contains("Zeile", i.Message));
        Assert.Equal(before, serializer.Serialize(project));
        var step = (StepDefinition)project.Definition.Find("label")!; step.Disabled = true;
        Assert.Empty(CabProjectDiagnostics.Check(project.Definition));
    }

    [Theory]
    [InlineData("!!!", null)]
    [InlineData("", null)]
    [InlineData("Sg1BIDEN", "{")]
    [InlineData("Sg1BIDEN", "[]")]
    public void Invalid_embedded_file_or_saved_options_are_reported_as_configuration_errors(string data, string? layout)
    {
        var step = (StepDefinition)Project("J\rA 1\r").Definition.Find("label")!;
        step.Parameters[CabStoredLabel.DataParameter] = data;
        if (layout is not null) step.Parameters[CabStoredLabel.LayoutParameter] = layout;
        Assert.Contains(CabProjectDiagnostics.ForStep(step), i => i.IsError);
    }

    [Fact]
    public async Task Errors_prevent_release_draft_but_warnings_are_visible_and_do_not_block_release()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ReleaseService(new FolderReleaseStore(directory)) { RequireRuntimeSnapshot = false };
            var actor = new Actor("cab-test", UserRights.Develop | UserRights.Release);
            await Assert.ThrowsAsync<ReleaseRuleException>(() => service.CreateDraftAsync(Project(ErrorSource), "1.0", actor));
            var project = Project(WarningSource); var serializer = new ProjectSerializer(); var before = serializer.Serialize(project);
            var draft = await service.CreateDraftAsync(project, "1.0", actor);
            var checks = await service.ReviewAsync(draft, actor);
            Assert.Contains(checks, c => c.Name == "cab JScript" && c.Passed && c.Detail.Contains("Warnung", StringComparison.Ordinal));
            var reviewed = (await service.Store.ListAsync()).Single();
            var released = await service.ReleaseAsync(reviewed, actor, confirmed: true);
            var opened = await service.OpenAsync(released);
            Assert.Equal(((StepDefinition)project.Definition.Find("label")!).Parameters[CabStoredLabel.DataParameter],
                ((StepDefinition)opened.Project.Definition.Find("label")!).Parameters[CabStoredLabel.DataParameter]);
            Assert.Equal(before, serializer.Serialize(project));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Legacy_layout_conversion_is_private_and_newly_saved_templates_need_no_text_offset()
    {
        var legacy = new LabelLayout { Elements = [new() { Value = "ID-{Kennung}", Preview = "CUSTOM", ExampleValues = "Kennung=EXAMPLE" }] };
        var before = legacy.Save(); var bytes = CabLabelLayout.Render(legacy);
        var migrated = CabEditingSource.Ensure(legacy);
        Assert.NotNull(migrated.CabSource); Assert.Null(legacy.CabSource);
        Assert.Equal(before, legacy.Save()); Assert.Equal(bytes, CabLabelLayout.Render(migrated));
        Assert.Equal("CUSTOM", Assert.Single(migrated.Elements).Preview);
        Assert.Same(migrated, CabEditingSource.Ensure(migrated));
        var project = Project("J\rA 1\r"); var editor = new EditorViewModel(project);
        var properties = Assert.IsType<StepPropertiesViewModel>(editor.AllNodes().Single(n => n.Id == "label").InlineProperties);
        properties.Step.Parameters[LabelPrintingBlocks.CabTextOffsetParameter] = "12";
        properties.SaveCabLabelLayout(legacy);
        Assert.DoesNotContain(LabelPrintingBlocks.CabTextOffsetParameter, properties.Step.Parameters.Keys);
        var clone = new ProjectSerializer().Clone(project);
        var parameters = ((StepDefinition)clone.Definition.Find("label")!).Parameters;
        var saved = LabelLayout.Load(parameters[CabLabelLayout.Parameter]);
        Assert.NotNull(saved.CabSource); Assert.Equal(bytes, saved.CabSource.Data);
        Assert.Equal(bytes, Convert.FromBase64String(parameters[CabStoredLabel.DataParameter]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_download_names_use_the_codepage_at_the_actual_insertion_boundary(bool atEnd)
    {
        var editor = new CabSourceEditor("\u001bowindows-1252;\rJ\rA 1\r"u8.ToArray());
        var target = atEnd ? null : CabJobEditor.Items(editor).First(i => i.Command?.Syntax.Command == "J");
        var next = CabResourceEditor.Add(editor, "TMP", "ä", [0, 255, 123], target);
        var resource = Assert.Single(CabResourceEditor.Resources(next));
        Assert.Equal("ä", resource.Name); Assert.Equal(new byte[] { 0, 255, 123 }, CabResourceEditor.Read(next, resource));
        Assert.False(next.Validation.HasErrors, string.Join("; ", next.Validation.Diagnostics.Select(d => d.Message)));
    }

    [Theory]
    [InlineData(WarningSource, true)]
    [InlineData(ErrorSource, false)]
    public void Command_line_validation_reports_cab_diagnostics_with_correct_success_status(string source, bool valid)
    {
        var folder = Path.Combine(Path.GetTempPath(), "cab-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "project.tproj.json"); new ProjectSerializer().Save(Project(source), path);
            var report = new Testumgebung.Cli.Report();
            Assert.Equal(valid, Testumgebung.Cli.ProjectChecks.Check(path, report).Valid);
            Assert.Contains(report.Results, r => r.Name.StartsWith("cab JScript", StringComparison.Ordinal)
                && (r.Outcome == Testumgebung.Cli.CheckOutcome.Passed) == valid && r.Message!.Contains("Zeile", StringComparison.Ordinal));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Older_reviewed_packages_are_rechecked_but_already_released_packages_load_unchanged(bool released)
    {
        var folder = Path.Combine(Path.GetTempPath(), "cab-legacy-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Project(ErrorSource); var now = DateTimeOffset.UtcNow;
            var (bytes, _) = ReleasePackage.Build(project, "previous-author", now);
            var info = new ReleaseInfo(Guid.NewGuid().ToString("N"), project.Definition.Name, project.Definition.Version,
                released ? ReleaseState.Released : ReleaseState.Review, ReleasePackage.Sha256(bytes), bytes.Length, "previous-author", now,
                ReviewedBy: "previous-reviewer", ReviewedAt: now, ReleasedBy: released ? "previous-approver" : null,
                ReleasedAt: released ? now : null, ReviewReport: "Fixture eines zuvor gespeicherten Pakets.");
            var store = new FolderReleaseStore(folder);
            await store.CreateAsync(info, bytes, new(now, "fixture", "Legacy fixture", "Unverändertes früheres Paket", info.ReleaseId));
            var service = new ReleaseService(store);
            if (!released)
                await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReleaseAsync(info, new("new-approver", UserRights.Release), true));
            else
            {
                var loaded = await service.LoadReleasedAsync(project.Definition.Name);
                Assert.NotNull(loaded);
                Assert.Equal(((StepDefinition)project.Definition.Find("label")!).Parameters[CabStoredLabel.DataParameter],
                    ((StepDefinition)loaded.Value.Project.Definition.Find("label")!).Parameters[CabStoredLabel.DataParameter]);
            }
            Assert.True((await service.OpenAsync(info)).IsIntact);
            Assert.Equal(info.State, (await store.ListAsync()).Single().State);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    internal static async Task VerifyContractAsync()
    {
        var project = new CabProjectIntegrationTests();
        project.Legacy_layout_conversion_is_private_and_newly_saved_templates_need_no_text_offset();
        project.New_download_names_use_the_codepage_at_the_actual_insertion_boundary(false);
        project.New_download_names_use_the_codepage_at_the_actual_insertion_boundary(true);
        project.Command_line_validation_reports_cab_diagnostics_with_correct_success_status(WarningSource, true);
        project.Command_line_validation_reports_cab_diagnostics_with_correct_success_status(ErrorSource, false);
        await project.Errors_prevent_release_draft_but_warnings_are_visible_and_do_not_block_release();
        await project.Older_reviewed_packages_are_rechecked_but_already_released_packages_load_unchanged(false);
        await project.Older_reviewed_packages_are_rechecked_but_already_released_packages_load_unchanged(true);
        var print = new LabelPrintingTests();
        foreach (var framing in new[] { "ESC.", "ESC:", "<BASE64>" })
        {
            await print.Syntax_bound_placeholders_leave_every_download_byte_unchanged(framing);
            await print.Readable_binary_downloads_are_not_validated_as_fields_or_interpolated(framing);
        }
        await print.Replacement_does_not_move_inline_codepage_controls_or_reencode_unchanged_literal_bytes();
        await print.Rich_text_values_are_html_encoded_without_changing_original_markup_or_native_fields();
        await print.Stored_interface_codepage_is_used_without_changing_the_saved_document();
        await print.Undecodable_text_is_preserved_but_cannot_silently_hide_a_placeholder(false);
        await print.Undecodable_text_is_preserved_but_cannot_silently_hide_a_placeholder(true);
        await print.Rendered_gs1_application_identifier_values_are_checked_before_transport("01", "12", false);
        await print.Rendered_gs1_application_identifier_values_are_checked_before_transport("01", "09506000134376", true);
    }

    [Fact]
    public void Stored_generation_and_preview_choices_are_read_without_changing_print_bytes()
    {
        var bytes = "J\rA 1\rJ\rA 1\r"u8.ToArray();
        var step = (StepDefinition)Project(Encoding.ASCII.GetString(bytes)).Definition.Find("label")!;
        var layout = new LabelLayout
        {
            CabSource = new() { SetupCodePage = "ISO-8859-1" },
            CabSettings = new() { DefaultGeneration = CabPrinterGeneration.Ax, JobGenerations = new() { [1] = CabPrinterGeneration.X2 },
                Preview = new() { ExampleTime = new DateTime(2024, 2, 29) } },
        };
        step.Parameters[CabLabelLayout.Parameter] = layout.Save();
        var stored = CabStoredLabel.Read(step.Parameters);
        Assert.Equal("ISO-8859-1", stored.SetupCodePage);
        Assert.Equal(CabPrinterGeneration.Ax, stored.Settings!.GenerationForJob(0));
        Assert.Equal(CabPrinterGeneration.X2, stored.Settings.GenerationForJob(1));
        Assert.Equal(new DateTime(2024, 2, 29), stored.Settings.Preview!.ExampleTime);
        Assert.Equal(bytes, stored.Document.Write());
        step.Parameters[CabLabelLayout.Parameter] = "null";
        Assert.Equal(bytes, CabStoredLabel.Read(step.Parameters).Document.Write());
    }
}
