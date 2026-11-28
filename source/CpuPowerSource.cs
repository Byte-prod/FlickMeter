using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal sealed class CpuPowerSource
{
    private const uint GetVersion = 2244608, GetMetadataSize = 2244612, GetMetadata = 2244616, GetMeasurement = 2244620;
    private const int MetadataNameBytes = 16 * sizeof(char);
    private const int SampleBytes = 2 * sizeof(ulong);
    private static readonly Guid EnergyMeterInterface = new Guid("45BD8344-7ED6-49cf-A440-C276C933B053");

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid guid, string device, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string device, [Out] byte[] list, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        [Out] byte[] output, int outputSize, out int bytes, IntPtr overlapped);

    private sealed class Meter
    {
        public readonly string Path;
        public readonly int[] Channels;
        public readonly byte[] Sample;
        public readonly ulong[] Energy, Time;

        public Meter(string path, int count, int[] channels)
        {
            Path = path;
            Channels = channels;
            Sample = new byte[count * SampleBytes];
            Energy = new ulong[channels.Length];
            Time = new ulong[channels.Length];
        }

        public double? Read()
        {
            using (SafeFileHandle handle = Open(Path))
                if (!Query(handle, GetMeasurement, Sample, Sample.Length)) return null;
            double total = 0;
            bool valid = true;
            for (int i = 0; i < Channels.Length; i++)
            {
                int offset = Channels[i] * SampleBytes;
                ulong energy = BitConverter.ToUInt64(Sample, offset);
                ulong time = BitConverter.ToUInt64(Sample, offset + sizeof(ulong));
                double? power = Watts(Energy[i], Time[i], energy, time);
                valid &= power.HasValue;
                total += power.GetValueOrDefault();
                Energy[i] = energy;
                Time[i] = time;
            }
            return valid ? GpuMetrics.ValidPower(total) : null;
        }
    }

    private readonly List<Meter> meters = new List<Meter>();
    private int nextDiscovery = Environment.TickCount;
    private bool retryDiscovery = true;
    internal volatile bool AccessDenied;

    internal double? Read()
    {
        int now = Environment.TickCount;
        if (retryDiscovery && unchecked(now - nextDiscovery) >= 0)
        {
            meters.Clear();
            Discover();
            nextDiscovery = unchecked(now + 30000);
        }
        double total = 0;
        bool valid = meters.Count > 0;
        foreach (Meter meter in meters)
        {
            double? power = meter.Read();
            valid &= power.HasValue;
            total += power.GetValueOrDefault();
        }
        retryDiscovery = !valid;
        if (valid) return GpuMetrics.ValidPower(total);
        return CpuTemperatureSource.ReadHwinfo(RankHwinfo, value => GpuMetrics.ValidPower(value).HasValue);
    }

    private void Discover()
    {
        AccessDenied = false;
        Guid guid = EnergyMeterInterface;
        uint size;
        if (CM_Get_Device_Interface_List_SizeW(out size, ref guid, null, 0) != 0 || size < 2 || size > 65536) return;
        var paths = new byte[size * sizeof(char)];
        if (CM_Get_Device_Interface_ListW(ref guid, null, paths, size, 0) != 0) return;

        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Encoding.Unicode.GetString(paths).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
        {
            using (SafeFileHandle handle = Open(path))
            {
                if (handle.IsInvalid)
                {
                    if (Marshal.GetLastWin32Error() == 5) AccessDenied = true;
                    continue;
                }
                byte[] version = new byte[2], length = new byte[4];
                if (!Query(handle, GetVersion, version, version.Length) || !Query(handle, GetMetadataSize, length, length.Length)) continue;
                int metadataSize = BitConverter.ToInt32(length, 0);
                if (metadataSize < 68 || metadataSize > 65536) continue;
                var metadata = new byte[metadataSize];
                if (!Query(handle, GetMetadata, metadata, metadataSize)) continue;
                int channelCount;
                int[] channels = PackageChannels(BitConverter.ToUInt16(version, 0), metadata, packages, out channelCount);
                if (channels.Length != 0) meters.Add(new Meter(path, channelCount, channels));
            }
        }
    }

    internal static int[] PackageChannels(int version, byte[] metadata, HashSet<string> packages, out int count)
    {
        count = 0;
        const int channelTable = 2 * MetadataNameBytes + 2 * sizeof(ushort);
        var channels = new List<int>();
        if (version == 1 && metadata.Length >= channelTable + 4)
        {
            count = 1;
            if (BitConverter.ToUInt32(metadata, 0) == 0 &&
                AddPackage(metadata, channelTable + 4, BitConverter.ToUInt16(metadata, channelTable + 2), packages))
                channels.Add(0);
        }
        else if (version == 2 && metadata.Length >= channelTable)
        {
            count = BitConverter.ToUInt16(metadata, channelTable - 2);
            if (count > 256) return channels.ToArray();
            int offset = channelTable;
            for (int index = 0; index < count && offset + 6 <= metadata.Length; index++)
            {
                int size = BitConverter.ToUInt16(metadata, offset + 4);
                if (size < 2 || size % 2 != 0 || offset + 6 + size > metadata.Length) break;
                if (BitConverter.ToUInt32(metadata, offset) == 0 && AddPackage(metadata, offset + 6, size, packages))
                    channels.Add(index);
                offset += 6 + size;
            }
        }
        return channels.ToArray();
    }

    private static bool AddPackage(byte[] metadata, int offset, int length, HashSet<string> packages)
    {
        if (length < 2 || length % 2 != 0 || length > metadata.Length - offset) return false;
        string name = Encoding.Unicode.GetString(metadata, offset, length).TrimEnd('\0').Trim();
        return (name.StartsWith("RAPL_Package", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(name, "CPU Package", StringComparison.OrdinalIgnoreCase)) && packages.Add(name);
    }

    internal static double? Watts(ulong previousEnergy, ulong previousTime, ulong energy, ulong time)
    {
        if (previousTime == 0 || time <= previousTime || energy < previousEnergy || time - previousTime > 100000000) return null;
        // EMI reports picowatt-hours and 100 ns ticks.
        return GpuMetrics.ValidPower(0.036 * (energy - previousEnergy) / (time - previousTime));
    }

    private static SafeFileHandle Open(string path)
    {
        return CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
    }

    private static bool Query(SafeFileHandle handle, uint code, byte[] data, int minimumBytes)
    {
        int bytes;
        return !handle.IsInvalid && DeviceIoControl(handle, code, IntPtr.Zero, 0, data, data.Length, out bytes,
            IntPtr.Zero) && bytes >= minimumBytes;
    }

    private static int RankHwinfo(string sensor, string label)
    {
        if (label == null || sensor == null || !sensor.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)) return 0;
        switch (label.Trim().ToUpperInvariant())
        {
            case "CPU PACKAGE POWER":
            case "CPU PACKAGE POWER (SMU)":
                return 2;
            case "CPU PPT":
                return 1;
            default:
                return 0;
        }
    }
}
