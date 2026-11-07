using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RemoteControlServer.App.Controls;

/// <summary>Circular usage gauge (0-100). A null value renders "n/a" - never a fake number.</summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double?), typeof(Gauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(Gauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WarnProperty = DependencyProperty.Register(nameof(Warn), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(80.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CritProperty = DependencyProperty.Register(nameof(Crit), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Warn { get => (double)GetValue(WarnProperty); set => SetValue(WarnProperty, value); }
    public double Crit { get => (double)GetValue(CritProperty); set => SetValue(CritProperty, value); }

    private double _shown;
    private DateTime _last = DateTime.UtcNow;

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 10) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double thick = Math.Max(6, size * 0.09), r = size / 2 - thick;
        var track = new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3B)), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, track, c, r, r);

        var v = Value;
        // smooth animation toward the real value
        double target = v ?? 0;
        var dt = (DateTime.UtcNow - _last).TotalSeconds; _last = DateTime.UtcNow;
        _shown += (target - _shown) * Math.Min(1, dt * 6 + 0.02);
        if (Math.Abs(_shown - target) > 0.3) InvalidateVisual(); else _shown = target;

        if (v != null && _shown > 0.2)
        {
            var color = v >= Crit ? Color.FromRgb(0xF8, 0x71, 0x71) : v >= Warn ? Color.FromRgb(0xFB, 0xBF, 0x24) : Color.FromRgb(0x22, 0xD3, 0xEE);
            var grad = new LinearGradientBrush(color, v >= Warn ? color : Color.FromRgb(0x8B, 0x5C, 0xF6), 45);
            var pen = new Pen(grad, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double sweep = Math.Min(359.9, _shown / 100.0 * 360);
            var start = new Point(c.X, c.Y - r);
            double a = (sweep - 90) * Math.PI / 180;
            var end = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
            dc.DrawGeometry(null, pen, new PathGeometry([fig]));
        }

        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(v == null ? "n/a" : $"{v:0}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size * (v == null ? 0.2 : 0.24), Brushes.White, dpi);
        dc.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2 - (Label.Length > 0 ? size * 0.05 : 0)));
        if (Label.Length > 0)
        {
            var lt = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size * 0.1, new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA7)), dpi);
            dc.DrawText(lt, new Point(c.X - lt.Width / 2, c.Y + size * 0.1));
        }
    }
}

/// <summary>Rolling line chart of the last N real samples (0-100 by default).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Samples { get => (IReadOnlyList<double>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), null, new Rect(0, 0, w, h));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x33)), 1);
        for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
        var s = Samples;
        if (s == null || s.Count < 2) return;
        double max = Math.Max(1, Max);
        var pts = new List<Point>();
        for (int i = 0; i < s.Count; i++) pts.Add(new Point(w * i / Math.Max(1, s.Count - 1), h - Math.Clamp(s[i] / max, 0, 1) * (h - 4) - 2));
        var fig = new PathFigure { StartPoint = pts[0] };
        fig.Segments.Add(new PolyLineSegment(pts.Skip(1), true));
        var fill = new PathFigure { StartPoint = new Point(0, h) };
        fill.Segments.Add(new LineSegment(pts[0], false));
        fill.Segments.Add(new PolyLineSegment(pts.Skip(1), false));
        fill.Segments.Add(new LineSegment(new Point(w, h), false));
        fill.IsClosed = true;
        var baseColor = (Stroke as SolidColorBrush)?.Color ?? Colors.Cyan;
        var fillBrush = new LinearGradientBrush(Color.FromArgb(90, baseColor.R, baseColor.G, baseColor.B), Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B), 90);
        dc.DrawGeometry(fillBrush, null, new PathGeometry([fill]));
        dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, new PathGeometry([fig]));
    }
}
