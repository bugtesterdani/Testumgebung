/**
 * @file
 * @brief Passt die Oberfläche an Fensterfläche und den aktuellen Fenstermonitor an.
 */
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Testumgebung.Desktop;

/// <summary>Scales the complete layout in DIPs; Windows applies each monitor's DPI independently.</summary>
internal sealed class AdaptiveWindowLayout
{
    private readonly Window _window;
    private readonly ScrollViewer _viewport;
    private readonly FrameworkElement _content;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Func<Window, Size> _workArea;
    private HwndSource? _source;
    private bool _updating, _closed, _queued;

    public AdaptiveWindowLayout(Window window, ScrollViewer viewport, FrameworkElement content, Func<Window, Size>? workArea = null)
    {
        _window = window; _viewport = viewport; _content = content; _workArea = workArea ?? CurrentWorkArea;
        content.LayoutTransform = _scale;
        window.SourceInitialized += Initialized;
        window.LocationChanged += Changed;
        viewport.SizeChanged += Changed;
        window.Closed += Closed;
    }
    private void Initialized(object? sender, EventArgs args)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(Message);
        Update();
    }
    private void Changed(object? sender, EventArgs args) => Update();
    private void Update()
    {
        if (_closed || _updating || _source is null || _window.WindowState == WindowState.Minimized) return;
        _updating = true;
        try
        {
            var work = _workArea(_window);
            _window.MinWidth = Math.Min(1000, work.Width);
            _window.MinHeight = Math.Min(620, work.Height);
            if (_window.WindowState == WindowState.Normal)
            {
                _window.Width = Math.Min(_window.Width, work.Width);
                _window.Height = Math.Min(_window.Height, work.Height);
            }
            var scale = Calculate(new(_viewport.ActualWidth, _viewport.ActualHeight), work);
            _scale.ScaleX = scale; _scale.ScaleY = scale;
            UpdateOverflow(scale);
        }
        finally { _updating = false; }
    }
    private void UpdateOverflow(double scale)
    {
        var scroll = _viewport;
        var overflow = scale == 0.65;
        // Preserve a readable lower bound; very small work areas get scrollbars
        // instead of controls disappearing outside the window.
        _content.MinWidth = overflow ? 1500 : 0;
        _content.MinHeight = overflow ? 850 : 0;
        scroll.HorizontalScrollBarVisibility = overflow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        scroll.VerticalScrollBarVisibility = overflow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
    /// <summary>A spacious monitor keeps normal text size; smaller viewports compact all layout dimensions together.</summary>
    internal static double Calculate(Size viewport, Size work)
    {
        var width = Math.Min(viewport.Width, work.Width);
        var height = Math.Min(viewport.Height, work.Height);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return 1;
        return Math.Clamp(Math.Min(width / 1500, height / 850), 0.65, 1);
    }
    private IntPtr Message(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Let WPF process DPI changes first, then use its new client dimensions.
        if (message is 0x02E0 or 0x007E or 0x001A) QueueUpdate();
        return IntPtr.Zero;
    }
    private void QueueUpdate()
    {
        if (_queued || _closed) return;
        _queued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _queued = false; Update(); }));
    }
    private void Closed(object? sender, EventArgs args)
    {
        _closed = true;
        _source?.RemoveHook(Message);
        _window.SourceInitialized -= Initialized;
        _window.LocationChanged -= Changed;
        _viewport.SizeChanged -= Changed;
        _window.Closed -= Closed;
    }
    /// <summary>Selects the monitor containing most of this window, never the primary-monitor defaults.</summary>
    internal static Size CurrentWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return new(window.Width, window.Height);
        var dpi = GetDpiForWindow(handle);
        return WorkAreaInDips(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, dpi);
    }
    internal static Size WorkAreaInDips(int width, int height, uint dpi)
        => new(width * 96d / Math.Max(96u, dpi), height * 96d / Math.Max(96u, dpi));
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
