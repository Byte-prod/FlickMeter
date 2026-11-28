using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

internal sealed class NvidiaManagementMetrics : IDisposable
{
    private const uint TrustedLibrarySearch = 0x900; // Library directory plus System32.
    private const int GraphicsClock = 0;
    private const int GpuTemperatureSensor = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr library, string name);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr library);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StatusCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceByIndex(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int DeviceByBusId(string busId, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadPower(IntPtr device, out uint milliwatts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadMemory(IntPtr device, out MemoryInfo memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadClock(IntPtr device, int type, out uint megahertz);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadTemperature(IntPtr device, int sensor, out uint degrees);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadUtilization(IntPtr device, out DeviceUtilization utilization);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceUtilization { public uint Gpu, Memory; }

    private IntPtr library, device;
    private StatusCall shutdown;
    private DeviceByBusId byBusId;
    private DeviceByIndex byIndex;
    private DeviceCount deviceCount;
    private ReadPower readPower;
    private ReadMemory readMemory;
    private ReadClock readClock;
    private ReadTemperature readTemperature;
    private ReadUtilization readUtilization;
    private string adapterId;
    private bool initialized;

    internal NvidiaManagementMetrics()
    {
        library = LoadLibraryExW(Path.Combine(Environment.SystemDirectory, "nvml.dll"), IntPtr.Zero, TrustedLibrarySearch);
        if (library == IntPtr.Zero)
        {
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvml.dll");
            library = LoadLibraryExW(legacy, IntPtr.Zero, TrustedLibrarySearch);
        }
        if (library == IntPtr.Zero) return;
        var initialize = Bind<StatusCall>("nvmlInit_v2");
        shutdown = Bind<StatusCall>("nvmlShutdown");
        byBusId = Bind<DeviceByBusId>("nvmlDeviceGetHandleByPciBusId_v2");
        byIndex = Bind<DeviceByIndex>("nvmlDeviceGetHandleByIndex_v2");
        deviceCount = Bind<DeviceCount>("nvmlDeviceGetCount_v2");
        readPower = Bind<ReadPower>("nvmlDeviceGetPowerUsage");
        readMemory = Bind<ReadMemory>("nvmlDeviceGetMemoryInfo");
        readClock = Bind<ReadClock>("nvmlDeviceGetClockInfo");
        readTemperature = Bind<ReadTemperature>("nvmlDeviceGetTemperature");
        readUtilization = Bind<ReadUtilization>("nvmlDeviceGetUtilizationRates");
        initialized = initialize != null && shutdown != null &&
            (readPower != null || readMemory != null || readClock != null || readTemperature != null || readUtilization != null) &&
            initialize() == 0;
        if (!initialized) Dispose();
    }

    internal void Read(WindowsGpuAdapter adapter, bool onlyNvidiaAdapter, bool powerEnabled, bool memoryEnabled,
        bool clockEnabled, bool temperatureEnabled, bool usageEnabled, out double? power, out double? memoryUsed,
        out double? clock, out double? temperature, out double? utilization)
    {
        power = memoryUsed = clock = temperature = utilization = null;
        if (!initialized) return;
        string currentAdapter = adapter == null ? String.Empty : adapter.CounterId;
        if (!String.Equals(adapterId, currentAdapter, StringComparison.OrdinalIgnoreCase))
        {
            adapterId = currentAdapter;
            device = FindDevice(adapter, onlyNvidiaAdapter);
        }
        if (device == IntPtr.Zero) return;
        uint milliwatts;
        if (powerEnabled && readPower != null && readPower(device, out milliwatts) == 0)
            power = GpuMetrics.ValidPower(milliwatts / 1000.0);
        MemoryInfo memory;
        if (memoryEnabled && readMemory != null && readMemory(device, out memory) == 0 &&
            memory.Total > 0 && memory.Used <= memory.Total)
            memoryUsed = memory.Used / PhysicalMemoryUsage.BytesPerGibibyte;
        uint megahertz;
        if (clockEnabled && readClock != null && readClock(device, GraphicsClock, out megahertz) == 0)
            clock = GpuMetrics.ValidClock(megahertz);
        uint degrees;
        if (temperatureEnabled && readTemperature != null && readTemperature(device, GpuTemperatureSensor, out degrees) == 0)
            temperature = GpuMetrics.ValidTemperature(degrees);
        DeviceUtilization load;
        if (usageEnabled && readUtilization != null && readUtilization(device, out load) == 0 && load.Gpu <= 100)
            utilization = load.Gpu;
    }

    private IntPtr FindDevice(WindowsGpuAdapter adapter, bool onlyNvidiaAdapter)
    {
        WindowsGpuAdapter.PciAddress? address = adapter == null ? null : adapter.ReadAddress();
        IntPtr found;
        if (address.HasValue && byBusId != null)
        {
            string busId = String.Format(CultureInfo.InvariantCulture, "0000:{0:x2}:{1:x2}.{2:x}",
                address.Value.Bus, address.Value.Device, address.Value.Function);
            if (byBusId(busId, out found) == 0) return found;
        }
        uint count;
        return onlyNvidiaAdapter && deviceCount != null && byIndex != null && deviceCount(out count) == 0 &&
            count == 1 && byIndex(0, out found) == 0 ? found : IntPtr.Zero;
    }

    private T Bind<T>(string name) where T : class
    {
        IntPtr address = GetProcAddress(library, name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
    }

    public void Dispose()
    {
        if (initialized) shutdown();
        if (library != IntPtr.Zero) FreeLibrary(library);
        library = device = IntPtr.Zero;
        initialized = false;
    }
}
