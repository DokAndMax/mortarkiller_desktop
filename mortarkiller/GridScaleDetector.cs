using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace mortarkiller;

// ═══════════════════════════════════════════════════════════════
//  Результат детекції
// ═══════════════════════════════════════════════════════════════

public class DetectionResult
{
    public bool Success;
    public string FailReason;

    /// <summary>Крок малої сітки (100м) у пікселях</summary>
    public double SmallGridStep;
    /// <summary>Крок великої сітки (1км) у пікселях</summary>
    public double LargeGridStep;
    /// <summary>Відношення великої до малої сітки</summary>
    public double Ratio;

    /// <summary>Зсув сітки по X (для оверлею)</summary>
    public int ShiftX;
    /// <summary>Зсув сітки по Y (для оверлею)</summary>
    public int ShiftY;

    /// <summary>Період, знайдений тільки по горизонтальному профілю</summary>
    public int DetectedPeriodH;
    /// <summary>Період, знайдений тільки по вертикальному профілю</summary>
    public int DetectedPeriodV;

    /// <summary>px на 100м (для сумісності з UI)</summary>
    public double PxPer100m => SmallGridStep;

    // ── Debug дані ──
    public DebugData Debug;

    public DetectionResult Fail(string reason)
    {
        Success = false;
        FailReason = reason;
        return this;
    }
}

public class DebugData
{
    public Mat MaskVert, MaskHoriz, MaskCombined, ValidArea;
    public double[] HorizProfile, VertProfile;
    public double[] AutocorrH, AutocorrV, AutocorrCombined;
    public List<(int lag, double value)> AutocorrPeaks = new();
}

// ═══════════════════════════════════════════════════════════════
//  Параметри детектора (мінімальний набір, без тренування)
// ═══════════════════════════════════════════════════════════════

public class DetectorParams
{
    /// <summary>Мінімальний період пошуку (пікселі)</summary>
    public int PMin = 4;
    /// <summary>Максимальний період пошуку (пікселі)</summary>
    public int PMax = 400;
    /// <summary>Розмір ядра морфологічної операції для виділення ліній</summary>
    public int MorphKernelSize = 5;
    /// <summary>Поріг бінаризації для масок ліній</summary>
    public int LineMaskThreshold = 10;
    /// <summary>Розмір вікна для аналізу текстури (фільтрація моря)</summary>
    public int TextureWindowSize = 21;
    /// <summary>Поріг стандартного відхилення для визначення "моря"</summary>
    public double SeaStdThreshold = 3.0;
    /// <summary>Розмір морфологічного ядра для очищення маски валідної зони</summary>
    public int ValidAreaMorphSize = 15;
    /// <summary>Розмір вікна для RemoveDC (має бути достатньо великим)</summary>
    public int DcRemovalWindow = 400;
    /// <summary>Поріг субгармоніки відносно найсильнішого піку</summary>
    public double SubharmonicThreshold = 0.75;
    /// <summary>Мінімальна кількість підтверджених кратних для субгармоніки</summary>
    public int MinConfirmedMultiples = 2;

    public DetectorParams Clone() => (DetectorParams)MemberwiseClone();
}

// ═══════════════════════════════════════════════════════════════
//  Основний детектор — автокореляційний алгоритм
// ═══════════════════════════════════════════════════════════════

public static class GridScaleDetector
{
    /// <summary>
    /// Головна точка входу: знаходить масштаб сітки на зображенні.
    /// Алгоритм:
    ///   1. Морфологічне виділення горизонтальних/вертикальних ліній
    ///   2. Фільтрація "моря" (однотонних ділянок без сітки)
    ///   3. Побудова проекційних профілів масок
    ///   4. Автокореляція кожного профілю
    ///   5. Об'єднання ACF та пошук фундаментального періоду
    ///   6. Перехресна валідація між осями
    /// </summary>
    public static DetectionResult Detect100m(
        Mat bgr, DetectorParams p = null, bool debug = false, double? priorPx = null)
    {
        p ??= new DetectorParams();
        var res = new DetectionResult();

        try
        {
            if (bgr == null || bgr.IsEmpty)
                return res.Fail("Empty input image");

            // ── Конвертація в grayscale ──
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

            // ── Крок 1: Морфологічне виділення ліній (dilate + absDiff + threshold) ──
            int ks = p.MorphKernelSize;

            using var hKernel = CvInvoke.GetStructuringElement(
                MorphShapes.Rectangle, new Size(ks, 1), new Point(-1, -1));
            using var maxH = new Mat();
            CvInvoke.Dilate(gray, maxH, hKernel, new Point(-1, -1), 1,
                BorderType.Reflect, default);

            using var vKernel = CvInvoke.GetStructuringElement(
                MorphShapes.Rectangle, new Size(1, ks), new Point(-1, -1));
            using var maxV = new Mat();
            CvInvoke.Dilate(gray, maxV, vKernel, new Point(-1, -1), 1,
                BorderType.Reflect, default);

            // Маски ліній: де dilate сильно відрізняється від оригіналу → лінія
            using var diffH = new Mat();
            CvInvoke.AbsDiff(maxH, gray, diffH);
            var maskVert = new Mat();
            CvInvoke.Threshold(diffH, maskVert, p.LineMaskThreshold, 255,
                ThresholdType.BinaryInv);

            using var diffV = new Mat();
            CvInvoke.AbsDiff(maxV, gray, diffV);
            var maskHoriz = new Mat();
            CvInvoke.Threshold(diffV, maskHoriz, p.LineMaskThreshold, 255,
                ThresholdType.BinaryInv);

            // ── Крок 2: Фільтрація "моря" (однотонних ділянок) ──
            var validAreaMask = ComputeValidAreaMask(gray, p);

            using var seaMask = new Mat();
            CvInvoke.BitwiseNot(validAreaMask, seaMask);

            // Заливаємо "море" білим (255 = відсутність ліній у масці)
            CvInvoke.BitwiseOr(maskVert, seaMask, maskVert);
            CvInvoke.BitwiseOr(maskHoriz, seaMask, maskHoriz);

            var lineMask = new Mat();
            CvInvoke.BitwiseOr(maskVert, maskHoriz, lineMask);

            // ── Крок 3: Профілі масок ──
            using var maskHImg = maskHoriz.ToImage<Gray, byte>();
            using var maskVImg = maskVert.ToImage<Gray, byte>();
            using var validImg = validAreaMask.ToImage<Gray, byte>();

            double[] horizProfile = ComputeHorizontalProfile(maskHImg, validImg);
            double[] vertProfile = ComputeVerticalProfile(maskVImg, validImg);

            // ── Крок 4: Аналіз через автокореляцію ──
            var analysis = AnalyzeAutocorrelation(
                horizProfile, vertProfile, p);

            if (analysis == null)
                return res.Fail("Autocorrelation analysis failed");

            // ── Заповнюємо результат ──
            res.Success = analysis.SmallGridStep > 0;
            res.SmallGridStep = analysis.SmallGridStep;
            res.LargeGridStep = analysis.LargeGridStep;
            res.Ratio = analysis.Ratio;
            res.DetectedPeriodH = analysis.DetectedPeriodH;
            res.DetectedPeriodV = analysis.DetectedPeriodV;

            // ── Визначення зсувів (для оверлею) ──
            if (res.Success)
            {
                int period = (int)Math.Round(res.SmallGridStep);
                res.ShiftX = FindBestShift(vertProfile, period);
                res.ShiftY = FindBestShift(horizProfile, period);
            }

            // ── Debug ──
            if (debug)
            {
                res.Debug = new DebugData
                {
                    MaskVert = maskVert.Clone(),
                    MaskHoriz = maskHoriz.Clone(),
                    MaskCombined = lineMask.Clone(),
                    ValidArea = validAreaMask.Clone(),
                    HorizProfile = horizProfile,
                    VertProfile = vertProfile,
                    AutocorrH = analysis.AutocorrH,
                    AutocorrV = analysis.AutocorrV,
                    AutocorrCombined = analysis.AutocorrCombined,
                    AutocorrPeaks = analysis.AutocorrPeaks
                };
            }

            // Cleanup (не-debug маски)
            if (!debug)
            {
                maskVert.Dispose();
                maskHoriz.Dispose();
                lineMask.Dispose();
                validAreaMask.Dispose();
            }

            return res;
        }
        catch (Exception ex)
        {
            return res.Fail("Exception: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Фільтрація "моря" — обчислення маски валідної зони
    // ═══════════════════════════════════════════════════════════════

    static Mat ComputeValidAreaMask(Mat gray, DetectorParams p)
    {
        using var floatGray = new Mat();
        gray.ConvertTo(floatGray, DepthType.Cv32F);

        int bSize = p.TextureWindowSize;
        using var mean = new Mat();
        CvInvoke.Blur(floatGray, mean, new Size(bSize, bSize), new Point(-1, -1));

        using var graySq = new Mat();
        CvInvoke.Multiply(floatGray, floatGray, graySq);

        using var sqMean = new Mat();
        CvInvoke.Blur(graySq, sqMean, new Size(bSize, bSize), new Point(-1, -1));

        using var meanSq = new Mat();
        CvInvoke.Multiply(mean, mean, meanSq);

        using var variance = new Mat();
        CvInvoke.Subtract(sqMean, meanSq, variance);

        using var stdDev = new Mat();
        CvInvoke.Sqrt(variance, stdDev);

        var validAreaMask = new Mat();
        CvInvoke.Threshold(stdDev, validAreaMask, p.SeaStdThreshold, 255,
            ThresholdType.Binary);
        validAreaMask.ConvertTo(validAreaMask, DepthType.Cv8U);

        // Морфологічне очищення
        int mSize = p.ValidAreaMorphSize;
        using var morphKernel = CvInvoke.GetStructuringElement(
            MorphShapes.Rectangle, new Size(mSize, mSize), new Point(-1, -1));
        CvInvoke.MorphologyEx(validAreaMask, validAreaMask, MorphOp.Close,
            morphKernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());
        CvInvoke.MorphologyEx(validAreaMask, validAreaMask, MorphOp.Open,
            morphKernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar());

        return validAreaMask;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Аналіз через автокореляцію
    // ═══════════════════════════════════════════════════════════════

    class AutocorrelationResult
    {
        public double SmallGridStep;
        public double LargeGridStep;
        public double Ratio;
        public double[] AutocorrH;
        public double[] AutocorrV;
        public double[] AutocorrCombined;
        public List<(int lag, double value)> AutocorrPeaks = new();
        public int DetectedPeriodH;
        public int DetectedPeriodV;
    }

    /// <summary>
    /// Аналіз через автокореляцію:
    /// 1. Інвертуємо та нормалізуємо профілі масок
    /// 2. ACF кожного профілю
    /// 3. Об'єднуємо ACF_H і ACF_V (середнє арифметичне)
    /// 4. Шукаємо всі піки об'єднаної ACF
    /// 5. Обираємо найсильніший пік, перевіряємо субгармоніки
    /// 6. Уточнюємо параболічною інтерполяцією
    /// 7. Перехресна валідація між осями
    /// </summary>
    static AutocorrelationResult AnalyzeAutocorrelation(
        double[] horizProfile, double[] vertProfile,
        DetectorParams p)
    {
        var result = new AutocorrelationResult();

        if (horizProfile.Length == 0 || vertProfile.Length == 0)
            return result;

        int pmin = p.PMin;
        int pmax = p.PMax;

        // ── Підготовка сигналу ──
        // Маска: 255 = не-лінія, ~0 = лінія → інвертуємо
        double[] sigH = InvertProfile(horizProfile);
        double[] sigV = InvertProfile(vertProfile);

        // Вирівнюємо (прибираємо DC + тренд)
        sigH = RemoveDCAndNormalize(sigH, p.DcRemovalWindow);
        sigV = RemoveDCAndNormalize(sigV, p.DcRemovalWindow);

        pmax = Math.Min(pmax, Math.Min(sigH.Length / 2, sigV.Length / 2));
        pmin = Math.Max(pmin, 2);

        if (pmin >= pmax)
            return result;

        // ── Автокореляція ──
        double[] acfH = ComputeAutocorrelation(sigH, pmax);
        double[] acfV = ComputeAutocorrelation(sigV, pmax);

        result.AutocorrH = acfH;
        result.AutocorrV = acfV;

        // ── Об'єднання: середнє арифметичне (логіка "АБО") ──
        int acfLen = Math.Min(acfH.Length, acfV.Length);
        double[] combined = new double[acfLen];
        for (int i = 0; i < acfLen; i++)
            combined[i] = (acfH[i] + acfV[i]) / 2.0;

        result.AutocorrCombined = combined;

        // ── Пошук піків ──
        int peakRadius = Math.Max(3, pmax / 100);
        var acPeaks = FindACFPeaks(combined, pmin,
            Math.Min(pmax, acfLen - 1), peakRadius);
        result.AutocorrPeaks = acPeaks;

        if (acPeaks.Count == 0)
            return result;

        // ── Вибір найсильнішого піку та перевірка субгармонік ──
        int bestPeriod = FindFundamentalPeriod(
            acPeaks, pmin, pmax, peakRadius, p.SubharmonicThreshold,
            p.MinConfirmedMultiples);

        // ── Уточнення параболічною інтерполяцією ──
        double refinedPeriod = RefineACFPeak(combined, bestPeriod);

        // ── Окремі ACF для перехресної валідації ──
        result.DetectedPeriodH = FindFundamentalPeriod(
            FindACFPeaks(acfH, pmin, Math.Min(pmax, acfH.Length - 1), peakRadius),
            pmin, pmax, peakRadius, p.SubharmonicThreshold * 0.9,
            p.MinConfirmedMultiples);

        result.DetectedPeriodV = FindFundamentalPeriod(
            FindACFPeaks(acfV, pmin, Math.Min(pmax, acfV.Length - 1), peakRadius),
            pmin, pmax, peakRadius, p.SubharmonicThreshold * 0.9,
            p.MinConfirmedMultiples);

        // ── Фінальне рішення ──
        result.SmallGridStep = refinedPeriod;
        result.LargeGridStep = refinedPeriod * 10.0;
        result.Ratio = 10.0;

        // Перехресна валідація
        if (result.DetectedPeriodH > 0 && result.DetectedPeriodV > 0)
        {
            double periodH = result.DetectedPeriodH;
            double periodV = result.DetectedPeriodV;

            bool hConfirms = IsMultipleOf(periodH, refinedPeriod, 0.15);
            bool vConfirms = IsMultipleOf(periodV, refinedPeriod, 0.15);

            if (!hConfirms && !vConfirms)
            {
                // Якщо ні H ні V не підтвердили combined —
                // пробуємо взяти менший з окремих, якщо вони узгоджені
                if (IsMultipleOf(periodH, periodV, 0.15))
                {
                    double smaller = Math.Min(periodH, periodV);
                    result.SmallGridStep = smaller;
                    result.LargeGridStep = smaller * 10.0;
                }
            }
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Пошук фундаментального періоду
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Знаходить фундаментальний період:
    /// 1. Бере найсильніший пік ACF
    /// 2. Перевіряє його дільники (2..7) — якщо дільник теж має
    ///    сильний пік і його кратні підтверджуються, обирає його
    /// 3. Додатково перевіряє дільники результату (глибша субгармоніка)
    /// </summary>
    static int FindFundamentalPeriod(
        List<(int lag, double value)> acPeaks,
        int pmin, int pmax, int peakRadius,
        double subThresholdRatio, int minConfirmed)
    {
        if (acPeaks.Count == 0) return 0;

        var sortedByValue = acPeaks.OrderByDescending(p => p.value).ToList();
        double strongestValue = sortedByValue[0].value;
        int bestPeriod = sortedByValue[0].lag;

        double subharmonicThreshold = strongestValue * subThresholdRatio;

        // Перший прохід: дільники найсильнішого піку
        bestPeriod = TryFindSubharmonic(
            bestPeriod, acPeaks, pmin, pmax, peakRadius,
            subharmonicThreshold, minConfirmed, 2, 7);

        // Другий прохід: дільники поточного bestPeriod (глибша субгармоніка)
        bestPeriod = TryFindSubharmonic(
            bestPeriod, acPeaks, pmin, pmax, peakRadius,
            subharmonicThreshold, minConfirmed, 2, 5);

        return bestPeriod;
    }

    static int TryFindSubharmonic(
        int currentPeriod,
        List<(int lag, double value)> acPeaks,
        int pmin, int pmax, int peakRadius,
        double subharmonicThreshold, int minConfirmed,
        int minDivisor, int maxDivisor)
    {
        for (int divisor = minDivisor; divisor <= maxDivisor; divisor++)
        {
            int subLag = currentPeriod / divisor;
            if (subLag < pmin) break;

            var subPeak = acPeaks
                .Where(p => Math.Abs(p.lag - subLag) <= peakRadius + 1)
                .OrderByDescending(p => p.value)
                .FirstOrDefault();

            if (subPeak.lag > 0 && subPeak.value >= subharmonicThreshold)
            {
                // Перевіряємо кратні
                int confirmed = 0;
                for (int mult = 2; mult <= 8; mult++)
                {
                    int target = subPeak.lag * mult;
                    if (target > pmax) break;

                    if (acPeaks.Any(p =>
                        Math.Abs(p.lag - target) <= peakRadius + 1 &&
                        p.value >= subharmonicThreshold * 0.3))
                        confirmed++;
                }

                if (confirmed >= minConfirmed)
                    return subPeak.lag; // Знайшли субгармоніку
            }
        }

        return currentPeriod; // Не знайшли, залишаємо поточний
    }

    // ═══════════════════════════════════════════════════════════════
    //  Допоміжні: ACF, профілі, піки
    // ═══════════════════════════════════════════════════════════════

    static double[] InvertProfile(double[] profile)
    {
        double max = 0;
        for (int i = 0; i < profile.Length; i++)
            if (profile[i] > max) max = profile[i];

        double[] result = new double[profile.Length];
        for (int i = 0; i < profile.Length; i++)
            result[i] = max - profile[i];
        return result;
    }

    /// <summary>
    /// Прибирає DC (локальний фон) та нормалізує.
    /// Критично для ACF — без цього плоский фон маски
    /// створює хибну високу кореляцію.
    /// </summary>
    static double[] RemoveDCAndNormalize(double[] signal, int maxWindow)
    {
        int n = signal.Length;
        double[] processed = new double[n];

        int window = Math.Min(n / 4, maxWindow);

        // Локальне віднімання фону + квадратичне підсилення піків
        for (int i = 0; i < n; i++)
        {
            int start = Math.Max(0, i - window);
            int end = Math.Min(n - 1, i + window);
            double localMean = 0;
            for (int j = start; j <= end; j++) localMean += signal[j];
            localMean /= (end - start + 1);

            double val = Math.Max(0, signal[i] - localMean);
            processed[i] = val * val; // Квадратичне підсилення
        }

        // Нормалізація дисперсії
        double variance = 0;
        for (int i = 0; i < n; i++) variance += processed[i] * processed[i];
        variance /= n;
        double std = Math.Sqrt(variance);

        if (std < 1e-9) return processed;

        for (int i = 0; i < n; i++) processed[i] /= std;
        return processed;
    }

    /// <summary>
    /// Обчислює нормалізовану автокореляцію для лагів від 0 до maxLag.
    /// Незміщена оцінка: ділимо на фактичну кількість точок перекриття.
    /// </summary>
    static double[] ComputeAutocorrelation(double[] signal, int maxLag)
    {
        int n = signal.Length;
        double mean = 0;
        for (int i = 0; i < n; i++) mean += signal[i];
        mean /= n;

        double acf0 = 0;
        for (int i = 0; i < n; i++)
            acf0 += (signal[i] - mean) * (signal[i] - mean);

        if (acf0 < 1e-12)
            return new double[maxLag + 1];

        double[] acf = new double[maxLag + 1];
        acf[0] = 1.0;

        double acf0PerN = acf0 / n;

        for (int lag = 1; lag <= maxLag; lag++)
        {
            double sum = 0;
            int count = n - lag;
            for (int i = 0; i < count; i++)
                sum += (signal[i] - mean) * (signal[i + lag] - mean);

            // Незміщена оцінка: ділимо на count, а не на n
            acf[lag] = (sum / count) / acf0PerN;
        }

        return acf;
    }

    /// <summary>
    /// Знаходить всі локальні максимуми ACF в діапазоні [pmin..pmax]
    /// з додатнім значенням.
    /// </summary>
    static List<(int lag, double value)> FindACFPeaks(
        double[] acf, int pmin, int pmax, int radius)
    {
        var peaks = new List<(int lag, double value)>();

        for (int lag = pmin; lag <= pmax && lag < acf.Length; lag++)
        {
            double val = acf[lag];
            if (val <= 0) continue;

            bool isPeak = true;
            int jMin = Math.Max(1, lag - radius);
            int jMax = Math.Min(acf.Length - 1, lag + radius);

            for (int j = jMin; j <= jMax; j++)
            {
                if (j != lag && acf[j] > val)
                {
                    isPeak = false;
                    break;
                }
            }

            if (isPeak) peaks.Add((lag, val));
        }

        return peaks;
    }

    /// <summary>
    /// Уточнює позицію піку ACF параболічною інтерполяцією.
    /// </summary>
    static double RefineACFPeak(double[] acf, int peakIdx)
    {
        if (peakIdx <= 0 || peakIdx >= acf.Length - 1)
            return peakIdx;

        double a = acf[peakIdx - 1];
        double b = acf[peakIdx];
        double c = acf[peakIdx + 1];

        double denom = 2.0 * (2.0 * b - a - c);
        if (Math.Abs(denom) < 1e-12)
            return peakIdx;

        double offset = (a - c) / denom;
        return peakIdx + Math.Max(-0.5, Math.Min(0.5, offset));
    }

    /// <summary>
    /// Перевіряє чи a кратне b (або b кратне a) з допуском.
    /// </summary>
    static bool IsMultipleOf(double a, double b, double tolerance)
    {
        if (a <= 0 || b <= 0) return false;
        double ratio = a > b ? a / b : b / a;
        double nearestInt = Math.Round(ratio);
        if (nearestInt < 1) nearestInt = 1;
        return Math.Abs(ratio - nearestInt) / nearestInt <= tolerance;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Профілі масок (з урахуванням валідної зони)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Горизонтальний профіль: для кожного рядка y — середнє значення
    /// пікселів маски в межах валідної зони.
    /// </summary>
    static double[] ComputeHorizontalProfile(
        Image<Gray, byte> mask, Image<Gray, byte> validArea)
    {
        int h = mask.Height, w = mask.Width;
        double[] profile = new double[h];

        for (int y = 0; y < h; y++)
        {
            double sum = 0;
            int validCount = 0;
            for (int x = 0; x < w; x++)
            {
                if (validArea.Data[y, x, 0] > 0)
                {
                    sum += mask.Data[y, x, 0];
                    validCount++;
                }
            }
            profile[y] = validCount > 0 ? sum / validCount : 255;
        }

        return profile;
    }

    /// <summary>
    /// Вертикальний профіль: для кожного стовпця x — середнє значення
    /// пікселів маски в межах валідної зони.
    /// </summary>
    static double[] ComputeVerticalProfile(
        Image<Gray, byte> mask, Image<Gray, byte> validArea)
    {
        int h = mask.Height, w = mask.Width;
        double[] profile = new double[w];

        for (int x = 0; x < w; x++)
        {
            double sum = 0;
            int validCount = 0;
            for (int y = 0; y < h; y++)
            {
                if (validArea.Data[y, x, 0] > 0)
                {
                    sum += mask.Data[y, x, 0];
                    validCount++;
                }
            }
            profile[x] = validCount > 0 ? sum / validCount : 255;
        }

        return profile;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Визначення зсуву сітки (для оверлею)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Знаходить зсув, при якому гребінчастий фільтр з кроком period
    /// дає максимальну суму (= найкраще вирівнювання з лініями).
    /// </summary>
    static int FindBestShift(double[] profile, int period)
    {
        if (period < 2 || profile.Length < period) return 0;

        // Інвертуємо: лінії (низькі значення маски) стануть піками
        double maxVal = 0;
        for (int i = 0; i < profile.Length; i++)
            if (profile[i] > maxVal) maxVal = profile[i];

        int bestShift = 0;
        double bestSum = double.NegativeInfinity;

        for (int s = 0; s < period; s++)
        {
            double sum = 0;
            for (int i = s; i < profile.Length; i += period)
                sum += maxVal - profile[i]; // Інвертований: лінії = високі значення

            if (sum > bestSum)
            {
                bestSum = sum;
                bestShift = s;
            }
        }

        return bestShift;
    }
}

// ═══════════════════════════════════════════════════════════════
//  Overlay (адаптований під новий DetectionResult)
// ═══════════════════════════════════════════════════════════════

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

        // Вертикальні лінії
        int idx = 0;
        for (int x = res.ShiftX; x < w; x += step, idx++)
        {
            bool big = idx % 10 == 0;
            CvInvoke.Line(img, new Point(x, 0), new Point(x, h - 1),
                big ? c1000 : c100, big ? 2 : 1, LineType.AntiAlias);
        }

        // Горизонтальні лінії
        idx = 0;
        for (int y = res.ShiftY; y < h; y += step, idx++)
        {
            bool big = idx % 10 == 0;
            CvInvoke.Line(img, new Point(0, y), new Point(w - 1, y),
                big ? c1000 : c100, big ? 2 : 1, LineType.AntiAlias);
        }

        // Текстова інформація
        void Text(string s, int y, double sz)
        {
            CvInvoke.PutText(img, s, new Point(10, y), FontFace.HersheySimplex, sz,
                new MCvScalar(20, 20, 20), 3, LineType.AntiAlias);
            CvInvoke.PutText(img, s, new Point(10, y), FontFace.HersheySimplex, sz,
                new MCvScalar(255, 255, 255), 1, LineType.AntiAlias);
        }

        Text($"100m ~ {res.SmallGridStep:F2}px  |  1km ~ {res.LargeGridStep:F1}px", 22, 0.6);
        Text($"Period H={res.DetectedPeriodH}  V={res.DetectedPeriodV}", 45, 0.55);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Plot (спрощений)
// ═══════════════════════════════════════════════════════════════

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
        double[] combined, List<(int lag, double value)> peaks,
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
                    int px = margin + (int)((double)(lag - plotStart) / (plotEnd - plotStart) * width);
                    int py = margin + height - (int)(value / maxVal * height);
                    py = Math.Max(margin, Math.Min(margin + height, py));
                    CvInvoke.Circle(chart, new Point(px, py), 4, colorPeak, -1);
                    CvInvoke.PutText(chart, $"{lag}",
                        new Point(px - 10, py - 8), FontFace.HersheyPlain, 0.7, colorPeak, 1);
                }
            }
        }

        // Лінія результату
        if (smallGridStep > 0)
        {
            int resultLag = (int)Math.Round(smallGridStep);
            if (resultLag >= plotStart && resultLag < plotEnd)
            {
                int px = margin + (int)((double)(resultLag - plotStart) / (plotEnd - plotStart) * width);
                CvInvoke.Line(chart, new Point(px, margin),
                    new Point(px, margin + height), colorResult, 2);
                CvInvoke.PutText(chart, $"Step={smallGridStep:F1}",
                    new Point(px + 5, margin + 15), FontFace.HersheyPlain, 0.9, colorResult, 1);
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
            int px = margin + (int)((double)(i - plotStart) / (plotEnd - plotStart) * width);
            CvInvoke.Line(chart, new Point(px, margin + height),
                new Point(px, margin + height + 5), colorAxis, 1);
            CvInvoke.PutText(chart, $"{i}",
                new Point(px - 10, margin + height + 18),
                FontFace.HersheyPlain, 0.7, colorAxis, 1);
        }

        return chart;
    }
}