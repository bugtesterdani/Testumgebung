using Testumgebung.Persistence.Programs;
using Testumgebung.Presentation.Releases;

namespace Testumgebung.Presentation.ViewModels;

public sealed record SourceLoginChoice(SourceLoginMode Mode, string Title);

public sealed partial class ProgramSourcesViewModel
{
    private SourceCredentialStore _authentication = new();
    private SourceLoginMode _loginMode;
    private bool _waitingForLogin;
    public SourceLoginMode LoginMode { get => _loginMode; set { if (SetProperty(ref _loginMode, value)) OnPropertyChanged(nameof(LoginNeedsSecret)); } }
    public bool LoginNeedsSecret => LoginMode is SourceLoginMode.GitAccessToken or SourceLoginMode.GitDeployToken or SourceLoginMode.SvnPassword;
    public string LoginUsername { get; set; } = "";
    public bool RememberLogin { get; set; }
    public string LoginStatus { get; private set; } = "Vorhandene Client-Anmeldung";
    public Func<Task<bool>>? RequestAuthentication { get; set; }
    public IReadOnlyList<SourceLoginChoice> LoginChoices => Selected?.Provider == "SVN"
        ? [new(SourceLoginMode.External, "Vorhandene Client-Anmeldung"), new(SourceLoginMode.SvnWindows, "Angemeldeter Windows-Benutzer (HTTPS/Negotiate)"), new(SourceLoginMode.SvnPassword, "SVN-Benutzername und Passwort (HTTPS)")]
        : [new(SourceLoginMode.External, "Vorhandene Client-Anmeldung / SSH"), new(SourceLoginMode.GitAccessToken, "Git Access Token (HTTPS)"), new(SourceLoginMode.GitDeployToken, "Git Deploy Token (HTTPS)")];
    public void ConfigureAuthentication(SourceCredentialStore store)
    {
        _authentication = store;
        ApplySourceClients();
        LoadAuthentication();
    }
    private void ApplySourceClients()
        => _hub.Service.SourceProviders = new ProgramSourceClientSettings(GitClient.Trim(), SvnClient.Trim()).Providers(_authentication.Get);
    private void LoadAuthentication()
    {
        try
        {
            var credentials = Selected is null ? null : _authentication.Get(Selected);
            LoginMode = credentials?.Mode ?? SourceLoginMode.External; LoginUsername = credentials?.Username ?? "";
            RememberLogin = Selected is not null && _authentication.IsRemembered(Selected);
            LoginStatus = LoginChoices.FirstOrDefault(c => c.Mode == LoginMode)?.Title ?? "Zugangsmethode prüfen";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { LoginStatus = ex.Message; }
        OnPropertiesChanged(nameof(LoginChoices), nameof(LoginMode), nameof(LoginUsername), nameof(RememberLogin), nameof(LoginStatus));
    }
    public bool SaveAuthentication(string secret)
    {
        try
        {
            Require();
            if (_busy && !_waitingForLogin) throw new InvalidOperationException("Laufenden Abruf zuerst beenden.");
            var source = Selected ?? throw new InvalidOperationException("Zuerst eine Programmquelle auswählen und speichern.");
            var old = secret.Length == 0 && LoginNeedsSecret ? _authentication.Get(source) : null;
            if (secret.Length == 0 && old?.Mode == LoginMode && old.Username == LoginUsername) secret = old.Secret;
            _authentication.Save(source, new(LoginMode, LoginUsername.Trim(), secret), RememberLogin);
            LoadAuthentication(); Status = RememberLogin ? "Zugangsdaten lokal für dieses Windows-Konto geschützt gespeichert." : "Zugangsmethode nur für diese Sitzung übernommen.";
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Status = ex.Message; return false; }
    }
    public void RemoveAuthentication()
    {
        try { Require(); if (_busy) return; if (Selected is { } source) _authentication.Remove(source); LoadAuthentication(); Status = "Lokales Zugangsprofil entfernt. Vorhandene Client-Anmeldung wird verwendet."; }
        catch (Exception ex) when (ex is not OperationCanceledException) { Status = ex.Message; }
    }
    private CancellationTokenSource? _sourceOperation;
    public void CancelSourceOperation() => _sourceOperation?.Cancel();
    private async Task RunSourceAsync(Func<CancellationToken, Task> work)
    {
        using var cancellation = _sourceOperation = new CancellationTokenSource();
        try { await work(cancellation.Token); } finally { _sourceOperation = null; }
    }
    public Task TestConnectionAsync() => Execute(() => RunSourceAsync(async token =>
    {
        Require(); var source = Selected ?? throw new InvalidOperationException("Quelle auswählen und speichern.");
        using var snapshot = await _hub.Service.SourceProviders.Get(source.Provider).ExportAsync(source, Revision.Trim(), token);
        _ = new Testumgebung.Persistence.ProjectSerializer().Load(snapshot.ProjectFile);
        Status = $"Verbindung und Projektdatei geprüft: {source.Name}, Revision {snapshot.Version.Revision}. Keine Freigabe erstellt.";
    }));
}