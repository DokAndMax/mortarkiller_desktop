using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using PUBGVisionTest.Core.Detection;
using System.Drawing;

namespace PUBGVisionTest.Core.Visualization;

// ===============================================================
//  Overlay (адаптований під DetectionResult)
// ===============================================================

public static class Overlay
{
    public static void DrawGridOverlay(Mat img, DetectionResult res)
    {
        if (!res.Success || res.SmallGridStep <= 0) return;

        int w = img.Cols, h = img.Rows;
        int step = (int)Math.Round(res.SmallGridStep);
        if (step < 2) return;

        var c100 = new MCvScalar(0, 255, 0);
        var c1000 = new MCvScalar(0, 165, 255);

        // ▼▼▼ ОПТИМІЗОВАНО: AntiAlias тільки для великих ліній ▼▼▼

        // Вертикальні лінії
        int idx = 0;
        for (int x = res.ShiftX; x < w; x += step, idx++)
        {
            bool big = idx % 10 == 0;
            CvInvoke.Line(img,
                new Point(x, 0), new Point(x, h - 1),
                big ? c1000 : c100,
                big ? 2 : 1,
                big ? LineType.AntiAlias : LineType.EightConnected);  // ← змінено
        }

        // Горизонтальні лінії
        idx = 0;
        for (int y = res.ShiftY; y < h; y += step, idx++)
        {
            bool big = idx % 10 == 0;
            CvInvoke.Line(img,
                new Point(0, y), new Point(w - 1, y),
                big ? c1000 : c100,
                big ? 2 : 1,
                big ? LineType.AntiAlias : LineType.EightConnected);  // ← змінено
        }
        // ▲▲▲

        void Text(string s, int y, double sz)
        {
            CvInvoke.PutText(img, s, new Point(10, y), FontFace.HersheySimplex, sz,
                new MCvScalar(20, 20, 20), 3, LineType.AntiAlias);
            CvInvoke.PutText(img, s, new Point(10, y), FontFace.HersheySimplex, sz,
                new MCvScalar(255, 255, 255), 1, LineType.AntiAlias);
        }

        Text($"100m ~ {res.SmallGridStep:F2}px  |  1km ~ {res.LargeGridStep:F1}px",
            22, 0.6);
        Text($"Period H={res.DetectedPeriodH}  V={res.DetectedPeriodV}  " +
             $"Score={res.BestScore:F3}", 45, 0.55);
    }
}