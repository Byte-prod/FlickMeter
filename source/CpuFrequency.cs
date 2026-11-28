using System;
using System.Runtime.InteropServices;

internal sealed class CpuFrequency
{
    private const int ProcessorInformation = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPower
    {
        public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr input, uint inputLength,
        [Out] ProcessorPower[] output, uint outputLength);

    private readonly ProcessorPower[] processors = new ProcessorPower[Environment.ProcessorCount];

    internal double? Read()
    {
        uint length = (uint)(Marshal.SizeOf(typeof(ProcessorPower)) * processors.Length);
        if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, processors, length) != 0) return null;
        double total = 0;
        int count = 0;
        foreach (ProcessorPower processor in processors)
        {
            if (processor.CurrentMhz == 0 || processor.CurrentMhz >= 10000) continue;
            total += processor.CurrentMhz;
            count++;
        }
        return count == 0 ? (double?)null : total / count;
    }
}
