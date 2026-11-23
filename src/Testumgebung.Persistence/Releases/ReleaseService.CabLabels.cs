using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Persistence.Releases;

public sealed partial class ReleaseService
{
    private static void AddCabReview(List<ReviewCheck> checks, TestDefinition definition)
    {
        var issues = CabProjectDiagnostics.Check(definition);
        if (issues.Count == 0) return;
        checks.Add(new("cab JScript", !issues.Any(i => i.IsError), string.Join(Environment.NewLine, issues)));
    }

    private static IEnumerable<string> CabReleaseErrors(TestDefinition definition)
        => CabProjectDiagnostics.Check(definition).Where(i => i.IsError).Select(i => i.ToString());

    private static void RequireValidCabLabels(TestDefinition definition)
    {
        var errors = CabReleaseErrors(definition).ToArray();
        if (errors.Length > 0) throw new ReleaseRuleException("cab JScript ist nicht gültig: " + string.Join("; ", errors));
    }
}
