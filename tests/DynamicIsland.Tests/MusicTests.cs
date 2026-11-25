using System.Windows.Media;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class ColorToolsTests
{
    private static byte[] Fill(int n, params (byte R, byte G, byte B)[] colors)
    {
        var px = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            var (r, g, b) = colors[i % colors.Length];
            px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255;
        }
        return px;
    }

    [Fact]
    public void A_vivid_colour_wins_over_a_dark_background()
    {
        // 9 black pixels for every red one: still reads red
        var colors = Enumerable.Repeat(((byte)8, (byte)8, (byte)10), 9).Append(((byte)230, (byte)40, (byte)40)).ToArray();
        var c = ColorTools.Dominant(Fill(100, colors), 10, 10);
        Assert.True(c.R > c.G * 2 && c.R > c.B * 2, $"got {c}");
    }

    [Fact]
    public void A_grey_picture_stays_grey()
    {
        var c = ColorTools.Dominant(Fill(64, (128, 128, 128)), 8, 8);
        Assert.True(Math.Abs(c.R - c.G) < 3 && Math.Abs(c.G - c.B) < 3);
    }

    [Theory]
    [InlineData(255, 230, 80)]
    [InlineData(120, 200, 255)]
    [InlineData(20, 20, 30)]
    public void Dark_variant_carries_white_text(byte r, byte g, byte b)
    {
        var d = ColorTools.DarkForWhiteText(Color.FromRgb(r, g, b));
    }
}

public class AudioMatchTests
{
    [Fact]
    public void Store_app_matches_its_package_family()
    {
        Assert.Equal(100, AudioMatch.Score("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0"));
    }

    [Fact]
    public void Desktop_app_matches_its_exe()
    {
        Assert.Equal(90, AudioMatch.Score("Spotify.exe", "Spotify", null));
        Assert.True(AudioMatch.Score("chrome", "chrome", null) > 0);
    }

    [Fact]
    public void Firefox_hash_matches_firefox_only()
    {
        Assert.True(AudioMatch.Score("308046B0AF4A39CB", "firefox", null) > 0);
        Assert.Equal(0, AudioMatch.Score("308046B0AF4A39CB", "chrome", null));
    }

    [Fact]
    public void Unrelated_process_does_not_match()
    {
        Assert.Equal(0, AudioMatch.Score("Spotify.exe", "Discord", null));
        Assert.Equal(0, AudioMatch.Score("", "Spotify", null));
    }
}
