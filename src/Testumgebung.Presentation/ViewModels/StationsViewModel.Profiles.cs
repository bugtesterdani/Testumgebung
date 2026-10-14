/**
 * @file
 * @brief Ergänzt die Prüfplatzkonfiguration um projektbezogene, benutzerdefinierte Geräteprofile.
 */

using System.Collections.ObjectModel;
using Testumgebung.Instruments;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationsViewModel
{
    public ObservableCollection<CustomDeviceProfile> DeviceProfiles { get; } = [];
    private CustomDeviceProfile? _selectedProfile;
    public CustomDeviceProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            ProfileChannels.Clear();
            foreach (var channel in value?.Channels ?? []) ProfileChannels.Add(channel);
            OnPropertyChanged(nameof(HasSelectedProfile));
        }
    }
    public bool HasSelectedProfile => SelectedProfile is not null;
    public ObservableCollection<CustomDeviceChannel> ProfileChannels { get; } = [];
    public CustomDeviceChannel? SelectedProfileChannel { get; set; }
    public RelayCommand AddProfileCommand { get; private set; } = null!;
    public RelayCommand RemoveProfileCommand { get; private set; } = null!;
    public RelayCommand ApplyProfilesCommand { get; private set; } = null!;
    public RelayCommand AddProfileChannelCommand { get; private set; } = null!;
    public RelayCommand RemoveProfileChannelCommand { get; private set; } = null!;
    public RelayCommand AddProfileDeviceCommand { get; private set; } = null!;
    public RelayCommand ImportProfileCommand { get; private set; } = null!;
    public RelayCommand ExportProfileCommand { get; private set; } = null!;

    private void InitProfileCommands()
    {
        AddProfileCommand = new RelayCommand(AddProfile);
        RemoveProfileCommand = new RelayCommand(RemoveProfile);
        ApplyProfilesCommand = new RelayCommand(() => ApplyProfiles());
        AddProfileChannelCommand = new RelayCommand(AddProfileChannel);
        RemoveProfileChannelCommand = new RelayCommand(RemoveProfileChannel);
        AddProfileDeviceCommand = new RelayCommand(AddProfileDevice);
        ImportProfileCommand = new RelayCommand(() =>
        {
            if (_editor.Dialogs?.OpenFile("Geräteprofil importieren", "Geräteprofil|*.tdevice.json|JSON|*.json") is { } path) ImportProfile(path);
        });
        ExportProfileCommand = new RelayCommand(() =>
        {
            if (_editor.Dialogs?.SaveFile("Geräteprofil exportieren", "Geräteprofil|*.tdevice.json", "Geraeteprofil.tdevice.json") is { } path) ExportProfile(path);
        });
    }

    public void ImportProfile(string path)
    {
        try
        {
            var profile = System.Text.Json.JsonSerializer.Deserialize<CustomDeviceProfile>(File.ReadAllText(path));
            if (profile is null) throw new FormatException("Geräteprofil fehlt.");
            var errors = profile.Validate();
            if (errors.Count > 0) throw new FormatException(string.Join(" ", errors));
            // Preserve cross-project hardware assignments, but never overwrite an existing profile.
            if (DeviceProfiles.Any(p => p.Id == profile.Id)) profile.Id = "import-" + Guid.NewGuid().ToString("N");
            DeviceProfiles.Add(profile);
            SelectedProfile = profile;
            Error = null;
            Status = "Geräteprofil importiert. Prüfen und ins Projekt übernehmen.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        { Error = "Geräteprofil nicht importiert: " + ex.Message; }
    }

    public void ExportProfile(string path)
    {
        if (SelectedProfile is not { } profile) return;
        try
        {
            var errors = profile.Validate();
            if (errors.Count > 0) throw new FormatException(string.Join(" ", errors));
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(profile, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Error = null;
            Status = "Geräteprofil exportiert.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        { Error = "Geräteprofil nicht exportiert: " + ex.Message; }
    }

    private void ReloadProfiles()
    {
        DeviceProfiles.Clear();
        foreach (var profile in Project.DeviceProfiles) DeviceProfiles.Add(profile.Clone());
        SelectedProfile = DeviceProfiles.FirstOrDefault();
        _channelCache.Clear();
        OnPropertyChanged(nameof(AdapterChoices));
    }

    private void AddProfile()
    {
        var n = 1;
        while (DeviceProfiles.Any(p => p.Id == $"eigenes-{n}")) n++;
        var profile = new CustomDeviceProfile { Id = $"eigenes-{n}", Name = $"Eigenes Gerät {n}" };
        DeviceProfiles.Add(profile);
        SelectedProfile = profile;
    }

    private void RemoveProfile()
    {
        if (SelectedProfile is not { } profile) return;
        if (_draft.Devices.Any(d => d.Adapter == profile.Adapter))
        {
            Error = "Das Geräteprofil wird noch von einem Gerät verwendet. Zuerst dessen Adapter ändern oder das Gerät entfernen.";
            return;
        }
        DeviceProfiles.Remove(profile);
        SelectedProfile = DeviceProfiles.FirstOrDefault();
    }

    private void AddProfileChannel()
    {
        if (SelectedProfile is not { } profile) return;
        var n = 1;
        while (profile.Channels.Any(c => c.Name == $"Kanal{n}")) n++;
        var channel = new CustomDeviceChannel { Name = $"Kanal{n}" };
        profile.Channels.Add(channel);
        ProfileChannels.Add(channel);
    }

    private void RemoveProfileChannel()
    {
        if (SelectedProfileChannel is not { } channel || SelectedProfile is null) return;
        SelectedProfile.Channels.Remove(channel);
        ProfileChannels.Remove(channel);
        SelectedProfileChannel = null;
    }

    public bool ApplyProfiles()
    {
        if (_anyRunning()) { Error = "Geräteprofile können erst nach Ende aller Prüfungen übernommen werden."; return false; }
        var errors = DeviceProfiles.SelectMany(p => p.Validate()).ToList();
        if (errors.Count > 0) { Error = string.Join(" ", errors); return false; }
        Project.DeviceProfiles.Clear();
        Project.DeviceProfiles.AddRange(DeviceProfiles.Select(p => p.Clone()));
        _coordinator.Pool.Reset();
        _channelCache.Clear();
        _editor.MarkChanged();
        OnPropertyChanged(nameof(AdapterChoices));
        foreach (var device in Devices) device.RaiseAllDevice();
        Edited();
        Error = null;
        Status = "Geräteprofile übernommen. Projektdatei speichern; die Gerätezuordnung separat speichern.";
        return true;
    }

    private void AddProfileDevice()
    {
        if (SelectedProfile is not { } profile || !ApplyProfiles()) return;
        AddDevice();
        var row = Devices.Last();
        row.Name = profile.Name;
        row.Adapter = profile.Adapter;
        SelectedDevice = row;
    }
}
