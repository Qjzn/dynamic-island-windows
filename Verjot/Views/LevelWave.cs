using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicIsland.Views;

/// <summary>
/// The sound of a meeting as a line of bars: the microphone's level, then the computer's. Every bar follows its
/// level on a quick rise and a slow fall, and sways along the line — faster the louder it is. Frames only while
/// something feeds it (<see cref="Drive"/>).
/// </summary>
public sealed class LevelWave : StackPanel
{
    public static readonly DependencyProperty MicFillProperty = DependencyProperty.Register(nameof(MicFill), typeof(Brush), typeof(LevelWave), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SystemFillProperty = DependencyProperty.Register(nameof(SystemFill), typeof(Brush), typeof(LevelWave), new PropertyMetadata(null, Changed));

    /// <summary>The bars of the microphone; null = the accent colour.</summary>
    public Brush? MicFill { get => (Brush?)GetValue(MicFillProperty); set => SetValue(MicFillProperty, value); }
    /// <summary>The bars of the computer's sound; null = the blue accent.</summary>
    public Brush? SystemFill { get => (Brush?)GetValue(SystemFillProperty); set => SetValue(SystemFillProperty, value); }

    /// <summary>How tall the quietest bar is (a share of the height): a line you can see, not a point.</summary>
    private const double Rest = 0.28;

    private readonly Rectangle[] _bars;
    private readonly double[] _values;
    private Func<(float Mic, float System)>? _source;
    private bool _ticking;
    private TimeSpan _lastFrame;
    private double _clock;

    public LevelWave() : this(bars: 32, barWidth: 4, barHeight: 30) { }

    public LevelWave(int bars, double barWidth, double barHeight)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _bars = new Rectangle[bars];
        _values = new double[bars];
        for (int i = 0; i < bars; i++)
        {
            var bar = new Rectangle
            {
                Width = barWidth, Height = barHeight, RadiusX = barWidth / 2, RadiusY = barWidth / 2,
                Margin = new Thickness(barWidth / 2, 0, barWidth / 2, 0),
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, Rest),
            };
            _bars[i] = bar;
            _values[i] = Rest;
            Children.Add(bar);
        }
        Paint();
        Unloaded += (_, _) => Cease();
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LevelWave)d).Paint();

    private void Paint()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            bool microphone = (double)i / Math.Max(1, _bars.Length - 1) < 0.4;
            var brush = microphone ? MicFill : SystemFill;
            if (brush != null) _bars[i].Fill = brush;
            else _bars[i].SetResourceReference(Shape.FillProperty, microphone ? "Accent" : "AccentBlue");
        }
    }

    /// <summary>The levels to show (0…1 each), read on every frame.</summary>
    public void Drive(Func<(float Mic, float System)> source)
    {
        _source = source;
        if (_ticking) return;
        _ticking = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Nothing to show any more: the bars settle down and the frames stop.</summary>
    public void Cease()
    {
        _source = null;
        StopLoop();
        for (int i = 0; i < _bars.Length; i++) ((ScaleTransform)_bars[i].RenderTransform).ScaleY = _values[i] = Rest;
    }

    private void StopLoop()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _lastFrame).TotalSeconds, 1 / 30.0);
        _lastFrame = now;
        if (_source == null) { StopLoop(); return; }
        _clock += dt;
        var (mic, system) = _source();
        for (int i = 0; i < _bars.Length; i++)
        {
            bool fromMicrophone = (double)i / Math.Max(1, _bars.Length - 1) < 0.4;
            double level = Math.Sqrt(Math.Clamp(fromMicrophone ? mic : system, 0, 1));
            double sway = 0.5 + 0.5 * Math.Abs(Math.Sin(_clock * (1.5 + level * 5) + i * 0.62));
            double target = Math.Clamp(Rest + level * (0.35 + 0.65 * sway) * (1 - Rest), Rest, 1);
            _values[i] += (target - _values[i]) * (target > _values[i] ? 0.5 : 0.10);   // quick up, slow down
            ((ScaleTransform)_bars[i].RenderTransform).ScaleY = _values[i];
        }
    }
}
