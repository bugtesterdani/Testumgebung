/**
 * @file
 * @brief Prüft Akteur, Rechte und Zustandsübergänge vor Erstellung und Freigabe von Releases.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Persistence.Releases;

/// <summary>Angemeldete, bestätigte Person mit ihren Rechten (nur diese Person handelt – A01).</summary>
public sealed record Actor(string User, UserRights Rights)
{
    public bool Can(UserRights right) => (Rights & right) == right;
}

public sealed class ReleaseRuleException(string message) : Exception(message);

/// <summary>Ergebnis der Entwicklungsprüfung (Validierung und Build der Codeartefakte).</summary>
public sealed record ReviewCheck(string Name, bool Passed, string Detail);

/// <summary>
/// Regeln von Abschnitt 10: Pakete sind unveränderlich; Entwurf und Entwicklungsprüfung erfordern „Entwickeln“,
/// die Freigabe erfordert „Freigeben“ und eine ausdrückliche Bestätigung. Beim Freigeben wird der Hash des gespeicherten
/// Pakets nochmals geprüft; die bisher freigegebene Version desselben Ablaufs wird archiviert. Alles wird auditiert.
/// </summary>
public sealed class ReleaseService(IReleaseStore store, IClock? clock = null, ProjectSerializer? serializer = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly ProjectSerializer _serializer = serializer ?? new ProjectSerializer();

    public IReleaseStore Store { get; } = store;
    public Testumgebung.Domain.Code.ICodeRuntime? CodeRuntime { get; set; }
    /// <summary>Produktiv aktivieren: alte Pakete ohne Laufzeitnachweis werden nicht gestartet.</summary>
    public bool RequireRuntimeSnapshot { get; set; }

    public async Task<IReadOnlyList<string>> VerifyEnvironmentAsync(ReleaseInfo info, CancellationToken ct = default)
    {
        var opened = await OpenAsync(info, ct).ConfigureAwait(false);
        if (!opened.IsIntact) return opened.Problems;
        if (opened.Environment is null)
            return RequireRuntimeSnapshot ? ["Paket enthält keinen Laufzeitnachweis. Neue Version erstellen, prüfen und freigeben."] : [];
        try
        {
            var current = await RuntimeSnapshot.CaptureAsync(opened.Project, CodeRuntime, ct).ConfigureAwait(false);
            return opened.Environment.Compare(current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ["Laufzeitumgebung konnte nicht bestätigt werden: " + ex.Message];
        }
    }

    /// <summary>Releases oder Audit wurden in dieser Anwendung geändert.</summary>
    public event Action? Changed;

    public Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken ct = default) => Store.ListAsync(ct);

    public async Task<ReleaseInfo?> CurrentReleasedAsync(string projectName, CancellationToken ct = default)
        => (await Store.ListAsync(ct).ConfigureAwait(false))
            .Where(r => r.ProjectName == projectName && r.State == ReleaseState.Released)
            .OrderByDescending(r => r.ReleasedAt)
            .FirstOrDefault();

    /// <summary>Paket laden und vollständig prüfen (Paket-Hash und alle Bereichshashes).</summary>
    public async Task<OpenedPackage> OpenAsync(ReleaseInfo info, CancellationToken ct = default)
    {
        var bytes = await Store.GetPackageAsync(info.ReleaseId, ct).ConfigureAwait(false);
        var opened = ReleasePackage.Open(bytes, _serializer);
        if (ReleasePackage.Sha256(bytes) != info.PackageSha256)
            return opened with { Problems = [.. opened.Problems, "Paket-Hash stimmt nicht mit dem Release überein – Paket verändert."] };
        return opened;
    }

    /// <summary>Freigegebene Version eines Ablaufs für den Werkerbetrieb (null = keine).</summary>
    public async Task<(ReleaseInfo Info, TestProject Project)?> LoadReleasedAsync(string projectName, CancellationToken ct = default)
    {
        if (await CurrentReleasedAsync(projectName, ct).ConfigureAwait(false) is not { } info) return null;
        var opened = await OpenAsync(info, ct).ConfigureAwait(false);
        if (!opened.IsIntact) throw new ReleasePackageException($"Freigegebene Version {info.Version} ist beschädigt: {string.Join("; ", opened.Problems)}");
        return (info, opened.Project);
    }

    public async Task<ReleaseInfo> CreateDraftAsync(TestProject project, string version, Actor actor, CancellationToken ct = default)
    {
        Require(actor, UserRights.Develop, "Einen Entwurf anlegen");
        version = version.Trim();
        if (version.Length == 0) throw new ReleaseRuleException("Version angeben.");
        var issues = project.Definition.Validate();
        if (issues.Count > 0) throw new ReleaseRuleException("Ablauf ist nicht gültig: " + string.Join("; ", issues));
        var existing = await Store.ListAsync(ct).ConfigureAwait(false);
        if (existing.Any(r => r.ProjectName == project.Definition.Name && r.Version == version))
            throw new ReleaseRuleException($"Version {version} existiert bereits – Pakete sind unveränderlich, bitte neue Version vergeben.");

        var copy = _serializer.Clone(project);
        copy.Definition.Version = version;
        var now = _clock.Now;
        var environment = RequireRuntimeSnapshot ? await RuntimeSnapshot.CaptureAsync(copy, CodeRuntime, ct).ConfigureAwait(false) : null;
        var (bytes, manifest) = ReleasePackage.Build(copy, actor.User, now, _serializer, environment);
        var info = new ReleaseInfo(Guid.NewGuid().ToString("N"), copy.Definition.Name, version, ReleaseState.Draft,
            ReleasePackage.Sha256(bytes), bytes.Length, actor.User, now);
        await Store.CreateAsync(info, bytes, new AuditDraft(now, actor.User, "Entwurf erstellt",
            $"{info.ProjectName} v{version} · Paket {info.ShortHash} · {manifest.Entries.Count} Bestandteile", info.ReleaseId), ct).ConfigureAwait(false);
        Changed?.Invoke();
        return info;
    }

    /// <summary>
    /// Entwicklungsprüfung: Paket öffnen und prüfen, Ablauf validieren, optional Codeartefakte bauen.
    /// Bestanden → Zustand „Entwicklungsprüfung abgeschlossen“ (bereit zur Freigabe).
    /// </summary>
    public async Task<IReadOnlyList<ReviewCheck>> ReviewAsync(ReleaseInfo info, Actor actor,
        Func<TestProject, CancellationToken, Task<IReadOnlyList<ReviewCheck>>>? buildCheck = null, CancellationToken ct = default)
    {
        Require(actor, UserRights.Develop, "Die Entwicklungsprüfung abschließen");
        if (info.State != ReleaseState.Draft) throw new ReleaseRuleException($"Nur Entwürfe können geprüft werden (Zustand: {info.StateText}).");
        var checks = new List<ReviewCheck>();
        var opened = await OpenAsync(info, ct).ConfigureAwait(false);
        checks.Add(new ReviewCheck("Paketintegrität", opened.IsIntact, opened.IsIntact ? $"Alle {opened.Manifest.Entries.Count} Hashes stimmen." : string.Join("; ", opened.Problems)));
        var issues = opened.Project.Definition.Validate();
        checks.Add(new ReviewCheck("Ablauf gültig", issues.Count == 0, issues.Count == 0 ? "Keine Befunde." : string.Join("; ", issues)));
        if (RequireRuntimeSnapshot || opened.Environment is not null)
        {
            var differences = await VerifyEnvironmentAsync(info, ct).ConfigureAwait(false);
            checks.Add(new("Laufzeitumgebung", differences.Count == 0, differences.Count == 0 ? "Versionen und Hashes stimmen." : string.Join("; ", differences)));
        }
        if (buildCheck is not null && opened.IsIntact) checks.AddRange(await buildCheck(opened.Project, ct).ConfigureAwait(false));

        var passed = checks.All(c => c.Passed);
        var report = string.Join("\n", checks.Select(c => $"{(c.Passed ? "OK" : "FEHLER")} {c.Name}: {c.Detail}"));
        var now = _clock.Now;
        if (passed)
        {
            if (!await Store.TransitionAsync(new ReleaseTransition(info.ReleaseId, ReleaseState.Draft, ReleaseState.Review, actor.User, now, report),
                    new AuditDraft(now, actor.User, "Entwicklungsprüfung abgeschlossen", $"Paket v{info.Version} geprüft (Ergebnisse OK)", info.ReleaseId), ct).ConfigureAwait(false))
                throw new ReleaseRuleException("Release wurde inzwischen geändert – bitte neu laden.");
        }
        else
        {
            await Store.AppendAuditAsync(new AuditDraft(now, actor.User, "Entwicklungsprüfung nicht bestanden",
                $"Paket v{info.Version}: {string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name))}", info.ReleaseId), ct).ConfigureAwait(false);
        }
        Changed?.Invoke();
        return checks;
    }

    /// <summary>Ausdrückliche Freigabe (eigenes Recht, Bestätigung erforderlich).</summary>
    public async Task<ReleaseInfo> ReleaseAsync(ReleaseInfo info, Actor actor, bool confirmed, CancellationToken ct = default)
    {
        Require(actor, UserRights.Release, "Freigeben");
        if (!confirmed) throw new ReleaseRuleException("Freigabe muss ausdrücklich bestätigt werden.");
        if (info.State != ReleaseState.Review) throw new ReleaseRuleException($"Nur geprüfte Pakete können freigegeben werden (Zustand: {info.StateText}).");
        var opened = await OpenAsync(info, ct).ConfigureAwait(false);
        if (!opened.IsIntact) throw new ReleaseRuleException("Paket ist nicht unversehrt: " + string.Join("; ", opened.Problems));
        if (RequireRuntimeSnapshot || opened.Environment is not null)
        {
            var differences = await VerifyEnvironmentAsync(info, ct).ConfigureAwait(false);
            if (differences.Count > 0) throw new ReleaseRuleException(string.Join("; ", differences));
        }

        var now = _clock.Now;
        var previous = await CurrentReleasedAsync(info.ProjectName, ct).ConfigureAwait(false);
        var details = $"{info.ProjectName} v{info.Version} freigegeben · Paket {info.ShortHash}" + (previous is null ? "" : $" · v{previous.Version} archiviert");
        if (!await Store.TransitionAsync(new ReleaseTransition(info.ReleaseId, ReleaseState.Review, ReleaseState.Released, actor.User, now, ArchiveOtherReleased: true),
                new AuditDraft(now, actor.User, "Version freigegeben", details, info.ReleaseId), ct).ConfigureAwait(false))
            throw new ReleaseRuleException("Release wurde inzwischen geändert – bitte neu laden.");
        Changed?.Invoke();
        return FolderReleaseStore.Apply(info, new ReleaseTransition(info.ReleaseId, info.State, ReleaseState.Released, actor.User, now));
    }

    public async Task AuditAsync(string user, string action, string details, string? releaseId = null, CancellationToken ct = default)
    {
        await Store.AppendAuditAsync(new AuditDraft(_clock.Now, user, action, details, releaseId), ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private static void Require(Actor actor, UserRights right, string what)
    {
        if (string.IsNullOrWhiteSpace(actor.User)) throw new ReleaseRuleException($"{what}: Anmeldung erforderlich.");
        if (!actor.Can(right))
            throw new ReleaseRuleException($"{what}: Recht „{(right == UserRights.Release ? "Freigeben" : "Entwickeln")}“ fehlt für {actor.User}.");
    }
}
