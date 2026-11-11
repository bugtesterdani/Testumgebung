/**
 * @file
 * @brief Ergänzt Gerätefabriken um Profile des geöffneten Projekts, ohne den globalen Katalog zu ändern.
 */

using Testumgebung.Instruments;

namespace Testumgebung.Persistence.Stations;

/// <summary>Resolve project profiles without modifying the global built-in catalog.</summary>
public static class ProjectInstruments
{
    public static CustomDeviceProfile? Profile(TestProject? project, string adapter)
        => project?.DeviceProfiles.FirstOrDefault(customDeviceProfile => customDeviceProfile.Adapter == adapter);

    public static InstrumentFamily? Family(TestProject? project, string adapter)
        => Profile(project, adapter) is { } customDeviceProfile
            ? new(adapter, customDeviceProfile.Name, [customDeviceProfile.Name], ExampleResource(customDeviceProfile.Interface), customDeviceProfile.Description)
            : InstrumentCatalog.Family(adapter);

    public static IInstrumentDriver Create(TestProject? project, DeviceConfig device)
    {
        if (Profile(project, device.Adapter) is not { } profile)
            return InstrumentCatalog.Create(device.Adapter, device.Model ?? "", device.Resource ?? "", device.Options, device.TimeoutMs);
        var errors = profile.Validate();
        if (errors.Count > 0) throw new InstrumentException(string.Join(" ", errors));
        var resource = device.Resource ?? "";
        if (ResourceError(profile.Interface, resource) is { } error) throw new InstrumentException(error);
        var options = new InstrumentOptions(device.Options);
        var timeout = TimeSpan.FromMilliseconds(device.TimeoutMs ?? 5000);
        var useSerial = profile.Interface switch
        {
            CustomDeviceInterface.Auto => resource.StartsWith("COM", StringComparison.OrdinalIgnoreCase),
            CustomDeviceInterface.RS232 or CustomDeviceInterface.RS485 or CustomDeviceInterface.USB => IsComPort(resource.Trim()),
            _ => false
        };
        var serialResource = profile.Interface == CustomDeviceInterface.Auto ? resource : resource.Trim();
        var session = useSerial
            ? new ScpiSession(resource, () => new SerialInstrumentIo(serialResource, options, profile.Interface == CustomDeviceInterface.RS485), timeout)
            : new ScpiSession(resource, () => profile.Interface == CustomDeviceInterface.VISA
                ? new VisaIo(resource.Trim())
                : InstrumentIo.Open(resource), timeout);
        return new CustomTextInstrument(profile, session);
    }

    private static string ExampleResource(CustomDeviceInterface connection) => connection switch
    {
        CustomDeviceInterface.RS232 or CustomDeviceInterface.RS485 => "COM1",
        CustomDeviceInterface.USB => "USB0::0x1234::0x5678::SERIAL::INSTR",
        CustomDeviceInterface.GPIB => "GPIB0::1::INSTR",
        CustomDeviceInterface.VISA => "ASRL1::INSTR",
        _ => "TCPIP::localhost::5025::SOCKET"
    };

    internal static string? ResourceError(CustomDeviceInterface connection, string resource)
    {
        var address = resource.Trim();
        var valid = connection switch
        {
            CustomDeviceInterface.Auto => true,
            CustomDeviceInterface.RS232 => IsComPort(address) || HasVisaPrefix(address, "ASRL"),
            CustomDeviceInterface.RS485 => IsComPort(address),
            CustomDeviceInterface.USB => IsComPort(address) || HasVisaPrefix(address, "USB"),
            CustomDeviceInterface.LAN => HasVisaPrefix(address, "TCPIP") ||
                (!address.Contains("::", StringComparison.Ordinal) && address.LastIndexOf(':') is var colon and > 0
                    && int.TryParse(address[(colon + 1)..], out var port) && port is > 0 and <= 65535),
            CustomDeviceInterface.GPIB => HasVisaPrefix(address, "GPIB"),
            CustomDeviceInterface.VISA => address.Length > 0,
            _ => false
        };
        return valid ? null : $"Ressource „{resource}“ passt nicht zur Schnittstelle {connection}. Beispiel: {ExampleResource(connection)}";
    }

    private static bool HasVisaPrefix(string resource, string prefix)
        => resource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && resource.Contains("::", StringComparison.Ordinal);

    private static bool IsComPort(string resource)
        => resource.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(resource.AsSpan(3), out var port) && port > 0;
}
