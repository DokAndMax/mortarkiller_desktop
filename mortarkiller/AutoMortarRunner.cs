using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mat = Emgu.CV.Mat;

namespace mortarkiller;

public sealed class AutoMortarRunner : IDisposable
{
    // Делегат, який рахує рішення стрільби (ваш фізичний блок) і оновлює UI
    private readonly Func<double, int?, (bool hasShort, bool hasOver, string bestItemText, string secondItemText, double impactTime)> _computeSolutions;

    private readonly double _cropSidePercent;
    private readonly double _cropTopPercent;
    private readonly string _debugRoot;
    private readonly bool _enableDebug;
    private readonly DetectorParams _gridParams;

    // Налаштування циклу
    private readonly int _intervalMs;

    private readonly PinDetector _pinDetector;
    private readonly ParameterSet _pinParams;

    private readonly LiveMode _playerLive;
    private readonly PlayerParams _playersParams;

    // Зовнішні залежності (детектори та параметри)
    private readonly string _processName;

    private readonly SpeechSynthesizer _tts = new();

    // Чи згладжувати масштаб (за замовчуванням — ні)
    private readonly bool _useScaleSmoothing;

    // Управління життєвим циклом
    private CancellationTokenSource _cts;

    // Debug сесія на кожен Start()
    private DebugDumper? _dbg;

    // Обчислення масштабу
    private ProgramCombined.EWMA? _ewma;            // створюється на Start()

    // Контроль збереження фейлів (щоб не засипати диск)
    private int _p1FailSaved, _p2FailSaved;

    // Глобальний лічильник детекцій від старту програми
    private static int s_detectionCounter = 0;

    public AutoMortarRunner(
           string processName,
           PinDetector pinDetector,
           ParameterSet pinParams,
           DetectorParams gridParams,
           LiveMode playerLive,
           PlayerParams playersParams,
           Func<double, int?, (bool hasShort, bool hasOver, string bestItemText, string secondItemText, double impactTime)> computeSolutions,
           int intervalMs = 200,
           double cropTopPercent = 0.08,
           double cropSidePercent = 0.47,
           bool enableDebug = true,
           string debugRoot = null,
           bool useScaleSmoothing = false) // <-- новий параметр
    {
        _processName = processName;
        _pinDetector = pinDetector;
        _pinParams = pinParams;
        _gridParams = gridParams;
        _playerLive = playerLive;
        _playersParams = playersParams;
        _computeSolutions = computeSolutions;

        _intervalMs = Math.Max(0, intervalMs);
        _cropTopPercent = Math.Clamp(cropTopPercent, 0, 0.9);
        _cropSidePercent = Math.Clamp(cropSidePercent, 0, 0.49);

        CvInvoke.NumThreads = Math.Max(1, Environment.ProcessorCount - 1);

        // Debug і згладжування
        _enableDebug = enableDebug;
        _debugRoot = debugRoot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug");
        _useScaleSmoothing = useScaleSmoothing;
    }

    public event Action<double> DistanceReady;
    public event Action<Point, Point> PairFound;
    // Готова дистанція, м
    public event Action<double> PxPer100Ready;
    // Події/зворотні виклики
    public event Action<string> Status;          // Текстовий статус

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Dispose()
    {
        Stop();
        try { _tts?.Dispose(); } catch { }
        try { _dbg?.Dispose(); } catch { }
    }

    public void Start(PinColor desiredPinColor, ColorName desiredMarkerColor)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Лічильник спроб (детекцій) від старту програми
        int detNo = Interlocked.Increment(ref s_detectionCounter);
        string sessionSuffix = $"det{detNo:0000}_pin-{desiredPinColor}_mark-{desiredMarkerColor}";

        // Нова debug-папка на кожен запуск ALT+1..4
        _dbg?.Dispose();
        _dbg = _enableDebug ? new DebugDumper(_debugRoot, enabled: true, sessionSuffix: sessionSuffix) : null;
        _dbg?.SaveText("session_info",
            $"Started: {DateTime.Now:O}\nDetNo={detNo}\nPin={desiredPinColor}\nMarker={desiredMarkerColor}\n");

        // Скидаємо лічильники фейлів
        _p1FailSaved = _p2FailSaved = 0;

        // Чисте EWMA на цю сесію (або вимкнено)
        _ewma = _useScaleSmoothing ? new ProgramCombined.EWMA(alpha: 0.25) : null;

        int? pinScreenY = null; // Y піна з фази 1 у координатах всього вікна

        Task.Run(async () =>
        {
            // Локальні метрики
            var totalSw = Stopwatch.StartNew();
            long setupMs = 0;

            // Фаза 1
            var p1ScrMs = new List<long>();
            var p1PinMs = new List<long>();
            var p1LoopMs = new List<long>();
            int p1Iters = 0;

            // Фаза 2
            var p2ScrMs = new List<long>();
            var p2GridMs = new List<long>();
            var p2PinMs = new List<long>();
            var p2MarkerMs = new List<long>();
            var p2OverlayMs = new List<long>();
            var p2LoopMs = new List<long>();
            int p2Iters = 0;

            // Хелпер форматування статистики
            string Stat(string name, List<long> v)
            {
                if (v == null || v.Count == 0) return $"{name}: n=0";
                double avg = v.Average();
                long min = v.Min();
                long max = v.Max();
                return $"{name}: n={v.Count}, avg={avg:F1}ms, min={min}ms, max={max}ms";
            }

            try
            {
                Status?.Invoke($"[AUTO][#{detNo}] Phase 1: searching pin={desiredPinColor}");

                // ===== ЦИКЛ 1: кроп центральної смуги, шукаємо PIN потрібного кольору
                var swSetup = Stopwatch.StartNew();
                Point? pin1 = null;
                bool firstLoopP1 = true;

                while (!token.IsCancellationRequested && pin1 == null)
                {
                    p1Iters++;
                    var swLoop = Stopwatch.StartNew();

                    // Фіксуємо час підготовки до першого скріншота (setup)
                    if (firstLoopP1)
                    {
                        setupMs = swSetup.ElapsedMilliseconds;
                        firstLoopP1 = false;
                    }

                    var swShot = Stopwatch.StartNew();
                    var (frame1, mode1) = ScreenshotHelper.CaptureSmart(_processName);
                    swShot.Stop();
                    p1ScrMs.Add(swShot.ElapsedMilliseconds);
                    Debug.WriteLine($"[AutoMortarRunner] Phase1 Screenshot time: {swShot.ElapsedMilliseconds} ms");

                    if (mode1 == WindowMode.FullScreenMinimized || frame1 == null)
                    {
                        swLoop.Stop();
                        p1LoopMs.Add(swLoop.ElapsedMilliseconds);
                        await Task.Delay(_intervalMs, token);
                        continue;
                    }
                    using var bmp = frame1;

                    var cropRect = BuildCentralStrip(bmp.Width, bmp.Height, _cropTopPercent, _cropSidePercent);
                    using var croppedBmp = bmp.Clone(cropRect, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using var mat = croppedBmp.ToMat();
                    EnsureBgr(ref Unsafe.AsRef(in mat));

                    var swPin = Stopwatch.StartNew();
                    var pinRes = _pinDetector.DetectFromMat(mat, "auto-phase1", _pinParams, desiredPinColor);
                    swPin.Stop();
                    p1PinMs.Add(swPin.ElapsedMilliseconds);

                    if (pinRes.Predictions.Count > 0)
                    {
                        var pr = pinRes.Predictions[0];
                        var foundLocal = new Point(pr.X + cropRect.X, pr.Y + cropRect.Y);

                        pin1 = foundLocal;
                        pinScreenY = pin1.Value.Y;

                        // DEBUG: оригінал + оброблений + оверлей
                        _dbg?.SaveBitmap(bmp, "phase1_full_original");
                        _dbg?.SaveBitmap(croppedBmp, "phase1_cropped_processed");

                        using var overlayCrop = mat.Clone();
                        Reporter.DrawDetectionsOnImage(overlayCrop, pinRes);
                        _dbg?.SaveMat(overlayCrop, "phase1_overlay_on_crop");

                        _dbg?.SaveText("phase1_notes",
                            $"DesiredPinColor={desiredPinColor}\n" +
                            $"CropRect=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height})\n" +
                            $"FoundPinAt(fullCoords)=({pin1.Value.X},{pin1.Value.Y})");
                    }
                    else
                    {
                        // Зберігаємо невдалі кадри: перші 5 і кожний 20-й далі — в підпапку fails/p1
                        if (_dbg != null && (_p1FailSaved < 5 || _p1FailSaved % 20 == 0))
                        {
                            _dbg.SaveBitmap(bmp, "phase1_fail_full_original", "fails/p1");
                            _dbg.SaveBitmap(croppedBmp, "phase1_fail_cropped", "fails/p1");
                            _dbg.SaveText("phase1_fail_notes", $"No pin yet. Crop=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height})", "fails/p1");
                        }
                        _p1FailSaved++;

                        swLoop.Stop();
                        p1LoopMs.Add(swLoop.ElapsedMilliseconds);
                        await Task.Delay(_intervalMs, token);
                        continue;
                    }

                    swLoop.Stop();
                    p1LoopMs.Add(swLoop.ElapsedMilliseconds);
                }

                BeepMid();
                Status?.Invoke($"[AUTO][#{detNo}] Phase 1: pin found");

                // ===== ЦИКЛ 2: натискаємо "M", повний скріншот, детекція маркера+піна+масштабу
                InputMini.FocusProcess(_processName);
                InputMini.PressM_KeybdEvent();
                await Task.Delay(180, token);

                Point? pin2 = null;
                Point? marker2 = null;
                double? pxPer100 = null;

                while (!token.IsCancellationRequested && (!pin2.HasValue || !marker2.HasValue || !pxPer100.HasValue))
                {
                    p2Iters++;
                    var swLoop = Stopwatch.StartNew();

                    var swShot = Stopwatch.StartNew();
                    var (frame2, mode2) = ScreenshotHelper.CaptureSmart(_processName);
                    swShot.Stop();
                    p2ScrMs.Add(swShot.ElapsedMilliseconds);
                    Debug.WriteLine($"[AutoMortarRunner] Phase2 Screenshot time: {swShot.ElapsedMilliseconds} ms");

                    if (mode2 == WindowMode.FullScreenMinimized || frame2 == null)
                    {
                        swLoop.Stop();
                        p2LoopMs.Add(swLoop.ElapsedMilliseconds);
                        await Task.Delay(_intervalMs, token);
                        continue;
                    }
                    using var bmpFull = frame2;

                    using var matFull = bmpFull.ToMat();
                    EnsureBgr(ref Unsafe.AsRef(in matFull));

                    // Перевіряємо, чи є чорна панель зліва, і якщо так — обрізаємо
                    int leftCut = DetectLeftPanelCutByDilatedBlack(matFull);
                    var workRect = new Rectangle(leftCut, 0, matFull.Width - leftCut, matFull.Height);
                    using var matWork = new Mat(matFull, workRect);
                    using var bmpWork = bmpFull.Clone(workRect, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

                    // Масштаб
                    var swGrid = Stopwatch.StartNew();
                    var gridRes = GridScaleDetector.Detect100m(matWork, _gridParams, produceDebug: false, priorPeriodPx: null);
                    swGrid.Stop();
                    p2GridMs.Add(swGrid.ElapsedMilliseconds);

                    if (gridRes.Success && double.IsFinite(gridRes.PxPer100m) && gridRes.PxPer100m > 0)
                    {
                        double raw = gridRes.PxPer100m;                    // значення від детектора (правильне)
                        double val = _useScaleSmoothing ? _ewma!.Update(raw) : raw; // згладження опційне
                        pxPer100 = val;
                        PxPer100Ready?.Invoke(pxPer100.Value);
                    }

                    // Пін (по обрізаному кадру)
                    var swPin = Stopwatch.StartNew();
                    var pinRes2 = _pinDetector.DetectFromMat(matWork, "auto-phase2", _pinParams, desiredPinColor);
                    swPin.Stop();
                    p2PinMs.Add(swPin.ElapsedMilliseconds);

                    if (pinRes2.Predictions.Count > 0)
                    {
                        var pr = pinRes2.Predictions[0];
                        pin2 = new Point(pr.X + leftCut, pr.Y);
                    }

                    // Маркер гравця (по обрізаному кадру) + корекція координат
                    var swMarker = Stopwatch.StartNew();
                    var markRes = _playerLive.Run(bmpWork, _playersParams, desiredMarkerColor);
                    swMarker.Stop();
                    p2MarkerMs.Add(swMarker.ElapsedMilliseconds);

                    var markerFound = markRes.markers
                        .OrderByDescending(m => m.Score)
                        .Select(m => new Point(m.X + leftCut, m.Y))
                        .FirstOrDefault();
                    if (markerFound != default) marker2 = markerFound;

                    // DEBUG
                    if (pin2.HasValue && marker2.HasValue && pxPer100.HasValue)
                    {
                        _dbg?.SaveBitmap(bmpFull, "phase2_full_original");
                        _dbg?.SaveBitmap(bmpWork, "phase2_workarea_processed");

                        var swOverlay = Stopwatch.StartNew();
                        using var overlay = matFull.Clone();
                        using var overlayROI = new Mat(overlay, workRect);
                        Overlay.DrawGridOverlay(overlayROI, gridRes);
                        Reporter.DrawDetectionsOnImage(overlayROI, pinRes2);
                        DrawMarkersForDebug(overlayROI, markRes);
                        _dbg?.SaveMat(overlay, "phase2_overlay");
                        swOverlay.Stop();
                        p2OverlayMs.Add(swOverlay.ElapsedMilliseconds);
                    }
                    else
                    {
                        if (_dbg != null && (_p2FailSaved < 5 || _p2FailSaved % 20 == 0))
                        {
                            _dbg.SaveBitmap(bmpFull, "phase2_fail_full_original", "fails/p2");
                            _dbg.SaveBitmap(bmpWork, "phase2_fail_workarea", "fails/p2");

                            var swOverlay = Stopwatch.StartNew();
                            using var overlay = matFull.Clone();
                            using var overlayROI = new Mat(overlay, workRect);
                            Overlay.DrawGridOverlay(overlayROI, gridRes);
                            Reporter.DrawDetectionsOnImage(overlayROI, pinRes2);
                            DrawMarkersForDebug(overlayROI, markRes);
                            _dbg.SaveMat(overlay, "phase2_fail_overlay", "fails/p2");
                            swOverlay.Stop();
                            p2OverlayMs.Add(swOverlay.ElapsedMilliseconds);

                            _dbg.SaveText("phase2_fail_notes",
                                $"pin={(pin2.HasValue)} marker={(marker2.HasValue)} scale={(pxPer100.HasValue)} leftCut={workRect.Left}",
                                "fails/p2");
                        }
                        _p2FailSaved++;

                        swLoop.Stop();
                        p2LoopMs.Add(swLoop.ElapsedMilliseconds);
                        await Task.Delay(_intervalMs, token);
                        continue;
                    }

                    swLoop.Stop();
                    p2LoopMs.Add(swLoop.ElapsedMilliseconds);
                }

                BeepMid();
                Status?.Invoke($"[AUTO][#{detNo}] Phase 2: pin+marker+scale found");

                InputMini.PressM_KeybdEvent(); // закрити карту

                if (pin2.HasValue && marker2.HasValue && pxPer100.HasValue)
                {
                    PairFound?.Invoke(pin2.Value, marker2.Value);

                    var distPx = Math.Sqrt(Math.Pow(pin2.Value.X - marker2.Value.X, 2) + Math.Pow(pin2.Value.Y - marker2.Value.Y, 2));
                    var distanceMeters = Math.Round(distPx / pxPer100.Value * 100.0, 2);
                    DistanceReady?.Invoke(distanceMeters);

                    var (hasShort, hasOver, bestAimLabel, secondItem, impactTime) = _computeSolutions(distanceMeters, pinScreenY);

                    // DEBUG: метрики фази 2 — детальні
                    _dbg?.SaveText("phase2_metrics",
                        $"Pin=({pin2.Value.X},{pin2.Value.Y})\n" +
                        $"Marker=({marker2.Value.X},{marker2.Value.Y})\n" +
                        $"PxPer100={pxPer100:F3}\n" +
                        $"DistPx={distPx:F2}\n" +
                        $"DistanceMeters={distanceMeters:F2}\n" +
                        $"BestAimLabel={bestAimLabel}\n" +
                        $"ImpactTime={impactTime:F3}\n" +
                        $"Short={hasShort}, Over={hasOver}");

                    // Озвучка тільки числа прицілу
                    var aimNumber = ExtractAimNumber(bestAimLabel);
                    if (aimNumber.HasValue)
                        await SpeakAsync(aimNumber.Value.ToString());

                    var bestLower = (bestAimLabel ?? "").ToLowerInvariant();
                    var isBestGreen = !(bestLower.Contains("short") || bestLower.Contains("overshoot"));

                    if (!isBestGreen && !string.IsNullOrEmpty(secondItem))
                    {
                        var secondLower = secondItem.ToLowerInvariant();
                        if (secondLower.Contains("short"))
                            BeepLow();
                        else if (secondLower.Contains("overshoot"))
                            BeepHigh();
                    }
                }

                // ===== Сумарні метрики по сесії
                totalSw.Stop();
                _dbg?.SaveText("metrics_summary",
                    $"DetNo={detNo}\n" +
                    $"Pin={desiredPinColor}\nMarker={desiredMarkerColor}\n" +
                    $"Setup={setupMs}ms\n" +
                    $"Phase1: iters={p1Iters}\n" +
                    $"  {Stat("P1 Screenshot", p1ScrMs)}\n" +
                    $"  {Stat("P1 PinDetect", p1PinMs)}\n" +
                    $"  {Stat("P1 LoopWork", p1LoopMs)}\n" +
                    $"Phase2: iters={p2Iters}\n" +
                    $"  {Stat("P2 Screenshot", p2ScrMs)}\n" +
                    $"  {Stat("P2 GridDetect", p2GridMs)}\n" +
                    $"  {Stat("P2 PinDetect", p2PinMs)}\n" +
                    $"  {Stat("P2 MarkerDetect", p2MarkerMs)}\n" +
                    $"  {Stat("P2 OverlayBuild", p2OverlayMs)}\n" +
                    $"  {Stat("P2 LoopWork", p2LoopMs)}\n" +
                    $"TotalDetectionTime={totalSw.ElapsedMilliseconds}ms\n");

                Status?.Invoke("[AUTO] Done.");
            }
            catch (TaskCanceledException) { /* ignore */ }
            catch (Exception ex)
            {
                Status?.Invoke($"[AUTO] ERROR: {ex.Message}");
            }
        }, token);
    }

    public void Stop()
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _cts = null;
    }

    // ===== Helpers =====

    private static void BeepHigh()
    { _ = Task.Run(() => Console.Beep(1200, 120)); }

    // C) Звуки та TTS
    private static void BeepLow()
    { _ = Task.Run(() => Console.Beep(400, 120)); }

    private static void BeepMid()
    { _ = Task.Run(() => Console.Beep(800, 90)); }

    private static Rectangle BuildCentralStrip(int w, int h, double topCut, double sideCut)
    {
        int x = (int)Math.Round(w * sideCut);
        int y = (int)Math.Round(h * topCut);
        int ww = w - 2 * x;
        int hh = h - y;
        return new Rectangle(x, y, Math.Max(1, ww), Math.Max(1, hh));
    }

    private int DetectLeftPanelCutByDilatedBlack(Mat matFull)
    {
        // 1) ROI: ліва частина кадру, без верх/низ “шапки”
        int w = matFull.Width;
        int h = matFull.Height;

        int xMax = Math.Max(1, (int)Math.Round(w * 0.48));      // шукати панель тільки в лівій половині
        int yPad = Math.Max(2, (int)Math.Round(h * 0.02));      // відрізати 2% зверху/знизу
        var roiRect = new Rectangle(0, yPad, xMax, Math.Max(1, h - 2 * yPad));
        if (roiRect.Width <= 0 || roiRect.Height <= 0) return 0;

        using var roi = new Mat(matFull, roiRect);

        // 2) Маска "майже чорних" (0..18) у BGR
        using var mask = new Mat();
        CvInvoke.InRange(
            roi,
            new ScalarArray(new MCvScalar(0, 0, 0)),
            new ScalarArray(new MCvScalar(18, 18, 18)),
            mask);

        // 3) Морфологія
        int kVert = Math.Max(9, h / 40);
        if ((kVert & 1) == 0) kVert++; // зробимо непарним
        using var kernelClose = CvInvoke.GetStructuringElement(MorphShapes.Rectangle, new Size(3, kVert), new Point(-1, -1));
        using var maskClosed = new Mat();
        CvInvoke.MorphologyEx(mask, maskClosed, MorphOp.Close, kernelClose, new Point(-1, -1), 1, BorderType.Reflect, default);

        using var kernelOpen = CvInvoke.GetStructuringElement(MorphShapes.Rectangle, new Size(3, 3), new Point(-1, -1));
        using var maskClean = new Mat();
        CvInvoke.MorphologyEx(maskClosed, maskClean, MorphOp.Open, kernelOpen, new Point(-1, -1), 1, BorderType.Reflect, default);

        // 4) Для кожної колонки рахуємо кількість чорних пікселів
        using var maskImg = maskClean.ToImage<Gray, byte>();
        int rw = maskImg.Width;
        int rh = maskImg.Height;
        byte[,,] data = maskImg.Data;
        var colCnt = new int[rw];

        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                if (data[y, x, 0] != 0) colCnt[x]++;

        // 5) Пошук правої межі чорної “стіни”
        double hiThr = rh * 0.94; // "майже повністю чорна" колонка
        double lowThr = rh * 0.20;

        // Знайдемо крайню праву "дуже чорну" колонку
        int lastHi = -1;
        for (int x = 0; x < rw; x++)
            if (colCnt[x] >= hiThr) lastHi = x;

        if (lastHi < 0)
        {
            _dbg?.SaveMat(mask, "leftpanel_mask_initial_nohit");
            return 0;
        }

        int win = Math.Max(4, w / 900); // невеличке вікно перевірки
        int candidate = lastHi;

        for (int x = lastHi; x <= rw - win - 1; x++)
        {
            int below = 0;
            for (int j = 0; j < win; j++)
                if (colCnt[x + j] <= lowThr) below++;

            if (below == win)
            {
                candidate = x;
                break;
            }
        }

        int leftBand = 0;
        for (int i = 0; i < 8 && candidate - i >= 0; i++)
            if (colCnt[candidate - i] >= hiThr) leftBand++;

        int rightBand = 0;
        for (int i = 1; i <= Math.Min(32, rw - 1 - candidate); i++)
            if (colCnt[candidate + i] <= lowThr) rightBand++;

        bool looksLikePanel = leftBand >= 3 && rightBand >= Math.Min(32, rw - 1 - candidate) * 0.7;
        if (!looksLikePanel)
        {
            _dbg?.SaveMat(maskClean, "leftpanel_mask_clean_lowconf");
            return 0; // низька впевненість — не ріжемо
        }

        int leftCut = roiRect.X + candidate;
        leftCut = Math.Clamp(leftCut, 0, (int)(w * 0.47));

        // Debug
        if (_dbg != null)
        {
            _dbg.SaveMat(mask, "leftpanel_mask_initial");
            _dbg.SaveMat(maskClean, "leftpanel_mask_clean");
            using var overlay = matFull.Clone();
            CvInvoke.Line(overlay, new Point(leftCut, 0), new Point(leftCut, h - 1), new MCvScalar(0, 255, 255), 2);
            _dbg.SaveMat(overlay, "leftpanel_cut_overlay");
            _dbg.SaveText("leftpanel_notes", $"roi=({roiRect.X},{roiRect.Y},{roiRect.Width},{roiRect.Height}), leftCut={leftCut}");
        }

        return leftCut;
    }

    private static void DrawMarkersForDebug(Mat img, LiveDetections dets)
    {
        if (dets?.markers == null || dets.markers.Count == 0) return;

        foreach (var m in dets.markers)
        {
            var color = Utils.BgrScalarFromColorName(m.Color);
            var p = new Point(m.X, m.Y);

            CvInvoke.Circle(img, p, 6, color, 2);
            string label = $"{m.Type} {m.Color} * {m.Score:0.00}";
            var org = new Point(p.X + 8, Math.Max(15, p.Y - 8));
            CvInvoke.PutText(img, label, org, FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 0), 2);
            CvInvoke.PutText(img, label, org, FontFace.HersheySimplex, 0.5, color, 1);
        }
    }

    private static void EnsureBgr(ref Mat mat)
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

    // Парсимо лише числове значення прицілу для TTS
    private static int? ExtractAimNumber(string bestAimLabel)
    {
        if (string.IsNullOrWhiteSpace(bestAimLabel)) return null;
        var matches = Regex.Matches(bestAimLabel, @"\d+");
        if (matches.Count == 0) return null;
        if (int.TryParse(matches[^1].Value, out int v))
            return v;
        return null;
    }

    private Task SpeakAsync(string text)
    {
        var tcs = new TaskCompletionSource<object>();
        void handler(object s, SpeakCompletedEventArgs e) { _tts.SpeakCompleted -= handler; tcs.TrySetResult(null); }
        _tts.SpeakCompleted += handler;
        _tts.SpeakAsync(text);
        return tcs.Task;
    }
}