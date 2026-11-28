using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

internal sealed class WindowsGpuAdapter
{
    internal const uint Nvidia = 0x10DE;
    internal const uint Amd = 0x1002;
    private const uint SoftwareAdapter = 2;
    private const uint AdapterAddress = 6;
    private const uint AdapterPerformanceData = 62;
    private const int DxgiNotFound = unchecked((int)0x887A0002);
    private static readonly Guid Factory1Id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public Luid Id;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { public Luid Id; public uint Handle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterQuery
    {
        public uint Handle, Type;
        public IntPtr Data;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PciAddress { public uint Bus, Device, Function; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOc, MemoryBandwidth, PcieBandwidth;
        public uint FanRpm, Power, Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter request);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTQueryAdapterInfo(ref AdapterQuery request);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTCloseAdapter(ref uint handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapter(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);

    private readonly Luid id;
    private PciAddress? address;
    private bool addressRead;
    internal readonly string Name, CounterId;
    internal readonly uint Vendor;
    internal readonly ulong DedicatedMemory;

    private WindowsGpuAdapter(AdapterDescription description)
    {
        id = description.Id;
        Name = description.Name;
        Vendor = description.VendorId;
        DedicatedMemory = description.DedicatedVideoMemory.ToUInt64();
        CounterId = String.Format(CultureInfo.InvariantCulture, "luid_0x{0:x8}_0x{1:x8}", unchecked((uint)id.High), id.Low);
    }

    internal static List<WindowsGpuAdapter> Enumerate()
    {
        var adapters = new List<WindowsGpuAdapter>();
        Guid iid = Factory1Id;
        IntPtr factory;
        if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return adapters;
        try
        {
            var enumerate = (EnumAdapter)GetMethod(factory, 12, typeof(EnumAdapter)); // IDXGIFactory1::EnumAdapters1
            for (uint index = 0; ; index++)
            {
                IntPtr handle;
                int status = enumerate(factory, index, out handle);
                if (status == DxgiNotFound || status < 0 || handle == IntPtr.Zero) break;
                try
                {
                    var read = (GetDescription)GetMethod(handle, 10, typeof(GetDescription)); // IDXGIAdapter1::GetDesc1
                    AdapterDescription description;
                    if (read(handle, out description) >= 0 && (description.Flags & SoftwareAdapter) == 0)
                        adapters.Add(new WindowsGpuAdapter(description));
                }
                finally { Marshal.Release(handle); }
            }
        }
        finally { Marshal.Release(factory); }
        return adapters;
    }

    internal double? ReadTemperature(uint physicalAdapter)
    {
        PerformanceData? data = Query(AdapterPerformanceData, new PerformanceData { PhysicalAdapterIndex = physicalAdapter });
        return data.HasValue ? GpuMetrics.ValidTemperature(data.Value.Temperature / 10.0) : null;
    }

    internal PciAddress? ReadAddress()
    {
        if (!addressRead)
        {
            PciAddress? value = Query(AdapterAddress, new PciAddress());
            if (value.HasValue && value.Value.Bus <= 255 && value.Value.Device <= 31 && value.Value.Function <= 7)
                address = value;
            addressRead = true;
        }
        return address;
    }

    private T? Query<T>(uint type, T value) where T : struct
    {
        var adapter = new OpenAdapter { Id = id };
        if (D3DKMTOpenAdapterFromLuid(ref adapter) != 0) return null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int length = Marshal.SizeOf(typeof(T));
            buffer = Marshal.AllocHGlobal(length);
            Marshal.StructureToPtr(value, buffer, false);
            var request = new AdapterQuery { Handle = adapter.Handle, Type = type, Data = buffer, Size = (uint)length };
            return D3DKMTQueryAdapterInfo(ref request) == 0 ? (T?)Marshal.PtrToStructure<T>(buffer) : null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            D3DKMTCloseAdapter(ref adapter.Handle);
        }
    }

    private static Delegate GetMethod(IntPtr instance, int slot, Type signature)
    {
        IntPtr table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), signature);
    }
}
