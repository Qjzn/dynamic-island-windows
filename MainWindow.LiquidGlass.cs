using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using DynamicIsland.Views;

namespace DynamicIsland;

/// <summary>
/// Liquid glass (the "Жидкое стекло" theme): the island is clear frosted glass over what's really behind it,
/// and near its edge the glass acts like a lens — it bends the picture from just outside the rim inwards,
/// and splits the colours a little (red bends more than blue), the way thick curved glass does.
/// The lens is a pixel shader (GlassEffect) on the glass's picture, before its blur; where the shader can't be
/// compiled, Refract does the same on the CPU.
/// </summary>
public partial class MainWindow
{
    private bool LiquidGlass => _settings.Scene == "liquid" && _settings.Glass && !Lite;
    /// <summary>"Полупрозрачная": no picture and no blur, the island itself is see-through (costs nothing, so with light graphics too).</summary>
    private bool ClearGlass => _settings.Scene == "clear" && IslandGlass;
    /// <summary>"Матовая": the strongly blurred picture under an even tint, no lens at the rim.</summary>
    private bool MatteGlass => _settings.Scene == "matte" && IslandGlass && !Lite;
    /// <summary>A theme whose island is one pane of glass, with panes inside and its own density slider.</summary>
    private bool GlassScene => IsGlassScene(_settings.Scene);
    private static bool IsGlassScene(string? scene) => scene is "liquid" or "clear" or "matte";

    private int SceneTint
    {
        get => _settings.Scene switch { "clear" => _settings.ClearTint, "matte" => _settings.MatteTint, _ => _settings.LiquidTint };
        set { if (_settings.Scene == "clear") _settings.ClearTint = value; else if (_settings.Scene == "matte") _settings.MatteTint = value; else _settings.LiquidTint = value; }
    }

    /// <summary>The see-through island's density: 0.15…0.95. There is no picture to measure, so it doesn't follow the backdrop.</summary>
    private double ClearAlpha() => 0.15 + 0.8 * Math.Clamp(_settings.ClearTint, 0, 100) / 100.0;

    private byte[]? _liquidPixels;

    private GlassEffect? _lens;
    private bool _frostShiftedOff;
    private bool _lensTried;

    /// <summary>Lighter blur than plain frost: matte, yet you can tell what's behind and see it bend.</summary>
    private void ApplyLiquidBlur()
    {
        if (LiquidGlass && !_lensTried) { _lensTried = true; _lens = GlassEffect.TryCreate(); }
        var lens = LiquidGlass ? _lens : null;
        if (FrostBlur.Effect is BlurEffect b) b.Radius = lens != null ? 22 : LiquidGlass ? 6 : 36;
        if (FrostLens.Effect == lens) return;
        FrostLens.Effect = lens;
        // the lens's picture is exactly the island and a band around it; the clear box keeps that size whatever is drawn
        FrostClip.ClipToBounds = lens != null;
        FrostClip.Background = lens != null ? Brushes.Transparent : null;
        PlaceFrost(Island.ActualWidth, Island.ActualHeight);
        UpdateGlassFill();   // the tint is the lens's own (or the fill's, without it)
    }

    // ---------------------------------------------------------------- the glass's tint

    private const string LiquidIsland = "#0B0B14";   // the theme's own colour; another one is the person's choice
    private double _backdropHi = 255, _backdropLo, _liquidNeed;

    /// <summary>How bright the picture behind is (its 85th and 15th percentiles, 0…255). The tint follows it, in steps.</summary>
    private void MeasureBackdrop(byte[] px)
    {
        if (!LiquidGlass && !MatteGlass) return;
        Span<int> bins = stackalloc int[32];
        int n = 0;
        for (int i = 0; i + 2 < px.Length; i += 64, n++) bins[(px[i + 2] * 54 + px[i + 1] * 183 + px[i] * 19) >> 11]++;
        int seen = 0, lo = 0, hi = 31;
        for (int b = 0; b < 32; b++) { seen += bins[b]; if (seen < n * 0.15) lo = b + 1; if (seen < n * 0.85) hi = b + 1; }
        _backdropLo = Math.Min(31, lo) * 8;
        _backdropHi = Math.Min(31, hi) * 8 + 7;
        if (Math.Abs(LiquidNeed() - _liquidNeed) > 0.03) UpdateGlassFill(fade: true);
    }

    /// <summary>
    /// The tint the text needs over this backdrop: dark glass over a bright page (or light glass over a dark one) gets
    /// just as dense as it takes to read; over anything that suits it, the clear tint of the slider is enough.
    /// </summary>
    private double LiquidNeed()
    {
        double easier = Math.Max(0, 50 - SceneTint) / 50.0;   // the clear half of the slider asks for a little less
        double v = _settings.DarkTheme ? _backdropHi : _backdropLo;
        double limit = _settings.DarkTheme ? 84 + 24 * easier : 178 - 24 * easier;
        double need = _settings.DarkTheme ? (v - limit) / Math.Max(1, v - 22) : (limit - v) / Math.Max(1, 255 - v);
        return Math.Clamp(need, 0, 0.92);
    }

    /// <summary>Clear to nearly opaque; the middle is white 0.14 (light) or near-black 0.32 (dark).</summary>
    private double LiquidAlpha()
    {
        double s = Math.Clamp(SceneTint, 0, 100) / 100.0;
        double mid = MatteGlass ? 0.5 : _settings.DarkTheme ? 0.32 : 0.14, clear = _settings.DarkTheme ? 0.10 : 0.04;
        double own = s <= 0.5 ? clear + (mid - clear) * s * 2 : mid + (0.92 - mid) * (s - 0.5) * 2;
        _liquidNeed = LiquidNeed();
        return Math.Max(own, _liquidNeed);
    }

    private Color LiquidTintColor(Color island) =>
        !string.Equals(_settings.IslandColor, LiquidIsland, StringComparison.OrdinalIgnoreCase) ? island : _settings.DarkTheme ? Color.FromRgb(20, 20, 24) : Colors.White;

    /// <summary>
    /// The same glass inside: tiles, chips, cards and buttons are clear panes over the one picture, with a 1 px ring
    /// that is brighter on top; the quieter text is brought nearer to the main one, so it reads over any backdrop.
    /// </summary>
        private static LinearGradientBrush Glass(byte top, byte bottom)
    {
        var b = new LinearGradientBrush(Color.FromArgb(top, 0xFF, 0xFF, 0xFF), Color.FromArgb(bottom, 0xFF, 0xFF, 0xFF), 90);
        b.Freeze();
        return b;
    }

private void ApplyLiquidBrushes()
    {
        var r = System.Windows.Application.Current.Resources;
        r.Remove("CardBg");
        if (!GlassScene) return;
        bool dark = _settings.DarkTheme;
        static SolidColorBrush Pane(double a, byte v = 255) { var b = new SolidColorBrush(Color.FromArgb((byte)(a * 255), v, v, v)); b.Freeze(); return b; }
        r["TileBg"] = dark ? Glass(0x18, 0x0C) : Glass(0x66, 0x40);
        r["TileBgHover"] = dark ? Glass(0x28, 0x16) : Glass(0x96, 0x70);
        var ring = new LinearGradientBrush { StartPoint = new System.Windows.Point(0.5, 0), EndPoint = new System.Windows.Point(0.5, 1) };
        byte edge = (byte)(dark ? 0x2E : 0x59), top = (byte)(dark ? 0x4D : 0x8C);
        ring.GradientStops.Add(new GradientStop(Color.FromArgb(top, 255, 255, 255), 0));
        ring.GradientStops.Add(new GradientStop(Color.FromArgb(edge, 255, 255, 255), 0.3));
        // light glass over a white page: white on white has no edge, the lower one is a breath of shade
        ring.GradientStops.Add(new GradientStop(dark ? Color.FromArgb(edge, 255, 255, 255) : Color.FromArgb(0x24, 0, 0, 0), 1));
        ring.Freeze();
        r["TileStroke"] = ring;
        r["ChipBg"] = Pane(dark ? 0.09 : 0.45);
        r["TrackBg"] = dark ? Pane(0.22) : Pane(0.14, 0);
        r["CardBg"] = dark ? Pane(0.26, 20) : Pane(0.40);
        r["Fg2"] = Pane(1, (byte)(dark ? 0xEE : 0x36));
        r["Fg3"] = Pane(1, (byte)(dark ? 0xDC : 0x44));
    }

    /// <summary>
    /// The glass's picture under the island, and the lens over the island's current size: a band around the
    /// island on every side (room for the picture's margin and the drip), the bend as wide as Refract's.
    /// Each picture lies where it was taken on the screen: when the island has moved since, the picture is
    /// shifted back, so what's behind stands still; one that no longer covers the island fades to the solid fill.
    /// </summary>
    private void PlaceFrost(double w, double h)
    {
        double band = FrostMargin + DripRoom;
        Canvas.SetLeft(FrostLens, -band);
        Canvas.SetTop(FrostLens, -band);
        FrostClip.Width = FrostLens.Width = Math.Max(1, w + 2 * band);
        FrostClip.Height = FrostLens.Height = Math.Max(1, h + 2 * band);
        Point now = default;
        bool placed = PresentationSource.FromVisual(Island) != null;
        if (placed) now = Island.PointToScreen(new Point(w / 2, 0));
        double k = Dpi * UiScale;
        var layers = FrostLayers;
        for (int i = 0; i < 2; i++)
        {
            double dx = placed && _layerAt[i] != default ? (_layerAt[i].X - now.X) / k : 0, dy = placed && _layerAt[i] != default ? (_layerAt[i].Y - now.Y) / k : 0;
            Canvas.SetLeft(layers[i], band + w / 2 - _layerHalf[i] + dx);
            Canvas.SetTop(layers[i], band - _layerAbove[i] + dy);
            if (i != _frostFront || _frostBmps[i] == null || FrostHost.Opacity < 0.5) continue;
            // the shown picture must still reach past every edge of the island (the drip aside)
            bool covers = dx - _layerHalf[i] <= -w / 2 + 0.5 && dx + _layerHalf[i] >= w / 2 - 0.5 && dy - _layerAbove[i] <= 0.5 && dy - _layerAbove[i] + layers[i].Height >= h - 0.5;
            if (!covers) { _frostShiftedOff = true; SetFrostCovers(false, fade: true); }
            else if (_frostShiftedOff) { _frostShiftedOff = false; SetFrostCovers(true, fade: true); }
        }
        if (FrostLens.Effect is not GlassEffect lens || w < 2 || h < 2) return;
        double rr = Math.Min(Radius(h), Math.Min(w, h) / 2);
        double bend = Math.Max(6, Math.Min(h * 0.4, 20));
        lens.Geometry = new Point4D(w + 2 * band, h + 2 * band, band, rr);
        // frosted in the middle; a band along the rim is clear and bends what's behind (pulled inwards, so it never
        // runs off the picture), red a touch further than blue; colours 60 % richer
        lens.Lens = new Point4D(bend, -bend * 1.1, -0.8, 1.6);
        lens.Light = new Point4D(-0.3, -0.95, 0.1, 0);
        // what the rim bends: the newest picture itself, the part of it that lies under the lens
        if (_frostBmp is not { } bmp || layers[_frostFront] is not { Width: > 1, Height: > 1 } pic) return;
        var clear = lens.Clear as ImageBrush;
        if (clear?.ImageSource != bmp) lens.Clear = clear = new ImageBrush(bmp) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        double sx = bmp.PixelWidth / pic.Width, sy = bmp.PixelHeight / pic.Height;
        clear.Viewbox = new Rect(-Canvas.GetLeft(pic) * sx, -Canvas.GetTop(pic) * sy, (w + 2 * band) * sx, (h + 2 * band) * sy);
    }

    /// <summary>
    /// The grab (BGRX, w×h) covers the island plus a margin around it; the island is the rounded rectangle
    /// at (x0, 0) of size iw×ih with corner radius rr (all in grab pixels). Returns the bent picture.
    /// </summary>
    private byte[] Refract(byte[] src, int w, int h, double x0, double iw, double ih, double rr)
    {
        if (_liquidPixels == null || _liquidPixels.Length != src.Length) _liquidPixels = new byte[src.Length];
        var dst = _liquidPixels;
        Buffer.BlockCopy(src, 0, dst, 0, src.Length);

        double cx = x0 + iw / 2, cy = ih / 2, bx = iw / 2, by = ih / 2;
        rr = Math.Min(rr, Math.Min(bx, by));
        double band = Math.Max(3, Math.Min(ih * 0.42, rr * 1.2 + 3));   // how far in from the rim the glass bends
        double push = band * 0.9;                                       // how far the picture is pulled towards the rim

        int yEnd = Math.Min(h, (int)Math.Ceiling(ih));
        int xStart = Math.Max(0, (int)x0), xEnd = Math.Min(w, (int)Math.Ceiling(x0 + iw));
        for (int y = 0; y < yEnd; y++)
        {
            double py = y + 0.5, dy = py - cy, qy = Math.Abs(dy) - (by - rr);
            for (int x = xStart; x < xEnd; x++)
            {
                double px = x + 0.5, dx = px - cx, qx = Math.Abs(dx) - (bx - rr);
                double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                double d = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - rr;   // < 0 inside
                int i = (y * w + x) * 4;
                if (d >= 0) continue;

                double t = -d / band;
                double nx, ny;
                if (qx > 0 && qy > 0) { double l = Math.Sqrt(qx * qx + qy * qy); nx = Math.Sign(dx) * qx / l; ny = Math.Sign(dy) * qy / l; }
                else if (qx > qy) { nx = Math.Sign(dx); ny = 0; }
                else { nx = 0; ny = Math.Sign(dy); }

                if (t < 1)
                {
                    // the lens: near the rim the picture under the glass is pulled out to the edge and squeezed,
                    // as if it curls over the rounded rim; red bends a bit more than blue — a thin rainbow fringe.
                    // (it reads from inside the glass, so it never runs off the grabbed picture)
                    double f = (1 - t) * (1 - t);
                    double off = f * push;
                    dst[i + 2] = Sample(src, w, h, px - nx * off * 1.12, py - ny * off * 1.12, 2);
                    dst[i + 1] = Sample(src, w, h, px - nx * off, py - ny * off, 1);
                    dst[i + 0] = Sample(src, w, h, px - nx * off * 0.88, py - ny * off * 0.88, 0);
                    // light gathers in the rim
                    int glow = (int)(f * 18);
                    dst[i] = (byte)Math.Min(255, dst[i] + glow);
                    dst[i + 1] = (byte)Math.Min(255, dst[i + 1] + glow);
                    dst[i + 2] = (byte)Math.Min(255, dst[i + 2] + glow);
                }
                // glass makes colours a little richer
                int r = dst[i + 2], g = dst[i + 1], b = dst[i];
                int gray = (r * 77 + g * 151 + b * 28) >> 8;
                dst[i + 2] = Clamp(gray + (r - gray) * 5 / 4);
                dst[i + 1] = Clamp(gray + (g - gray) * 5 / 4);
                dst[i] = Clamp(gray + (b - gray) * 5 / 4);
            }
        }
        return dst;
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static byte Sample(byte[] src, int w, int h, double x, double y, int channel)
    {
        int ix = Math.Clamp((int)x, 0, w - 1), iy = Math.Clamp((int)y, 0, h - 1);
        return src[(iy * w + ix) * 4 + channel];
    }
}
