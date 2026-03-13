// Detection/PinDetector.cs
using PUBGVisionTest.Core.Capture;
using PUBGVisionTest.Core.Yolo;
using PUBGVisionTest.Core.Visualization;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace mortarkiller.Detection;

/// <summary>
/// Результат Phase 1 — знайдений пін на першому екрані (вид від першої особи).
/// </summary>
public sealed record PinSearchResult(
    Point PinFullCoords,
    int PinScreenY);

/// <summary>
/// Phase 1: шукає пін на екрані гри (центральна смуга).
/// </summary>
public sealed class PinDetector
{
    private readonly IFrameSource _frameSource;
    private readonly MortarYoloAdapter _yolo;
    private readonly double _cropTopPercent;
    private readonly double _cropSidePercent;
    private readonly int _intervalMs;
    private readonly DebugDumper? _dbg;

    private int _failSaved;

    public PinDetector(
        IFrameSource frameSource,
        MortarYoloAdapter yolo,
        double cropTopPercent,
        double cropSidePercent,
        int intervalMs,
        DebugDumper? dbg)
    {
        _frameSource = frameSource;
        _yolo = yolo;
        _cropTopPercent = cropTopPercent;
        _cropSidePercent = cropSidePercent;
        _intervalMs = intervalMs;
        _dbg = dbg;
        _failSaved = 0;
    }

    /// <summary>
    /// Шукає пін у циклі, поки не знайде або не скасують.
    /// </summary>
    public async Task<PinSearchResult?> SearchAsync(
        string pinClassName, PhaseMetrics metrics, CancellationToken ct)
    {
        var phaseSw = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
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
                metrics.Record("Total Phase 1 Loop", swLoop.ElapsedMilliseconds);

                var swDelayEarly = Stopwatch.StartNew();
                await Task.Delay(_intervalMs, ct);
                swDelayEarly.Stop();
                metrics.Record("Idle (Task.Delay)", swDelayEarly.ElapsedMilliseconds);
                continue;
            }

            using var bmp = capture.Frame;

            // ── Crop ──
            var swCrop = Stopwatch.StartNew();
            var cropRect = FramePreprocessor.BuildCentralStrip(
                bmp.Width, bmp.Height, _cropTopPercent, _cropSidePercent);
            var (croppedBmp, mat) = FramePreprocessor.CropAndConvert(bmp, cropRect);
            swCrop.Stop();
            metrics.Record("2. Crop+Convert", swCrop.ElapsedMilliseconds);

            using (croppedBmp)
            using (mat)
            {
                // ── YOLO ──
                var swYolo = Stopwatch.StartNew();
                var predictions = _yolo.Detect(mat);
                swYolo.Stop();
                metrics.Record("3. YOLO", swYolo.ElapsedMilliseconds);

                // ── Filter ──
                var swFilter = Stopwatch.StartNew();
                var pinPred = predictions
                    .Where(p => p.ClassName == pinClassName)
                    .OrderByDescending(p => p.Confidence)
                    .FirstOrDefault();
                swFilter.Stop();
                metrics.Record("4. Filter", swFilter.ElapsedMilliseconds);

                if (pinPred != null)
                {
                    var tipLocal = pinPred.BottomTip;
                    var pinFull = new Point(
                        tipLocal.X + cropRect.X,
                        tipLocal.Y + cropRect.Y);

                    // ── Debug (success) ──
                    SaveSuccessDebug(bmp, croppedBmp, mat, predictions,
                        cropRect, pinFull, pinPred, pinClassName, metrics);

                    swLoop.Stop();
                    metrics.Record("Total Phase 1 Loop", swLoop.ElapsedMilliseconds);

                    phaseSw.Stop();
                    metrics.TotalPhaseMs = phaseSw.ElapsedMilliseconds;

                    return new PinSearchResult(pinFull, pinFull.Y);
                }

                // ── Debug (fail) ──
                SaveFailDebug(bmp, croppedBmp, mat, predictions,
                    pinClassName, metrics);
            }

            swLoop.Stop();
            metrics.Record("Total Phase 1 Loop", swLoop.ElapsedMilliseconds);

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
        Bitmap bmpFull, Bitmap croppedBmp, Emgu.CV.Mat mat,
        System.Collections.Generic.List<YoloPrediction> predictions,
        Rectangle cropRect, Point pinFull, YoloPrediction pinPred,
        string pinClassName, PhaseMetrics metrics)
    {
        var sw = Stopwatch.StartNew();

        _dbg?.SaveBitmap(bmpFull, "phase1_full_original");
        _dbg?.SaveBitmap(croppedBmp, "phase1_cropped_processed");

        if (_dbg != null)
        {
            using var overlayCrop = mat.Clone();
            YoloOverlayRenderer.Draw(overlayCrop, predictions);
            _dbg.SaveMat(overlayCrop, "phase1_overlay_on_crop");
        }

        _dbg?.SaveText("phase1_notes",
            $"DesiredPin={pinClassName}\n" +
            $"CropRect=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height})\n" +
            $"FoundPinAt(fullCoords)=({pinFull.X},{pinFull.Y})\n" +
            $"Confidence={pinPred.Confidence:F3}\n" +
            $"TotalDetections={predictions.Count}");

        sw.Stop();
        metrics.Record("5. Debug IO", sw.ElapsedMilliseconds);
    }

    private void SaveFailDebug(
        Bitmap bmpFull, Bitmap croppedBmp, Emgu.CV.Mat mat,
        System.Collections.Generic.List<YoloPrediction> predictions,
        string pinClassName, PhaseMetrics metrics)
    {
        var sw = Stopwatch.StartNew();

        if (_dbg != null && (_failSaved < 5 || _failSaved % 20 == 0))
        {
            _dbg.SaveBitmap(bmpFull, "phase1_fail_full_original", "fails/p1");
            _dbg.SaveBitmap(croppedBmp, "phase1_fail_cropped", "fails/p1");

            using var overlayFail = mat.Clone();
            YoloOverlayRenderer.Draw(overlayFail, predictions);
            _dbg.SaveMat(overlayFail, "phase1_fail_overlay", "fails/p1");

            _dbg.SaveText("phase1_fail_notes",
                $"No {pinClassName} found. Detections: " +
                $"[{string.Join(", ", predictions.Select(p => $"{p.ClassName}:{p.Confidence:F2}"))}]",
                "fails/p1");
        }
        _failSaved++;

        sw.Stop();
        metrics.Record("5. Debug IO", sw.ElapsedMilliseconds);
    }
}