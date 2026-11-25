using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

/// <summary>
/// Bringing another program to the front from an island that never takes the focus. Windows only lets the
/// foreground process change the foreground window, so the island borrows the foreground thread for a moment;
/// a packaged program without a window of its own is started the way Windows starts it from its own toast.
/// </summary>
internal static class WindowsFocus
{
    private const int SwRestore = 9;
    private const uint VkMenu = 0x12, KeyUp = 0x0002;

    /// <summary>Shows a window and puts it in front. False when it cannot (a hidden window, a program that ignores it).</summary>
    public static bool Activate(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window)) return false;   // hidden: it lives in its tray
        if (IsIconic(window)) ShowWindowAsync(window, SwRestore);
        if (GetForegroundWindow() == window) return true;

        uint mine = GetCurrentThreadId();
        IntPtr foreground = GetForegroundWindow();
        uint theirs = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        bool attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            if (GetForegroundWindow() != window) SwitchToThisWindow(window, true);
            if (GetForegroundWindow() == window) return true;
            // a press of Alt is the old way to make the next SetForegroundWindow allowed
            keybd_event((byte)VkMenu, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VkMenu, 0, KeyUp, UIntPtr.Zero);
            SetForegroundWindow(window);
            return GetForegroundWindow() == window;
        }
        finally { if (attached) AttachThreadInput(mine, theirs, false); }
    }

    /// <summary>A packaged program by its AppUserModelId ("Publisher!AppId") — what a Windows toast does when clicked.</summary>
    public static bool ActivateApp(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid) || !aumid.Contains('!')) return false;
        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            return manager.ActivateApplication(aumid, null, 0, out _) >= 0;
        }
        catch (Exception ex) { Log.Write("focus: " + aumid + ": " + ex.GetType().Name + ": " + ex.Message); return false; }
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachTo, uint attach, bool attachUn);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
