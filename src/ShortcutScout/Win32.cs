using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ShortcutScout;

public static class Win32
{
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int GWL_EXSTYLE = -20;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    public const int WM_HOTKEY = 0x0312;
    public const int HOTKEY_ID = 1;
    public const int HOTKEY_SETTINGS_ID = 2;
    public const uint MOD_ALT = 0x0001, MOD_CTRL = 0x0002, MOD_NOREPEAT = 0x4000;
    public const uint VK_S = 0x53;
    public const uint VK_O = 0x4F;

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(out LASTINPUTINFO p);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr val);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr insert, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    /// <summary>Adds click-through + always-on-top + no-activate styles without stealing focus.</summary>
    public static void MakeClickThroughAlwaysOnTop(System.Windows.Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var current = ExStyle(hwnd);
        var wanted = current | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)wanted);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static long ExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

    /// <summary>
    /// Seconds since the last keyboard/mouse input. Uses unsigned 32-bit tick
    /// arithmetic because LASTINPUTINFO.dwTime is a 32-bit GetTickCount value that
    /// wraps every ~49.7 days; mixing it with the 64-bit tick count would report
    /// bogus idle time after a long uptime.
    /// </summary>
    public static int IdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(out lii)) return 0;
        var now = unchecked((uint)Environment.TickCount64);
        var elapsedMs = unchecked(now - lii.dwTime);
        return (int)(elapsedMs / 1000u);
    }

    public static (int x, int y) CursorPos()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }
}
