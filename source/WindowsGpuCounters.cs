using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

internal sealed class WindowsGpuCounters : IDisposable
{
    private const uint DoubleFormat = 0x200;
    private const uint UnscaledDoubleFormat = DoubleFormat | 0x1000 | 0x8000;
    private const uint MoreData = 0x800007D2;
    private const int LuidLength = 26;

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Number; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public CounterValue Value; }

    private struct EngineLoad
    {
        public string Adapter;
        public uint PhysicalAdapter;
        public double Total, Process;
        public bool Is3d;
    }

    internal struct AdapterLoad
    {
        public double Total, Rendering, Process;
        public uint PhysicalAdapter;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string source, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhRemoveCounter(IntPtr counter);

    private readonly Dictionary<string, EngineLoad> engines = new Dictionary<string, EngineLoad>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AdapterLoad> adapters = new Dictionary<string, AdapterLoad>(StringComparer.OrdinalIgnoreCase);
    private static readonly int ItemSize = Marshal.SizeOf(typeof(CounterItem));
    private IntPtr query, engineCounter, memoryCounter, buffer;
    private bool engineRequested, memoryRequested, collected;
    private uint capacity;

    internal WindowsGpuCounters()
    {
        if (PdhOpenQueryW(null, UIntPtr.Zero, out query) != 0) query = IntPtr.Zero;
    }

    internal Dictionary<string, AdapterLoad> Read(int processId, bool readEngines, bool readMemory)
    {
        adapters.Clear();
        ConfigureCounter(readEngines, ref engineRequested, ref engineCounter, @"\GPU Engine(*)\Utilization Percentage");
        ConfigureCounter(readMemory, ref memoryRequested, ref memoryCounter, @"\GPU Adapter Memory(*)\Dedicated Usage");
        collected = (engineCounter != IntPtr.Zero || memoryCounter != IntPtr.Zero) && PdhCollectQueryData(query) == 0;
        int count = ReadArray(engineCounter, DoubleFormat);
        if (count == 0) return adapters;

        engines.Clear();
        string target = processId > 0 ? "pid_" + processId.ToString(CultureInfo.InvariantCulture) + "_" : null;
        for (int index = 0; index < count; index++)
        {
            var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, index * ItemSize));
            if (item.Value.Status > 1 || item.Name == IntPtr.Zero) continue;
            string name = Marshal.PtrToStringUni(item.Name);
            AddEngine(name, item.Value.Number, target);
        }

        foreach (EngineLoad engine in engines.Values)
        {
            AdapterLoad load;
            adapters.TryGetValue(engine.Adapter, out load);
            if (engine.Total > load.Total)
            {
                load.Total = engine.Total;
                load.PhysicalAdapter = engine.PhysicalAdapter;
            }
            if (engine.Is3d) load.Rendering = Math.Max(load.Rendering, engine.Process);
            load.Process = Math.Max(load.Process, engine.Process);
            adapters[engine.Adapter] = load;
        }
        return adapters;
    }

    internal double? ReadDedicatedMemory(string adapterId, uint physicalAdapter)
    {
        int count = ReadArray(memoryCounter, UnscaledDoubleFormat);
        if (count == 0) return null;
        string instance = adapterId + "_phys_" + physicalAdapter.ToString(CultureInfo.InvariantCulture);
        for (int index = 0; index < count; index++)
        {
            var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, index * ItemSize));
            if (item.Value.Status <= 1 && item.Name != IntPtr.Zero &&
                String.Equals(Marshal.PtrToStringUni(item.Name), instance, StringComparison.OrdinalIgnoreCase) &&
                item.Value.Number >= 0 && !Double.IsInfinity(item.Value.Number))
                return item.Value.Number / PhysicalMemoryUsage.BytesPerGibibyte;
        }
        return null;
    }

    private void ConfigureCounter(bool enabled, ref bool requested, ref IntPtr handle, string path)
    {
        if (query == IntPtr.Zero || enabled == requested) return;
        requested = enabled;
        if (enabled)
        {
            if (PdhAddEnglishCounterW(query, path, UIntPtr.Zero, out handle) != 0) handle = IntPtr.Zero;
        }
        else if (handle != IntPtr.Zero)
        {
            PdhRemoveCounter(handle);
            handle = IntPtr.Zero;
        }
    }

    private int ReadArray(IntPtr handle, uint format)
    {
        if (!collected || handle == IntPtr.Zero) return 0;
        uint size = capacity, count;
        uint status = PdhGetFormattedCounterArrayW(handle, format, ref size, out count, buffer);
        if (status == MoreData)
        {
            if (capacity != 0)
            {
                size = 0;
                status = PdhGetFormattedCounterArrayW(handle, format, ref size, out count, IntPtr.Zero);
            }
            if (status != MoreData || size <= capacity || size > 16 * 1024 * 1024) return 0;
            IntPtr next = Marshal.AllocHGlobal((int)size);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            buffer = next;
            capacity = size;
            status = PdhGetFormattedCounterArrayW(handle, format, ref size, out count, buffer);
        }
        return status == 0 && buffer != IntPtr.Zero && count <= capacity / ItemSize ? (int)count : 0;
    }

    private void AddEngine(string name, double usage, string target)
    {
        if (String.IsNullOrEmpty(name) || !(usage >= 0) || Double.IsInfinity(usage)) return;
        int adapterMarker = name.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
        if (adapterMarker < 0) return;
        int adapterStart = adapterMarker + 1;
        int physicalStart = adapterStart + LuidLength;
        if (physicalStart >= name.Length || name.IndexOf("_phys_", physicalStart, StringComparison.OrdinalIgnoreCase) != physicalStart) return;
        physicalStart += 6;
        int engineStart = name.IndexOf("_eng_", physicalStart, StringComparison.OrdinalIgnoreCase);
        int typeStart = name.IndexOf("_engtype_", physicalStart, StringComparison.OrdinalIgnoreCase);
        if (engineStart < 0 || typeStart <= engineStart) return;

        string key = name.Substring(adapterStart, typeStart - adapterStart);
        EngineLoad engine;
        if (!engines.TryGetValue(key, out engine))
        {
            engine.Adapter = name.Substring(adapterStart, LuidLength);
            uint.TryParse(name.Substring(physicalStart, engineStart - physicalStart), NumberStyles.None,
                CultureInfo.InvariantCulture, out engine.PhysicalAdapter);
            int engineType = typeStart + 9;
            engine.Is3d = name.Length >= engineType + 2 &&
                String.Compare(name, engineType, "3D", 0, 2, StringComparison.OrdinalIgnoreCase) == 0;
        }
        engine.Total += usage;
        if (target != null && name.StartsWith(target, StringComparison.OrdinalIgnoreCase)) engine.Process += usage;
        engines[key] = engine;
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        query = engineCounter = memoryCounter = buffer = IntPtr.Zero;
        capacity = 0;
    }
}
