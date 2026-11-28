using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

internal static class WindowsSession
{
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    internal static IntPtr ForegroundWindow() { return GetForegroundWindow(); }

    internal static void KeepAbove(IntPtr window)
    {
        const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, NoSize | NoMove | NoActivate | NoOwnerZOrder);
    }

    internal static bool IsAdministrator()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool CanCaptureFrames()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator) ||
                principal.IsInRole(new SecurityIdentifier(WellKnownSidType.BuiltinPerformanceLoggingUsersSid, null));
        }
    }
}
