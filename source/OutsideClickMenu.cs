using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class OutsideClickMenu : ContextMenuStrip
{
    private const int LowLevelMouseHook = 14;
    private const int LeftButtonDown = 0x0201;
    private const int RightButtonDown = 0x0204;
    private const int MiddleButtonDown = 0x0207;
    private const int XButtonDown = 0x020b;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetWindowsHookExW(int type, MouseHook callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    private readonly MouseHook onMouse;
    private IntPtr hook;
    private int openingNumber;

    public OutsideClickMenu()
    {
        onMouse = ObserveMouse;
    }

    protected override void OnOpened(EventArgs e)
    {
        ++openingNumber;
        if (hook == IntPtr.Zero)
            hook = SetWindowsHookExW(LowLevelMouseHook, onMouse, GetModuleHandleW(null), 0);
        base.OnOpened(e);
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        RemoveMouseHook();
        base.OnClosed(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveMouseHook();
        base.OnHandleDestroyed(e);
    }

    private void RemoveMouseHook()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr ObserveMouse(int code, IntPtr message, IntPtr data)
    {
        if (code == 0 && data != IntPtr.Zero)
        {
            // MSLLHOOKSTRUCT starts with the pointer position in screen coordinates.
            var point = new Point(Marshal.ReadInt32(data), Marshal.ReadInt32(data, sizeof(int)));
            DismissOnOutsideClick(message.ToInt32(), point);
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void DismissOnOutsideClick(int message, Point point)
    {
        if (message != LeftButtonDown && message != RightButtonDown && message != MiddleButtonDown && message != XButtonDown)
            return;
        if (!IsHandleCreated || Disposing || !Visible || ContainsPoint(this, point)) return;

        int shown = openingNumber;
        BeginInvoke((Action)(() =>
        {
            if (Visible && shown == openingNumber) Close();
        }));
    }

    private static bool ContainsPoint(ToolStripDropDown menu, Point point)
    {
        if (!menu.Visible) return false;
        if (menu.Bounds.Contains(point)) return true;
        foreach (ToolStripItem item in menu.Items)
        {
            var branch = item as ToolStripDropDownItem;
            if (branch != null && branch.HasDropDownItems && ContainsPoint(branch.DropDown, point)) return true;
        }
        return false;
    }
}
