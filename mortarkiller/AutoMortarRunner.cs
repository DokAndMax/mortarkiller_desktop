// AutoMortarRunner.cs
using Emgu.CV;
using GridDetector.Core.Helpers;
using mortarkiller.Detection;
using PUBGVisionTest.Core.Detection;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace mortarkiller;

/// <summary>
/// Оркеструє автоматичний цикл: Phase 1 (пошук піна) → Phase 2 (карта) → обчислення рішення.
/// Делегує всю роботу спеціалізованим компонентам.
/// </summary>
public sealed class AutoMortarRunner : IDisposable
{
    private readonly Func<double, int?, (bool hasShort, bool hasOver,
        string bestItemText, string secondItemText, double impactTime)> _computeSolutions;

    private readonly AutoMortarConfig _config;
    private readonly MortarYoloAdapter _yolo;
    private readonly DetectorParams _gridParams;
    private readonly SpeechSynthesizer _tts = new();

    private CancellationTokenSource? _cts;
    private static int s_detectionCounter;

    public AutoMortarRunner(
        AutoMortarConfig config,
        MortarYoloAdapter yoloDetector,
        DetectorParams gridParams,
        Func<double, int?, (bool hasShort, bool hasOver,
            string bestItemText, string secondItemText, double impactTime)> computeSolutions)
    {
        _config = config;
        _yolo = yoloDetector;
        _gridParams = gridParams;
        _computeSolutions = computeSolutions;

        CvInvoke.NumThreads = Math.Max(1, Environment.ProcessorCount - 1);
    }

    // ── Events ──
    public event Action<double>? DistanceReady;
    public event Action<Point, Point>? PairFound;
    public event Action<double>? PxPer100Ready;
    public event Action<string>? Status;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Dispose()
    {
        Stop();
        try { _tts.Dispose(); } catch { }
    }

    public void Stop()
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _cts = null;
    }

    public void Start(string pinClassName, string playerClassName)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        int detNo = Interlocked.Increment(ref s_detectionCounter);
        string sessionSuffix = $"det{detNo:0000}_pin-{pinClassName}_mark-{playerClassName}";

        Task.Run(async () =>
        {
            var totalSw = Stopwatch.StartNew();
            var stepSw = Stopwatch.StartNew();

            // ── Створення компонентів сесії ──
            using var dbg = _config.EnableDebug
                ? new DebugDumper(_config.DebugRoot, enabled: true, sessionSuffix: sessionSuffix)
                : null;

            dbg?.SaveText("session_info",
                $"Started: {DateTime.Now:O}\nDetNo={detNo}\n" +
                $"Pin={pinClassName}\nPlayer={playerClassName}\n");

            using var frameSource = new ProcessFrameSource(_config.ProcessName);
            var metrics = new DetectionMetrics
            {
                DetectionNumber = detNo,
                PinClass = pinClassName,
                PlayerClass = playerClassName
            };

            stepSw.Stop();
            metrics.SetupMs = stepSw.ElapsedMilliseconds;

            try
            {
                // ═══════════════════════════════
                //  PHASE 1: Пошук піна
                // ═══════════════════════════════
                Status?.Invoke($"[AUTO][#{detNo}] Phase 1: searching pin={pinClassName}");

                var pinDetector = new PinDetector(
                    frameSource, _yolo,
                    _config.CropTopPercent, _config.CropSidePercent,
                    _config.IntervalMs, dbg);

                var pinResult = await pinDetector.SearchAsync(
                    pinClassName, metrics.Phase1, token);

                if (pinResult == null || token.IsCancellationRequested)
                    return;

                stepSw.Restart();
                BeepMid();
                Status?.Invoke($"[AUTO][#{detNo}] Phase 1: pin found");

                // ═══════════════════════════════
                //  Відкриваємо карту
                // ═══════════════════════════════
                InputMini.FocusProcess(_config.ProcessName);
                InputMini.PressM_KeybdEvent();
                await Task.Delay(180, token);

                stepSw.Stop();
                metrics.TransitionMs = stepSw.ElapsedMilliseconds;

                // ═══════════════════════════════
                //  PHASE 2: Карта → пін + маркер + масштаб
                // ═══════════════════════════════
                Status?.Invoke($"[AUTO][#{detNo}] Phase 2: searching map objects");

                var mapDetector = new MapDetector(
                    frameSource, _yolo, _gridParams,
                    _config.IntervalMs, _config.UseScaleSmoothing, dbg);

                mapDetector.PxPer100Ready += v => PxPer100Ready?.Invoke(v);

                var mapResult = await mapDetector.SearchAsync(
                    pinClassName, playerClassName, metrics.Phase2, token);

                if (mapResult == null || token.IsCancellationRequested)
                    return;

                stepSw.Restart();
                BeepMid();
                Status?.Invoke($"[AUTO][#{detNo}] Phase 2: pin+marker+scale found");

                // Закриваємо карту
                InputMini.PressM_KeybdEvent();

                // ═══════════════════════════════
                //  Обчислення рішення
                // ═══════════════════════════════
                PairFound?.Invoke(mapResult.Pin, mapResult.Marker);

                var distPx = Math.Sqrt(
                    Math.Pow(mapResult.Pin.X - mapResult.Marker.X, 2) +
                    Math.Pow(mapResult.Pin.Y - mapResult.Marker.Y, 2));
                var distanceMeters = Math.Round(
                    distPx / mapResult.PxPer100 * 100.0, 2);
                DistanceReady?.Invoke(distanceMeters);

                var (hasShort, hasOver, bestAimLabel, secondItem, impactTime) =
                    _computeSolutions(distanceMeters, pinResult.PinScreenY);

                // ── Метрики ──
                stepSw.Stop();
                metrics.PostProcessMs = stepSw.ElapsedMilliseconds;

                totalSw.Stop();
                metrics.TotalMs = totalSw.ElapsedMilliseconds;

                // ── Debug: результат ──
                dbg?.SaveText("result_metrics",
                    $"Pin=({mapResult.Pin.X},{mapResult.Pin.Y})\n" +
                    $"Marker=({mapResult.Marker.X},{mapResult.Marker.Y})\n" +
                    $"PxPer100={mapResult.PxPer100:F3}\n" +
                    $"DistPx={distPx:F2}\n" +
                    $"DistanceMeters={distanceMeters:F2}\n" +
                    $"BestAimLabel={bestAimLabel}\n" +
                    $"ImpactTime={impactTime:F3}\n" +
                    $"Short={hasShort}, Over={hasOver}");

                // ── TTS & Beep ──
                var audioSw = Stopwatch.StartNew();
                await HandleAudioFeedback(bestAimLabel, secondItem);
                audioSw.Stop();
                metrics.AudioFeedbackMs = audioSw.ElapsedMilliseconds;

                dbg?.SaveText("metrics_summary", metrics.FormatSummary());

                Status?.Invoke("[AUTO] Done.");
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Status?.Invoke($"[AUTO] ERROR: {ex.Message}");
                dbg?.SaveText("error", $"{ex}");
            }
        }, token);
    }

    // ==========================================================
    //  Private helpers
    // ==========================================================

    private async Task HandleAudioFeedback(string? bestAimLabel, string? secondItem)
    {
        var aimNumber = ExtractAimNumber(bestAimLabel);
        if (aimNumber.HasValue)
            await SpeakAsync(aimNumber.Value.ToString());

        var bestLower = (bestAimLabel ?? "").ToLowerInvariant();
        bool isBestGreen = !(bestLower.Contains("short") || bestLower.Contains("overshoot"));

        if (!isBestGreen && !string.IsNullOrEmpty(secondItem))
        {
            var secondLower = secondItem.ToLowerInvariant();
            if (secondLower.Contains("short")) BeepLow();
            else if (secondLower.Contains("overshoot")) BeepHigh();
        }
    }

    private Task SpeakAsync(string text)
    {
        var tcs = new TaskCompletionSource<object?>();
        void handler(object? s, SpeakCompletedEventArgs e)
        {
            _tts.SpeakCompleted -= handler;
            tcs.TrySetResult(null);
        }
        _tts.SpeakCompleted += handler;
        _tts.SpeakAsync(text);
        return tcs.Task;
    }

    private static int? ExtractAimNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var matches = Regex.Matches(text, @"\d+");
        if (matches.Count == 0) return null;
        return int.TryParse(matches[^1].Value, out int v) ? v : null;
    }

    private static void BeepHigh() => Task.Run(() => Console.Beep(1200, 120));
    private static void BeepLow() => Task.Run(() => Console.Beep(400, 120));
    private static void BeepMid() => Task.Run(() => Console.Beep(800, 90));
}