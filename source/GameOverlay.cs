using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("FlickMeter")]
[assembly: AssemblyProduct("FlickMeter")]
[assembly: AssemblyDescription("FlickMeter 游戏帧率与硬件监控")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && (args[0] == StartupRegistration.EnableArgument || args[0] == StartupRegistration.DisableArgument))
            return StartupRegistration.ApplyElevated(args[0] == StartupRegistration.EnableArgument, args[1]);
        Run();
        return 0;
    }

    private static void Run()
    {
        using (Mutex instance = new Mutex(false, @"Local\GameOverlayPerformanceBar"))
        {
            try { if (!instance.WaitOne(6000)) return; }
            catch (AbandonedMutexException) { }
            try
            {
                OverlaySettings preferences = OverlaySettings.Load(OverlaySettings.DefaultPath);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                StartupRegistration.MigratePreviousPath();
                using (OverlayWindow window = new OverlayWindow(null, preferences))
                    Application.Run(window);
            }
            finally { instance.ReleaseMutex(); }
        }
    }

    internal static bool RestartElevated()
    {
        try
        {
            using (Process process = Process.Start(new ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true, Verb = "runas"
            }))
                return process != null;
        }
        catch (Win32Exception) { return false; }
    }
}

internal sealed class OverlayWindow : Form
{
    private static readonly Color Background = Color.FromArgb(20, 26, 37);
    private static readonly Color Border = Color.FromArgb(58, 70, 87);
    private static readonly Color Muted = Color.FromArgb(166, 181, 202);
    private static readonly Color Bright = Color.FromArgb(240, 245, 251);
    private static readonly Color Accent = Color.FromArgb(119, 231, 194);
    private sealed class MetricGroup
    {
        public readonly string Title;
        public readonly OverlayMetric[] Metrics;

        public MetricGroup(string title, params OverlayMetric[] metrics)
        {
            Title = title;
            Metrics = metrics;
        }
    }

    private static readonly MetricGroup[] Groups =
    {
        new MetricGroup("FPS", OverlayMetric.Fps),
        new MetricGroup("1% Low", OverlayMetric.LowFps),
        new MetricGroup("CPU", OverlayMetric.CpuUsage, OverlayMetric.CpuTemperature, OverlayMetric.CpuPower, OverlayMetric.CpuClock),
        new MetricGroup("GPU", OverlayMetric.GpuUsage, OverlayMetric.GpuTemperature, OverlayMetric.GpuPower, OverlayMetric.GpuClock),
        new MetricGroup("内存", OverlayMetric.MemoryUsage, OverlayMetric.MemoryUsed),
        new MetricGroup("显存", OverlayMetric.VideoMemoryUsed)
    };
    private static readonly string[] MetricNames = { "FPS", "1% Low", "CPU 占用率", "CPU 温度",
        "GPU 占用率", "GPU 温度", "内存占用率", "CPU 功耗", "GPU 功耗", "内存占用", "显存占用",
        "CPU 频率", "GPU 频率" };
    private static readonly string[] MetricUnits = { "", "", "%", "°C", "%", "°C", "%", "W", "W", "GiB", "GiB",
        "MHz", "MHz" };
    private static readonly Color[] ReadingColors = { Accent, Color.FromArgb(143, 219, 255),
        Color.FromArgb(163, 203, 255), Color.FromArgb(255, 217, 141),
        Color.FromArgb(215, 188, 255), Color.FromArgb(255, 195, 136),
        Color.FromArgb(165, 222, 171), Color.FromArgb(255, 203, 145), Color.FromArgb(255, 169, 165),
        Color.FromArgb(165, 222, 171), Color.FromArgb(209, 187, 251), Color.FromArgb(140, 218, 251),
        Color.FromArgb(215, 201, 255) };
    private static readonly string[] ShellProcesses = { "explorer", "dwm", "ShellExperienceHost",
        "StartMenuExperienceHost", "ShellHost", "SearchHost", "SearchApp", "LockApp", "TextInputHost" };
    private const int RowHeight = 30;
    private const int SectionPadding = 7;
    private const int LabelGap = 4;
    private const int ReadingGap = 5;
    private const int DisplayChangedMessage = 0x007E;
    private const int DpiChangedMessage = 0x02E0;
    private const int GetDpiScaledSizeMessage = 0x02E4;
    private const int CurrentDisplaySettings = -1;
    private const int SavedDisplaySettings = -2;
    private const int DisplayModePosition = 0x20;
    private const int CandidateFrameCount = 6;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public int Fields;
        [FieldOffset(76)] public int X;
        [FieldOffset(80)] public int Y;
        [FieldOffset(172)] public int Width;
        [FieldOffset(176)] public int Height;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int mode, ref DisplayMode settings);

    private CpuUsage cpu;
    private CpuFrequency cpuFrequency;
    private GpuMetrics gpu;
    private readonly FrameCollector capture = new FrameCollector();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
    private readonly ContextMenuStrip menu = new OutsideClickMenu();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly OverlaySettings settings;
    private readonly string settingsPath;
    private readonly Font menuFont = new Font("Microsoft YaHei UI", 9);
    private readonly Font labelFont = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font valueFont = new Font("Segoe UI Semibold", 14, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font unitFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font compactValueFont = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly ToolStripMenuItem elevateItem, startupItem, passThrough;
    private readonly CpuPowerSource cpuPower = new CpuPowerSource();
    private readonly int self;
    private readonly bool elevated = WindowsSession.IsAdministrator();
    private readonly bool hasCapturePermission = WindowsSession.CanCaptureFrames();
    private readonly double?[] values = new double?[(int)OverlayMetric.Count];
    private readonly int[] paintedValues = new int[(int)OverlayMetric.Count];
    private readonly Rectangle[] sections = new Rectangle[Groups.Length];
    private Rectangle displayArea, placementArea;
    private Size layoutSize, overlayClientSize;
    private float scale, displayScale, readingWidth, frameRateWidth, decimalWidth, frequencyWidth;
    private LayeredSurface surface;
    private TransparentHitArea hitArea;
    private bool layered = true, clickThrough, pressing, dragging, disposed;
    private bool positionChanged;
    private Point desktopOffset, dragOrigin, dragCursor;
    private string desktopDisplay, targetName = "", candidateName;
    private Screen menuDisplay;
    private int lastCandidateForeground, candidatePid, targetPid, tick, nextTemperaturePoll, nextPowerPoll;
    private long candidateSince;
    private IntPtr lastForeground;
    private Task<double?> temperaturePoll;
    private Task<double?> powerPoll;

    public OverlayWindow(string path = null, OverlaySettings preferences = null)
    {
        settingsPath = path ?? OverlaySettings.DefaultPath;
        settings = preferences ?? OverlaySettings.Load(settingsPath);
        using (Process process = Process.GetCurrentProcess()) self = process.Id;
        using (Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            if (icon != null) Icon = (Icon)icon.Clone();
        Text = "FlickMeter";
        BackColor = Background;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Screen initial = settings.Position.HasValue ? Screen.FromPoint(settings.Position.Value) : Screen.PrimaryScreen;
        desktopDisplay = settings.DisplayName ?? initial.DeviceName;
        UpdateSize();
        Point defaultOffset = new Point((int)Math.Round((placementArea.Width - Width - Pixels(18)) / displayScale),
            (int)Math.Round(Pixels(18) / displayScale));
        desktopOffset = settings.DisplayOffset ?? (settings.Position.HasValue
            ? new Point(settings.Position.Value.X - displayArea.Left, settings.Position.Value.Y - displayArea.Top)
            : defaultOffset);
        RestorePosition();

        menu.Font = menuFont;
        KeepOpenOnSelection(menu);
        elevateItem = new ToolStripMenuItem("以管理员身份重启采集", null, (s, e) =>
        {
            menu.Close();
            if (Program.RestartElevated()) Close();
            else ShowNotice("管理员启动已取消或不可用");
        });
        passThrough = new ToolStripMenuItem("鼠标穿透") { CheckOnClick = true };
        passThrough.CheckedChanged += (s, e) => SetClickThrough(passThrough.Checked);
        var hudItem = new ToolStripMenuItem("性能浮窗");
        hudItem.Click += (s, e) =>
        {
            ToggleHud(menuDisplay ?? ForegroundDisplay());
            hudItem.Checked = settings.HudVisible;
        };
        startupItem = new ToolStripMenuItem("开机自启动", null, ToggleStartup);
        menu.Items.AddRange(new ToolStripItem[]
        {
            hudItem, AnchorMenu(), new ToolStripSeparator(),
            DisplayDataMenu(), LayoutMenu(), SizeMenu(), BackgroundMenu(), ColorMenu(), new ToolStripSeparator(),
            passThrough, elevateItem, startupItem,
            new ToolStripMenuItem("创建桌面快捷方式", null, CreateShortcut),
            new ToolStripSeparator(), new ToolStripMenuItem("退出", null, (s, e) => Close())
        });
        menu.Opening += (s, e) =>
        {
            menuDisplay = ForegroundDisplay();
            hudItem.Checked = settings.HudVisible;
            if (startupItem.Enabled)
            {
                try { startupItem.Checked = StartupRegistration.IsEnabled(); }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
            elevateItem.Available = !elevated && (UsesFrameCapture && capture.DependencyIssue == null &&
                (!hasCapturePermission || capture.AccessDenied) ||
                settings.Shows(OverlayMetric.CpuTemperature) &&
                !values[(int)OverlayMetric.CpuTemperature].HasValue && CpuTemperatureSource.AccessDenied ||
                settings.Shows(OverlayMetric.CpuPower) && !values[(int)OverlayMetric.CpuPower].HasValue && cpuPower.AccessDenied);
        };
        tray.ContextMenuStrip = menu;
        tray.Text = Text;
        tray.Icon = Icon;
        timer.Tick += (s, e) => RefreshMetrics();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    private bool IsPinned { get { return settings != null && settings.PositionLocked; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080; // No activation and no taskbar button.
            if (layered) parameters.ExStyle |= 0x00080000;
            if (clickThrough) parameters.ExStyle |= 0x00000020;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        tray.Visible = tray.Icon != null;
        UpdateVisibility();
        RefreshMetrics();
        Render();
        if (settings.HudVisible) timer.Start();
    }

    private void RefreshMetrics()
    {
        if (!settings.HudVisible) return;
        tick++;
        IntPtr foreground = WindowsSession.ForegroundWindow();
        uint processId;
        GetWindowThreadProcessId(foreground, out processId);
        long now = Stopwatch.GetTimestamp();
        ConfigureFrameCapture();
        UpdateGameTarget((int)processId, now);
        if (UsesFrameCapture)
            values[(int)OverlayMetric.Fps] = capture.Read(targetPid, now, out values[(int)OverlayMetric.LowFps]);
        if (settings.Shows(OverlayMetric.CpuUsage))
        {
            if (cpu == null) cpu = new CpuUsage();
            values[(int)OverlayMetric.CpuUsage] = cpu.Read();
        }
        else cpu = null;
        if (tick % 2 == 1 && settings.Shows(OverlayMetric.CpuClock))
        {
            if (cpuFrequency == null) cpuFrequency = new CpuFrequency();
            values[(int)OverlayMetric.CpuClock] = cpuFrequency.Read();
        }
        if (settings.Shows(OverlayMetric.MemoryUsage) || settings.Shows(OverlayMetric.MemoryUsed))
            PhysicalMemoryUsage.Read(out values[(int)OverlayMetric.MemoryUsage], out values[(int)OverlayMetric.MemoryUsed]);
        RefreshGpu();
        RefreshCpuTemperature();
        RefreshCpuPower();
        bool needsPaint = Visible && UpdateDisplayedValues();
        string trayStatus = UsesFrameCapture ? Text + "\n" + CaptureStatus() : Text;
        if (trayStatus.Length > 63) trayStatus = trayStatus.Substring(0, 62) + "…";
        if (trayStatus != tray.Text) tray.Text = trayStatus;
        if (Visible && !menu.Visible && !pressing && (foreground != lastForeground || tick % 4 == 0))
        {
            WindowsSession.KeepAbove(Handle);
            lastForeground = foreground;
        }
        if (needsPaint) Render();
    }

    private bool UsesFrameCapture
    {
        get { return settings.HudVisible && (settings.Shows(OverlayMetric.Fps) || settings.Shows(OverlayMetric.LowFps)); }
    }

    private void ConfigureFrameCapture()
    {
        bool trackLow = settings.Shows(OverlayMetric.LowFps);
        bool trackFrames = UsesFrameCapture;
        capture.Configure(trackFrames, trackLow);
        if (!trackFrames) values[(int)OverlayMetric.Fps] = null;
        if (!trackLow) values[(int)OverlayMetric.LowFps] = null;
    }

    private void RefreshGpu()
    {
        bool usageEnabled = settings.Shows(OverlayMetric.GpuUsage);
        bool temperatureEnabled = settings.Shows(OverlayMetric.GpuTemperature);
        bool powerEnabled = settings.Shows(OverlayMetric.GpuPower);
        bool memoryEnabled = settings.Shows(OverlayMetric.VideoMemoryUsed);
        bool clockEnabled = settings.Shows(OverlayMetric.GpuClock);
        if (!usageEnabled && !temperatureEnabled && !powerEnabled && !memoryEnabled && !clockEnabled)
        {
            if (gpu != null) gpu.Dispose();
            gpu = null;
            return;
        }
        if (gpu != null && tick % 2 == 0) return;
        if (gpu == null) gpu = new GpuMetrics();
        gpu.Read(targetPid, usageEnabled, temperatureEnabled, powerEnabled, memoryEnabled, clockEnabled,
            out values[(int)OverlayMetric.GpuTemperature], out values[(int)OverlayMetric.GpuUsage],
            out values[(int)OverlayMetric.GpuPower], out values[(int)OverlayMetric.VideoMemoryUsed], out values[(int)OverlayMetric.GpuClock]);
    }

    private void RefreshCpuTemperature()
    {
        if (temperaturePoll != null && temperaturePoll.IsCompleted)
        {
            if (temperaturePoll.IsFaulted) Debug.WriteLine(temperaturePoll.Exception);
            double? temperature = temperaturePoll.Status == TaskStatus.RanToCompletion ? temperaturePoll.Result : null;
            values[(int)OverlayMetric.CpuTemperature] = temperature;
            temperaturePoll = null;
            nextTemperaturePoll = tick + (temperature.HasValue ? 4 : 40);
        }
        if (temperaturePoll == null && tick >= nextTemperaturePoll && settings.Shows(OverlayMetric.CpuTemperature))
            temperaturePoll = Task.Run((Func<double?>)CpuTemperatureSource.Read);
    }

    private void RefreshCpuPower()
    {
        if (powerPoll != null && powerPoll.IsCompleted)
        {
            if (powerPoll.IsFaulted) Debug.WriteLine(powerPoll.Exception);
            values[(int)OverlayMetric.CpuPower] = powerPoll.Status == TaskStatus.RanToCompletion ? powerPoll.Result : null;
            powerPoll = null;
            nextPowerPoll = tick + 2;
        }
        if (powerPoll == null && tick >= nextPowerPoll && settings.Shows(OverlayMetric.CpuPower))
            powerPoll = Task.Run((Func<double?>)cpuPower.Read);
    }

    private static bool IsAlive(int pid)
    {
        try { using (Process process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    private void UpdateGameTarget(int foreground, long now)
    {
        if (targetPid != 0 && tick % 4 == 0 && !IsAlive(targetPid))
        {
            targetPid = 0;
            targetName = "";
        }
        bool measureTarget = foreground == targetPid;
        if (menu.Visible || pressing || foreground == self || measureTarget) foreground = 0;
        if (foreground != lastCandidateForeground)
        {
            lastCandidateForeground = foreground;
            candidateName = ForegroundName(foreground);
            candidatePid = candidateName == null ? 0 : foreground;
            candidateSince = now;
        }
        capture.Observe(targetPid, candidatePid, measureTarget, now);
        if (candidatePid == 0 || now - candidateSince < Stopwatch.Frequency) return;
        if (UsesFrameCapture && (capture.IsStarting || capture.IsRunning && !capture.HasRecentFrames(candidatePid, now, CandidateFrameCount))) return;

        targetPid = candidatePid;
        targetName = candidateName;
        candidatePid = 0;
        capture.Observe(targetPid, 0, measureActive: true, now: now);
    }

    private static string ForegroundName(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using (Process target = Process.GetProcessById(pid))
            {
                string name = target.ProcessName;
                return Array.Exists(ShellProcesses, entry => String.Equals(entry, name, StringComparison.OrdinalIgnoreCase)) ? null : name;
            }
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return "前台程序"; }
    }

    private string CaptureStatus()
    {
        if (!capture.IsRunning && capture.DependencyIssue != null) return capture.DependencyIssue;
        if (!capture.IsRunning && (!hasCapturePermission || capture.AccessDenied))
            return "画面采集缺少权限，可右键以管理员身份重启";
        if (capture.IsStarting) return "正在启动画面采集";
        if (!capture.IsRunning) return elevated ? "画面采集启动失败" : "画面采集未启动，可从托盘重试";
        if (targetPid == 0) return "等待游戏";
        if (!values[(int)OverlayMetric.Fps].HasValue) return targetName + " · 等待画面";
        return targetName;
    }

    private int Pixels(float units) { return (int)Math.Round(units * scale); }

    private static int DisplayNumber(double? value)
    {
        if (!value.HasValue || Double.IsNaN(value.Value) || value.Value < 0 || value.Value > Int32.MaxValue) return -1;
        return (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    private bool UpdateDisplayedValues()
    {
        bool changed = false;
        for (int i = 0; i < values.Length; i++)
        {
            int number = DisplayNumber(UsesDecimal(i) ? values[i] * 10 : values[i]);
            if (paintedValues[i] == number) continue;
            paintedValues[i] = number;
            changed |= settings.Shows((OverlayMetric)i);
        }
        return changed;
    }

    private void UpdateSize()
    {
        UpdateDisplayedValues();
        RefreshDisplayMode();
        float selectedScale = settings.Scale / 100f;
        scale = selectedScale * displayScale;
        int width = 0, height = 0;
        using (Bitmap measurement = new Bitmap(1, 1))
        using (Graphics g = Graphics.FromImage(measurement))
        using (var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap })
        {
            g.PageUnit = GraphicsUnit.Pixel;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(selectedScale, selectedScale);
            readingWidth = (float)Math.Ceiling(g.MeasureString("888", valueFont, PointF.Empty, format).Width);
            frameRateWidth = Math.Max(readingWidth,
                (float)Math.Ceiling(g.MeasureString("8888", compactValueFont, PointF.Empty, format).Width));
            decimalWidth = (float)Math.Ceiling(g.MeasureString("888.8", valueFont, PointF.Empty, format).Width);
            frequencyWidth = (float)Math.Ceiling(g.MeasureString("8888", valueFont, PointF.Empty, format).Width);
            for (int group = 0; group < sections.Length; group++)
            {
                sections[group] = Rectangle.Empty;
                float contentEnd = LayoutGroup(g, group, 0, 0, format, false);
                if (contentEnd == 0) continue;
                int sectionWidth = (int)Math.Ceiling(contentEnd) + SectionPadding;
                if (settings.Vertical)
                {
                    sections[group] = new Rectangle(0, height, sectionWidth, RowHeight);
                    width = Math.Max(width, sectionWidth);
                    height += RowHeight;
                }
                else
                {
                    sections[group] = new Rectangle(width, 0, sectionWidth, RowHeight);
                    width += sectionWidth;
                    height = RowHeight;
                }
            }
        }
        layoutSize = new Size(width, height);
        if (IsPinned && width > 0 && height > 0)
        {
            double fit = Math.Min(displayArea.Width / (double)width, displayArea.Height / (double)height);
            scale = (float)Math.Min(scale, fit);
        }
        overlayClientSize = new Size(Math.Max(1, Pixels(width)), Math.Max(1, Pixels(height)));
        if (!IsPinned && ClientSize != overlayClientSize) ClientSize = overlayClientSize;
    }

    private Screen OverlayDisplay()
    {
        return Array.Find(Screen.AllScreens, item => item.DeviceName == desktopDisplay) ?? Screen.PrimaryScreen;
    }

    private void RefreshDisplayMode()
    {
        Screen display = OverlayDisplay();
        placementArea = displayArea = display.Bounds;
        displayScale = 1f;
        var current = new DisplayMode { Size = (ushort)Marshal.SizeOf(typeof(DisplayMode)) };
        var saved = current;
        if (!EnumDisplaySettingsW(display.DeviceName, CurrentDisplaySettings, ref current) ||
            current.Width <= 0 || current.Height <= 0) return;

        Point origin = (current.Fields & DisplayModePosition) != 0
            ? new Point(current.X, current.Y) : displayArea.Location;
        placementArea = displayArea = new Rectangle(origin, new Size(current.Width, current.Height));
        if (!EnumDisplaySettingsW(display.DeviceName, SavedDisplaySettings, ref saved) ||
            saved.Width <= 0 || saved.Height <= 0) return;

        // A fullscreen mode can be fitted to the screen after the desktop has been composed.
        displayScale = Math.Max(current.Width / (float)saved.Width, current.Height / (float)saved.Height);
        int referenceWidth = (int)Math.Round(saved.Width * displayScale);
        int referenceHeight = (int)Math.Round(saved.Height * displayScale);
        placementArea = new Rectangle(displayArea.X + (current.Width - referenceWidth) / 2,
            displayArea.Y + (current.Height - referenceHeight) / 2, referenceWidth, referenceHeight);
    }

    private void RestorePosition()
    {
        if (IsPinned)
        {
            var bounds = new Rectangle(settings.GetFixedLocation(displayArea, overlayClientSize), overlayClientSize);
            if (Bounds != bounds) Bounds = bounds;
            return;
        }
        Point preferred = new Point(
            placementArea.Left + (int)Math.Round(desktopOffset.X * displayScale),
            placementArea.Top + (int)Math.Round(desktopOffset.Y * displayScale));
        Point position = Clamp(preferred, displayArea);
        if (position != Location) Location = position;
    }

    private void RememberFreePosition()
    {
        settings.Position = Location;
        settings.DisplayOffset = desktopOffset = new Point(
            (int)Math.Round((Left - placementArea.Left) / displayScale),
            (int)Math.Round((Top - placementArea.Top) / displayScale));
    }

    private Point Clamp(Point point, Rectangle area)
    {
        return new Point(Math.Max(area.Left, Math.Min(point.X, area.Right - Width)),
            Math.Max(area.Top, Math.Min(point.Y, area.Bottom - Height)));
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == GetDpiScaledSizeMessage && message.LParam != IntPtr.Zero && !overlayClientSize.IsEmpty)
        {
            Marshal.WriteInt32(message.LParam, overlayClientSize.Width);
            Marshal.WriteInt32(message.LParam, sizeof(int), overlayClientSize.Height);
            message.Result = new IntPtr(1);
            return;
        }
        if (message.Msg == DpiChangedMessage)
        {
            QueueDisplayRefresh();
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
        if (message.Msg == DisplayChangedMessage) QueueDisplayRefresh();
    }

    private void QueueDisplayRefresh()
    {
        if (!IsHandleCreated || disposed || overlayClientSize.IsEmpty) return;
        BeginInvoke((Action)(() =>
        {
            if (disposed || !IsHandleCreated) return;
            UpdateSize();
            if (!pressing) RestorePosition();
            if (hitArea != null) hitArea.Bounds = Bounds;
            Render();
        }));
    }

    private Screen ForegroundDisplay()
    {
        IntPtr foreground = WindowsSession.ForegroundWindow();
        uint process;
        GetWindowThreadProcessId(foreground, out process);
        if (foreground != IntPtr.Zero && process != self) return Screen.FromHandle(foreground);
        return OverlayDisplay();
    }

    private void ToggleHud(Screen display)
    {
        settings.HudVisible = !settings.HudVisible;
        if (settings.HudVisible && IsPinned) settings.DisplayName = desktopDisplay = display.DeviceName;
        if (!settings.HudVisible)
        {
            timer.Stop();
            capture.Configure(false, false);
            if (gpu != null) { gpu.Dispose(); gpu = null; }
            cpu = null;
            Array.Clear(values, 0, values.Length);
        }
        SetGeometry();
        if (settings.HudVisible) { RefreshMetrics(); timer.Start(); }
        else tray.Text = Text;
    }

    private void SetGeometry()
    {
        UpdateSize();
        RestorePosition();
        UpdateVisibility();
        SaveAndRender();
    }

    private void UpdateVisibility()
    {
        bool showHud = settings.HudVisible && settings.HasVisibleMetrics;
        if (!tray.Visible) return;
        if (!showHud)
        {
            FinishPositionDrag(false);
            if (hitArea != null) hitArea.Hide();
            Hide();
        }
        else if (!Visible)
        {
            Show();
            if (!layered) ApplyFallback();
            if (hitArea != null && !hitArea.Visible) hitArea.Show(this);
        }
    }

    private void Render()
    {
        if (!IsHandleCreated || !Visible) return;
        if (layered)
        {
            bool pinned = IsPinned;
            Size renderSize = pinned ? overlayClientSize : ClientSize;
            if (surface == null || surface.Size != renderSize)
            {
                if (surface != null) surface.Dispose();
                surface = LayeredSurface.Create(renderSize);
            }
            if (surface != null)
            {
                using (Graphics graphics = Graphics.FromImage(surface.Image))
                {
                    graphics.Clear(Color.Transparent);
                    DrawOverlay(graphics);
                }
                Point location = pinned ? settings.GetFixedLocation(displayArea, renderSize) : Location;
                if (surface.Update(Handle, location)) return;
                surface.Dispose();
                surface = null;
            }
            layered = false;
            RecreateHandle();
            RestorePosition();
            ApplyFallback();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!layered) DrawOverlay(e.Graphics);
    }

    private void ApplyFallback()
    {
        bool transparent = settings.Alpha == 0;
        BackColor = transparent ? Color.Fuchsia : Background;
        TransparencyKey = transparent ? Color.Fuchsia : Color.Empty;
        Opacity = transparent ? 1 : settings.Alpha / 255.0;
        if (transparent && !clickThrough && hitArea == null && Visible)
        {
            hitArea = new TransparentHitArea(this) { Bounds = Bounds };
            hitArea.Show(this);
        }
        else if ((!transparent || clickThrough) && hitArea != null)
        {
            hitArea.Dispose();
            hitArea = null;
        }
    }

    private sealed class TransparentHitArea : Form
    {
        public TransparentHitArea(OverlayWindow overlay)
        {
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 1.0 / 255;
            MouseDown += (s, e) => overlay.PressMouse(this, e);
            MouseMove += (s, e) => overlay.MoveMouse(this, e);
            MouseUp += (s, e) => overlay.ReleaseMouse(this, e);
            MouseCaptureChanged += (s, e) => { if (!Capture) overlay.FinishPositionDrag(false); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.ExStyle |= 0x08000080; return parameters; }
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (hitArea != null) hitArea.Bounds = Bounds;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (hitArea != null) hitArea.Bounds = Bounds;
    }

    private void SetClickThrough(bool enabled)
    {
        if (clickThrough == enabled) return;
        clickThrough = enabled;
        FinishPositionDrag(false);
        RecreateHandle();
        if (!layered) ApplyFallback();
        RestorePosition();
        if (!menu.Visible) WindowsSession.KeepAbove(Handle);
        Render();
    }

    internal void DrawOverlay(Graphics g)
    {
        if (!settings.HasVisibleMetrics) return;
        if (layered && settings.Alpha == 0 && !clickThrough) g.Clear(Color.FromArgb(1, 0, 0, 0));
        g.PageUnit = GraphicsUnit.Pixel;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(scale, scale);
        Size panel = layoutSize;
        if (settings.Alpha > 0)
        {
            int alpha = layered ? settings.Alpha : 255;
            using (GraphicsPath shape = RoundedPanel(panel))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(alpha, Background)))
            using (Pen line = new Pen(Color.FromArgb(alpha, Border)))
            {
                g.FillPath(fill, shape);
                g.DrawPath(line, shape);
            }
        }
        DrawSeparators(g, panel);
        using (var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap })
        {
            for (int group = 0; group < sections.Length; group++)
            {
                Rectangle section = sections[group];
                if (!section.IsEmpty) LayoutGroup(g, group, section.X, section.Y, format, true);
            }
        }
    }

    private float LayoutGroup(Graphics g, int index, int x, int y, StringFormat format, bool draw)
    {
        MetricGroup group = Groups[index];
        Color? custom = settings.TextColor ??
            (settings.MetricColors == null ? (Color?)null : settings.MetricColors[index % settings.MetricColors.Length]);
        float end = 0;
        foreach (OverlayMetric metric in group.Metrics)
        {
            if (!settings.Shows(metric)) continue;
            int gap = ReadingGap;
            if (end == 0)
            {
                end = LayoutText(g, group.Title, labelFont, custom ?? Bright, x + SectionPadding, y + 7, format, draw);
                gap = LabelGap;
            }
            end = LayoutReading(g, (int)metric, end + gap, y, custom, format, draw);
        }
        return end;
    }

    private void DrawSeparators(Graphics g, Size panel)
    {
        using (Pen white = new Pen(Color.White, Math.Max(1, (float)Math.Round(scale)) / scale))
        {
            g.SmoothingMode = SmoothingMode.None;
            foreach (Rectangle section in sections)
            {
                if (settings.Vertical && section.Y > 0)
                    g.DrawLine(white, 8, section.Y, panel.Width - 8, section.Y);
                else if (!settings.Vertical && section.X > 0)
                    g.DrawLine(white, section.X, 7, section.X, 23);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }
    }

    private float LayoutReading(Graphics g, int metric, float x, int y, Color? custom, StringFormat format, bool draw)
    {
        bool decimalValue = UsesDecimal(metric);
        bool frequency = metric == (int)OverlayMetric.CpuClock || metric == (int)OverlayMetric.GpuClock;
        float slotWidth = frequency ? frequencyWidth :
            decimalValue ? decimalWidth : metric <= (int)OverlayMetric.LowFps ? frameRateWidth : readingWidth;
        float next = x + slotWidth;
        string unit = MetricUnits[metric];
        int value = paintedValues[metric];
        Color color = custom ?? (value >= 0 ? ReadingColors[metric] : Muted);
        if (draw)
        {
            string number = value < 0 ? "—" : decimalValue ? (value / 10.0).ToString("F1", CultureInfo.InvariantCulture) :
                value.ToString(CultureInfo.InvariantCulture);
            bool compact = !frequency && !decimalValue && number.Length > 3;
            Font font = compact ? compactValueFont : valueFont;
            int top = y + (compact ? 9 : 5);
            float numberWidth = g.MeasureString(number, font, PointF.Empty, format).Width;
            if (numberWidth <= slotWidth)
                DrawText(g, number, font, color, next - numberWidth, top, format);
            else
            {
                GraphicsState state = g.Save();
                g.TranslateTransform(x, 0);
                g.ScaleTransform(slotWidth / numberWidth, 1);
                DrawText(g, number, font, color, 0, top, format);
                g.Restore(state);
            }
        }
        return unit.Length == 0 ? next : LayoutText(g, unit, unitFont, color, next + 1, y + 8, format, draw);
    }

    private float LayoutText(Graphics g, string text, Font font, Color color, float x, float y, StringFormat format, bool draw)
    {
        if (draw) DrawText(g, text, font, color, x, y, format);
        return x + g.MeasureString(text, font, PointF.Empty, format).Width;
    }

    private static bool UsesDecimal(int metric)
    {
        return metric >= (int)OverlayMetric.CpuPower && metric <= (int)OverlayMetric.VideoMemoryUsed;
    }

    private void DrawText(Graphics g, string text, Font font, Color color, float x, float y, StringFormat format)
    {
        if (settings.Alpha < 200)
        {
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(218, color.GetBrightness() < 0.28f ? Color.White : Color.Black)))
                g.DrawString(text, font, shadow, new PointF(x + 1, y + 1), format);
        }
        using (SolidBrush ink = new SolidBrush(color)) g.DrawString(text, font, ink, new PointF(x, y), format);
    }

    private static GraphicsPath RoundedPanel(Size size)
    {
        const int corner = 20;
        float right = size.Width - corner - .5f;
        float bottom = size.Height - corner - .5f;
        GraphicsPath shape = new GraphicsPath();
        shape.AddArc(.5f, .5f, corner, corner, 180, 90);
        shape.AddArc(right, .5f, corner, corner, 270, 90);
        shape.AddArc(right, bottom, corner, corner, 0, 90);
        shape.AddArc(.5f, bottom, corner, corner, 90, 90);
        shape.CloseFigure();
        return shape;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        PressMouse(this, e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MoveMouse(this, e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        ReleaseMouse(this, e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) FinishPositionDrag(false);
    }

    private void PressMouse(Control source, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || IsPinned || clickThrough) return;
        pressing = true;
        dragCursor = source.PointToScreen(e.Location);
        dragOrigin = Location;
        source.Capture = true;
    }

    private void MoveMouse(Control source, MouseEventArgs e)
    {
        if (!pressing || !source.Capture) return;
        Point cursor = source.PointToScreen(e.Location);
        int dx = cursor.X - dragCursor.X, dy = cursor.Y - dragCursor.Y;
        dragging |= Math.Abs(dx) > Pixels(3) || Math.Abs(dy) > Pixels(3);
        if (dragging) Location = Clamp(new Point(dragOrigin.X + dx, dragOrigin.Y + dy), Screen.FromPoint(cursor).Bounds);
    }

    private void ReleaseMouse(Control source, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && !clickThrough)
            menu.Show(source.PointToScreen(e.Location));
        if (e.Button != MouseButtons.Left || !pressing) return;
        MoveMouse(source, e);
        FinishPositionDrag(true);
        source.Capture = false;
    }

    private void FinishPositionDrag(bool commit)
    {
        if (!pressing) return;
        bool moved = dragging && Location != dragOrigin;
        pressing = dragging = false;
        if (!moved) return;
        if (!commit)
        {
            RestorePosition();
            return;
        }
        settings.DisplayName = desktopDisplay = Screen.FromRectangle(Bounds).DeviceName;
        UpdateSize();
        Location = Clamp(Location, displayArea);
        RememberFreePosition();
        SaveAndRender();
    }

    private ToolStripMenuItem DisplayDataMenu()
    {
        var data = new ToolStripMenuItem("显示数据");
        KeepOpenOnSelection(data.DropDown);
        foreach (MetricGroup group in Groups)
        {
            if (data.DropDownItems.Count != 0) data.DropDownItems.Add(new ToolStripSeparator());
            foreach (OverlayMetric metric in group.Metrics)
            {
                var option = new ToolStripMenuItem(MetricNames[(int)metric]) { CheckOnClick = true, Checked = settings.Shows(metric) };
                option.Click += (s, e) =>
                {
                    settings.SetVisible(metric, option.Checked);
                    nextTemperaturePoll = nextPowerPoll = tick;
                    ConfigureFrameCapture();
                    SetGeometry();
                };
                data.DropDownItems.Add(option);
            }
        }
        return data;
    }

    private ToolStripMenuItem AnchorMenu()
    {
        var anchors = new ToolStripMenuItem("固定位置");
        var unlock = new ToolStripMenuItem("解除固定（可拖动）") { Checked = !IsPinned };
        anchors.DropDown.BackColor = Color.White;
        var positions = new PositionSliders(settings.FixedPosition, position =>
        {
            ApplyFixedPosition(position);
            unlock.Checked = false;
        });
        var host = new ToolStripControlHost(positions)
        {
            AutoSize = false, Size = positions.Size, Margin = Padding.Empty
        };
        unlock.Click += (s, e) =>
        {
            if (IsPinned) RememberFreePosition();
            settings.PositionLocked = false;
            passThrough.Checked = false;
            unlock.Checked = true;
            SetGeometry();
        };
        anchors.DropDownItems.AddRange(new ToolStripItem[] { host, new ToolStripSeparator(), unlock });
        KeepOpenOnSelection(anchors.DropDown);
        anchors.DropDownClosed += (s, e) => CommitPosition();
        return anchors;
    }

    private void ApplyFixedPosition(Point position)
    {
        bool wasPinned = IsPinned;
        if (wasPinned && settings.FixedPosition == position) return;
        FinishPositionDrag(false);
        settings.FixedPosition = position;
        settings.PositionLocked = true;
        positionChanged = true;
        if (!wasPinned)
        {
            settings.DisplayName = desktopDisplay = (menuDisplay ?? ForegroundDisplay()).DeviceName;
            UpdateSize();
        }
        RestorePosition();
        if (!wasPinned) Render();
    }

    private void CommitPosition()
    {
        if (!positionChanged) return;
        settings.Save(settingsPath);
        positionChanged = false;
    }

    private ToolStripMenuItem LayoutMenu()
    {
        ToolStripMenuItem layout = new ToolStripMenuItem("显示方向");
        AddChoice(layout, "横版", () => !settings.Vertical, () => { settings.Vertical = false; SetGeometry(); });
        AddChoice(layout, "竖版", () => settings.Vertical, () => { settings.Vertical = true; SetGeometry(); });
        return layout;
    }

    private ToolStripMenuItem SizeMenu()
    {
        ToolStripMenuItem size = new ToolStripMenuItem("浮窗大小");
        foreach (int percent in OverlaySettings.ScaleChoices)
            AddChoice(size, percent + "%", () => settings.Scale == percent, () => { settings.Scale = percent; SetGeometry(); });
        return size;
    }

    private ToolStripMenuItem BackgroundMenu()
    {
        ToolStripMenuItem background = new ToolStripMenuItem("背景透明度");
        AddChoice(background, "实色", () => settings.Alpha == 255, () => SetBackground(255));
        AddChoice(background, "半透明", () => settings.Alpha == 135, () => SetBackground(135));
        AddChoice(background, "全透明", () => settings.Alpha == 0, () => SetBackground(0));
        return background;
    }

    private void SetBackground(byte alpha)
    {
        settings.Alpha = alpha;
        if (!layered) ApplyFallback();
        SaveAndRender();
    }

    private ToolStripMenuItem ColorMenu()
    {
        ToolStripMenuItem color = new ToolStripMenuItem("文字颜色");
        AddChoice(color, "默认", () => settings.TextColor == null && settings.MetricColors == null, () => SetColor(null));
        AddChoice(color, "彩色（随机配色）", () => settings.MetricColors != null, () =>
        {
            settings.SetPalette(Guid.NewGuid().GetHashCode());
            SaveAndRender();
        });
        AddColorChoice(color, "白色", Color.White);
        AddColorChoice(color, "薄荷绿", Accent);
        AddColorChoice(color, "浅蓝色", Color.FromArgb(147, 214, 255));
        AddColorChoice(color, "琥珀色", Color.FromArgb(255, 216, 139));
        color.DropDownItems.Add(new ToolStripMenuItem("自定义…", null, (s, e) =>
        {
            menu.Close();
            using (ColorDialog dialog = new ColorDialog { Color = settings.TextColor ?? Bright, FullOpen = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) SetColor(dialog.Color);
        }));
        return color;
    }

    private void SetColor(Color? color)
    {
        settings.SetColor(color);
        SaveAndRender();
    }

    private void AddColorChoice(ToolStripMenuItem parent, string text, Color color)
    {
        AddChoice(parent, text, () => settings.TextColor.HasValue && settings.TextColor.Value.ToArgb() == color.ToArgb(),
            () => SetColor(color));
    }

    private static void KeepOpenOnSelection(ToolStripDropDown dropDown)
    {
        dropDown.Closing += (s, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
        };
    }

    private static void RefreshChoices(ToolStripMenuItem parent)
    {
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            var option = item as ToolStripMenuItem;
            var selected = item.Tag as Func<bool>;
            if (option != null && selected != null) option.Checked = selected();
        }
    }

    private static void AddChoice(ToolStripMenuItem parent, string text, Func<bool> selected, Action select)
    {
        if (parent.DropDownItems.Count == 0)
        {
            KeepOpenOnSelection(parent.DropDown);
            parent.DropDownOpening += (s, e) => RefreshChoices(parent);
        }
        var option = new ToolStripMenuItem(text) { Tag = selected, Checked = selected() };
        option.Click += (s, e) =>
        {
            select();
            RefreshChoices(parent);
        };
        parent.DropDownItems.Add(option);
    }

    private void SaveAndRender()
    {
        settings.Save(settingsPath);
        positionChanged = false;
        Render();
    }

    private void ShowNotice(string text)
    {
        tray.ShowBalloonTip(3000, Text, text, ToolTipIcon.None);
    }

    private async void ToggleStartup(object sender, EventArgs e)
    {
        if (!startupItem.Enabled) return;
        bool enable = !startupItem.Checked;
        startupItem.Enabled = false;
        string notice = "自启动设置失败或管理员授权已取消";
        try
        {
            if (await Task.Run(() => StartupRegistration.SetEnabled(enable, elevated)))
            {
                if (IsDisposed) return;
                startupItem.Checked = enable;
                notice = enable ? "已开启开机自启动" : "已关闭开机自启动";
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { if (!IsDisposed) startupItem.Enabled = true; }
        if (!IsDisposed) ShowNotice(notice);
    }

    private void CreateShortcut(object sender, EventArgs e)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (String.IsNullOrEmpty(desktop)) throw new IOException("Desktop directory is unavailable.");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, "FlickMeter.lnk"));
            shortcut.TargetPath = Application.ExecutablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            shortcut.Description = "查看游戏帧率、显卡和处理器指标";
            shortcut.Save();
            ShowNotice("已创建桌面快捷方式");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowNotice("无法创建桌面快捷方式");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            CommitPosition();
            disposed = true;
            FinishPositionDrag(false);
            timer.Dispose();
            tray.Dispose();
            menu.Dispose();
            capture.Dispose();
            if (gpu != null) gpu.Dispose();
            if (surface != null) surface.Dispose();
            if (hitArea != null) hitArea.Dispose();
            if (Icon != null) Icon.Dispose();
            menuFont.Dispose();
            labelFont.Dispose();
            valueFont.Dispose();
            unitFont.Dispose();
            compactValueFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
