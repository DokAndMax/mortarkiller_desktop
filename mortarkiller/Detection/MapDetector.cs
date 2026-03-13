// Detection/MapDetector.cs
using Emgu.CV;
using GridDetector.Core.Helpers;
using PUBGVisionTest.Core.Capture;
using PUBGVisionTest.Core.Detection;
using PUBGVisionTest.Core.Visualization;
using PUBGVisionTest.Core.Yolo;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace mortarkiller.Detection;

/// <summary>
/// Результат Phase 2 — пін, маркер гравця та масштаб на карті.
/// </summary>
public sealed record MapDetectionResult(
    Point Pin,
    Point Marker,
    double PxPer100);

/// <summary>
/// Phase 2: знаходить пін + маркер + масштаб на відкритій карті.
/// </summary>
public sealed class MapDetector
{
    private readonly IFrameSource _frameSource;
    private readonly MortarYoloAdapter _yolo;
    private readonly DetectorParams _gridParams;
    private readonly int _intervalMs;
    private readonly bool _useScaleSmoothing;
    private readonly DebugDumper? _dbg;

    private EWMA? _ewma;
    private int _failSaved;

    public event Action<double>? PxPer100Ready;

    public MapDetector(
        IFrameSource frameSource,
        MortarYoloAdapter yolo,
        DetectorParams gridParams,
        int intervalMs,
        bool useScaleSmoothing,
        DebugDumper? dbg)
    {
        _frameSource = frameSource;
        _yolo = yolo;
        _gridParams = gridParams;
        _intervalMs = intervalMs;
        _useScaleSmoothing = useScaleSmoothing;
        _dbg = dbg;
        _ewma = useScaleSmoothing ? new EWMA(alpha: 0.25) : null;
        _failSaved = 0;
    }

    /// <summary>
    /// Шукає пін + маркер + масштаб у циклі.
    /// </summary>
    public async Task<MapDetectionResult?> SearchAsync(
        string pinClassName, string playerClassName,
        PhaseMetrics metrics, CancellationToken ct)
    {
        var phaseSw = Stopwatch.StartNew();

        Point? pin = null;
        Point? marker = null;
        double? pxPer100 = null;

        while (!ct.IsCancellationRequested
               && (!pin.HasValue || !marker.HasValue || !pxPer100.HasValue))
        {
            metrics.IncrementIterations();
            var swLoop = Stopwatch.StartNew();

            // ── Screenshot ──
            var swShot = Stopwatch.StartNew();
            var capture = _frameSource.Capture();
            swShot.Stop();
            metrics.Record("1. Screenshot", swShot.ElapsedMilliseconds);

            if (capture.Mode == WindowMode.FullScreenMinimized || capture.Frame == null)
            {
                capture.Frame?.Dispose();
                swLoop.Stop();
                metrics.Record("Total Phase 2 Loop", swLoop.ElapsedMilliseconds);

                var swDelayEarly = Stopwatch.StartNew();
                await Task.Delay(_intervalMs, ct);
                swDelayEarly.Stop();
                metrics.Record("Idle (Task.Delay)", swDelayEarly.ElapsedMilliseconds);
                continue;
            }

            using var bmpFull = capture.Frame;

            // ── ToMat ──
            var swToMat = Stopwatch.StartNew();
            using var matFull = FramePreprocessor.ToBgrMat(bmpFull);
            swToMat.Stop();
            metrics.Record("2. ToMat", swToMat.ElapsedMilliseconds);

            // ── Left panel cut ──
            var swLeftCut = Stopwatch.StartNew();
            int leftCut = FramePreprocessor.DetectLeftPanelCut(matFull, _dbg);
            swLeftCut.Stop();
            metrics.Record("3. LeftPanelCut", swLeftCut.ElapsedMilliseconds);

            // ── Work area ──
            var swWork = Stopwatch.StartNew();
            var (workRect, matWork, bmpWork) =
                FramePreprocessor.ExtractWorkArea(bmpFull, matFull, leftCut);
            swWork.Stop();
            metrics.Record("4. WorkCrop", swWork.ElapsedMilliseconds);

            using (matWork)
            using (bmpWork)
            {
                // ── Parallel YOLO + Grid ──
                var swParallel = Stopwatch.StartNew();

                DetectionResult? gridRes = null;
                List<YoloPrediction>? predictions = null;
                long gridMs = 0, yoloMs = 0;

                var gridTask = Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    gridRes = GridScaleDetector.Detect100m(
                        matWork, _gridParams, debug: false, priorPx: null);
                    sw.Stop();
                    gridMs = sw.ElapsedMilliseconds;
                });

                var yoloTask = Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    predictions = _yolo.Detect(matWork);
                    sw.Stop();
                    yoloMs = sw.ElapsedMilliseconds;
                });

                await Task.WhenAll(gridTask, yoloTask);

                swParallel.Stop();
                metrics.Record("5. Parallel (Grid+YOLO)", swParallel.ElapsedMilliseconds);
                metrics.Record("   ↳ GridDetect", gridMs);
                metrics.Record("   ↳ YOLO", yoloMs);

                // ── Grid result ──
                if (gridRes is { Success: true }
                    && double.IsFinite(gridRes.PxPer100m)
                    && gridRes.PxPer100m > 0)
                {
                    double raw = gridRes.PxPer100m;
                    double val = _useScaleSmoothing ? _ewma!.Update(raw) : raw;
                    pxPer100 = val;
                    PxPer100Ready?.Invoke(pxPer100.Value);
                }

                // ── Filter predictions ──
                var swFilter = Stopwatch.StartNew();
                var pinPred = predictions?
                    .Where(p => p.ClassName == pinClassName)
                    .OrderByDescending(p => p.Confidence)
                    .FirstOrDefault();

                if (pinPred != null)
                    pin = new Point(pinPred.BottomTip.X + leftCut, pinPred.BottomTip.Y);

                var playerPred = predictions?
                    .Where(p => p.ClassName == playerClassName)
                    .OrderByDescending(p => p.Confidence)
                    .FirstOrDefault();

                if (playerPred != null)
                    marker = new Point(playerPred.Center.X + leftCut, playerPred.Center.Y);

                swFilter.Stop();
                metrics.Record("6. Filter", swFilter.ElapsedMilliseconds);

                // ── Debug ──
                if (pin.HasValue && marker.HasValue && pxPer100.HasValue)
                {
                    SaveSuccessDebug(bmpFull, bmpWork, matFull, matWork,
                        workRect, gridRes, predictions, metrics);

                    swLoop.Stop();
                    metrics.Record("Total Phase 2 Loop", swLoop.ElapsedMilliseconds);

                    phaseSw.Stop();
                    metrics.TotalPhaseMs = phaseSw.ElapsedMilliseconds;

                    return new MapDetectionResult(pin.Value, marker.Value, pxPer100.Value);
                }

                SaveFailDebug(bmpFull, bmpWork, matFull, workRect,
                    gridRes, predictions, pin, marker, pxPer100,
                    leftCut, metrics);
            }

            swLoop.Stop();
            metrics.Record("Total Phase 2 Loop", swLoop.ElapsedMilliseconds);

            var swDelay = Stopwatch.StartNew();
            await Task.Delay(_intervalMs, ct);
            swDelay.Stop();
            metrics.Record("Idle (Task.Delay)", swDelay.ElapsedMilliseconds);
        }

        phaseSw.Stop();
        metrics.TotalPhaseMs = phaseSw.ElapsedMilliseconds;
        return null;
    }

    private void SaveSuccessDebug(
        Bitmap bmpFull, Bitmap bmpWork, Mat matFull, Mat matWork,
        Rectangle workRect, DetectionResult? gridRes,
        List<YoloPrediction>? predictions, PhaseMetrics metrics)
    {
        var sw = Stopwatch.StartNew();

        _dbg?.SaveBitmap(bmpFull, "phase2_full_original");
        _dbg?.SaveBitmap(bmpWork, "phase2_workarea_processed");

        if (_dbg != null)
        {
            var swOvl = Stopwatch.StartNew();
            using var overlay = matFull.Clone();
            using var overlayROI = new Mat(overlay, workRect);
            Overlay.DrawGridOverlay(overlayROI, gridRes);
            YoloOverlayRenderer.Draw(overlayROI, predictions);
            _dbg.SaveMat(overlay, "phase2_overlay");

            swOvl.Stop();
            metrics.Record("   ↳ Overlay", swOvl.ElapsedMilliseconds);
        }

        if (predictions != null)
        {
            _dbg?.SaveText("phase2_detections",
                string.Join("\n", predictions.Select(p =>
                    $"{p.ClassName}: conf={p.Confidence:F3} " +
                    $"box=({p.BoundingBox.X},{p.BoundingBox.Y}," +
                    $"{p.BoundingBox.Width},{p.BoundingBox.Height})")));
        }

        sw.Stop();
        metrics.Record("7. Debug IO", sw.ElapsedMilliseconds);
    }

    private void SaveFailDebug(
        Bitmap bmpFull, Bitmap bmpWork, Mat matFull,
        Rectangle workRect, DetectionResult? gridRes,
        List<YoloPrediction>? predictions,
        Point? pin, Point? marker, double? pxPer100,
        int leftCut, PhaseMetrics metrics)
    {
        var sw = Stopwatch.StartNew();

        if (_dbg != null && (_failSaved < 5 || _failSaved % 20 == 0))
        {
            _dbg.SaveBitmap(bmpFull, "phase2_fail_full_original", "fails/p2");
            _dbg.SaveBitmap(bmpWork, "phase2_fail_workarea", "fails/p2");

            using var overlay = matFull.Clone();
            using var overlayROI = new Mat(overlay, workRect);
            Overlay.DrawGridOverlay(overlayROI, gridRes);
            YoloOverlayRenderer.Draw(overlayROI, predictions);
            _dbg.SaveMat(overlay, "phase2_fail_overlay", "fails/p2");

            _dbg.SaveText("phase2_fail_notes",
                $"pin={pin.HasValue} marker={marker.HasValue} " +
                $"scale={pxPer100.HasValue} leftCut={leftCut}\n" +
                $"Detections: [{string.Join(", ", predictions?.Select(p => $"{p.ClassName}:{p.Confidence:F2}") ?? Array.Empty<string>())}]",
                "fails/p2");
        }
        _failSaved++;

        sw.Stop();
        metrics.Record("7. Debug IO", sw.ElapsedMilliseconds);
    }
}