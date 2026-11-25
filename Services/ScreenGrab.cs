using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace DynamicIsland.Services;

/// <summary>
/// Small screen grabbers for the frosted glass, and saving a picture as PNG. The plain StretchBlt path leaves
/// layered windows (the island itself) out of the picture.
/// </summary>
public static class ScreenGrab
{
    /// <summary>Like CaptureSmall, but from a picture of the desktop taken earlier (whose top-left is at screen point at).</summary>
    public static byte[]? CropSmall(Drawing.Bitmap plate, Drawing.Point at, Drawing.Rectangle r, int w, int h, byte[]? buffer)
    {
        if (w < 1 || h < 1 || r.Width < 1 || r.Height < 1) return null;
        using var small = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(plate, new Drawing.Rectangle(0, 0, w, h), new Drawing.Rectangle(r.Left - at.X, r.Top - at.Y, r.Width, r.Height), Drawing.GraphicsUnit.Pixel);
        }
        if (buffer == null || buffer.Length != w * h * 4) buffer = new byte[w * h * 4];
        var data = small.LockBits(new Drawing.Rectangle(0, 0, w, h), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * w * 4, w * 4); }
        finally { small.UnlockBits(data); }
        return buffer;
    }

    private static readonly object SmallLock = new();

    /// <summary>Any thread (the glass's grab loop and the one-off grabs share the small bitmap).</summary>
    public static byte[]? CaptureSmall(Drawing.Rectangle r, int w, int h, byte[]? buffer)
    {
        if (w < 1 || h < 1 || r.Width < 1 || r.Height < 1) return null;
        lock (SmallLock) return CaptureSmallLocked(r, w, h, buffer);
    }

    private static Drawing.Bitmap? _small;

    private static byte[]? CaptureSmallLocked(Drawing.Rectangle r, int w, int h, byte[]? buffer)
    {
        if (_small == null || _small.Width != w || _small.Height != h)
        {
            _small?.Dispose();
            _small = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppRgb);
        }
        using (var g = Drawing.Graphics.FromImage(_small))
        {
            var dst = g.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                SetStretchBltMode(dst, 4);   // HALFTONE: averages the pixels instead of skipping them
                StretchBlt(dst, 0, 0, w, h, screen, r.Left, r.Top, r.Width, r.Height, 0x00CC0020);
            }
            finally { ReleaseDC(IntPtr.Zero, screen); g.ReleaseHdc(dst); }
        }
        if (buffer == null || buffer.Length != w * h * 4) buffer = new byte[w * h * 4];
        var data = _small.LockBits(new Drawing.Rectangle(0, 0, w, h), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * w * 4, w * 4); }
        finally { _small.UnlockBits(data); }
        return buffer;
    }

    public static string SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
}
