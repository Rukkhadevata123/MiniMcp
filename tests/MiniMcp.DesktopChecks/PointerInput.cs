using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MiniMcp.DesktopChecks;

// Only used by the explicit interactive UI check; restore the user's pointer even on failure.
internal sealed class PointerInput : IDisposable
{
    private readonly ScreenPoint _original;
    private readonly OverlappedPresenter _presenter;
    private readonly bool _wasAlwaysOnTop;
    private bool _clicked;
    internal PointerInput(Window window)
    {
        if (!GetCursorPos(out _original)) throw new Exception("Cannot read pointer position");
        _presenter = (OverlappedPresenter)window.AppWindow.Presenter;
        _wasAlwaysOnTop = _presenter.IsAlwaysOnTop;
        _presenter.IsAlwaysOnTop = true;
        window.AppWindow.Show(); window.Activate();
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(window), 9);
    }

    internal void MoveTo(FrameworkElement element, FrameworkElement root, Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var point = element.TransformToVisual(root).TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var scale = root.XamlRoot.RasterizationScale;
        var screen = new ScreenPoint { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) };
        if (!ClientToScreen(handle, ref screen) || !SetCursorPos(screen.X, screen.Y))
            throw new Exception("Cannot move pointer to the UI check window");
        // WinUI needs an input packet as well as a cursor-position change to update hover state.
        mouse_event(1, 1, 0, 0, UIntPtr.Zero);
        if (!GetCursorPos(out var current) || GetAncestor(WindowFromPoint(current), 2) != handle)
            throw new Exception("Pointer did not hit the UI check window");
        if (!_clicked && element is TextBox)
        {
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); _clicked = true;
        }
    }

    public void Dispose() { _presenter.IsAlwaysOnTop = _wasAlwaysOnTop; SetCursorPos(_original.X, _original.Y); }
    internal void PressTab(Window window)
    {
        if (GetForegroundWindow() != WinRT.Interop.WindowNative.GetWindowHandle(window)) throw new Exception("Keyboard input requires the test window in the foreground");
        keybd_event(9, 0, 0, UIntPtr.Zero); keybd_event(9, 0, 2, UIntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
}
