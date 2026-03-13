using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace PUBGVisionTest.Core.Detection;

/// <summary>
/// Головний детектор масштабу сітки.
/// 
/// Алгоритм (перенесений з оригінального Main):
///   1. Морфологічне виділення 4-х масок:
///      - світлі горизонтальні лінії (TopHat + kernelV)
///      - світлі вертикальні лінії  (TopHat + kernelH)
///      - темні горизонтальні лінії  (BlackHat + kernelV)
///      - темні вертикальні лінії    (BlackHat + kernelH)
///   2. Побудова проекційних профілів з кожної маски
///   3. Результуючий сигнал: netSignal = profLight − profDark
///      - спайки > 0  → світлі лінії (100 м)
///      - провали < 0 → чорні лінії  (1000 м)
///   4. Фільтрація паразитних двійників (FilterHalos)
///   5. Зсув на 128 і подача в GridAnalyzerInternal
///   6. Крос-валідація, вибір найкращого кандидата
/// </summary>
public static class GridScaleDetector
{
    public static DetectionResult Detect100m(
        Mat bgr, DetectorParams? p = null, bool debug = false, double? priorPx = null)
    {
        p ??= new DetectorParams();
        var res = new DetectionResult();

        try
        {
            if (bgr == null || bgr.IsEmpty)
                return res.Fail("Empty input image");

            // ── Конвертація в grayscale ─────────────────────────────
            using var gray = new Mat();
            if (bgr.NumberOfChannels == 1)
                bgr.CopyTo(gray);
            else if (bgr.NumberOfChannels == 4)
            {
                using var bgr3 = new Mat();
                CvInvoke.CvtColor(bgr, bgr3, ColorConversion.Bgra2Bgr);
                CvInvoke.CvtColor(bgr3, gray, ColorConversion.Bgr2Gray);
            }
            else
                CvInvoke.CvtColor(bgr, gray, ColorConversion.Bgr2Gray);

            int ks = p.MorphKernelSize;

            // ── Морфологічні ядра ───────────────────────────────────
            // kernelH (ks × 1) — горизонтальне ядро → виділяє вертикальні лінії
            // kernelV (1 × ks) — вертикальне ядро  → виділяє горизонтальні лінії
            using var kernelH = CvInvoke.GetStructuringElement(
                MorphShapes.Rectangle, new Size(ks, 1), new Point(-1, -1));
            using var kernelV = CvInvoke.GetStructuringElement(
                MorphShapes.Rectangle, new Size(1, ks), new Point(-1, -1));

            // ── Світлі лінії (White Top-Hat) ────────────────────────
            // TopHat(kernelV) → горизонтальні світлі лінії
            using var topHatV = new Mat();
            CvInvoke.MorphologyEx(gray, topHatV, MorphOp.Tophat, kernelV,
                new Point(-1, -1), 1, BorderType.Reflect, default);
            using var maskLightHoriz = new Mat();
            CvInvoke.Threshold(topHatV, maskLightHoriz, 10, 255, ThresholdType.Binary);

            // TopHat(kernelH) → вертикальні світлі лінії
            using var topHatH = new Mat();
            CvInvoke.MorphologyEx(gray, topHatH, MorphOp.Tophat, kernelH,
                new Point(-1, -1), 1, BorderType.Reflect, default);
            using var maskLightVert = new Mat();
            CvInvoke.Threshold(topHatH, maskLightVert, 10, 255, ThresholdType.Binary);

            // ── Темні лінії (Black-Hat) ─────────────────────────────
            // BlackHat(kernelV) → горизонтальні темні лінії
            using var blackHatV = new Mat();
            CvInvoke.MorphologyEx(gray, blackHatV, MorphOp.Blackhat, kernelV,
                new Point(-1, -1), 1, BorderType.Reflect, default);
            using var maskDarkHoriz = new Mat();
            CvInvoke.Threshold(blackHatV, maskDarkHoriz, 10, 255, ThresholdType.Binary);

            // BlackHat(kernelH) → вертикальні темні лінії
            using var blackHatH = new Mat();
            CvInvoke.MorphologyEx(gray, blackHatH, MorphOp.Blackhat, kernelH,
                new Point(-1, -1), 1, BorderType.Reflect, default);
            using var maskDarkVert = new Mat();
            CvInvoke.Threshold(blackHatH, maskDarkVert, 10, 255, ThresholdType.Binary);

            // ── Об'єднані маски (для debug / overlay) ───────────────
            using var maskVertAll = new Mat();
            CvInvoke.BitwiseOr(maskLightVert, maskDarkVert, maskVertAll);

            using var maskHorizAll = new Mat();
            CvInvoke.BitwiseOr(maskLightHoriz, maskDarkHoriz, maskHorizAll);

            using var maskAll = new Mat();
            CvInvoke.BitwiseOr(maskVertAll, maskHorizAll, maskAll);

            // ── Профілі з окремих масок ─────────────────────────────
            using var lHImg = maskLightHoriz.ToImage<Gray, byte>();
            using var dHImg = maskDarkHoriz.ToImage<Gray, byte>();
            using var lVImg = maskLightVert.ToImage<Gray, byte>();
            using var dVImg = maskDarkVert.ToImage<Gray, byte>();

            double[] profLightH = ComputeHorizontalProfile(lHImg);
            double[] profDarkH = ComputeHorizontalProfile(dHImg);
            double[] profLightV = ComputeVerticalProfile(lVImg);
            double[] profDarkV = ComputeVerticalProfile(dVImg);

            // ── Результуючий сигнал: light − dark ───────────────────
            //   > 0  →  світлі лінії домінують  (100 м)
            //   < 0  →  темні лінії домінують    (1000 м)
            double[] netH = new double[profLightH.Length];
            for (int i = 0; i < netH.Length; i++)
                netH[i] = profLightH[i] - profDarkH[i];

            double[] netV = new double[profLightV.Length];
            for (int i = 0; i < netV.Length; i++)
                netV[i] = profLightV[i] - profDarkV[i];

            // ── Фільтрація паразитних двійників ─────────────────────
            double[] cleanNetH = FilterHalos(netH, ks);
            double[] cleanNetV = FilterHalos(netV, ks);

            // ── Зсув на 128 для GridAnalyzerInternal ────────────────
            // GridAnalyzerInternal вважає baseline = 128:
            //   > 128 → спайки  (світлі лінії, 100 м)
            //   < 128 → провали (темні лінії,  1000 м)
            double[] profileH = ShiftAndClamp(cleanNetH, 128.0);
            double[] profileV = ShiftAndClamp(cleanNetV, 128.0);

            // ── Аналіз через GridAnalyzerInternal ───────────────────
            var analyzer = new GridAnalyzerInternal(p.PMin, p.PMax);
            var analysis = analyzer.Analyze(profileH, profileV);

            if (analysis == null || !analysis.Success)
                return res.Fail(analysis?.FailReason ?? "Analysis failed");

            // ── Заповнення результату ───────────────────────────────
            res.Success = true;
            res.SmallGridStep = analysis.SmallGridStep;
            res.LargeGridStep = analysis.SmallGridStep * 10.0;
            res.Ratio = 10.0;
            res.DetectedPeriodH = analysis.DetectedPeriodH;
            res.DetectedPeriodV = analysis.DetectedPeriodV;
            res.Method = analysis.Method;
            res.BestScore = analysis.BestScore;

            // Визначення зсувів (для оверлею)
            int period = (int)Math.Round(res.SmallGridStep);
            if (period >= 2)
            {
                res.ShiftX = FindBestShift(profileV, period);
                res.ShiftY = FindBestShift(profileH, period);
            }

            // ── Debug дані ──────────────────────────────────────────
            if (debug)
            {
                res.Debug = new DebugData
                {
                    MaskVert = maskVertAll.Clone(),
                    MaskHoriz = maskHorizAll.Clone(),
                    MaskCombined = maskAll.Clone(),
                    HorizProfile = profileH,
                    VertProfile = profileV,
                    AutocorrH = analysis.AutocorrH,
                    AutocorrV = analysis.AutocorrV,
                    AutocorrCombined = analysis.AutocorrCombined,
                    AutocorrPeaks = analysis.AutocorrPeaks,
                    DebugLog = analysis.DebugLog,
                    SpikePeaks = analysis.SpikePeaks,
                    DipPeaks = analysis.DipPeaks
                };
            }

            return res;
        }
        catch (Exception ex)
        {
            return res.Fail("Exception: " + ex.Message);
        }
    }

    // =================================================================
    //  Фільтрація паразитних артефактів (з оригінального алгоритму)
    //
    //  Морфологічні TopHat/BlackHat створюють «двійників»: поруч із
    //  справжнім піком з'являється менший пік протилежного знаку.
    //  Алгоритм: у вікні ±searchRadius зберігаємо лише піки, чий знак
    //  збігається зі знаком домінантного (найбільшого за модулем) піка.
    // =================================================================

    private static double[] FilterHalos(double[] signal, int searchRadius)
    {
        double[] filtered = new double[signal.Length];

        for (int i = 0; i < signal.Length; i++)
        {
            if (signal[i] == 0) continue;

            double maxAbsNearby = 0;
            int signOfMax = 0;

            int start = Math.Max(0, i - searchRadius);
            int end = Math.Min(signal.Length - 1, i + searchRadius);

            for (int j = start; j <= end; j++)
            {
                if (Math.Abs(signal[j]) > maxAbsNearby)
                {
                    maxAbsNearby = Math.Abs(signal[j]);
                    signOfMax = Math.Sign(signal[j]);
                }
            }

            // Зберігаємо, якщо знак збігається з домінантним АБО це сам домінант
            if (Math.Sign(signal[i]) == signOfMax || Math.Abs(signal[i]) >= maxAbsNearby)
                filtered[i] = signal[i];
            // Інакше: паразитний двійник → видаляємо (залишаємо 0)
        }

        return filtered;
    }

    // =================================================================
    //  Допоміжні методи
    // =================================================================

    private static double[] ShiftAndClamp(double[] signal, double baseline)
    {
        double[] result = new double[signal.Length];
        for (int i = 0; i < signal.Length; i++)
            result[i] = Math.Clamp(baseline + signal[i], 0, 255);
        return result;
    }

    private static double[] ComputeHorizontalProfile(Image<Gray, byte> img)
    {
        int h = img.Height, w = img.Width;
        double[] profile = new double[h];
        for (int y = 0; y < h; y++)
        {
            double sum = 0;
            for (int x = 0; x < w; x++)
                sum += img.Data[y, x, 0];
            profile[y] = sum / w;
        }
        return profile;
    }

    private static double[] ComputeVerticalProfile(Image<Gray, byte> img)
    {
        int h = img.Height, w = img.Width;
        double[] profile = new double[w];
        for (int x = 0; x < w; x++)
        {
            double sum = 0;
            for (int y = 0; y < h; y++)
                sum += img.Data[y, x, 0];
            profile[x] = sum / h;
        }
        return profile;
    }

    /// <summary>
    /// Знаходить зсув, при якому гребінчастий фільтр з кроком period
    /// дає максимальну суму відхилень від baseline (= найкраще вирівнювання з лініями).
    /// </summary>
    private static int FindBestShift(double[] profile, int period)
    {
        if (period < 2 || profile.Length < period) return 0;

        int bestShift = 0;
        double bestSum = double.NegativeInfinity;

        for (int s = 0; s < period; s++)
        {
            double sum = 0;
            for (int i = s; i < profile.Length; i += period)
                sum += Math.Abs(profile[i] - 128.0);

            if (sum > bestSum)
            {
                bestSum = sum;
                bestShift = s;
            }
        }

        return bestShift;
    }
}