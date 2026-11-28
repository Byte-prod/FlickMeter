using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// Win32 ETW 结构按项目的 Windows x64 目标布局。
internal sealed class DirectXFrameTrace : IDisposable
{
    private const int PropertiesSize = 120;
    private const int RealTimeIndependent = 0x08000100;
    private const uint ConsumeRecordsWithQpc = 0x10001100;
    private const ulong GraphicsEvents = 0x8000000000000002;
    private static readonly Guid Dxgi = new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    private static readonly Guid D3d9 = new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");

    [StructLayout(LayoutKind.Explicit, Size=448)]
    private struct RealtimeSource
    {
        [FieldOffset(8)] public IntPtr Name;
        [FieldOffset(28)] public uint Mode;
        [FieldOffset(424)] public IntPtr Callback;
    }

    [StructLayout(LayoutKind.Explicit, Size=112)]
    private struct Record
    {
        [FieldOffset(4)] public ushort Flags;
        [FieldOffset(8)] public uint Thread;
        [FieldOffset(12)] public uint Process;
        [FieldOffset(16)] public long Timestamp;
        [FieldOffset(24)] public Guid Provider;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(86)] public ushort DataLength;
        [FieldOffset(96)] public IntPtr Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Consume(ref Record record);

    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern uint StartTraceW(out ulong session, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern uint ControlTraceW(ulong session, string name, IntPtr properties, uint operation);
    [DllImport("advapi32.dll", ExactSpelling=true)]
    private static extern uint EnableTraceEx2(ulong session, ref Guid provider, uint enable, byte level,
        ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr options);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    private static extern ulong OpenTraceW(ref RealtimeSource source);
    [DllImport("advapi32.dll", ExactSpelling=true)]
    private static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll", ExactSpelling=true)]
    private static extern uint CloseTrace(ulong trace);

    private readonly string name = "FlickMeter-DirectX-" + Guid.NewGuid().ToString("N");
    private readonly Func<int,bool> watches;
    private readonly Action<int,ulong,long> frame;
    private readonly Consume callback;
    private readonly System.Collections.Generic.Dictionary<ulong,Pending> pending =
        new System.Collections.Generic.Dictionary<ulong,Pending>();
    private IntPtr properties, nameBuffer;
    private ulong session, consumer = ulong.MaxValue;
    private Thread thread;
    private volatile bool stopping;
    internal uint Status { get; private set; }
    internal bool Running { get { Thread active = thread; return active != null && active.IsAlive; } }

    private struct Pending
    {
        public ulong SwapChain;
        public long Timestamp;
    }

    internal DirectXFrameTrace(Func<int,bool> watches, Action<int,ulong,long> frame)
    {
        this.watches = watches;
        this.frame = frame;
        callback = OnEvent;
    }

    internal bool Start()
    {
        byte[] blank = new byte[PropertiesSize + 512];
        properties = Marshal.AllocHGlobal(blank.Length);
        Marshal.Copy(blank, 0, properties, blank.Length);
        Marshal.WriteInt32(properties, 0, blank.Length);
        Marshal.WriteInt32(properties, 40, 1); // 使用 QPC 时钟。
        Marshal.WriteInt32(properties, 44, 0x00020000);
        Marshal.WriteInt32(properties, 48, 16); // 单个缓冲区大小，单位 KiB。
        Marshal.WriteInt32(properties, 52, 32);
        Marshal.WriteInt32(properties, 56, 128);
        Marshal.WriteInt32(properties, 64, RealTimeIndependent);
        Marshal.WriteInt32(properties, 68, 1);
        Marshal.WriteInt32(properties, 116, PropertiesSize);
        Status = StartTraceW(out session, name, properties);
        if (Status != 0) { Dispose(); return false; }

        nameBuffer = Marshal.StringToHGlobalUni(name);
        var source = new RealtimeSource
        {
            Name = nameBuffer, Mode = ConsumeRecordsWithQpc, Callback = Marshal.GetFunctionPointerForDelegate(callback)
        };
        consumer = OpenTraceW(ref source);
        if (consumer == ulong.MaxValue)
        {
            Status = (uint)Marshal.GetLastWin32Error();
            Dispose();
            return false;
        }
        ulong processing = consumer;
        thread = new Thread(() => { ProcessTrace(new[] { processing }, 1, IntPtr.Zero, IntPtr.Zero); })
        {
            IsBackground = true, Name = "FlickMeter DirectX ETW"
        };
        thread.Start();

        Guid dxgi = Dxgi, d3d9 = D3d9;
        uint first = EnableTraceEx2(session, ref dxgi, 1, 0, GraphicsEvents, 0, 0, IntPtr.Zero);
        uint second = EnableTraceEx2(session, ref d3d9, 1, 0, GraphicsEvents, 0, 0, IntPtr.Zero);
        if (first != 0 && second != 0)
        {
            Status = first;
            Dispose();
            return false;
        }
        return true;
    }

    private void OnEvent(ref Record e)
    {
        if (stopping || e.Data == IntPtr.Zero) return;
        bool dxgi = e.Provider == Dxgi;
        if (!dxgi && e.Provider != D3d9) return;
        bool start = dxgi ? e.EventId == 42 || e.EventId == 55 : e.EventId == 1;
        bool end = dxgi ? e.EventId == 43 || e.EventId == 56 : e.EventId == 2;
        if (!start && !end) return;
        int pid = (int)e.Process;
        ulong key = ((ulong)e.Process << 33) | ((ulong)e.Thread << 1) | (dxgi ? 1UL : 0UL);
        if (!watches(pid))
        {
            pending.Remove(key);
            return;
        }
        if (start)
        {
            int pointerBytes = (e.Flags & 0x20) != 0 ? 4 : 8;
            if (e.DataLength < pointerBytes + sizeof(uint))
            {
                pending.Remove(key);
                return;
            }
            ulong chain = pointerBytes == 4 ? (uint)Marshal.ReadInt32(e.Data) : unchecked((ulong)Marshal.ReadInt64(e.Data));
            int flags = Marshal.ReadInt32(e.Data, pointerBytes);
            if (chain == 0 || (flags & (dxgi ? 1 : 4)) != 0)
            {
                pending.Remove(key);
                return;
            }
            if (pending.Count >= 512) pending.Clear();
            pending[key] = new Pending { SwapChain = chain, Timestamp = e.Timestamp };
        }
        else
        {
            Pending present;
            if (!pending.TryGetValue(key, out present)) return;
            pending.Remove(key);
            if (e.DataLength >= sizeof(uint) && Marshal.ReadInt32(e.Data) == 0 &&
                e.Timestamp >= present.Timestamp && e.Timestamp - present.Timestamp <= 2 * Stopwatch.Frequency)
                frame(pid, present.SwapChain, present.Timestamp);
        }
    }

    public void Dispose()
    {
        stopping = true;
        if (session != 0) { ControlTraceW(session, name, properties, 1); session = 0; }
        if (consumer != ulong.MaxValue) { CloseTrace(consumer); consumer = ulong.MaxValue; }
        if (thread != null) { thread.Join(); thread = null; }
        if (nameBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(nameBuffer); nameBuffer = IntPtr.Zero; }
        if (properties != IntPtr.Zero) { Marshal.FreeHGlobal(properties); properties = IntPtr.Zero; }
        GC.KeepAlive(callback);
    }
}
