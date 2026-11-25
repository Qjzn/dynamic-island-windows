using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIsland.Views;

/// <summary>A CSS cubic-bezier(x1, y1, x2, y2) curve for WPF animations.</summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    private readonly double _x1, _y1, _x2, _y2;

    public CubicBezierEase() : this(0.25, 0.1, 0.25, 1) { }

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        EasingMode = EasingMode.EaseIn;   // EaseInCore is the whole curve
    }

    private static double Bezier(double t, double a, double b) => 3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;

    protected override double EaseInCore(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        // the t whose x is the given time, by bisection (the curve's x grows with t)
        double lo = 0, hi = 1, t = x;
        for (int i = 0; i < 24; i++)
        {
            t = (lo + hi) / 2;
            if (Bezier(t, _x1, _x2) < x) lo = t; else hi = t;
        }
        return Bezier(t, _y1, _y2);
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(_x1, _y1, _x2, _y2);
}
