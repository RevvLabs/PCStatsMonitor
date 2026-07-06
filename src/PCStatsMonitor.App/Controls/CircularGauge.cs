using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace PCStatsMonitor.App.Controls;

/// <summary>
/// Custom circular progress gauge — Avalonia/Skia-rendered.
///
/// Rendering optimizations:
/// - Only calls InvalidateVisual() when the displayed integer percent changes
///   OR an animation is still in flight — idle paint rate approaches 0.
/// - FormattedText cached per integer value (0-100 array) — no per-frame string alloc.
/// - Brushes cached per accent color — rebuilt only when color changes.
/// - Animation: critically-damped spring, self-scheduled via Dispatcher.
/// </summary>
public class CircularGauge : Control
{
    /// <summary>
    /// App-wide potato mode: no ring drawn at all (text only) and target changes
    /// snap instantly with zero animation frames. Set via SetPotatoMode from
    /// MainWindow when the setting changes; read on the UI thread only.
    /// </summary>
    public static bool PotatoMode { get; private set; }

    private static event Action? PotatoModeChanged;

    public static void SetPotatoMode(bool on)
    {
        if (PotatoMode == on) return;
        PotatoMode = on;
        // Attached gauges repaint immediately — no data change fires otherwise
        PotatoModeChanged?.Invoke();
    }

    private Action? _onPotatoChanged;

    public static readonly StyledProperty<double> TargetPercentProperty =
        AvaloniaProperty.Register<CircularGauge, double>(nameof(TargetPercent), 0.0);

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<CircularGauge, Color>(nameof(AccentColor), Color.FromRgb(0, 188, 212));

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<CircularGauge, string>(nameof(Caption), "");

    public double TargetPercent
    {
        get => GetValue(TargetPercentProperty);
        set => SetValue(TargetPercentProperty, Math.Clamp(value, 0, 100));
    }

    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    /// <summary>Small muted label under the percentage (e.g. "LOAD", "USED").</summary>
    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    // Animation state
    private double _currentPct;
    private double _velocity;
    private bool _animating;
    private int _lastRenderedInt = -1;
    private DateTime _lastFrameTime = DateTime.UtcNow;

    private const double LineWidth = 7.0;
    private const double MarginFactor = 0.09;
    private Rect _arcRect;

    // FormattedText cached per integer (0-100) — avoids alloc per frame
    private readonly FormattedText?[] _textCache = new FormattedText?[101];
    private FormattedText? _captionText;
    private string _cachedCaption = "";
    private static readonly Typeface _typeface = new(
        FontFamily.Parse("Inter, Segoe UI, Arial"),
        FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface _captionTypeface = new(
        FontFamily.Parse("Inter, Segoe UI, Arial"),
        FontStyle.Normal, FontWeight.SemiBold);
    private const double FontSize = 24.0;
    private const double CaptionFontSize = 9.0;

    // Text wears ink tokens, not the series color — the arc carries identity.
    private static readonly SolidColorBrush _inkPrimary = new(Color.FromRgb(0xEC, 0xED, 0xEF));
    private static readonly SolidColorBrush _inkMuted   = new(Color.FromRgb(0x6E, 0x71, 0x76));

    // Brush + Pen cache — rebuilt only when AccentColor changes
    private SolidColorBrush? _accentBrush;
    private SolidColorBrush? _trackBrush;
    private SolidColorBrush? _glowBrush;
    private Pen? _trackPen;
    private Pen? _glowPen;
    private Pen? _accentPen;
    private Color _cachedAccentColor;

    static CircularGauge()
    {
        // TargetPercent excluded — the animation loop calls InvalidateVisual itself;
        // including it here causes a redundant extra render on every data update.
        AffectsRender<CircularGauge>(AccentColorProperty, CaptionProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TargetPercentProperty)
            return;

        if (PotatoMode)
        {
            // Snap: no animation frames scheduled at all
            _animating  = false;
            _velocity   = 0;
            _currentPct = TargetPercent;
            int intPct = (int)Math.Round(_currentPct);
            if (intPct != _lastRenderedInt)
            {
                _lastRenderedInt = intPct;
                InvalidateVisual();
            }
            return;
        }

        if (!_animating)
        {
            _animating = true;
            _lastFrameTime = DateTime.UtcNow;
            ScheduleFrame();
        }
    }

    private Action<TimeSpan>? _rafCallback;

    private void ScheduleFrame()
    {
        // Compositor vsync caps the frame rate; an unthrottled Dispatcher.Post loop
        // spins the UI thread as fast as it can drain the queue for the whole animation.
        // No Post fallback: detached means the window is closing or the control was
        // removed — posting during dispatcher shutdown throws and kills the process.
        var top = TopLevel.GetTopLevel(this);
        if (top == null)
        {
            StopAnimation();
            return;
        }
        top.RequestAnimationFrame(_rafCallback ??= _ => AnimationFrame());
    }

    private void StopAnimation()
    {
        _animating  = false;
        _currentPct = TargetPercent;
        _velocity   = 0;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PotatoModeChanged += _onPotatoChanged ??= InvalidateVisual;
        // Resume if a target arrived while detached
        if (_animating) ScheduleFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        PotatoModeChanged -= _onPotatoChanged;
        StopAnimation();
    }

    private void AnimationFrame()
    {
        var now = DateTime.UtcNow;
        double dtSeconds = Math.Clamp((now - _lastFrameTime).TotalSeconds, 0, 0.1);
        _lastFrameTime = now;

        double target = TargetPercent;
        double diff = target - _currentPct;

        // Critically-damped spring
        const double stiffness = 200.0, damping = 28.3;
        _velocity  += (stiffness * diff - damping * _velocity) * dtSeconds;
        _currentPct += _velocity * dtSeconds;

        bool done = Math.Abs(diff) < 0.05 && Math.Abs(_velocity) < 0.05;
        if (done)
        {
            _currentPct = target;
            _velocity   = 0;
            _animating  = false;
        }

        int intPct = (int)Math.Round(_currentPct);
        if (intPct != _lastRenderedInt || _animating)
        {
            _lastRenderedInt = intPct;
            InvalidateVisual();
        }

        if (_animating) ScheduleFrame();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        double size = Math.Min(Bounds.Width, Bounds.Height);
        double margin = size * MarginFactor + LineWidth / 2;
        _arcRect = new Rect(margin, margin, size - margin * 2, size - margin * 2);
    }

    public override void Render(DrawingContext ctx)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 2) return;

        double pct = Math.Clamp(_currentPct, 0, 100);

        // Potato mode: no ring at all — text only, zero geometry work
        if (!PotatoMode)
        {
            EnsureBrushes();

            // Track ring
            ctx.DrawEllipse(null, _trackPen, _arcRect.Center, _arcRect.Width / 2, _arcRect.Height / 2);

            // Progress arc
            if (pct > 0.01)
            {
                double sweepDeg = pct / 100.0 * 360.0;
                var geo = BuildArc(_arcRect, sweepDeg);
                ctx.DrawGeometry(null, _glowPen, geo);
                ctx.DrawGeometry(null, _accentPen, geo);

                // Progress head — bright dot at the arc's leading edge
                double endRad = -Math.PI / 2 + sweepDeg * Math.PI / 180.0;
                var head = new Point(
                    _arcRect.Center.X + _arcRect.Width / 2 * Math.Cos(endRad),
                    _arcRect.Center.Y + _arcRect.Height / 2 * Math.Sin(endRad));
                ctx.DrawEllipse(_inkPrimary, null, head, LineWidth / 2 - 1, LineWidth / 2 - 1);
            }
        }

        // Percentage label (primary ink) + caption (muted ink)
        int intPct = (int)Math.Round(pct);
        var text = GetOrCreateText(intPct);
        var caption = GetOrCreateCaption();
        double captionH = caption?.Height ?? 0;
        if (text != null)
        {
            double textTop = (Bounds.Height - text.Height - captionH) / 2;
            ctx.DrawText(text, new Point((Bounds.Width - text.Width) / 2, textTop));
            if (caption != null)
                ctx.DrawText(caption, new Point((Bounds.Width - caption.Width) / 2, textTop + text.Height));
        }
    }

    private static StreamGeometry BuildArc(Rect rect, double sweepDeg)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        double cx = rect.Center.X, cy = rect.Center.Y;
        double rx = rect.Width / 2, ry = rect.Height / 2;
        double startRad = -Math.PI / 2;
        double endRad   = startRad + sweepDeg * Math.PI / 180.0;
        ctx.BeginFigure(new Point(cx + rx * Math.Cos(startRad), cy + ry * Math.Sin(startRad)), false);
        ctx.ArcTo(new Point(cx + rx * Math.Cos(endRad), cy + ry * Math.Sin(endRad)),
            new Size(rx, ry), 0, sweepDeg > 180, SweepDirection.Clockwise);
        ctx.EndFigure(false);
        return geo;
    }

    private FormattedText GetOrCreateText(int pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        return _textCache[pct] ??= new FormattedText(
            $"{pct}%",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            _inkPrimary);
    }

    private FormattedText? GetOrCreateCaption()
    {
        string caption = Caption;
        if (string.IsNullOrEmpty(caption)) return null;
        if (_captionText == null || caption != _cachedCaption)
        {
            _cachedCaption = caption;
            _captionText = new FormattedText(
                caption,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _captionTypeface,
                CaptionFontSize,
                _inkMuted);
        }
        return _captionText;
    }

    private void EnsureBrushes()
    {
        Color accent = AccentColor;
        if (accent == _cachedAccentColor && _accentBrush != null) return;
        _cachedAccentColor = accent;
        _accentBrush = new SolidColorBrush(accent);
        _trackBrush  = new SolidColorBrush(Color.FromArgb(22, accent.R, accent.G, accent.B));
        _glowBrush   = new SolidColorBrush(Color.FromArgb(45, accent.R, accent.G, accent.B));
        _trackPen    = new Pen(_trackBrush, LineWidth);
        _glowPen     = new Pen(_glowBrush,  LineWidth + 5) { LineCap = PenLineCap.Round };
        _accentPen   = new Pen(_accentBrush, LineWidth)    { LineCap = PenLineCap.Round };
        Array.Clear(_textCache, 0, _textCache.Length);
    }
}
