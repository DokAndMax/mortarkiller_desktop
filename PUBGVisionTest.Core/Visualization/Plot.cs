using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using PUBGVisionTest.Core.Detection;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace PUBGVisionTest.Core.Visualization;

public static class Plot
{
    public static Mat RenderSignal(
        IReadOnlyList<float> s, int width, int height, string title = "")
    {
        var img = new Mat(new Size(width, height), DepthType.Cv8U, 3);
        img.SetTo(new MCvScalar(250, 250, 250));

        if (!string.IsNullOrWhiteSpace(title))
            CvInvoke.PutText(img, title, new Point(10, 20), FontFace.HersheySimplex, 0.6,
                new MCvScalar(0, 0, 0), 1);

        if (s == null || s.Count == 0) return img;

        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in s) { if (v < min) min = v; if (v > max) max = v; }
        float range = Math.Max(1e-6f, max - min);

        const int L = 40, R = 10, T = 30, B = 20;
        int w = Math.Max(10, width - L - R), h = Math.Max(10, height - T - B);

        var prev = new Point(L, T + h - (int)((s[0] - min) / range * h));
        for (int i = 1; i < s.Count; i++)
        {
            int x = L + (int)Math.Round((double)i / Math.Max(1, s.Count - 1) * w);
            int y = T + h - (int)((s[i] - min) / range * h);
            var cur = new Point(x, y);
            CvInvoke.Line(img, prev, cur, new MCvScalar(30, 120, 200), 1, LineType.AntiAlias);
            prev = cur;
        }

        return img;
    }

    /// <summary>
    /// Малює графік автокореляції з піками та результатом.
    /// </summary>
    public static Mat RenderAutocorrelation(
        double[] combined, List<(int lag, double value)>? peaks,
        double smallGridStep, int pmin, int pmax,
        int width = 800, int height = 300)
    {
        int margin = 60;
        int totalW = width + margin * 2;
        int totalH = height + margin * 2 + 50;

        var chart = new Mat(totalH, totalW, DepthType.Cv8U, 3);
        chart.SetTo(new MCvScalar(255, 255, 255));

        if (combined == null || combined.Length == 0) return chart;

        int plotStart = Math.Max(0, pmin - 5);
        int plotEnd = Math.Min(combined.Length, pmax + 10);

        double maxVal = 0;
        for (int i = plotStart; i < plotEnd; i++)
            if (combined[i] > maxVal) maxVal = combined[i];
        if (maxVal < 1e-9) maxVal = 1;

        var colorACF = new MCvScalar(50, 150, 50);
        var colorPeak = new MCvScalar(0, 0, 255);
        var colorResult = new MCvScalar(255, 0, 0);
        var colorAxis = new MCvScalar(0, 0, 0);
        var colorGrid = new MCvScalar(230, 230, 230);

        // Сітка
        for (int i = 0; i <= 4; i++)
        {
            int gy = margin + (int)(height * i / 4.0);
            CvInvoke.Line(chart, new Point(margin, gy),
                new Point(margin + width, gy), colorGrid, 1);
            double val = maxVal * (4 - i) / 4.0;
            CvInvoke.PutText(chart, $"{val:F4}",
                new Point(2, gy + 5), FontFace.HersheyPlain, 0.7, colorAxis, 1);
        }

        // Осі
        CvInvoke.Line(chart, new Point(margin, margin),
            new Point(margin, margin + height), colorAxis, 2);
        CvInvoke.Line(chart, new Point(margin, margin + height),
            new Point(margin + width, margin + height), colorAxis, 2);

        // ACF крива
        Point? prev = null;
        for (int i = plotStart; i < plotEnd; i++)
        {
            int px = margin + (int)((double)(i - plotStart) / (plotEnd - plotStart) * width);
            int py = margin + height - (int)(combined[i] / maxVal * height);
            py = Math.Max(margin, Math.Min(margin + height, py));
            var cur = new Point(px, py);
            if (prev.HasValue)
                CvInvoke.Line(chart, prev.Value, cur, colorACF, 2);
            prev = cur;
        }

        // Піки
        if (peaks != null)
        {
            foreach (var (lag, value) in peaks)
            {
                if (lag >= plotStart && lag < plotEnd)
                {
                    int px = margin + (int)((double)(lag - plotStart) /
                        (plotEnd - plotStart) * width);
                    int py = margin + height - (int)(value / maxVal * height);
                    py = Math.Max(margin, Math.Min(margin + height, py));
                    CvInvoke.Circle(chart, new Point(px, py), 4, colorPeak, -1);
                    CvInvoke.PutText(chart, $"{lag}",
                        new Point(px - 10, py - 8), FontFace.HersheyPlain, 0.7,
                        colorPeak, 1);
                }
            }
        }

        // Лінія результату
        if (smallGridStep > 0)
        {
            int resultLag = (int)Math.Round(smallGridStep);
            if (resultLag >= plotStart && resultLag < plotEnd)
            {
                int px = margin + (int)((double)(resultLag - plotStart) /
                    (plotEnd - plotStart) * width);
                CvInvoke.Line(chart, new Point(px, margin),
                    new Point(px, margin + height), colorResult, 2);
                CvInvoke.PutText(chart, $"Step={smallGridStep:F1}",
                    new Point(px + 5, margin + 15), FontFace.HersheyPlain, 0.9,
                    colorResult, 1);
            }
        }

        // Заголовок
        CvInvoke.PutText(chart,
            $"Combined ACF | SmallStep={smallGridStep:F1}px",
            new Point(margin, 20), FontFace.HersheyPlain, 1.0, colorAxis, 1);

        // X мітки
        int step = Math.Max(1, (plotEnd - plotStart) / 10);
        for (int i = plotStart; i < plotEnd; i += step)
        {
            int px = margin + (int)((double)(i - plotStart) /
                (plotEnd - plotStart) * width);
            CvInvoke.Line(chart, new Point(px, margin + height),
                new Point(px, margin + height + 5), colorAxis, 1);
            CvInvoke.PutText(chart, $"{i}",
                new Point(px - 10, margin + height + 18),
                FontFace.HersheyPlain, 0.7, colorAxis, 1);
        }

        return chart;
    }

    /// <summary>
    /// Малює графік профілю з піками та лініями сітки (для benchmark).
    /// </summary>
    public static Mat RenderProfileChart(
        double[] data, DetectionResult res,
        string title, int chartWidth = 1200, int chartHeight = 400)
    {
        int margin = 60, legendH = 50;
        int totalW = chartWidth + margin * 2;
        int totalH = chartHeight + margin * 2 + legendH;
        var chart = new Mat(totalH, totalW, DepthType.Cv8U, 3);
        chart.SetTo(new MCvScalar(255, 255, 255));

        if (data.Length == 0) return chart;

        double globalMax = 255;
        var colorData = new MCvScalar(150, 50, 50);
        var colorGrid = new MCvScalar(235, 235, 235);
        var colorAxis = new MCvScalar(0, 0, 0);
        var colorSpike = new MCvScalar(0, 0, 255);
        var colorDip = new MCvScalar(255, 0, 0);
        var colorPeriod = new MCvScalar(0, 180, 0);
        var color128 = new MCvScalar(200, 200, 200);

        // Горизонтальна сітка
        for (int i = 0; i <= 4; i++)
        {
            int gy = margin + (int)(chartHeight * i / 4.0);
            CvInvoke.Line(chart, new Point(margin, gy),
                new Point(margin + chartWidth, gy), colorGrid, 1);
            CvInvoke.PutText(chart, $"{globalMax * (4 - i) / 4.0:F0}",
                new Point(5, gy + 5), FontFace.HersheyPlain, 0.8, colorAxis, 1);
        }

        // Лінія 128
        int y128 = margin + chartHeight - (int)(128.0 / globalMax * chartHeight);
        CvInvoke.Line(chart, new Point(margin, y128),
            new Point(margin + chartWidth, y128), color128, 1);

        // Осі
        CvInvoke.Line(chart, new Point(margin, margin),
            new Point(margin, margin + chartHeight), colorAxis, 2);
        CvInvoke.Line(chart, new Point(margin, margin + chartHeight),
            new Point(margin + chartWidth, margin + chartHeight), colorAxis, 2);

        // Дані
        DrawLine(chart, data, globalMax, margin, chartWidth, chartHeight, colorData, 1);

        // Лінії періоду
        if (res.Success && res.SmallGridStep > 0)
        {
            var allPeaks = (res.Debug?.SpikePeaks ?? new())
                .Concat(res.Debug?.DipPeaks ?? new())
                .OrderBy(x => x.Position).ToList();
            double start = allPeaks.Count > 0 ? allPeaks[0].Position : 0;

            for (double p = start; p < data.Length; p += res.SmallGridStep)
            {
                int px = margin + (int)(p / data.Length * chartWidth);
                if (px >= margin && px <= margin + chartWidth)
                    CvInvoke.Line(chart, new Point(px, margin),
                        new Point(px, margin + chartHeight), colorPeriod, 1);
            }
            for (double p = start - res.SmallGridStep; p >= 0; p -= res.SmallGridStep)
            {
                int px = margin + (int)(p / data.Length * chartWidth);
                if (px >= margin && px <= margin + chartWidth)
                    CvInvoke.Line(chart, new Point(px, margin),
                        new Point(px, margin + chartHeight), colorPeriod, 1);
            }
        }

        // Піки
        if (res.Debug != null)
        {
            foreach (var s in res.Debug.SpikePeaks)
            {
                int px = margin + (int)((double)s.Position / data.Length * chartWidth);
                int py = Math.Clamp(margin + chartHeight -
                    (int)((128 + s.Value) / globalMax * chartHeight), margin, margin + chartHeight);
                CvInvoke.Circle(chart, new Point(px, py), 4, colorSpike, -1);
            }
            foreach (var d in res.Debug.DipPeaks)
            {
                int px = margin + (int)((double)d.Position / data.Length * chartWidth);
                int py = Math.Clamp(margin + chartHeight -
                    (int)((128 + d.Value) / globalMax * chartHeight), margin, margin + chartHeight);
                CvInvoke.Circle(chart, new Point(px, py), 4, colorDip, -1);
            }
        }

        // Заголовок
        CvInvoke.PutText(chart, title,
            new Point(margin, 20), FontFace.HersheyPlain, 1.0, colorAxis, 1);

        string info = res.Success
            ? $"Det:{res.SmallGridStep:F1} Score:{res.BestScore:F3}"
            : "No grid";
        CvInvoke.PutText(chart, info,
            new Point(margin, 38), FontFace.HersheyPlain, 0.9, colorAxis, 1);

        // X мітки
        int step = Math.Max(1, data.Length / 10);
        for (int i = 0; i < data.Length; i += step)
        {
            int px = margin + (int)((double)i / data.Length * chartWidth);
            CvInvoke.PutText(chart, $"{i}",
                new Point(px - 10, margin + chartHeight + 18),
                FontFace.HersheyPlain, 0.7, colorAxis, 1);
        }

        return chart;
    }

    private static void DrawLine(Mat c, double[] d, double mv,
        int m, int cw, int ch, MCvScalar col, int thickness)
    {
        Point? prev = null;
        for (int i = 0; i < d.Length; i++)
        {
            int px = m + (int)((double)i / d.Length * cw);
            int py = Math.Clamp(m + ch - (int)(d[i] / mv * ch), m, m + ch);
            if (prev.HasValue)
                CvInvoke.Line(c, prev.Value, new Point(px, py), col, thickness);
            prev = new Point(px, py);
        }
    }
}