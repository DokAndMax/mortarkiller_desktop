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
    // ==========================================================
    //  Залежності
    // ==========================================================

    private readonly Func<double, int?, (bool hasShort, bool hasOver,
        string bestItemText, string secondItemText, double impactTime)> _computeSolutions;

    private readonly double _cropSidePercent;
    private readonly double _cropTopPercent;
    private readonly string _debugRoot;
    private readonly bool _enableDebug;
    private readonly DetectorParams _gridParams;
    private readonly int _intervalMs;
    private readonly string _processName;
    private readonly bool _useScaleSmoothing;

    // -- НОВЕ: один YOLO детектор замість PinDetector + LiveMode --
    private readonly YoloDetector _yolo;

    private readonly SpeechSynthesizer _tts = new();

    // -- Управління життєвим циклом --
    private CancellationTokenSource _cts;
    private DebugDumper? _dbg;
    private ProgramCombined.EWMA? _ewma;
    private int _p1FailSaved, _p2FailSaved;
    private static int s_detectionCounter = 0;

    // ==========================================================
    //  Конструктор — спрощений
    // ==========================================================

    public AutoMortarRunner(
        string processName,
        YoloDetector yoloDetector,          // <- ЗАМІСТЬ pinDetector, pinParams, playerLive, playersParams
        DetectorParams gridParams,
        Func<double, int?, (bool hasShort, bool hasOver,
            string bestItemText, string secondItemText, double impactTime)> computeSolutions,
        int intervalMs = 200,
        double cropTopPercent = 0.08,
        double cropSidePercent = 0.47,
        bool enableDebug = true,
        string debugRoot = null,
        bool useScaleSmoothing = false)
    {
        _processName = processName;
        _yolo = yoloDetector;
        _gridParams = gridParams;
        _computeSolutions = computeSolutions;

        _intervalMs = Math.Max(0, intervalMs);
        _cropTopPercent = Math.Clamp(cropTopPercent, 0, 0.9);
        _cropSidePercent = Math.Clamp(cropSidePercent, 0, 0.49);

        CvInvoke.NumThreads = Math.Max(1, Environment.ProcessorCount - 1);

        _enableDebug = enableDebug;
        _debugRoot = debugRoot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug");
        _useScaleSmoothing = useScaleSmoothing;
    }

    // ==========================================================
    //  Події
    // ==========================================================

    public event Action<double> DistanceReady;
    public event Action<Point, Point> PairFound;
    public event Action<double> PxPer100Ready;
    public event Action<string> Status;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    // ==========================================================
    //  Dispose / Stop
    // ==========================================================

    public void Dispose()
    {
        Stop();
        try { _tts?.Dispose(); } catch { }
        try { _dbg?.Dispose(); } catch { }
        // НЕ dispose _yolo — він належить Form1
    }

    public void Stop()
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _cts = null;
    }

    // ==========================================================
    //  Start — НОВА СИГНАТУРА: string className замість enum
    // ==========================================================

    /// <summary>
    /// Запускає авто-режим.
    /// pinClassName:    YOLO class name піна, наприклад "pin_yellow"
    /// playerClassName: YOLO class name маркера гравця, наприклад "player_yellow"
    /// </summary>
    public void Start(string pinClassName, string playerClassName)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        int detNo = Interlocked.Increment(ref s_detectionCounter);
        string sessionSuffix = $"det{detNo:0000}_pin-{pinClassName}_mark-{playerClassName}";

        _dbg?.Dispose();
        _dbg = _enableDebug ? new DebugDumper(_debugRoot, enabled: true, sessionSuffix: sessionSuffix) : null;
        _dbg?.SaveText("session_info",
            $"Started: {DateTime.Now:O}\nDetNo={detNo}\nPin={pinClassName}\nPlayer={playerClassName}\n");

        _p1FailSaved = _p2FailSaved = 0;
        _ewma = _useScaleSmoothing ? new ProgramCombined.EWMA(alpha: 0.25) : null;

        int? pinScreenY = null;

        Task.Run(async () =>
        {
            var totalSw = Stopwatch.StartNew();
            long setupMs = 0;

            // -- Метрики фази 1 --
            var p1ScrMs = new List<long>();
            var p1YoloMs = new List<long>();
            var p1LoopMs = new List<long>();
            int p1Iters = 0;

            // -- Метрики фази 2 --
            var p2ScrMs = new List<long>();
            var p2GridMs = new List<long>();
            var p2YoloMs = new List<long>();
            var p2OverlayMs = new List<long>();
            var p2LoopMs = new List<long>();
            int p2Iters = 0;

            string Stat(string name, List<long> v)
            {
                if (v == null || v.Count == 0) return $"{name}: n=0";
                return $"{name}: n={v.Count}, avg={v.Average():F1}ms, min={v.Min()}ms, max={v.Max()}ms";
            }

            try
            {
                Status?.Invoke($"[AUTO][#{detNo}] Phase 1: searching pin={pinClassName}");

                // ===================================================
                //  ФАЗА 1: Кроп центральної смуги, шукаємо PIN
                // ===================================================
                var swSetup = Stopwatch.StartNew();
                Point? pin1 = null;
                bool firstLoopP1 = true;

                while (!token.IsCancellationRequested && pin1 == null)
                {
                    p1Iters++;
                    var swLoop = Stopwatch.StartNew();

                    if (firstLoopP1)
                    {
                        setupMs = swSetup.ElapsedMilliseconds;
                        firstLoopP1 = false;
                    }

                    // Скріншот
                    var swShot = Stopwatch.StartNew();
                    var (frame1, mode1) = ScreenshotHelper.CaptureSmart(_processName);
                    swShot.Stop();
                    p1ScrMs.Add(swShot.ElapsedMilliseconds);

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

                    // -- YOLO детекція замість PinDetector --
                    var swYolo = Stopwatch.StartNew();
                    var predictions = _yolo.Detect(mat);
                    swYolo.Stop();
                    p1YoloMs.Add(swYolo.ElapsedMilliseconds);

                    // Шукаємо пін потрібного класу
                    var pinPred = predictions
                        .Where(p => p.ClassName == pinClassName)
                        .OrderByDescending(p => p.Confidence)
                        .FirstOrDefault();

                    if (pinPred != null)
                    {
                        // BottomTip = гостряк піна -> переводимо в координати повного кадру
                        var tipLocal = pinPred.BottomTip;
                        var foundFull = new Point(tipLocal.X + cropRect.X, tipLocal.Y + cropRect.Y);

                        pin1 = foundFull;
                        pinScreenY = pin1.Value.Y;

                        // -- DEBUG --
                        _dbg?.SaveBitmap(bmp, "phase1_full_original");
                        _dbg?.SaveBitmap(croppedBmp, "phase1_cropped_processed");

                        using var overlayCrop = mat.Clone();
                        DrawYoloPredictions(overlayCrop, predictions);
                        _dbg?.SaveMat(overlayCrop, "phase1_overlay_on_crop");

                        _dbg?.SaveText("phase1_notes",
                            $"DesiredPin={pinClassName}\n" +
                            $"CropRect=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height})\n" +
                            $"FoundPinAt(fullCoords)=({pin1.Value.X},{pin1.Value.Y})\n" +
                            $"Confidence={pinPred.Confidence:F3}\n" +
                            $"TotalDetections={predictions.Count}");
                    }
                    else
                    {
                        // Зберігаємо фейли (перші 5 + кожний 20-й)
                        if (_dbg != null && (_p1FailSaved < 5 || _p1FailSaved % 20 == 0))
                        {
                            _dbg.SaveBitmap(bmp, "phase1_fail_full_original", "fails/p1");
                            _dbg.SaveBitmap(croppedBmp, "phase1_fail_cropped", "fails/p1");

                            using var overlayFail = mat.Clone();
                            DrawYoloPredictions(overlayFail, predictions);
                            _dbg.SaveMat(overlayFail, "phase1_fail_overlay", "fails/p1");

                            _dbg.SaveText("phase1_fail_notes",
                                $"No {pinClassName} found. " +
                                $"Detections: [{string.Join(", ", predictions.Select(p => $"{p.ClassName}:{p.Confidence:F2}"))}]",
                                "fails/p1");
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

                // ===================================================
                //  ФАЗА 2: Відкриваємо карту, детектуємо пін+маркер+масштаб
                // ===================================================
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

                    // Скріншот
                    var swShot = Stopwatch.StartNew();
                    var (frame2, mode2) = ScreenshotHelper.CaptureSmart(_processName);
                    swShot.Stop();
                    p2ScrMs.Add(swShot.ElapsedMilliseconds);

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

                    // Обрізаємо чорну панель зліва (якщо є)
                    int leftCut = DetectLeftPanelCutByDilatedBlack(matFull);
                    var workRect = new Rectangle(leftCut, 0, matFull.Width - leftCut, matFull.Height);
                    using var matWork = new Mat(matFull, workRect);
                    using var bmpWork = bmpFull.Clone(workRect, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

                    // -- Масштаб сітки (GridScaleDetector — без змін) --
                    var swGrid = Stopwatch.StartNew();
                    var gridRes = GridScaleDetector.Detect100m(matWork, _gridParams, debug: false, priorPx: null);
                    swGrid.Stop();
                    p2GridMs.Add(swGrid.ElapsedMilliseconds);

                    if (gridRes.Success && double.IsFinite(gridRes.PxPer100m) && gridRes.PxPer100m > 0)
                    {
                        double raw = gridRes.PxPer100m;
                        double val = _useScaleSmoothing ? _ewma!.Update(raw) : raw;
                        pxPer100 = val;
                        PxPer100Ready?.Invoke(pxPer100.Value);
                    }

                    // -- YOLO: один виклик знаходить і пін, і маркер --
                    var swYolo = Stopwatch.StartNew();
                    var predictions = _yolo.Detect(matWork);
                    swYolo.Stop();
                    p2YoloMs.Add(swYolo.ElapsedMilliseconds);

                    // Пін (потрібного кольору) -> BottomTip + корекція leftCut
                    var pinPred = predictions
                        .Where(p => p.ClassName == pinClassName)
                        .OrderByDescending(p => p.Confidence)
                        .FirstOrDefault();

                    if (pinPred != null)
                        pin2 = new Point(pinPred.BottomTip.X + leftCut, pinPred.BottomTip.Y);

                    // Маркер гравця (потрібного кольору) -> Center + корекція leftCut
                    var playerPred = predictions
                        .Where(p => p.ClassName == playerClassName)
                        .OrderByDescending(p => p.Confidence)
                        .FirstOrDefault();

                    if (playerPred != null)
                        marker2 = new Point(playerPred.Center.X + leftCut, playerPred.Center.Y);

                    // -- DEBUG --
                    if (pin2.HasValue && marker2.HasValue && pxPer100.HasValue)
                    {
                        _dbg?.SaveBitmap(bmpFull, "phase2_full_original");
                        _dbg?.SaveBitmap(bmpWork, "phase2_workarea_processed");

                        var swOverlay = Stopwatch.StartNew();
                        using var overlay = matFull.Clone();
                        using var overlayROI = new Mat(overlay, workRect);
                        Overlay.DrawGridOverlay(overlayROI, gridRes);
                        DrawYoloPredictions(overlayROI, predictions);
                        _dbg?.SaveMat(overlay, "phase2_overlay");
                        swOverlay.Stop();
                        p2OverlayMs.Add(swOverlay.ElapsedMilliseconds);

                        _dbg?.SaveText("phase2_detections",
                            string.Join("\n", predictions.Select(p =>
                                $"{p.ClassName}: conf={p.Confidence:F3} box=({p.BoundingBox.X},{p.BoundingBox.Y},{p.BoundingBox.Width},{p.BoundingBox.Height})")));
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
                            DrawYoloPredictions(overlayROI, predictions);
                            _dbg.SaveMat(overlay, "phase2_fail_overlay", "fails/p2");
                            swOverlay.Stop();
                            p2OverlayMs.Add(swOverlay.ElapsedMilliseconds);

                            _dbg.SaveText("phase2_fail_notes",
                                $"pin={pin2.HasValue} marker={marker2.HasValue} scale={pxPer100.HasValue} leftCut={leftCut}\n" +
                                $"Detections: [{string.Join(", ", predictions.Select(p => $"{p.ClassName}:{p.Confidence:F2}"))}]",
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

                // ===================================================
                //  Обчислення рішення
                // ===================================================
                if (pin2.HasValue && marker2.HasValue && pxPer100.HasValue)
                {
                    PairFound?.Invoke(pin2.Value, marker2.Value);

                    var distPx = Math.Sqrt(
                        Math.Pow(pin2.Value.X - marker2.Value.X, 2) +
                        Math.Pow(pin2.Value.Y - marker2.Value.Y, 2));
                    var distanceMeters = Math.Round(distPx / pxPer100.Value * 100.0, 2);
                    DistanceReady?.Invoke(distanceMeters);

                    var (hasShort, hasOver, bestAimLabel, secondItem, impactTime) =
                        _computeSolutions(distanceMeters, pinScreenY);

                    _dbg?.SaveText("phase2_metrics",
                        $"Pin=({pin2.Value.X},{pin2.Value.Y})\n" +
                        $"Marker=({marker2.Value.X},{marker2.Value.Y})\n" +
                        $"PxPer100={pxPer100:F3}\n" +
                        $"DistPx={distPx:F2}\n" +
                        $"DistanceMeters={distanceMeters:F2}\n" +
                        $"BestAimLabel={bestAimLabel}\n" +
                        $"ImpactTime={impactTime:F3}\n" +
                        $"Short={hasShort}, Over={hasOver}");

                    var aimNumber = ExtractAimNumber(bestAimLabel);
                    if (aimNumber.HasValue)
                        await SpeakAsync(aimNumber.Value.ToString());

                    var bestLower = (bestAimLabel ?? "").ToLowerInvariant();
                    var isBestGreen = !(bestLower.Contains("short") || bestLower.Contains("overshoot"));

                    if (!isBestGreen && !string.IsNullOrEmpty(secondItem))
                    {
                        var secondLower = secondItem.ToLowerInvariant();
                        if (secondLower.Contains("short")) BeepLow();
                        else if (secondLower.Contains("overshoot")) BeepHigh();
                    }
                }

                // ===================================================
                //  Метрики сесії
                // ===================================================
                totalSw.Stop();
                _dbg?.SaveText("metrics_summary",
                    $"DetNo={detNo}\n" +
                    $"Pin={pinClassName}\nPlayer={playerClassName}\n" +
                    $"Setup={setupMs}ms\n" +
                    $"Phase1: iters={p1Iters}\n" +
                    $"  {Stat("P1 Screenshot", p1ScrMs)}\n" +
                    $"  {Stat("P1 YOLO", p1YoloMs)}\n" +
                    $"  {Stat("P1 Loop", p1LoopMs)}\n" +
                    $"Phase2: iters={p2Iters}\n" +
                    $"  {Stat("P2 Screenshot", p2ScrMs)}\n" +
                    $"  {Stat("P2 GridDetect", p2GridMs)}\n" +
                    $"  {Stat("P2 YOLO", p2YoloMs)}\n" +
                    $"  {Stat("P2 Overlay", p2OverlayMs)}\n" +
                    $"  {Stat("P2 Loop", p2LoopMs)}\n" +
                    $"TotalDetectionTime={totalSw.ElapsedMilliseconds}ms\n");

                Status?.Invoke("[AUTO] Done.");
            }
            catch (TaskCanceledException) { /* ignore */ }
            catch (Exception ex)
            {
                Status?.Invoke($"[AUTO] ERROR: {ex.Message}");
                _dbg?.SaveText("error", $"{ex}");
            }
        }, token);
    }

    // ==========================================================
    //  Helpers
    // ==========================================================

    private static void BeepHigh() => Task.Run(() => Console.Beep(1200, 120));
    private static void BeepLow() => Task.Run(() => Console.Beep(400, 120));
    private static void BeepMid() => Task.Run(() => Console.Beep(800, 90));

    private static Rectangle BuildCentralStrip(int w, int h, double topCut, double sideCut)
    {
        int x = (int)Math.Round(w * sideCut);
        int y = (int)Math.Round(h * topCut);
        int ww = w - 2 * x;
        int hh = h - y;
        return new Rectangle(x, y, Math.Max(1, ww), Math.Max(1, hh));
    }

    /// <summary>
    /// Малює всі YOLO-предікції на зображенні для дебагу.
    /// Замінює Reporter.DrawDetectionsOnImage + DrawMarkersForDebug.
    /// </summary>
    private static void DrawYoloPredictions(Mat img, List<YoloPrediction> predictions)
    {
        if (predictions == null || predictions.Count == 0) return;

        foreach (var pred in predictions)
        {
            // Колір рамки залежить від типу
            MCvScalar color;
            if (pred.ClassName.StartsWith("pin_"))
                color = new MCvScalar(0, 0, 255);       // червоний для пінів
            else if (pred.ClassName.StartsWith("player_"))
                color = new MCvScalar(255, 200, 0);     // блакитний для гравців
            else
                color = new MCvScalar(200, 200, 200);   // сірий для невідомого

            // Рамка
            CvInvoke.Rectangle(img, pred.BoundingBox, color, 2);

            // Точка прив'язки: BottomTip для пінів, Center для гравців
            Point anchor = pred.ClassName.StartsWith("pin_")
                ? pred.BottomTip
                : pred.Center;
            CvInvoke.Circle(img, anchor, 4, color, -1);

            // Підпис
            string label = $"{pred.ClassName} {pred.Confidence:F2}";
            var textOrg = new Point(pred.BoundingBox.X, Math.Max(15, pred.BoundingBox.Y - 5));
            CvInvoke.PutText(img, label, textOrg, FontFace.HersheySimplex, 0.45,
                new MCvScalar(0, 0, 0), 2, LineType.AntiAlias);
            CvInvoke.PutText(img, label, textOrg, FontFace.HersheySimplex, 0.45,
                color, 1, LineType.AntiAlias);
        }
    }

    private int DetectLeftPanelCutByDilatedBlack(Mat matFull)
    {
        // -- Без змін — ця логіка не залежить від детектора --
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

        using var maskImg = maskClean.ToImage<Gray, byte>();
        int rw = maskImg.Width;
        int rh = maskImg.Height;
        byte[,,] data = maskImg.Data;
        var colCnt = new int[rw];

        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                if (data[y, x, 0] != 0) colCnt[x]++;

        double hiThr = rh * 0.94;
        double lowThr = rh * 0.20;

        int lastHi = -1;
        for (int x = 0; x < rw; x++)
            if (colCnt[x] >= hiThr) lastHi = x;

        if (lastHi < 0)
        {
            _dbg?.SaveMat(mask, "leftpanel_mask_initial_nohit");
            return 0;
        }

        int win = Math.Max(4, w / 900);
        int candidate = lastHi;

        for (int x = lastHi; x <= rw - win - 1; x++)
        {
            int below = 0;
            for (int j = 0; j < win; j++)
                if (colCnt[x + j] <= lowThr) below++;
            if (below == win) { candidate = x; break; }
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
            return 0;
        }

        int leftCut = roiRect.X + candidate;
        leftCut = Math.Clamp(leftCut, 0, (int)(w * 0.47));

        if (_dbg != null)
        {
            _dbg.SaveMat(mask, "leftpanel_mask_initial");
            _dbg.SaveMat(maskClean, "leftpanel_mask_clean");
            using var overlay = matFull.Clone();
            CvInvoke.Line(overlay, new Point(leftCut, 0), new Point(leftCut, h - 1),
                new MCvScalar(0, 255, 255), 2);
            _dbg.SaveMat(overlay, "leftpanel_cut_overlay");
            _dbg.SaveText("leftpanel_notes",
                $"roi=({roiRect.X},{roiRect.Y},{roiRect.Width},{roiRect.Height}), leftCut={leftCut}");
        }

        return leftCut;
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

    private static int? ExtractAimNumber(string bestAimLabel)
    {
        if (string.IsNullOrWhiteSpace(bestAimLabel)) return null;
        var matches = Regex.Matches(bestAimLabel, @"\d+");
        if (matches.Count == 0) return null;
        return int.TryParse(matches[^1].Value, out int v) ? v : null;
    }

    private Task SpeakAsync(string text)
    {
        var tcs = new TaskCompletionSource<object>();
        void handler(object s, SpeakCompletedEventArgs e)
        {
            _tts.SpeakCompleted -= handler;
            tcs.TrySetResult(null);
        }
        _tts.SpeakCompleted += handler;
        _tts.SpeakAsync(text);
        return tcs.Task;
    }
}