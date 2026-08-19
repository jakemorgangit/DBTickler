using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DBTickler.App.Controls;

/// <summary>
/// A small time-series chart drawn directly with <see cref="DrawingContext"/>.
///
/// Written by hand rather than pulled from a charting package: the app ships as a single
/// self-contained executable, and a charting library would add several megabytes for one
/// line and a filled area. It also means the chart picks up theme brushes like any other
/// control.
/// </summary>
public sealed class SparklineChart : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(SparklineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Secondary series drawn as vertical marks — used for errors against throughput.</summary>
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<double>), typeof(SparklineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SparklineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(SparklineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(SparklineChart),
        new FrameworkPropertyMetadata("N0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(SparklineChart),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SparklineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerBrushProperty = DependencyProperty.Register(
        nameof(MarkerBrush), typeof(Brush), typeof(SparklineChart),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(SparklineChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(SparklineChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double>? Markers
    {
        get => (IReadOnlyList<double>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush MarkerBrush
    {
        get => (Brush)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 8 || height <= 8) return;

        const double LeftPadding = 4;
        const double TopPadding = 20;
        const double BottomPadding = 4;

        var plotHeight = height - TopPadding - BottomPadding;
        var plotWidth = width - LeftPadding * 2;
        if (plotHeight <= 4 || plotWidth <= 4) return;

        var values = Values;
        var maximum = values is { Count: > 0 } ? values.Max() : 0;

        // A flat zero series would divide by zero and render as a line along the top; give it
        // a nominal ceiling so an idle chart looks idle.
        var scale = maximum > 0 ? maximum : 1;

        DrawGrid(drawingContext, LeftPadding, TopPadding, plotWidth, plotHeight);
        DrawHeader(drawingContext, values, maximum, width);

        if (values is not { Count: > 1 })
            return;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var stepX = plotWidth / (values.Count - 1);
            var start = new Point(LeftPadding, TopPadding + plotHeight - values[0] / scale * plotHeight);
            context.BeginFigure(start, isFilled: true, isClosed: false);

            var points = new List<Point>(values.Count - 1);
            for (var i = 1; i < values.Count; i++)
                points.Add(new Point(LeftPadding + i * stepX, TopPadding + plotHeight - values[i] / scale * plotHeight));

            context.PolyLineTo(points, isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();

        if (FillBrush is not null)
        {
            var fill = new StreamGeometry();
            using (var context = fill.Open())
            {
                var stepX = plotWidth / (values.Count - 1);
                var baseline = TopPadding + plotHeight;
                context.BeginFigure(new Point(LeftPadding, baseline), isFilled: true, isClosed: true);

                var points = new List<Point>(values.Count + 1);
                for (var i = 0; i < values.Count; i++)
                    points.Add(new Point(LeftPadding + i * stepX, TopPadding + plotHeight - values[i] / scale * plotHeight));
                points.Add(new Point(LeftPadding + (values.Count - 1) * stepX, baseline));

                context.PolyLineTo(points, isStroked: false, isSmoothJoin: true);
            }
            fill.Freeze();
            drawingContext.DrawGeometry(FillBrush, null, fill);
        }

        var pen = new Pen(LineBrush, 1.5);
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);

        DrawMarkers(drawingContext, LeftPadding, TopPadding, plotWidth, plotHeight, values.Count);
    }

    private void DrawGrid(DrawingContext drawingContext, double left, double top, double width, double height)
    {
        var pen = new Pen(GridBrush, 0.5) { DashStyle = new DashStyle([3, 3], 0) };
        pen.Freeze();

        for (var i = 0; i <= 4; i++)
        {
            var y = top + height / 4 * i;
            drawingContext.DrawLine(pen, new Point(left, y), new Point(left + width, y));
        }
    }

    private void DrawMarkers(
        DrawingContext drawingContext, double left, double top, double width, double height, int sampleCount)
    {
        var markers = Markers;
        if (markers is not { Count: > 0 } || sampleCount <= 1) return;

        var pen = new Pen(MarkerBrush, 2);
        pen.Freeze();

        var stepX = width / (sampleCount - 1);
        var limit = Math.Min(markers.Count, sampleCount);

        for (var i = 0; i < limit; i++)
        {
            if (markers[i] <= 0) continue;

            var x = left + i * stepX;
            drawingContext.DrawLine(pen, new Point(x, top + height), new Point(x, top + height - 6));
        }
    }

    private void DrawHeader(DrawingContext drawingContext, IReadOnlyList<double>? values, double maximum, double width)
    {
        var current = values is { Count: > 0 } ? values[^1] : 0;

        var titleText = FormatText(Title, 11, LabelBrush);
        drawingContext.DrawText(titleText, new Point(4, 2));

        var summary = values is { Count: > 0 }
            ? $"{current.ToString(ValueFormat, CultureInfo.CurrentCulture)} {Unit}   peak {maximum.ToString(ValueFormat, CultureInfo.CurrentCulture)}"
            : "no data yet";

        var summaryText = FormatText(summary, 11, LineBrush);
        var x = Math.Max(titleText.Width + 12, width - summaryText.Width - 4);
        drawingContext.DrawText(summaryText, new Point(x, 2));
    }

    private FormattedText FormatText(string text, double size, Brush brush) =>
        new(text ?? "",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
