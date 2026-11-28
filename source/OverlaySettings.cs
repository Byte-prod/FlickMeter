using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security;

internal enum OverlayMetric
{
    Fps, LowFps, CpuUsage, CpuTemperature, GpuUsage, GpuTemperature, MemoryUsage,
    CpuPower, GpuPower, MemoryUsed, VideoMemoryUsed, CpuClock, GpuClock, Count
}

internal sealed class OverlaySettings
{
    private const int AllMetrics = (1 << (int)OverlayMetric.Count) - 1;
    private const int LegacyMetrics = (1 << (int)OverlayMetric.CpuPower) - 1;
    private const int PreviousMetrics = (1 << (int)OverlayMetric.CpuClock) - 1;
    private const string MetricsVersion = "metrics-version:3";
    private const string LegacyPalettePrefix = "palette-";
    private const string ExpandedPalettePrefix = "palette-v2-";
    public static readonly int[] ScaleChoices = { 130, 145, 160, 175, 190, 205, 220, 235, 250, 265 };
    public byte Alpha = 255;
    public int Scale = ScaleChoices[0];
    public bool Vertical;
    public bool HudVisible = true;
    public bool PositionLocked = true;
    public Point FixedPosition = new Point(50, 0);
    public Point? Position;
    public Point? DisplayOffset;
    public string DisplayName;
    public Color? TextColor { get; private set; }
    public Color[] MetricColors { get; private set; }
    private int visibleMetrics = AllMetrics;
    public bool HasVisibleMetrics { get { return visibleMetrics != 0; } }
    private int colorSeed;
    private bool expandedPalette;
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string DefaultPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameOverlay", "appearance.txt"); }
    }

    public static OverlaySettings Load(string path)
    {
        var settings = new OverlaySettings();
        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 3) return settings;
            byte alpha;
            int scale, color, seed, x, y;
            if (Byte.TryParse(lines[0], NumberStyles.None, Culture, out alpha)) settings.Alpha = alpha;
            if (Int32.TryParse(lines[1], NumberStyles.None, Culture, out scale) &&
                scale >= 50 && scale <= ScaleChoices[ScaleChoices.Length - 1])
                settings.Scale = ClosestScale(scale);
            if (Int32.TryParse(lines[2], NumberStyles.HexNumber, Culture, out color) && color >= 0 && color <= 0xffffff)
                settings.SetColor(Color.FromArgb(unchecked((int)0xff000000) | color));
            else if (lines[2].StartsWith(ExpandedPalettePrefix, StringComparison.Ordinal) &&
                Int32.TryParse(lines[2].Substring(ExpandedPalettePrefix.Length), NumberStyles.Integer, Culture, out seed))
                settings.SetPalette(seed);
            else if (lines[2].StartsWith(LegacyPalettePrefix, StringComparison.Ordinal) &&
                Int32.TryParse(lines[2].Substring(LegacyPalettePrefix.Length), NumberStyles.Integer, Culture, out seed))
                settings.SetPalette(seed, false);
            if (lines.Length >= 6 && Int32.TryParse(lines[3], NumberStyles.Integer, Culture, out x) &&
                Int32.TryParse(lines[4], NumberStyles.Integer, Culture, out y))
                settings.Position = new Point(x, y);
            if (lines.Length >= 6 && !String.IsNullOrEmpty(lines[5])) settings.DisplayName = lines[5];
            settings.Vertical = lines.Length >= 7 && lines[6] == "vertical";
            int mask;
            if (lines.Length >= 8 && lines[7].StartsWith("metrics:", StringComparison.Ordinal) &&
                Int32.TryParse(lines[7].Substring(8), NumberStyles.None, Culture, out mask))
            {
                settings.visibleMetrics = mask & AllMetrics;
                if (mask != 0 && (lines.Length < 13 || lines[12] != MetricsVersion))
                {
                    int previousMask = lines.Length >= 13 && lines[12] == "metrics-version:2" ? PreviousMetrics : LegacyMetrics;
                    settings.visibleMetrics |= AllMetrics & ~previousMask;
                }
            }
            if (lines.Length >= 9) settings.HudVisible = lines[8] != "hud:off";
            if (lines.Length >= 10)
            {
                switch (lines[9])
                {
                    case "anchor:TopLeft": settings.FixedPosition = new Point(0, 0); break;
                    case "anchor:TopRight": settings.FixedPosition = new Point(100, 0); break;
                    case "anchor:BottomLeft": settings.FixedPosition = new Point(0, 100); break;
                    case "anchor:BottomRight": settings.FixedPosition = new Point(100, 100); break;
                    case "anchor:Free": settings.PositionLocked = false; break;
                }
            }
            if (lines.Length >= 12 && Int32.TryParse(lines[10], NumberStyles.Integer, Culture, out x) &&
                Int32.TryParse(lines[11], NumberStyles.Integer, Culture, out y))
                settings.DisplayOffset = new Point(x, y);
            if (lines.Length >= 14 && lines[13].StartsWith("fixed:", StringComparison.Ordinal))
            {
                string[] position = lines[13].Substring(6).Split(',');
                if (position.Length == 2 && Int32.TryParse(position[0], NumberStyles.None, Culture, out x) &&
                    Int32.TryParse(position[1], NumberStyles.None, Culture, out y) && x <= 100 && y <= 100)
                    settings.FixedPosition = new Point(x, y);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        return settings;
    }

    private static int ClosestScale(int saved)
    {
        int closest = ScaleChoices[0];
        foreach (int choice in ScaleChoices)
            if (Math.Abs(choice - saved) < Math.Abs(closest - saved)) closest = choice;
        return closest;
    }

    public Point GetFixedLocation(Rectangle display, Size hud)
    {
        int x = (int)Math.Round(Math.Max(0, display.Width - hud.Width) * (FixedPosition.X / 100.0));
        int y = (int)Math.Round(Math.Max(0, display.Height - hud.Height) * (FixedPosition.Y / 100.0));
        return new Point(display.Left + x, display.Top + y);
    }

    public void SetColor(Color? color)
    {
        TextColor = color;
        MetricColors = null;
    }

    public bool Shows(OverlayMetric metric)
    {
        return (visibleMetrics & (1 << (int)metric)) != 0;
    }

    public void SetVisible(OverlayMetric metric, bool visible)
    {
        int bit = 1 << (int)metric;
        visibleMetrics = visible ? visibleMetrics | bit : visibleMetrics & ~bit;
    }

    public void SetPalette(int seed)
    {
        SetPalette(seed, true);
    }

    private void SetPalette(int seed, bool expanded)
    {
        TextColor = null;
        colorSeed = seed;
        expandedPalette = expanded;
        var originalColors = new[] { Color.FromArgb(119, 231, 194), Color.FromArgb(255, 207, 118),
            Color.FromArgb(131, 205, 255), Color.FromArgb(238, 165, 225),
            Color.FromArgb(192, 175, 255), Color.FromArgb(255, 163, 139) };
        if (expanded)
        {
            var additionalColors = new[]
            {
                Color.FromArgb(255, 147, 166), Color.FromArgb(255, 189, 226),
                Color.FromArgb(255, 185, 117), Color.FromArgb(250, 232, 133),
                Color.FromArgb(211, 240, 132), Color.FromArgb(159, 229, 146),
                Color.FromArgb(112, 224, 224), Color.FromArgb(156, 246, 239),
                Color.FromArgb(155, 180, 255), Color.FromArgb(197, 219, 255),
                Color.FromArgb(220, 168, 255), Color.FromArgb(232, 220, 249)
            };
            MetricColors = new Color[originalColors.Length + additionalColors.Length];
            originalColors.CopyTo(MetricColors, 0);
            additionalColors.CopyTo(MetricColors, originalColors.Length);
        }
        else MetricColors = originalColors;

        var random = new Random(seed);
        for (int end = MetricColors.Length - 1; end > 0; end--)
        {
            int pick = random.Next(end + 1);
            Color last = MetricColors[end];
            MetricColors[end] = MetricColors[pick];
            MetricColors[pick] = last;
        }
    }

    public void Save(string path)
    {
        string pending = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string color = TextColor.HasValue ? (TextColor.Value.ToArgb() & 0xffffff).ToString("X6", Culture) : "default";
            if (MetricColors != null)
                color = (expandedPalette ? ExpandedPalettePrefix : LegacyPalettePrefix) + colorSeed.ToString(Culture);
            File.WriteAllLines(pending, new[] { Alpha.ToString(Culture), Scale.ToString(Culture), color,
                Position.HasValue ? Position.Value.X.ToString(Culture) : "",
                Position.HasValue ? Position.Value.Y.ToString(Culture) : "", DisplayName ?? "",
                Vertical ? "vertical" : "horizontal", "metrics:" + visibleMetrics.ToString(Culture),
                HudVisible ? "hud:on" : "hud:off", PositionLocked ? "anchor:TopCenter" : "anchor:Free",
                DisplayOffset.HasValue ? DisplayOffset.Value.X.ToString(Culture) : "",
                DisplayOffset.HasValue ? DisplayOffset.Value.Y.ToString(Culture) : "", MetricsVersion,
                "fixed:" + FixedPosition.X.ToString(Culture) + "," + FixedPosition.Y.ToString(Culture) });
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
    }
}
