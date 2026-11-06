using System.Runtime.InteropServices;
using System.Text;

namespace Testumgebung.Instruments;

/// <summary>Own ABI declarations for the externally installed Windows Universal Library. No SDK binaries are redistributed.</summary>
public sealed class NativeUlApi : IUlApi
{
    public static NativeUlApi Instance { get; } = new();
    private static readonly object Gate = new();
    private static readonly HashSet<int> Boards = [];
    private static nint _library;
    private static void Ensure()
    {
        lock (Gate)
        {
            if (_library != 0) return;
            _library = LoadLibrary();
        }
    }
    private static nint LoadLibrary()
    {
        if (NativeLibrary.TryLoad("cbw64.dll", typeof(NativeUlApi).Assembly, DllImportSearchPath.SafeDirectories, out var handle)) return handle;
        var folders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        var file = folders.Select(folder => Path.Combine(folder, "Measurement Computing", "DAQ", "cbw64.dll")).FirstOrDefault(File.Exists)
            ?? throw new DllNotFoundException("cbw64.dll: Universal Library/InstaCal installieren.");
        return NativeLibrary.Load(file);
    }
    public IReadOnlyList<UlDevice> Inventory(string connection)
    {
        Ensure(); var inventory = new Descriptor[256]; var count = inventory.Length;
        Check(Native.Inventory(connection == "USB" ? 1 : 4, inventory, ref count));
        if (count < 0 || count > inventory.Length) throw new InstrumentException("UL meldet eine ungültige Geräteliste.");
        return inventory.Take(count).Select(ToDevice).ToArray();
    }
    public UlDevice NetworkDevice(string host, int port, int timeoutMs)
    {
        CheckRange(port, 65535, "Port");
        CheckRange(timeoutMs, 120000, "Erkennungszeit [ms]");
        Ensure(); Check(Native.NetworkDevice(host, port, out var descriptor, timeoutMs)); return ToDevice(descriptor);
    }
    private static void CheckRange(int value, int maximum, string name)
    {
        if (value < 1 || value > maximum) throw new InstrumentException($"UL-{name}: ganze Zahl von 1 bis {maximum} erforderlich.");
    }
    private static UlDevice ToDevice(Descriptor descriptor)
        => new(descriptor.Product, descriptor.Identifier, descriptor.Interface == 1 ? "USB" : descriptor.Interface == 4 ? "Ethernet" : "Nicht unterstützt", descriptor);
    public int Open(UlDevice device)
    {
        Ensure();
        if (device.NativeDescriptor is not Descriptor descriptor) throw new InstrumentException("UL-Gerätebeschreibung fehlt.");
        lock (Gate)
        {
            var board = Enumerable.Range(0, 100).FirstOrDefault(IsFree, -1);
            if (board < 0) throw new InstrumentException("Keine freie UL-Boardnummer; vorhandene InstaCal-Geräte werden nicht überschrieben.");
            Check(Native.Create(board, descriptor)); Boards.Add(board); return board;
        }
    }
    private static bool IsFree(int board)
    {
        if (Boards.Contains(board)) return false;
        var error = Native.BoardName(board, new StringBuilder(256));
        if (error == 1) return true; Check(error); return false;
    }
    public void Close(int board) { lock (Gate) { if (Boards.Remove(board)) Check(Native.Release(board)); } }
    public int Config(int board, int type, int device, int item)
    {
        Check(Native.Config(type, board, device, item, out var value)); return value;
    }
    public double AnalogIn(int board, int channel, int range) { Check(Native.AnalogIn(board, channel, range, out var value, 0)); return value; }
    public double TemperatureIn(int board, int channel) { Check(Native.TemperatureIn(board, channel, 0, out var value, 0)); return value; }
    public void AnalogOut(int board, int channel, int range, double value) => Check(Native.AnalogOut(board, channel, range, (float)value, 0));
    public int DigitalIn(int board, int port, int bit) { Check(Native.DigitalIn(board, port, bit, out var value)); return value; }
    public void DigitalOut(int board, int port, int bit, int value) => Check(Native.DigitalOut(board, port, bit, (ushort)value));
    public void ConfigureBit(int board, int port, int bit, bool output) => Check(Native.ConfigureBit(board, port, bit, output ? 1 : 2));
    public void ConfigurePort(int board, int port, bool output) => Check(Native.ConfigurePort(board, port, output ? 1 : 2));
    private static void Check(int error)
    {
        if (error == 0) return;
        var message = new StringBuilder(256); Native.Error(error, message);
        var exception = new InstrumentException($"Universal Library ({error}): {message}"); exception.Data["ULCode"] = error; throw exception;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
    private struct Descriptor
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Product;
        public uint ProductId;
        public uint Interface;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Identifier;
        public ulong NumericId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=512)] public string Reserved;
    }
    private static class Native
    {
        private const string Library = "cbw64.dll";
        [DllImport(Library, EntryPoint="cbGetDaqDeviceInventory"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int Inventory(int interfaces, [Out] Descriptor[] inventory, ref int count);
        [DllImport(Library, EntryPoint="cbGetNetDeviceDescriptor", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int NetworkDevice(string host, int port, out Descriptor descriptor, int timeout);
        [DllImport(Library, EntryPoint="cbCreateDaqDevice"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int Create(int board, Descriptor descriptor);
        [DllImport(Library, EntryPoint="cbReleaseDaqDevice"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int Release(int board);
        [DllImport(Library, EntryPoint="cbGetBoardName", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int BoardName(int board, StringBuilder name);
        [DllImport(Library, EntryPoint="cbGetConfig"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int Config(int type, int board, int device, int item, out int value);
        [DllImport(Library, EntryPoint="cbVIn32"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int AnalogIn(int board, int channel, int range, out double value, int options);
        [DllImport(Library, EntryPoint="cbTIn"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int TemperatureIn(int board, int channel, int scale, out float value, int options);
        [DllImport(Library, EntryPoint="cbVOut"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int AnalogOut(int board, int channel, int range, float value, int options);
        [DllImport(Library, EntryPoint="cbDBitIn"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int DigitalIn(int board, int port, int bit, out ushort value);
        [DllImport(Library, EntryPoint="cbDBitOut"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int DigitalOut(int board, int port, int bit, ushort value);
        [DllImport(Library, EntryPoint="cbDConfigBit"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int ConfigureBit(int board, int port, int bit, int direction);
        [DllImport(Library, EntryPoint="cbDConfigPort"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int ConfigurePort(int board, int port, int direction);
        [DllImport(Library, EntryPoint="cbGetErrMsg", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int Error(int error, StringBuilder text);
    }
}
