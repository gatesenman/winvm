using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace WinVM.Controls;

/// <summary>
/// Fluent-style line icon drawn from a 24×24 path geometry. Uses the inherited Foreground, so icons
/// automatically follow light/dark theme and the disabled state of their button. Vector paths render
/// identically everywhere (no dependency on Segoe Fluent Icons being installed).
/// </summary>
public class IconView : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<IconView, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<IconView>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<IconView, double>(nameof(StrokeThickness), 1.6);

    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<IconView, bool>(nameof(Filled));

    static IconView()
    {
        AffectsRender<IconView>(DataProperty, ForegroundProperty, StrokeThicknessProperty, FilledProperty);
        AffectsMeasure<IconView>(DataProperty);
    }

    public IconView()
    {
        Width = 16;
        Height = 16;
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    public override void Render(DrawingContext context)
    {
        var g = Data;
        var brush = Foreground ?? Brushes.Gray;
        if (g == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var s = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        var dx = (Bounds.Width - 24 * s) / 2;
        var dy = (Bounds.Height - 24 * s) / 2;
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(dx, dy)))
        {
            if (Filled)
                context.DrawGeometry(brush, null, g);
            else
                context.DrawGeometry(null, new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
        }
    }
}
