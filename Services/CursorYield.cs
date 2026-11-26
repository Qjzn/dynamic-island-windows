using System.Windows;

namespace DynamicIsland.Services;

/// <summary>
/// When the island steps aside for the cursor. A cursor that flies onto it (a game's mouse thrown to the top of the
/// screen, a hand on its way to a browser's tabs) is passing by: the island fades and lets clicks through to the
/// window under it. A cursor that slows down and rests on it means the island: it comes back after a short
/// dwell, or at once at the very top edge of the screen. In a full-screen game it never comes back while the
/// cursor stays over it. Only over another program's window — over the desktop nothing is in the way.
/// </summary>
public sealed class CursorYield
{
    /// <summary>Entering faster than this (px/s, over the last ~60 ms) is passing by.</summary>
    public const double FastPxPerSec = 1200;
    /// <summary>Resting this long (moving less than <see cref="StillPx"/>) brings the island back.</summary>
    public const int DwellMs = 200;
    public const double StillPx = 5;

    private readonly List<(long Ms, Point P)> _trail = [];
    private bool _wasInside;
    private long _stillSince;
    private Point _stillAt;

    public bool Yielding { get; private set; }

    /// <summary>
    /// One look at the cursor (screen pixels, a clock in ms). Returns +1 when the island starts stepping aside,
    /// -1 when it comes back, 0 otherwise.
    /// </summary>
    public int Step(long ms, Point cursor, bool inside, bool atTopEdge, bool windowUnder, bool game)
    {
        _trail.Add((ms, cursor));
        while (_trail.Count > 2 && ms - _trail[0].Ms > 120) _trail.RemoveAt(0);
        bool entered = inside && !_wasInside;
        _wasInside = inside;

        if (!Yielding)
        {
            if (!inside || !windowUnder) return 0;
            // a game: whenever it is over the island; otherwise only when it flew in
            if (!(game || entered && Speed(ms) >= FastPxPerSec)) return 0;
            Yielding = true;
            _stillSince = ms;
            _stillAt = cursor;
            return 1;
        }

        if (!inside || !windowUnder) { Yielding = false; return -1; }
        if (game) return 0;
        if (atTopEdge) { Yielding = false; return -1; }
        if ((cursor - _stillAt).Length > StillPx) { _stillAt = cursor; _stillSince = ms; return 0; }
        if (ms - _stillSince < DwellMs) return 0;
        Yielding = false;
        return -1;
    }

    /// <summary>The island is folded away or hidden: forget the cursor.</summary>
    public bool Reset()
    {
        bool was = Yielding;
        Yielding = false;
        _wasInside = false;
        _trail.Clear();
        return was;
    }

    private double Speed(long ms)
    {
        // over the last ~60 ms before now
        var last = _trail[^1];
        var first = last;
        for (int i = _trail.Count - 2; i >= 0; i--)
        {
            first = _trail[i];
            if (ms - first.Ms >= 60) break;
        }
        long dt = last.Ms - first.Ms;
        return dt > 0 ? (last.P - first.P).Length * 1000.0 / dt : 0;
    }
}
