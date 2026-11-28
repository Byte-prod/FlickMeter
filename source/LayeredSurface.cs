using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

internal sealed class LayeredSurface : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, BitsPerPixel;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ImportantColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend
    {
        public byte Operation, Flags, ConstantAlpha, AlphaFormat;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapHeader info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr item);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, ref Point location,
        ref Size size, IntPtr source, ref Point origin, uint colorKey, ref Blend blend, uint flags);

    private IntPtr dc, dib, previous;
    internal Bitmap Image { get; private set; }
    internal Size Size { get; private set; }

    private LayeredSurface(Size size) { Size = size; }

    internal static LayeredSurface Create(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return null;
        var surface = new LayeredSurface(size);
        try
        {
            if (surface.Initialize()) return surface;
        }
        catch (ArgumentException) { }
        catch (ExternalException) { }
        surface.Dispose();
        return null;
    }

    private bool Initialize()
    {
        dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        var info = new BitmapHeader
        {
            HeaderSize = (uint)Marshal.SizeOf(typeof(BitmapHeader)),
            Width = Size.Width, Height = -Size.Height,
            Planes = 1, BitsPerPixel = 32
        };
        IntPtr bits;
        dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
        previous = SelectObject(dc, dib);
        if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return false;
        Image = new Bitmap(Size.Width, Size.Height, Size.Width * 4, PixelFormat.Format32bppPArgb, bits);
        return true;
    }

    internal bool Update(IntPtr window, Point location)
    {
        const uint UseAlpha = 2;
        Size size = Size;
        Point origin = Point.Empty;
        var blend = new Blend { ConstantAlpha = 255, AlphaFormat = 1 };
        return UpdateLayeredWindow(window, IntPtr.Zero, ref location, ref size, dc, ref origin, 0, ref blend, UseAlpha);
    }

    public void Dispose()
    {
        if (Image != null) { Image.Dispose(); Image = null; }
        if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        if (dc != IntPtr.Zero) DeleteDC(dc);
        dc = dib = previous = IntPtr.Zero;
    }
}
