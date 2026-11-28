using System;
using System.Runtime.InteropServices;

internal sealed class CpuUsage
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    private ulong previousIdle, previousKernel, previousUser;
    private bool sampled;

    internal double? Read()
    {
        ulong idle, kernel, user;
        if (!GetSystemTimes(out idle, out kernel, out user)) return null;

        double? load = null;
        if (sampled && idle >= previousIdle && kernel >= previousKernel && user >= previousUser)
        {
            double total = (double)(kernel - previousKernel) + (user - previousUser);
            if (total > 0)
            {
                double busy = total - (idle - previousIdle); // Kernel time includes idle time.
                load = Math.Max(0, Math.Min(100, busy * 100 / total));
            }
        }
        previousIdle = idle;
        previousKernel = kernel;
        previousUser = user;
        sampled = true;
        return load;
    }
}
