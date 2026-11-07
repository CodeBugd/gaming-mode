using System.Runtime.InteropServices;

namespace GamingMode.App;

/// <summary>Which app you're using right now, and whether it covers the whole screen (= probably a game).</summary>
internal static class Foreground
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    public static (int Pid, bool Fullscreen) Get()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (0, false);
        GetWindowThreadProcessId(hwnd, out var pid);
        if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow() || !GetWindowRect(hwnd, out var r)) return ((int)pid, false);
        var screen = Screen.FromHandle(hwnd).Bounds;
        bool full = r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
        return ((int)pid, full);
    }
}
