// Detection/FramePreprocessor.cs
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace mortarkiller.Detection;

/// <summary>
/// Кроп, конвертація кольорів, детекція лівої чорної панелі.
/// Stateless — всі методи чисті або залежать лише від параметрів.
/// </summary>
public static class FramePreprocessor
{
    /// <summary>
    /// Будує прямокутник центральної смуги для Phase 1.
    /// </summary>
    public static Rectangle BuildCentralStrip(int w, int h, double topCut, double sideCut)
    {
        int x = (int)Math.Round(w * sideCut);
        int y = (int)Math.Round(h * topCut);
        int ww = w - 2 * x;
        int hh = h - y;
        return new Rectangle(x, y, Math.Max(1, ww), Math.Max(1, hh));
    }

    /// <summary>
    /// Кропає Bitmap і конвертує в BGR Mat, готовий для YOLO.
    /// </summary>
    public static (Bitmap croppedBmp, Mat bgrMat) CropAndConvert(
        Bitmap source, Rectangle cropRect)
    {
        var cropped = source.Clone(cropRect, PixelFormat.Format24bppRgb);
        var mat = cropped.ToMat();
        EnsureBgr(ref mat);
        return (cropped, mat);
    }

    /// <summary>
    /// Конвертує повний Bitmap → BGR Mat.
    /// </summary>
    public static Mat ToBgrMat(Bitmap source)
    {
        var mat = source.ToMat();
        EnsureBgr(ref mat);
        return mat;
    }

    /// <summary>
    /// Вирізає робочу область після лівої чорної панелі.
    /// </summary>
    public static (Rectangle workRect, Mat matWork, Bitmap bmpWork) ExtractWorkArea(
        Bitmap bmpFull, Mat matFull, int leftCut)
    {
        var workRect = new Rectangle(leftCut, 0,
            matFull.Width - leftCut, matFull.Height);
        var matWork = new Mat(matFull, workRect);
        var bmpWork = bmpFull.Clone(workRect, PixelFormat.Format24bppRgb);
        return (workRect, matWork, bmpWork);
    }

    /// <summary>
    /// Гарантує 3-канальний BGR формат Mat.
    /// </summary>
    public static void EnsureBgr(ref Mat mat)
    {
        if (mat.NumberOfChannels == 4)
        {
            var bgr = new Mat();
            CvInvoke.CvtColor(mat, bgr, ColorConversion.Bgra2Bgr);
            mat.Dispose();
            mat = bgr;
        }
        else if (mat.NumberOfChannels == 1)
        {
            var bgr = new Mat();
            CvInvoke.CvtColor(mat, bgr, ColorConversion.Gray2Bgr);
            mat.Dispose();
            mat = bgr;
        }
    }

    /// <summary>
    /// Знаходить праву межу лівої чорної панелі на карті.
    /// Повертає 0, якщо панель не знайдена.
    /// </summary>
    public static int DetectLeftPanelCut(Mat matFull, DebugDumper? dbg = null)
    {
        int w = matFull.Width;
        int h = matFull.Height;

        int xMax = Math.Max(1, (int)Math.Round(w * 0.48));
        int yPad = Math.Max(2, (int)Math.Round(h * 0.02));
        var roiRect = new Rectangle(0, yPad, xMax, Math.Max(1, h - 2 * yPad));
        if (roiRect.Width <= 0 || roiRect.Height <= 0) return 0;

        using var roi = new Mat(matFull, roiRect);

        using var mask = new Mat();
        CvInvoke.InRange(roi,
            new ScalarArray(new MCvScalar(0, 0, 0)),
            new ScalarArray(new MCvScalar(18, 18, 18)),
            mask);

        int kVert = Math.Max(9, h / 40);
        if ((kVert & 1) == 0) kVert++;
        using var kernelClose = CvInvoke.GetStructuringElement(
            MorphShapes.Rectangle, new Size(3, kVert), new Point(-1, -1));
        using var maskClosed = new Mat();
        CvInvoke.MorphologyEx(mask, maskClosed, MorphOp.Close, kernelClose,
            new Point(-1, -1), 1, BorderType.Reflect, default);

        using var kernelOpen = CvInvoke.GetStructuringElement(
            MorphShapes.Rectangle, new Size(3, 3), new Point(-1, -1));
        using var maskClean = new Mat();
        CvInvoke.MorphologyEx(maskClosed, maskClean, MorphOp.Open, kernelOpen,
            new Point(-1, -1), 1, BorderType.Reflect, default);

        using var maskFloat = new Mat();
        maskClean.ConvertTo(maskFloat, DepthType.Cv32F, 1.0 / 255.0, 0);
        using var colSums = new Mat();
        CvInvoke.Reduce(maskFloat, colSums, ReduceDimension.SingleRow,
            ReduceType.ReduceSum);

        int rw = colSums.Cols;
        int rh = maskClean.Rows;
        float[] colCntF = new float[rw];
        Marshal.Copy(colSums.DataPointer, colCntF, 0, rw);

        double hiThr = rh * 0.94;
        double lowThr = rh * 0.20;

        int lastHi = -1;
        for (int x = 0; x < rw; x++)
            if (colCntF[x] >= hiThr) lastHi = x;

        if (lastHi < 0)
        {
            dbg?.SaveMat(mask, "leftpanel_mask_initial_nohit");
            return 0;
        }

        int win = Math.Max(4, w / 900);
        int candidate = lastHi;

        for (int x = lastHi; x <= rw - win - 1; x++)
        {
            int below = 0;
            for (int j = 0; j < win; j++)
                if (colCntF[x + j] <= lowThr) below++;
            if (below == win) { candidate = x; break; }
        }

        int leftBand = 0;
        for (int i = 0; i < 8 && candidate - i >= 0; i++)
            if (colCntF[candidate - i] >= hiThr) leftBand++;

        int rightBand = 0;
        for (int i = 1; i <= Math.Min(32, rw - 1 - candidate); i++)
            if (colCntF[candidate + i] <= lowThr) rightBand++;

        bool looksLikePanel = leftBand >= 3
            && rightBand >= Math.Min(32, rw - 1 - candidate) * 0.7;
        if (!looksLikePanel)
        {
            dbg?.SaveMat(maskClean, "leftpanel_mask_clean_lowconf");
            return 0;
        }

        int leftCutResult = roiRect.X + candidate;
        leftCutResult = Math.Clamp(leftCutResult, 0, (int)(w * 0.47));

        if (dbg != null)
        {
            dbg.SaveMat(mask, "leftpanel_mask_initial");
            dbg.SaveMat(maskClean, "leftpanel_mask_clean");
            using var overlay = matFull.Clone();
            CvInvoke.Line(overlay,
                new Point(leftCutResult, 0),
                new Point(leftCutResult, h - 1),
                new MCvScalar(0, 255, 255), 2);
            dbg.SaveMat(overlay, "leftpanel_cut_overlay");
            dbg.SaveText("leftpanel_notes",
                $"roi=({roiRect.X},{roiRect.Y},{roiRect.Width},{roiRect.Height}), " +
                $"leftCut={leftCutResult}");
        }

        return leftCutResult;
    }
}