using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WinVM.Controls;

/// <summary>Minimal area sparkline for live performance (oldest sample left, newest right). The X axis always
/// spans <see cref="Capacity"/> samples, so the line grows from the right as data arrives.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 60);
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(GridBrush));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, MaximumProperty, CapacityProperty, StrokeProperty, GridBrushProperty);
    }

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;
        if (GridBrush is { } gb)
        {
            var gp = new Pen(gb, 1, new DashStyle(new double[] { 2, 3 }, 0));
            for (var i = 1; i <= 3; i++)
            {
                var y = Math.Round(h * i / 4.0) + 0.5;
                ctx.DrawLine(gp, new Point(0, y), new Point(w, y));
            }
        }
        var v = Values;
        if (v == null || v.Count == 0) return;
        var stroke = Stroke ?? Brushes.DodgerBlue;
        var max = Maximum <= 0 ? 1 : Maximum;
        var cap = Math.Max(2, Math.Max(Capacity, v.Count));
        var step = w / (cap - 1);
        var x0 = w - step * (v.Count - 1);
        Point P(int i) => new(x0 + step * i, h - 1 - Math.Clamp(v[i] / max, 0, 1) * (h - 3));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false);
            for (var i = 1; i < v.Count; i++) g.LineTo(P(i));
            g.EndFigure(false);
        }
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, h), true);
            for (var i = 0; i < v.Count; i++) g.LineTo(P(i));
            g.LineTo(new Point(x0 + step * (v.Count - 1), h));
            g.EndFigure(true);
        }
        var fill = stroke is ISolidColorBrush sc
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0x55, sc.Color.R, sc.Color.G, sc.Color.B), 0), new GradientStop(Color.FromArgb(0x08, sc.Color.R, sc.Color.G, sc.Color.B), 1) }
            }
            : (IBrush)Brushes.Transparent;
        ctx.DrawGeometry(fill, null, area);
        ctx.DrawGeometry(null, new Pen(stroke, 1.6, lineJoin: PenLineJoin.Round), line);
        if (v.Count == 1) ctx.DrawEllipse(stroke, null, P(0), 2, 2);
    }
}
