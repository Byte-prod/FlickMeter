using System;
using System.Collections.Generic;

internal sealed class GpuMetrics : IDisposable
{
    private readonly WindowsGpuCounters counters = new WindowsGpuCounters();
    private List<WindowsGpuAdapter> adapters;
    private WindowsGpuAdapter selected, preferred;
    private NvidiaManagementMetrics nvidiaManagement;
    private AmdGpuDriver amd;
    private string amdAdapterId;
    private int nvidiaCount, lastProcess, nextEnumeration;

    internal GpuMetrics() { RefreshAdapters(); }

    internal void Read(int processId, bool readUsage, bool readTemperature, bool readPower, bool readMemory, bool readClock,
        out double? temperature, out double? utilization, out double? power, out double? memoryUsed, out double? clock)
    {
        if (unchecked(Environment.TickCount - nextEnumeration) >= 0) RefreshAdapters();
        var loads = counters.Read(processId, readUsage || adapters.Count > 1, readMemory);
        if (processId != lastProcess)
        {
            selected = preferred;
            lastProcess = processId;
        }
        selected = SelectAdapter(adapters, loads) ?? selected;
        WindowsGpuCounters.AdapterLoad load = new WindowsGpuCounters.AdapterLoad();
        bool hasLoad = selected != null && loads.TryGetValue(selected.CounterId, out load);
        utilization = readUsage && hasLoad ? (double?)Math.Min(100, load.Total) : null;
        temperature = power = memoryUsed = clock = null;
        if (readMemory && selected != null)
            memoryUsed = counters.ReadDedicatedMemory(selected.CounterId, load.PhysicalAdapter);

        bool needsMemoryFallback = readMemory && !memoryUsed.HasValue;
        bool readNvidia = readPower || needsMemoryFallback || readClock || readTemperature || readUsage;
        if (readNvidia && (selected == null || selected.Vendor == WindowsGpuAdapter.Nvidia))
        {
            if (nvidiaManagement == null) nvidiaManagement = new NvidiaManagementMetrics();
            double? driverMemory, driverUtilization;
            nvidiaManagement.Read(selected, selected == null || nvidiaCount == 1, readPower, needsMemoryFallback, readClock,
                readTemperature, readUsage, out power, out driverMemory, out clock, out temperature, out driverUtilization);
            memoryUsed = memoryUsed ?? driverMemory;
            utilization = driverUtilization ?? utilization;
        }
        else if (!readNvidia && nvidiaManagement != null)
        {
            nvidiaManagement.Dispose();
            nvidiaManagement = null;
        }

        if (readTemperature && !temperature.HasValue && selected != null)
            temperature = selected.ReadTemperature(load.PhysicalAdapter);
        if (selected != null && selected.Vendor == WindowsGpuAdapter.Amd &&
            (readPower || needsMemoryFallback || readClock || readTemperature && !temperature.HasValue || readUsage && !utilization.HasValue))
        {
            if (!String.Equals(amdAdapterId, selected.CounterId, StringComparison.OrdinalIgnoreCase))
            {
                if (amd != null) amd.Dispose();
                WindowsGpuAdapter.PciAddress? address = selected.ReadAddress();
                amd = address.HasValue ? new AmdGpuDriver(address.Value) : null;
                amdAdapterId = selected.CounterId;
            }
            if (amd != null)
            {
                double? amdTemperature, amdUtilization;
                if (readPower || readClock || readTemperature && !temperature.HasValue || readUsage && !utilization.HasValue)
                {
                    amd.Read(readTemperature && !temperature.HasValue, readUsage && !utilization.HasValue, readPower,
                        readClock, out amdTemperature, out amdUtilization, out power, out clock);
                    temperature = temperature ?? amdTemperature;
                    utilization = utilization ?? amdUtilization;
                }
                if (needsMemoryFallback) memoryUsed = amd.ReadDedicatedMemory();
            }
        }
    }

    internal static WindowsGpuAdapter SelectAdapter(IEnumerable<WindowsGpuAdapter> adapters,
        Dictionary<string, WindowsGpuCounters.AdapterLoad> loads)
    {
        WindowsGpuAdapter rendering = null, computing = null;
        double bestRendering = 0.1, bestProcess = 0.1;
        foreach (WindowsGpuAdapter adapter in adapters)
        {
            WindowsGpuCounters.AdapterLoad load;
            if (!loads.TryGetValue(adapter.CounterId, out load)) continue;
            if (load.Rendering > bestRendering)
            {
                bestRendering = load.Rendering;
                rendering = adapter;
            }
            if (load.Process > bestProcess)
            {
                bestProcess = load.Process;
                computing = adapter;
            }
        }
        return rendering ?? computing;
    }

    private void RefreshAdapters()
    {
        string previousId = selected == null ? null : selected.CounterId;
        int previousNvidiaCount = nvidiaCount;
        adapters = WindowsGpuAdapter.Enumerate();
        preferred = selected = null;
        nvidiaCount = 0;
        foreach (WindowsGpuAdapter adapter in adapters)
        {
            if (preferred == null || adapter.DedicatedMemory > preferred.DedicatedMemory) preferred = adapter;
            if (adapter.CounterId == previousId) selected = adapter;
            if (adapter.Vendor == WindowsGpuAdapter.Nvidia) nvidiaCount++;
        }
        if (nvidiaManagement != null && previousNvidiaCount != nvidiaCount)
        {
            nvidiaManagement.Dispose();
            nvidiaManagement = null;
        }
        selected = selected ?? preferred;
        nextEnumeration = unchecked(Environment.TickCount + 30000);
    }

    internal static double? ValidTemperature(double degrees)
    {
        return degrees > 0 && degrees <= 130 ? (double?)degrees : null;
    }

    internal static double? ValidPower(double watts)
    {
        return watts >= 0 && watts <= 3000 ? (double?)watts : null;
    }

    internal static double? ValidClock(double megahertz)
    {
        return megahertz > 0 && megahertz < 10000 ? (double?)megahertz : null;
    }

    public void Dispose()
    {
        counters.Dispose();
        if (nvidiaManagement != null) nvidiaManagement.Dispose();
        if (amd != null) amd.Dispose();
    }
}
