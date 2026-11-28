using System;
using System.Runtime.InteropServices;

internal sealed class AmdGpuDriver : IDisposable
{
    private const string Library = "atiadlxx.dll";
    private const int EdgeTemperature = 8;
    private const int CpuTemperature = 32;
    private const int GraphicsActivity = 19;
    private const int AsicPower = 23;
    private const int BoardPower = 73;
    private const int GraphicsClock = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdapterInfo
    {
        public int Size, Index;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Udid;
        public int Bus, Device, Function, Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
        public int Present, Exists;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExtended;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Pnp;
        public int OsDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerActivity
    {
        public int Size, EngineClock, MemoryClock, Voltage, Percent, Level;
        public int BusSpeed, BusLanes, MaxBusLanes, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThermalReading { public int Size, MilliDegrees; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Sensor { public int Supported, Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SensorLog
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public Sensor[] Readings;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr Allocate(int bytes);

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr malloc(UIntPtr bytes);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Main_Control_Create(Allocate allocate, int connectedOnly, out IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr adapters, int bytes);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int index, ref SensorLog log);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Adapter_DedicatedVRAMUsage_Get(IntPtr context, int index, out int usedMiB);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_OverdriveN_Temperature_Get(IntPtr context, int index, int type, out int milliDegrees);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Overdrive6_Temperature_Get(IntPtr context, int index, out int milliDegrees);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Overdrive5_Temperature_Get(IntPtr context, int index, int controller, ref ThermalReading reading);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Overdrive5_CurrentActivity_Get(IntPtr context, int index, ref PowerActivity activity);

    private static readonly Allocate AllocateMemory = bytes => bytes > 0 ? malloc(new UIntPtr((uint)bytes)) : IntPtr.Zero;

    private IntPtr context;
    private int index = -1;
    private bool hasLog = true, hasOdN = true, hasOd6 = true, hasOd5 = true, hasActivity = true, hasMemory = true;
    private SensorLog log;

    internal AmdGpuDriver(WindowsGpuAdapter.PciAddress address)
    {
        try
        {
            if (ADL2_Main_Control_Create(AllocateMemory, 0, out context) == 0)
                index = FindAdapter(address);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }
        if (index < 0) Dispose();
    }

    private int FindAdapter(WindowsGpuAdapter.PciAddress address)
    {
        int count;
        if (ADL2_Adapter_NumberOfAdapters_Get(context, out count) != 0 || count <= 0 || count > 128) return -1;
        int size = Marshal.SizeOf(typeof(AdapterInfo));
        IntPtr buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            for (int offset = 0; offset < size * count; offset += sizeof(int)) Marshal.WriteInt32(buffer, offset, 0);
            for (int item = 0; item < count; item++) Marshal.WriteInt32(buffer, item * size, size);
            if (ADL2_Adapter_AdapterInfo_Get(context, buffer, size * count) != 0) return -1;
            for (int item = 0; item < count; item++)
            {
                var adapter = Marshal.PtrToStructure<AdapterInfo>(IntPtr.Add(buffer, item * size));
                if (adapter.Index >= 0 && adapter.Bus == address.Bus &&
                    adapter.Device == address.Device && adapter.Function == address.Function)
                    return adapter.Index;
            }
            return -1;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal void Read(bool readTemperature, bool readUsage, bool readPower, bool readClock,
        out double? temperature, out double? usage, out double? power, out double? clock)
    {
        temperature = usage = power = clock = null;
        if (context == IntPtr.Zero || index < 0) return;

        if (ReadSensorLog())
        {
            Sensor edge = log.Readings[EdgeTemperature], graphics = log.Readings[GraphicsActivity];
            if (readTemperature && edge.Supported != 0) temperature = GpuMetrics.ValidTemperature(edge.Value);
            if (readUsage && graphics.Supported != 0) usage = ValidUsage(graphics.Value);
            if (readPower) power = ReadPower(log.Readings[BoardPower]) ?? ReadPower(log.Readings[AsicPower]);
            Sensor coreClock = log.Readings[GraphicsClock];
            if (readClock && coreClock.Supported != 0) clock = GpuMetrics.ValidClock(coreClock.Value);
        }
        if (readTemperature && !temperature.HasValue) temperature = ReadLegacyTemperature();
        if (readUsage && !usage.HasValue && hasActivity)
        {
            var activity = new PowerActivity { Size = Marshal.SizeOf(typeof(PowerActivity)) };
            try
            {
                if (ADL2_Overdrive5_CurrentActivity_Get(context, index, ref activity) == 0)
                    usage = ValidUsage(activity.Percent);
            }
            catch (EntryPointNotFoundException) { hasActivity = false; }
        }
    }

    private bool ReadSensorLog()
    {
        if (!hasLog || context == IntPtr.Zero || index < 0) return false;
        if (log.Readings == null) log = new SensorLog { Size = Marshal.SizeOf(typeof(SensorLog)), Readings = new Sensor[256] };
        Array.Clear(log.Readings, 0, log.Readings.Length);
        try { return ADL2_New_QueryPMLogData_Get(context, index, ref log) == 0; }
        catch (EntryPointNotFoundException) { hasLog = false; return false; }
    }

    internal double? ReadCpuTemperature()
    {
        if (!ReadSensorLog()) return null;
        Sensor reading = log.Readings[CpuTemperature];
        return reading.Supported == 0 ? null : GpuMetrics.ValidTemperature(reading.Value);
    }

    internal double? ReadDedicatedMemory()
    {
        if (!hasMemory || context == IntPtr.Zero || index < 0) return null;
        try
        {
            int usedMiB;
            if (ADL2_Adapter_DedicatedVRAMUsage_Get(context, index, out usedMiB) == 0 && usedMiB >= 0)
                return usedMiB / 1024.0;
        }
        catch (EntryPointNotFoundException) { hasMemory = false; }
        return null;
    }

    private double? ReadLegacyTemperature()
    {
        int milliDegrees;
        double? value;
        if (hasOdN)
        {
            try
            {
                if (ADL2_OverdriveN_Temperature_Get(context, index, 1, out milliDegrees) == 0 &&
                    (value = GpuMetrics.ValidTemperature(milliDegrees / 1000.0)).HasValue) return value;
            }
            catch (EntryPointNotFoundException) { hasOdN = false; }
        }
        if (hasOd6)
        {
            try
            {
                if (ADL2_Overdrive6_Temperature_Get(context, index, out milliDegrees) == 0 &&
                    (value = GpuMetrics.ValidTemperature(milliDegrees / 1000.0)).HasValue) return value;
            }
            catch (EntryPointNotFoundException) { hasOd6 = false; }
        }
        if (hasOd5)
        {
            var reading = new ThermalReading { Size = Marshal.SizeOf(typeof(ThermalReading)) };
            try
            {
                if (ADL2_Overdrive5_Temperature_Get(context, index, 0, ref reading) == 0)
                    return GpuMetrics.ValidTemperature(reading.MilliDegrees / 1000.0);
            }
            catch (EntryPointNotFoundException) { hasOd5 = false; }
        }
        return null;
    }

    private static double? ValidUsage(int value) { return value >= 0 && value <= 100 ? (double?)value : null; }
    private static double? ReadPower(Sensor sensor)
    {
        return sensor.Supported != 0 ? GpuMetrics.ValidPower(sensor.Value) : null;
    }

    public void Dispose()
    {
        if (context == IntPtr.Zero) return;
        try { ADL2_Main_Control_Destroy(context); }
        catch (EntryPointNotFoundException) { }
        context = IntPtr.Zero;
    }
}
