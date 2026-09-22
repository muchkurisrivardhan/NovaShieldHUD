using System.Windows;
using System.Windows.Media;

namespace Dotline;

/// <summary>
/// A filled, optionally outlined plate with independently cut corners, drawn at whatever
/// size layout gives it.
///
/// Two properties make it fit the floating HUD look: the corner cuts stay a fixed
/// 45 degrees at any width (a stretched Path would shear them), and it asks for no size of
/// its own, so it can sit behind content in a Grid without feeding back into the window's
/// SizeToContent measurement.
/// </summary>
public sealed class ChamferPlate : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ChamferPlate),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ChamferPlate),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(ChamferPlate),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CutTopLeftProperty = Cut(nameof(CutTopLeft));
    public static readonly DependencyProperty CutTopRightProperty = Cut(nameof(CutTopRight));
    public static readonly DependencyProperty CutBottomRightProperty = Cut(nameof(CutBottomRight));
    public static readonly DependencyProperty CutBottomLeftProperty = Cut(nameof(CutBottomLeft));

    private static DependencyProperty Cut(string name) => DependencyProperty.Register(
        name, typeof(double), typeof(ChamferPlate),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double CutTopLeft { get => (double)GetValue(CutTopLeftProperty); set => SetValue(CutTopLeftProperty, value); }
    public double CutTopRight { get => (double)GetValue(CutTopRightProperty); set => SetValue(CutTopRightProperty, value); }
    public double CutBottomRight { get => (double)GetValue(CutBottomRightProperty); set => SetValue(CutBottomRightProperty, value); }
    public double CutBottomLeft { get => (double)GetValue(CutBottomLeftProperty); set => SetValue(CutBottomLeftProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var fill = Fill;
        var stroke = Stroke;
        if (fill is null && stroke is null) return;

        var inset = stroke is null ? 0 : StrokeThickness / 2;
        double left = inset, top = inset, right = width - inset, bottom = height - inset;

        // No cut may exceed half the plate, or opposite corners would cross.
        var max = Math.Max(0, Math.Min(right - left, bottom - top) / 2);
        double tl = Math.Min(CutTopLeft, max), tr = Math.Min(CutTopRight, max);
        double br = Math.Min(CutBottomRight, max), bl = Math.Min(CutBottomLeft, max);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left + tl, top), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(right - tr, top), true, false);
            if (tr > 0) ctx.LineTo(new Point(right, top + tr), true, false);
            ctx.LineTo(new Point(right, bottom - br), true, false);
            if (br > 0) ctx.LineTo(new Point(right - br, bottom), true, false);
            ctx.LineTo(new Point(left + bl, bottom), true, false);
            if (bl > 0) ctx.LineTo(new Point(left, bottom - bl), true, false);
            ctx.LineTo(new Point(left, top + tl), true, false);
        }
        geometry.Freeze();

        dc.DrawGeometry(fill, stroke is null ? null : new Pen(stroke, StrokeThickness), geometry);
    }
}
