using Testumgebung.Instruments;

namespace Testumgebung.Persistence.Stations;

/// <summary>Resolve project profiles without modifying the global built-in catalog.</summary>
public static class ProjectInstruments
{
    public static CustomDeviceProfile? Profile(TestProject? project, string adapter)
        => project?.DeviceProfiles.FirstOrDefault(p => p.Adapter == adapter);

    public static InstrumentFamily? Family(TestProject? project, string adapter)
        => Profile(project, adapter) is { } p
            ? new(adapter, p.Name, [p.Name], "TCPIP::localhost::5025::SOCKET", p.Description)
            : InstrumentCatalog.Family(adapter);

    public static IInstrumentDriver Create(TestProject? project, DeviceConfig device)
    {
        if (Profile(project, device.Adapter) is not { } profile)
            return InstrumentCatalog.Create(device.Adapter, device.Model ?? "", device.Resource ?? "", device.Options, device.TimeoutMs);
        var errors = profile.Validate();
        if (errors.Count > 0) throw new InstrumentException(string.Join(" ", errors));
        var resource = device.Resource ?? "";
        var options = new InstrumentOptions(device.Options);
        var timeout = TimeSpan.FromMilliseconds(device.TimeoutMs ?? 5000);
        var session = resource.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            ? new ScpiSession(resource, () => new SerialInstrumentIo(resource, options), timeout)
            : new ScpiSession(resource, timeout);
        return new CustomTextInstrument(profile, session);
    }
}
