using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

internal sealed class FrameCollector : IDisposable
{
    public readonly FrameHistory Frames = new FrameHistory();
    private readonly FrameHistory directFrames = new FrameHistory();
    private readonly object sync = new object();
    private readonly string sessionName = "FlickMeter-" + Guid.NewGuid().ToString("N");
    private const string PresentMonHash = "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF";
    private readonly string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PresentMon.exe");
    private string dependencyIssue;
    private Process process;
    private Task worker;
    private Task directWorker;
    private DirectXFrameTrace direct;
    private int revision, runningRevision;
    private int directRevision, runningDirectRevision, watchedPid;
    private long retryAt, directRetryAt, missingSince;
    private bool enabled, disposed, accessDenied, wantDirect;

    public bool IsRunning
    {
        get { lock (sync) return runningDirectRevision == directRevision && direct != null && direct.Running ||
            runningRevision == revision && process != null && !process.HasExited; }
    }

    public bool IsStarting
    {
        get { lock (sync) return enabled && (worker != null || directWorker != null); }
    }

    public bool AccessDenied
    {
        get { lock (sync) return accessDenied; }
    }

    public string DependencyIssue
    {
        get { lock (sync) return dependencyIssue; }
    }

    public void Configure(bool captureEnabled, bool includeLow)
    {
        lock (sync)
        {
            if (disposed) return;
            long now = Stopwatch.GetTimestamp();
            bool changed = enabled != captureEnabled;
            bool retry = captureEnabled && worker == null && !accessDenied && now >= retryAt &&
                (process == null || process.HasExited);
            Frames.SetLowEnabled(captureEnabled && includeLow);
            directFrames.SetLowEnabled(captureEnabled && includeLow);
            if (!changed && !retry) return;
            enabled = captureEnabled;
            if (changed)
            {
                ResetDirect();
                dependencyIssue = null;
            }
            accessDenied = false;
            retryAt = now + 5 * Stopwatch.Frequency;
            Frames.BeginSession(++revision, clearSamples: changed);
            if (worker == null) worker = Task.Run((Action)ApplyCapture);
        }
    }

    private void ResetDirect()
    {
        wantDirect = false;
        watchedPid = 0;
        directRetryAt = 0;
        directFrames.BeginSession(++directRevision, clearSamples: true);
        if (direct != null && directWorker == null) directWorker = Task.Run((Action)ApplyDirect);
    }

    public void Observe(int active, int candidate, bool measureActive, long now)
    {
        Frames.Observe(active, candidate, measureActive);
        directFrames.Observe(active, candidate, measureActive);
        int watched = candidate != 0 ? candidate : measureActive ? active : 0;
        lock (sync)
        {
            if (!enabled || disposed || accessDenied) return;
            if (wantDirect && direct != null && !direct.Running) ResetDirect();
            if (watchedPid != watched || Frames.HasRecentFrames(watched, now, 3))
            {
                watchedPid = watched;
                missingSince = now;
            }
            if (watched == 0 || wantDirect || now < directRetryAt || now - missingSince < 2 * Stopwatch.Frequency) return;
            wantDirect = true;
            if (directWorker == null) directWorker = Task.Run((Action)ApplyDirect);
        }
    }

    public bool HasRecentFrames(int pid, long now, int minimum)
    {
        return directFrames.HasRecentFrames(pid, now, minimum) || Frames.HasRecentFrames(pid, now, minimum);
    }

    public double? Read(int pid, long now, out double? low)
    {
        double? fps = directFrames.Read(pid, now, out low);
        return fps.HasValue ? fps : Frames.Read(pid, now, out low);
    }

    private void ApplyDirect()
    {
        while (true)
        {
            int version;
            bool start;
            DirectXFrameTrace previous;
            lock (sync)
            {
                version = directRevision;
                start = wantDirect && enabled && !disposed;
                if (start && direct != null && runningDirectRevision == version)
                {
                    directWorker = null;
                    return;
                }
                previous = direct;
                direct = null;
            }
            if (previous != null) previous.Dispose();

            DirectXFrameTrace candidate = null;
            if (start)
            {
                candidate = new DirectXFrameTrace(directFrames.IsObserved, (pid, chain, timestamp) =>
                    directFrames.AddPresent(pid, chain, timestamp, Stopwatch.GetTimestamp(), version));
                if (!candidate.Start())
                {
                    Debug.WriteLine("DirectX trace: " + candidate.Status);
                    candidate.Dispose();
                    candidate = null;
                }
            }
            lock (sync)
            {
                if (version == directRevision && start == (wantDirect && enabled && !disposed))
                {
                    direct = candidate;
                    runningDirectRevision = version;
                    if (start && candidate == null)
                    {
                        wantDirect = false;
                        directRetryAt = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
                    }
                    directWorker = null;
                    return;
                }
            }
            if (candidate != null) candidate.Dispose();
        }
    }

    private void ApplyCapture()
    {
        while (true)
        {
            int version;
            bool start;
            lock (sync)
            {
                start = enabled;
                version = revision;
            }
            StopCurrent();
            lock (sync)
            {
                if (version != revision) continue;
            }
            if (start) Start(version);
            lock (sync)
            {
                if (version != revision) continue;
                worker = null;
                return;
            }
        }
    }

    private void SetDependencyIssue(int version, string issue)
    {
        lock (sync) { if (version == revision) dependencyIssue = issue; }
    }

    private void Start(int version)
    {
        Process child = null;
        try
        {
            if (!File.Exists(executable))
            {
                SetDependencyIssue(version, "缺少 PresentMon.exe");
                return;
            }
            using (var component = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (SHA256 sha = SHA256.Create())
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(component)).Replace("-", "");
                    if (!String.Equals(hash, PresentMonHash, StringComparison.Ordinal))
                    {
                        SetDependencyIssue(version, "PresentMon.exe 校验失败");
                        return;
                    }
                }
                SetDependencyIssue(version, null);
                child = CreatePresentMon(version);
                child.Start();
            }
            lock (sync)
            {
                process = child;
                runningRevision = version;
            }
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is UnauthorizedAccessException ||
                ex is Win32Exception || ex is InvalidOperationException)) throw;
            Win32Exception win32 = ex as Win32Exception;
            if (ex is UnauthorizedAccessException || win32 != null && win32.NativeErrorCode == 5)
                lock (sync) { if (version == revision) accessDenied = true; }
            Debug.WriteLine(ex);
            if (process == null && child != null) child.Dispose();
            StopCurrent();
        }
    }

    private Process CreatePresentMon(int version)
    {
        var child = new Process
        {
            StartInfo = new ProcessStartInfo(executable, "--output_stdout --no_console_stats --v1_metrics " +
                "--no_track_display --no_track_gpu --no_track_input --qpc_time " +
                "--stop_existing_session --exclude dwm.exe --exclude explorer.exe" +
                " --session_name " + sessionName)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Environment.SystemDirectory
            }
        };
        child.OutputDataReceived += (s, e) => Frames.AddLine(e.Data, Stopwatch.GetTimestamp(), version);
        child.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.IndexOf("failed to start trace session: access denied", StringComparison.OrdinalIgnoreCase) >= 0)
                lock (sync) { if (version == revision) accessDenied = true; }
            Debug.WriteLine(e.Data);
        };
        return child;
    }

    private void StopCurrent()
    {
        Process previous;
        lock (sync)
        {
            previous = process;
            process = null;
        }
        if (previous == null) return;
        try
        {
            if (!previous.HasExited)
            {
                StopTrace();
                if (!previous.WaitForExit(700))
                {
                    previous.Kill();
                    previous.WaitForExit(1000);
                }
            }
        }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
        finally { previous.Dispose(); }
    }

    public void Dispose()
    {
        Task pending, directPending;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            Frames.BeginSession(++revision, clearSamples: true);
            ResetDirect();
            pending = worker;
            directPending = directWorker;
        }
        try { if (pending != null) pending.GetAwaiter().GetResult(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        try { if (directPending != null) directPending.GetAwaiter().GetResult(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        StopCurrent();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(ulong handle, string name, IntPtr properties, uint code);

    private void StopTrace()
    {
        const int header = 120; // EVENT_TRACE_PROPERTIES on x64.
        byte[] storage = new byte[header + 2048];
        IntPtr buffer = Marshal.AllocHGlobal(storage.Length);
        try
        {
            Marshal.Copy(storage, 0, buffer, storage.Length);
            Marshal.WriteInt32(buffer, 0, storage.Length);
            Marshal.WriteInt32(buffer, 116, header); // LoggerNameOffset.
            ControlTraceW(0, sessionName, buffer, 1);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

internal sealed class FrameHistory
{
    private struct Frame
    {
        public long Timestamp;
        public double Duration;
    }

    private sealed class SwapChain
    {
        public readonly Queue<Frame> Recent = new Queue<Frame>();
        public readonly Queue<Frame> History = new Queue<Frame>();
        public long Latest;
        public long PreviousPresent;
        public double RecentTotal;
        public long LowUpdated;
        public double? LastLow;
        public bool SkipResumeFrame;

        public void Expire(long now)
        {
            while (Recent.Count != 0 && Recent.Peek().Timestamp < now - LiveWindow)
                RecentTotal -= Recent.Dequeue().Duration;
            if (Recent.Count == 0) RecentTotal = 0;
            while (History.Count != 0 && History.Peek().Timestamp < now - LowWindow)
                History.Dequeue();
        }
    }

    private sealed class ProcessFrames
    {
        public readonly Dictionary<string, SwapChain> Chains = new Dictionary<string, SwapChain>(StringComparer.Ordinal);
        public SwapChain Selected;
    }

    private const int MinimumLowSamples = 120;
    private const double PauseMilliseconds = 2000;
    private static readonly long LiveWindow = Stopwatch.Frequency * 3;
    private static readonly long FreshWindow = Stopwatch.Frequency * 2;
    private static readonly long LowWindow = Stopwatch.Frequency * 30;
    private static readonly long LowRefresh = Stopwatch.Frequency * 2;
    private readonly object sync = new object();
    private readonly Dictionary<int, ProcessFrames> processes = new Dictionary<int, ProcessFrames>();
    private readonly List<string> expired = new List<string>();
    private double[] slowest = new double[0];
    private int activePid, candidatePid, revision;
    private int processColumn = -1, chainColumn = -1, durationColumn = -1, timeColumn = -1;
    private bool lowEnabled, measureActive;

    public void BeginSession(int version, bool clearSamples)
    {
        lock (sync)
        {
            revision = version;
            processColumn = chainColumn = durationColumn = timeColumn = -1;
            if (clearSamples) processes.Clear();
        }
    }

    public void Observe(int active, int candidate, bool measureActiveProcess)
    {
        lock (sync)
        {
            ProcessFrames resumed;
            if (active == activePid && measureActiveProcess && !measureActive && processes.TryGetValue(active, out resumed))
                foreach (SwapChain chain in resumed.Chains.Values) chain.SkipResumeFrame = true;
            if (activePid != active && activePid != candidate) processes.Remove(activePid);
            if (candidatePid != active && candidatePid != candidate) processes.Remove(candidatePid);
            activePid = active;
            candidatePid = candidate;
            measureActive = measureActiveProcess;
        }
    }

    public void SetLowEnabled(bool enabled)
    {
        lock (sync)
        {
            if (lowEnabled == enabled) return;
            lowEnabled = enabled;
            if (!enabled)
                foreach (ProcessFrames process in processes.Values)
                    foreach (SwapChain chain in process.Chains.Values)
                    {
                        chain.History.Clear();
                        chain.LastLow = null;
                    }
        }
    }

    public void AddLine(string line, long now, int version)
    {
        if (String.IsNullOrEmpty(line)) return;
        lock (sync)
        {
            if (version != revision) return;
            if (line[0] == '\ufeff') line = line.Substring(1);
            if (line.StartsWith("Application,", StringComparison.OrdinalIgnoreCase))
            {
                string[] columns = line.Split(',');
                for (int i = 0; i < columns.Length; i++) columns[i] = columns[i].Trim().Trim('"');
                processColumn = FindColumn(columns, "ProcessID");
                chainColumn = FindColumn(columns, "SwapChainAddress");
                durationColumn = FindColumn(columns, "msBetweenPresents");
                timeColumn = FindColumn(columns, "QPCTime");
                if (timeColumn < 0) timeColumn = FindColumn(columns, "TimeInQPC");
                return;
            }
            if (processColumn < 0 || chainColumn < 0 || durationColumn < 0 || timeColumn < 0) return;
            int pid;
            if (!Int32.TryParse(Field(line, processColumn), NumberStyles.None, CultureInfo.InvariantCulture, out pid) ||
                !IsWatched(pid)) return;
            string address = Field(line, chainColumn);
            double duration;
            long timestamp;
            if (String.IsNullOrEmpty(address) || !Double.TryParse(Field(line, durationColumn),
                NumberStyles.Float, CultureInfo.InvariantCulture, out duration) || !(duration > 0) ||
                !Int64.TryParse(Field(line, timeColumn), NumberStyles.None, CultureInfo.InvariantCulture, out timestamp) ||
                !HasValidTimestamp(timestamp, now)) return;
            AddFrame(GetChain(pid, address), timestamp, duration, now);
        }
    }

    public bool IsObserved(int pid) { lock (sync) return IsWatched(pid); }

    private bool IsWatched(int pid)
    {
        return pid > 0 && (pid == candidatePid || pid == activePid && measureActive);
    }

    public void AddPresent(int pid, ulong chainId, long timestamp, long now, int version)
    {
        lock (sync)
        {
            if (version != revision || !IsWatched(pid) || !HasValidTimestamp(timestamp, now)) return;
            SwapChain chain = GetChain(pid, chainId.ToString("x", CultureInfo.InvariantCulture));
            if (timestamp <= chain.PreviousPresent) return;
            long previous = chain.PreviousPresent;
            chain.PreviousPresent = timestamp;
            if (previous > 0) AddFrame(chain, timestamp, (timestamp - previous) * 1000.0 / Stopwatch.Frequency, now);
        }
    }

    private bool HasValidTimestamp(long timestamp, long now)
    {
        return timestamp > 0 && timestamp <= now && timestamp >= now - (lowEnabled ? LowWindow : LiveWindow);
    }

    private SwapChain GetChain(int pid, string address)
    {
        ProcessFrames process;
        if (!processes.TryGetValue(pid, out process)) processes.Add(pid, process = new ProcessFrames());
        SwapChain chain;
        if (!process.Chains.TryGetValue(address, out chain)) process.Chains.Add(address, chain = new SwapChain());
        return chain;
    }

    private void AddFrame(SwapChain chain, long timestamp, double duration, long now)
    {
        if (timestamp <= chain.Latest) return;
        if (chain.SkipResumeFrame)
        {
            // 切回游戏后的第一帧可能把切出期间的停顿计入帧间隔。
            chain.SkipResumeFrame = false;
            return;
        }
        if (duration > PauseMilliseconds) return;
        chain.Expire(now);
        var frame = new Frame { Timestamp = timestamp, Duration = duration };
        if (timestamp >= now - LiveWindow)
        {
            chain.Recent.Enqueue(frame);
            chain.RecentTotal += duration;
        }
        if (lowEnabled) chain.History.Enqueue(frame);
        chain.Latest = timestamp;
    }

    public bool HasRecentFrames(int pid, long now, int minimum)
    {
        lock (sync)
        {
            ProcessFrames process;
            if (!processes.TryGetValue(pid, out process)) return false;
            foreach (SwapChain chain in process.Chains.Values)
            {
                chain.Expire(now);
                if (chain.Latest >= now - FreshWindow && chain.Recent.Count >= minimum) return true;
            }
            return false;
        }
    }

    public double? Read(int pid, long now, out double? low)
    {
        lock (sync)
        {
            low = null;
            ProcessFrames process;
            if (!processes.TryGetValue(pid, out process)) return null;
            Dictionary<string, SwapChain> chains = process.Chains;
            SwapChain busiest = null;
            int bestCount = 0, selectedCount = 0;
            expired.Clear();
            foreach (KeyValuePair<string, SwapChain> pair in chains)
            {
                SwapChain chain = pair.Value;
                chain.Expire(now);
                if (chain.Recent.Count == 0 && chain.History.Count == 0)
                {
                    expired.Add(pair.Key);
                    continue;
                }
                int frames = chain.Latest >= now - FreshWindow ? chain.Recent.Count : 0;
                if (chain == process.Selected) selectedCount = frames;
                if (frames > bestCount)
                {
                    bestCount = frames;
                    busiest = chain;
                }
            }
            foreach (string key in expired) chains.Remove(key);
            if (bestCount < 3) return null;
            // Keep the primary game surface through short bursts on secondary swap chains.
            if (selectedCount < 3 || busiest != process.Selected && bestCount >= selectedCount * 1.35 + 4)
                process.Selected = busiest;
            SwapChain selected = process.Selected;
            if (lowEnabled && selected.History.Count >= MinimumLowSamples &&
                selected.Latest - selected.History.Peek().Timestamp >= LiveWindow)
            {
                if (!selected.LastLow.HasValue || now - selected.LowUpdated >= LowRefresh)
                {
                    selected.LastLow = CalculateLow(selected.History);
                    selected.LowUpdated = now;
                }
                low = selected.LastLow;
            }
            return selected.Recent.Count * 1000.0 / selected.RecentTotal;
        }
    }

    private double CalculateLow(Queue<Frame> frames)
    {
        int limit = (frames.Count + 99) / 100;
        if (slowest.Length < limit) Array.Resize(ref slowest, Math.Max(limit, slowest.Length * 2));
        int count = 0;
        // A min-heap keeps only the slowest 1%, avoiding a full sort every update.
        foreach (Frame frame in frames)
        {
            double duration = frame.Duration;
            if (count < limit)
            {
                int hole = count++;
                while (hole > 0)
                {
                    int parent = (hole - 1) / 2;
                    if (slowest[parent] <= duration) break;
                    slowest[hole] = slowest[parent];
                    hole = parent;
                }
                slowest[hole] = duration;
            }
            else if (duration > slowest[0])
            {
                int hole = 0;
                while (2 * hole + 1 < count)
                {
                    int child = 2 * hole + 1;
                    if (child + 1 < count && slowest[child + 1] < slowest[child]) child++;
                    if (slowest[child] >= duration) break;
                    slowest[hole] = slowest[child];
                    hole = child;
                }
                slowest[hole] = duration;
            }
        }
        double totalFps = 0;
        for (int i = 0; i < count; i++) totalFps += 1000.0 / slowest[i];
        return totalFps / count;
    }

    private static int FindColumn(string[] columns, string name)
    {
        return Array.FindIndex(columns, column => String.Equals(column, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string Field(string row, int column)
    {
        int start = 0, current = 0;
        bool inQuote = false;
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] == '"') inQuote = !inQuote;
            if (row[i] != ',' || inQuote) continue;
            if (column == current) return row.Substring(start, i - start).Trim('"');
            start = i + 1;
            current++;
        }
        return column == current ? row.Substring(start).Trim('"') : null;
    }
}
