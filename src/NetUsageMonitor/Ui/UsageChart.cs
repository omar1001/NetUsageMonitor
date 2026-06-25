using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetUsageMonitor.Common;
using NetUsageMonitor.Storage;

namespace NetUsageMonitor.Ui;

/// <summary>
/// A compact, dependency-free timeline of a single app's recorded usage. Download is drawn as a filled
/// area, upload as a line, across the retention window. Rendered directly via <see cref="OnRender"/>.
/// </summary>
public sealed class UsageChart : FrameworkElement
{
    private List<HistoryPoint> _points = new();
    private int _windowMinutes = 60;
    private long _nowUnix;

    private static readonly Brush DownFill = Make(Color.FromArgb(0x55, 0x10, 0xB9, 0x81));
    private static readonly Pen DownPen = MakePen(Color.FromRgb(0x10, 0xB9, 0x81), 1.5);
    private static readonly Pen UpPen = MakePen(Color.FromRgb(0x3B, 0x82, 0xF6), 1.5);
    private static readonly Pen GridPen = MakePen(Color.FromArgb(0x33, 0x9C, 0xA3, 0xAF), 0.5);
    private static readonly Brush AxisText = Make(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Typeface Face = new("Segoe UI");

    public void SetData(List<HistoryPoint> points, int windowMinutes, long nowUnix)
    {
        _points = points;
        _windowMinutes = Math.Max(1, windowMinutes);
        _nowUnix = nowUnix;
        InvalidateVisual();
    }

    public void Clear()
    {
        _points = new();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        const double left = 4, right = 4, top = 14, bottom = 14;
        double plotW = Math.Max(1, w - left - right);
        double plotH = Math.Max(1, h - top - bottom);

        // Horizontal grid lines.
        for (int i = 0; i <= 2; i++)
        {
            double y = top + plotH * i / 2.0;
            dc.DrawLine(GridPen, new Point(left, y), new Point(left + plotW, y));
        }

        if (_points.Count == 0)
        {
            DrawText(dc, "No recorded history yet for this app.", left + 4, top + plotH / 2 - 8);
            return;
        }

        long endTs = _nowUnix;
        long startTs = endTs - _windowMinutes * 60L;
        long span = Math.Max(1, endTs - startTs);

        long maxVal = 1;
        foreach (var p in _points)
            maxVal = Math.Max(maxVal, Math.Max(p.Sent, p.Received));

        double X(long ts) => left + (Math.Clamp(ts, startTs, endTs) - startTs) / (double)span * plotW;
        double Y(long v) => top + plotH - Math.Min(1.0, v / (double)maxVal) * plotH;

        // Download area.
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(X(_points[0].UnixSeconds), top + plotH), isFilled: true, isClosed: true);
            foreach (var p in _points)
                ctx.LineTo(new Point(X(p.UnixSeconds), Y(p.Received)), false, false);
            ctx.LineTo(new Point(X(_points[^1].UnixSeconds), top + plotH), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(DownFill, DownPen, area);

        // Upload line.
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(_points[0].UnixSeconds), Y(_points[0].Sent)), isFilled: false, isClosed: false);
            foreach (var p in _points)
                ctx.LineTo(new Point(X(p.UnixSeconds), Y(p.Sent)), true, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, UpPen, line);

        // Labels: peak (top-left), time span (bottom corners).
        DrawText(dc, "peak " + ByteFormatter.Bytes(maxVal), left + 2, 0);
        DrawText(dc, $"-{_windowMinutes} min", left + 2, h - 13);
        DrawText(dc, "now", left + plotW - 22, h - 13);
    }

    private void DrawText(DrawingContext dc, string text, double x, double y)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            Face, 10, AxisText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(x, y));
    }

    private static Brush Make(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen MakePen(Color c, double t) { var p = new Pen(Make(c), t); p.Freeze(); return p; }
}
