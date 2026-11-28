using System;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Management;
using System.Runtime.InteropServices;

internal static class CpuMonitorTemperature
{
    private static readonly string[] Namespaces = { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };
    private static readonly bool AmdProcessor = (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "")
        .IndexOf("AuthenticAMD", StringComparison.OrdinalIgnoreCase) >= 0;

    internal static double? Read()
    {
        double? value = ReadCoreTemp("CoreTempMappingObjectEx") ?? ReadCoreTemp(@"Global\CoreTempMappingObjectEx");
        if (value.HasValue) return value;
        foreach (string source in Namespaces)
        {
            value = ReadWmi(source);
            if (value.HasValue) return value;
        }
        if (!AmdProcessor) return null;
        foreach (WindowsGpuAdapter adapter in WindowsGpuAdapter.Enumerate())
        {
            if (adapter.Vendor != WindowsGpuAdapter.Amd) continue;
            WindowsGpuAdapter.PciAddress? address = adapter.ReadAddress();
            if (!address.HasValue) continue;
            using (var driver = new AmdGpuDriver(address.Value))
            {
                value = driver.ReadCpuTemperature();
                if (value.HasValue) return value;
            }
        }
        return null;
    }

    private static double? ReadCoreTemp(string name)
    {
        // Field offsets from the Core Temp Shared Data Ex SDK.
        const int junctionLimits = 1024, coreCount = 1536, cpuCount = 1540, temperatures = 1544;
        const int fahrenheitFlag = 2684, junctionDeltaFlag = 2685;
        try
        {
            using (MemoryMappedFile map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
            using (MemoryMappedViewAccessor view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                if (view.Capacity <= junctionDeltaFlag) return null;
                uint cores = view.ReadUInt32(coreCount), cpus = view.ReadUInt32(cpuCount);
                if (cores == 0 || cores > 256 || cpus == 0 || cpus > 128 || cores * cpus > 256) return null;
                bool fahrenheit = view.ReadByte(fahrenheitFlag) != 0;
                bool junctionDelta = view.ReadByte(junctionDeltaFlag) != 0;
                double best = 0;
                for (int i = 0; i < cores * cpus; i++)
                {
                    double reading = view.ReadSingle(temperatures + i * sizeof(float));
                    if (fahrenheit) reading = (reading - (junctionDelta ? 0 : 32)) * 5 / 9;
                    if (junctionDelta) reading = view.ReadUInt32(junctionLimits + (i / cores) * sizeof(uint)) - reading;
                    if (reading > best && reading <= 130) best = reading;
                }
                return GpuMetrics.ValidTemperature(best);
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static double? ReadWmi(string source)
    {
        try
        {
            var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(1), ReturnImmediately = true, Rewindable = false };
            using (var search = new ManagementObjectSearcher(source,
                "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType = 'Temperature'", options))
            using (ManagementObjectCollection sensors = search.Get())
            {
                double? best = null;
                int bestPriority = 0;
                foreach (ManagementObject sensor in sensors)
                    using (sensor)
                    {
                        string id = sensor["Identifier"] as string ?? "";
                        if (!id.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase) &&
                            !id.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase)) continue;
                        int priority = TemperaturePriority(sensor["Name"] as string);
                        if (priority == 0 || priority < bestPriority || sensor["Value"] == null) continue;
                        double value = Convert.ToDouble(sensor["Value"], CultureInfo.InvariantCulture);
                        if (GpuMetrics.ValidTemperature(value).HasValue && (priority > bestPriority || value > best))
                        {
                            bestPriority = priority;
                            best = value;
                        }
                    }
                return best;
            }
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (COMException) { return null; }
    }

    private static int TemperaturePriority(string label)
    {
        switch ((label ?? "").ToUpperInvariant())
        {
            case "CPU PACKAGE": case "CORE (TDIE)": case "CORE (TCTL/TDIE)": return 2;
            case "CORE AVERAGE": case "CORE MAX": return 1;
            default: return 0;
        }
    }
}
