using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

public sealed partial class LabelPrinter
{
    private static void SendToWindows(LabelPrintRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var printer = OpenQueue(request.Destination);
        cancellationToken.ThrowIfCancellationRequested();
        var document = new DocumentInfo { Name = "Testumgebung – Etikett", DataType = "RAW" };
        RequireSpoolSuccess(StartDocPrinter(printer, 1, ref document) != 0, "Druckauftrag starten");
        try { WriteDocument(printer, request.Data, cancellationToken); }
        catch
        {
            // Ein unvollständiges RAW-Dokument soll nicht in der Warteschlange verbleiben.
            AbortPrinter(printer);
            throw;
        }
    }

    private static PrinterHandle OpenQueue(string destination)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows-Druckwarteschlangen benötigen Windows.");
        RequireSpoolSuccess(OpenPrinter(destination, out var printer, IntPtr.Zero), "Druckwarteschlange öffnen");
        return printer;
    }

    private static void WriteDocument(PrinterHandle printer, byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireSpoolSuccess(WritePrinter(printer, data, data.Length, out var written), "Druckdaten schreiben");
        if (written != data.Length) throw new IOException($"Nur {written} von {data.Length} Bytes an die Druckwarteschlange übergeben.");
        cancellationToken.ThrowIfCancellationRequested();
        RequireSpoolSuccess(EndDocPrinter(printer), "Druckauftrag abschließen");
    }

    private static void RequireSpoolSuccess(bool succeeded, string operation)
    {
        if (succeeded) return;
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        throw new IOException($"{operation}: {error.Message}", error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocumentInfo
    {
        public string Name;
        public string? OutputFile;
        public string DataType;
    }

    private sealed class PrinterHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out PrinterHandle handle, IntPtr defaults);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint StartDocPrinter(PrinterHandle printer, uint level, ref DocumentInfo document);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrinter(PrinterHandle printer, byte[] data, int count, out int written);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDocPrinter(PrinterHandle printer);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AbortPrinter(PrinterHandle printer);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr printer);
}
