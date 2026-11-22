using System.Runtime.InteropServices;

namespace LogLens.App.Services;

/// <summary>The few Win32 calls WinUI has no API for.</summary>
internal static class NativeMethods
{
    /// <summary>Lets process <paramref name="processId"/> take the foreground (only the foreground process may grant this).</summary>
    public static void AllowForeground(int processId) => _ = AllowSetForegroundWindow(processId);

    /// <summary>Brings a window to the front; works when this process was allowed to (see <see cref="AllowForeground"/>).</summary>
    public static void BringToFront(IntPtr hwnd) => _ = SetForegroundWindow(hwnd);

    /// <summary>A plain error box, for failures before any window exists (a bad command line).</summary>
    public static void ShowError(string title, string message) => _ = MessageBoxW(IntPtr.Zero, message, title, 0x10 /* MB_ICONERROR */);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
