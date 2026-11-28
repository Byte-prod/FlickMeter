using System.Runtime.InteropServices;

internal static class PhysicalMemoryUsage
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    internal const double BytesPerGibibyte = 1073741824.0;

    public static void Read(out double? percentage, out double? usedGiB)
    {
        percentage = usedGiB = null;
        MemoryStatus status = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 ||
            status.AvailablePhysical > status.TotalPhysical) return;
        ulong used = status.TotalPhysical - status.AvailablePhysical;
        percentage = 100.0 * used / status.TotalPhysical;
        usedGiB = used / BytesPerGibibyte;
    }
}
