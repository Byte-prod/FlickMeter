using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

internal static class CpuTemperatureSource
{
    private const uint ExecuteMethodAccess = 0x0010;
    private const uint ErrorAccessDenied = 5;
    private const string FirmwareInstance = @"ACPI\PNP0C14\MIFS_0";
    private static readonly Guid FirmwareProvider = new Guid("b60bfb48-3e5b-49e4-a0e9-8cffe1b3434b");
    private const string GadgetKey = @"SOFTWARE\HWiNFO64\VSB";
    public static volatile bool AccessDenied;

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint WmiOpenBlock(ref Guid guid, uint access, out IntPtr handle);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint WmiCloseBlock(IntPtr handle);
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WmiExecuteMethodW(IntPtr handle, string instance, uint method, uint inputSize,
        byte[] input, ref uint outputSize, [Out] byte[] output);

    public static double? Read()
    {
        AccessDenied = false;
        double? packageTemperature = ReadHwinfo(CpuTemperatureRank);
        if (packageTemperature.HasValue) return packageTemperature;
        byte[] request = new byte[32];
        request[1] = 0xfa; // Read.
        request[3] = 22;   // CPU thermometer.
        double? temperature = ReadFirmware(request) ?? ReadFirmwareWmi(request);
        return temperature ?? CpuMonitorTemperature.Read();
    }

    private static double? ReadFirmware(byte[] request)
    {
        try
        {
            Guid guid = FirmwareProvider;
            IntPtr handle;
            uint status = WmiOpenBlock(ref guid, ExecuteMethodAccess, out handle);
            if (status != 0)
            {
                AccessDenied = status == ErrorAccessDenied;
                return null;
            }
            try
            {
                byte[] response = new byte[32];
                uint responseLength = (uint)response.Length;
                status = WmiExecuteMethodW(handle, FirmwareInstance, 1, (uint)request.Length,
                    request, ref responseLength, response);
                AccessDenied = status == ErrorAccessDenied;
                return status == 0 && responseLength >= 5 ? DecodeFirmware(response) : null;
            }
            finally { WmiCloseBlock(handle); }
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static double? ReadFirmwareWmi(byte[] request)
    {
        try
        {
            EnumerationOptions options = new EnumerationOptions
            {
                Timeout = TimeSpan.FromSeconds(1), ReturnImmediately = true, Rewindable = false
            };
            using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM MICommonInterface", options))
            using (ManagementObjectCollection instances = search.Get())
                foreach (ManagementObject instance in instances)
                    using (instance)
                    using (ManagementBaseObject input = instance.GetMethodParameters("MiInterface"))
                    {
                        input["InData"] = request;
                        using (ManagementBaseObject result = instance.InvokeMethod("MiInterface", input, null))
                        {
                            double? degrees = result == null ? null : DecodeFirmware(result["OutData"] as byte[]);
                            if (degrees.HasValue) return degrees;
                        }
                    }
        }
        catch (ManagementException ex) { if (ex.ErrorCode == ManagementStatus.AccessDenied) AccessDenied = true; }
        catch (UnauthorizedAccessException) { AccessDenied = true; }
        catch (COMException ex) { if (ex.ErrorCode == unchecked((int)0x80070005)) AccessDenied = true; }
        return null;
    }

    internal static double? DecodeFirmware(byte[] response)
    {
        return response != null && response.Length >= 5 && response[0] == 0 && response[1] == 0x80 && Valid(response[4]) ?
            (double?)response[4] : null;
    }

    internal static double? ReadHwinfo(Func<string, string, int> rankSensor, Func<double, bool> validate = null)
    {
        Process[] processes = Process.GetProcessesByName("HWiNFO64");
        try
        {
            int session;
            using (Process current = Process.GetCurrentProcess()) session = current.SessionId;
            bool running = false;
            foreach (Process process in processes)
            {
                try { running |= !process.HasExited && process.SessionId == session; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (!running) return null;
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
        try
        {
            using (RegistryKey user = Registry.CurrentUser.OpenSubKey(GadgetKey))
            using (RegistryKey machine = Registry.LocalMachine.OpenSubKey(GadgetKey))
            {
                double? userReading = user == null ? null : ReadGadgetValues(user.GetValueNames(), key => user.GetValue(key), rankSensor, validate);
                return userReading ?? (machine == null ? null : ReadGadgetValues(machine.GetValueNames(), key => machine.GetValue(key), rankSensor, validate));
            }
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (SecurityException) { return null; }
        catch (System.IO.IOException) { return null; }
    }

    internal static double? ReadGadgetValues(string[] keys, Func<string, object> getValue, Func<string, string, int> rankSensor,
        Func<double, bool> validate = null)
    {
        bool temperature = validate == null;
        validate = validate ?? Valid;
        double? best = null;
        int priority = 0;
        foreach (string key in keys)
        {
            if (!key.StartsWith("Label", StringComparison.OrdinalIgnoreCase)) continue;
            string id = key.Substring(5);
            int index;
            if (!Int32.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out index)) continue;
            string sensor = getValue("Sensor" + id) as string;
            string label = getValue(key) as string;
            int rank = rankSensor(sensor, label);
            if (rank == 0 || rank < priority) continue;
            string text = getValue("Value" + id) as string ?? "";
            string raw = Convert.ToString(getValue("ValueRaw" + id), CultureInfo.InvariantCulture);
            double reading;
            if (!Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out reading) &&
                !Double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out reading)) continue;
            if (temperature && text.IndexOf("°F", StringComparison.OrdinalIgnoreCase) >= 0) reading = (reading - 32) * 5 / 9;
            if (validate(reading) && (rank > priority || !best.HasValue || reading > best.Value))
            {
                priority = rank;
                best = reading;
            }
        }
        return best;
    }

    internal static int CpuTemperatureRank(string sensor, string label)
    {
        if (label == null || sensor == null || !sensor.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)) return 0;
        switch (label.Trim().ToUpperInvariant())
        {
            case "CPU (TCTL/TDIE)":
            case "CPU (TDIE)":
            case "CPU PACKAGE":
                return 3;
            case "CPU (TCTL)":
            case "CPU DIE (AVERAGE)":
                return 2;
            case "CORE MAX":
                return 1;
            default:
                return 0;
        }
    }

    private static bool Valid(double temperature) { return temperature > 0 && temperature <= 130; }
}
