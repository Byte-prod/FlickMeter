using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class PositionSliders : TableLayoutPanel
{
    private readonly NumericUpDown horizontal, vertical;
    private readonly Action<Point> change;

    public PositionSliders(Point value, Action<Point> change)
    {
        this.change = change;
        BackColor = Color.White;
        ForeColor = Color.Black;
        Padding = new Padding(12, 8, 12, 8);
        Size = new Size(336, 118);
        ColumnCount = 2;
        RowCount = 4;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        horizontal = AddAxis(0, "横向位置", value.X);
        vertical = AddAxis(2, "纵向位置", value.Y);
    }

    private NumericUpDown AddAxis(int row, string title, int value)
    {
        var label = new Label { Text = title, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        var slider = new PositionTrackBar
        {
            AccessibleName = title,
            Maximum = 100, Value = value, SmallChange = 1, LargeChange = 10,
            TickStyle = TickStyle.None, AutoSize = false, Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 4), BackColor = BackColor
        };
        var number = new NumericUpDown
        {
            AccessibleName = title + "数值",
            Maximum = 100, Value = value, TextAlign = HorizontalAlignment.Center, Dock = DockStyle.Top,
            Margin = new Padding(0, 3, 0, 0), BackColor = BackColor, ForeColor = ForeColor
        };
        Controls.Add(label, 0, row);
        SetColumnSpan(label, 2);
        Controls.Add(slider, 0, row + 1);
        Controls.Add(number, 1, row + 1);

        slider.ValueChanged += (s, e) => number.Value = slider.Value;
        slider.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) Apply(); };
        number.ValueChanged += (s, e) =>
        {
            slider.Value = (int)number.Value;
            Apply();
        };
        return number;
    }

    private void Apply()
    {
        change(new Point((int)horizontal.Value, (int)vertical.Value));
    }

    private sealed class PositionTrackBar : TrackBar
    {
        private const int ReflectedNotify = 0x204e, CustomDrawNotice = -12;
        private const int PrePaint = 1, ItemPrePaint = 0x10001, Channel = 3;
        private const int NotifyItemDraw = 0x20, SkipDefault = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct NotificationHeader
        {
            public IntPtr Window;
            public UIntPtr ControlId;
            public int Code;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CustomDraw
        {
            public NotificationHeader Header;
            public int Stage;
            public IntPtr DeviceContext;
            public int Left, Top, Right, Bottom;
            public UIntPtr Item;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == ReflectedNotify && message.LParam != IntPtr.Zero &&
                Marshal.ReadInt32(message.LParam, 2 * IntPtr.Size) == CustomDrawNotice)
            {
                var draw = Marshal.PtrToStructure<CustomDraw>(message.LParam);
                if (draw.Stage == PrePaint)
                {
                    message.Result = new IntPtr(NotifyItemDraw);
                    return;
                }
                if (draw.Stage == ItemPrePaint && draw.Item.ToUInt64() == Channel &&
                    draw.DeviceContext != IntPtr.Zero && draw.Right > draw.Left)
                {
                    int middle = draw.Top + (draw.Bottom - draw.Top) / 2;
                    using (var graphics = Graphics.FromHdc(draw.DeviceContext))
                        graphics.DrawLine(Pens.Black, draw.Left, middle, draw.Right - 1, middle);
                    message.Result = new IntPtr(SkipDefault);
                    return;
                }
            }
            base.WndProc(ref message);
        }
    }
}
