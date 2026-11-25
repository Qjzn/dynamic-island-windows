using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

/// <summary>Colours taken from pictures (album covers, photos) — pure maths, unit-tested.</summary>
public static class ColorTools
{
    /// <summary>
    /// The colour a picture "feels like": an average of its pixels where vivid, mid-bright ones count most and
    /// near-black, near-white and grey ones hardly at all (so a dark cover with a red title reads red, not black).
    /// Pixels are BGRA, 4 bytes each.
    /// </summary>
    public static Color Dominant(byte[] bgra, int width, int height)
    {
        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i + 3 < bgra.Length && i < width * height * 4; i += 4)
        {
            double pb = bgra[i] / 255.0, pg = bgra[i + 1] / 255.0, pr = bgra[i + 2] / 255.0;
            double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
            double sat = max <= 0 ? 0 : (max - min) / max;
            double light = (max + min) / 2;
            double weight = 0.05 + sat * sat * (1 - Math.Abs(light - 0.5) * 1.6);
            if (weight <= 0) weight = 0.01;
            r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
        }
        if (total <= 0) return Color.FromRgb(0x40, 0x40, 0x48);
        return Color.FromRgb((byte)(r / total * 255), (byte)(g / total * 255), (byte)(b / total * 255));
    }

    /// <summary>The dominant colour of a bitmap (sampled down to 24×24 first).</summary>
    public static Color Dominant(BitmapSource bmp)
    {
        var small = new TransformedBitmap(bmp, new ScaleTransform(24.0 / Math.Max(1, bmp.PixelWidth), 24.0 / Math.Max(1, bmp.PixelHeight)));
        var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        return Dominant(px, w, h);
    }

    /// <summary>
    /// A colour of the same hue dark enough for white text on it to reach 4.5:1 (keeps it as bright as allowed,
    /// so the glass still glows with the cover's colour).
    /// </summary>
    public static Color DarkForWhiteText(Color c)
    {
        for (double k = 1; k > 0.05; k -= 0.04)
        {
            var d = Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
            if (1.05 / (0.2126 * d.R / 255 + 0.7152 * d.G / 255 + 0.0722 * d.B / 255 + 0.05) >= 4.5) return d;
        }
        return Color.FromRgb((byte)(c.R * 0.05), (byte)(c.G * 0.05), (byte)(c.B * 0.05));
    }
}

/// <summary>Which audio session belongs to a media player (pure matching, unit-tested).</summary>
public static class AudioMatch
{
    /// <summary>
    /// How well a process (its exe name, and its package family if it is a Store app) fits the player's media
    /// id (AUMID). 0 = not this one. A Store app's AUMID starts with its package family ("Family!App"); a
    /// desktop app's names the exe; browsers that report a hash instead are matched by their exe names.
    /// </summary>
    public static int Score(string aumid, string processName, string? packageFamily)
    {
        if (string.IsNullOrWhiteSpace(aumid) || string.IsNullOrWhiteSpace(processName)) return 0;
        var id = aumid.ToLowerInvariant();
        var proc = processName.ToLowerInvariant();
        if (proc.EndsWith(".exe")) proc = proc[..^4];
        int bang = id.IndexOf('!');
        if (packageFamily is { Length: > 0 } fam && bang > 0 && id[..bang] == fam.ToLowerInvariant()) return 100;
        var exe = id.Replace(".exe", "");
        if (exe == proc || exe.EndsWith("\\" + proc) || exe.EndsWith("/" + proc)) return 90;
        // a hash (Firefox and its forks name their media sessions after their install path's hash)
        if (exe.Length >= 12 && exe.All(Uri.IsHexDigit)) return proc is "firefox" or "librewolf" or "waterfox" or "zen" or "floorp" ? 40 : 0;
        // the Yandex Music app reports "ru.yandex.desktop.music" from "Яндекс Музыка.exe"
        if (id.Contains("yandex") && id.Contains("music") && (proc.Contains("яндекс") || proc.Replace(" ", "").Contains("yandexmusic"))) return 80;
        // words any player could have would match the wrong app ("desktop" GitHub Desktop, "music" YouTube Music Desktop)
        var tokens = exe.Split(['.', '_', '!', ' ', '-', '\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                        .Where(t => t.Length >= 4 && t is not ("microsoft" or "app" or "desktop" or "music" or "player" or "media") && !t.All(Uri.IsHexDigit));
        return tokens.Any(t => proc.Contains(t) || t.Contains(proc)) ? 60 : 0;
    }
}
