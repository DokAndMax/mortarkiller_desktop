using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using mortarkiller;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Mat = Emgu.CV.Mat;

namespace WinFormsApp1;

public static class ProgramCombined
{
    // Виклик: combined-live <best.onnx> "Process Name" [--interval=500]
    public static async Task<int> MainCombined(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  combined-live <best.onnx> \"Process Name\" [--interval=500]");
            return 1;
        }

        string yoloModelPath = args[0];
        string processName = args[1];

        int intervalMs = 200;
        foreach (var a in args.Skip(2))
        {
            if (a.StartsWith("--interval=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a.AsSpan(11), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                intervalMs = Math.Max(0, v);
            }
        }

        if (!File.Exists(yoloModelPath))
        {
            Console.WriteLine($"YOLO model not found: {yoloModelPath}");
            return 1;
        }

        // 1) Параметри Grid — дефолтні, тренування не потрібне
        var gridParams = new DetectorParams();

        // 2) Ініціалізація YOLO
        string[] yoloLabels = {
            "Pin_Blue", "Pin_Green", "Pin_Orange", "Pin_Yellow",
            "Player_Blue", "Player_Green", "Player_Orange", "Player_Yellow"
        };

        using var yoloDetector = new YoloDetector(yoloModelPath, yoloLabels, imgsz: 1920, confThreshold: 0.2f);

        CvInvoke.NumThreads = Math.Max(1, Environment.ProcessorCount - 1);

        Console.WriteLine($"[combined-live] process=\"{processName}\", interval={intervalMs} ms");
        Console.WriteLine("Клавіші: ESC/Q – вихід; G/Y – toggle overlays; S – save snapshot.");

        // СТВОРЮЄМО ВІКНО З МОЖЛИВІСТЮ МАСШТАБУВАННЯ
        string windowName = "Combined Live (Grid + YOLO)";
        CvInvoke.NamedWindow(windowName, WindowFlags.Normal);

        // 3) Стан UI
        bool showGrid = true, showYolo = true;
        var ewma = new EWMA(alpha: 0.25);
        var fpsSw = Stopwatch.StartNew();
        int frameCounter = 0;

        var perfSw = new Stopwatch();

        while (true)
        {
            Bitmap? bmp = null;
            Mat? mat = null;

            try
            {
                perfSw.Restart();
                // 4) Захоплення вікна
                bmp = ScreenshotHelper.CaptureWindow(processName);
                if (bmp == null)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:T}] Процес \"{processName}\" не знайдено " +
                        $"або вікно процесу мінімізовано. Повтор через {intervalMs} мс ...");
                    await Task.Delay(intervalMs);
                    continue;
                }

                mat = bmp.ToMat();
                EnsureBgr(ref mat);
                long captureTime = perfSw.ElapsedMilliseconds;

                // 5) GridScaleDetector
                perfSw.Restart();
                var gridRes = GridScaleDetector.Detect100m(mat, gridParams, debug: true);

                double? pxPer100 = null;
                if (gridRes.Success && double.IsFinite(gridRes.PxPer100m) && gridRes.PxPer100m > 0)
                    pxPer100 = ewma.Update(gridRes.PxPer100m);
                long gridTime = perfSw.ElapsedMilliseconds;

                // 6) YOLO Detection
                perfSw.Restart();
                var yoloResults = yoloDetector.Detect(mat);
                long yoloTime = perfSw.ElapsedMilliseconds;

                // 8) Візуалізація
                if (showGrid) Overlay.DrawGridOverlay(mat, gridRes);
                if (showYolo) DrawYoloResults(mat, yoloResults);

                // Заголовок + статистика
                frameCounter++;
                double fps = frameCounter / Math.Max(1e-6, fpsSw.Elapsed.TotalSeconds);
                string gridInfo = pxPer100.HasValue
                    ? $"{pxPer100.Value:F1} px/100m"
                    : (gridRes.Success ? $"{gridRes.PxPer100m:F1} px/100m" : "FAIL");

                string stats1 = $"Grid: {gridInfo} | YOLO Objs: {yoloResults.Count} | FPS~{fps:F1}";
                string stats2 = $"Timing: Capture {captureTime}ms | Grid {gridTime}ms | YOLO {yoloTime}ms";

                int textY1 = 80;
                int textY2 = 105;

                PutTextWithOutline(mat, stats1, new Point(10, textY1), 0.6,
                    new MCvScalar(255, 255, 255), 1);
                PutTextWithOutline(mat, stats2, new Point(10, textY2), 0.5,
                    new MCvScalar(0, 255, 255), 1);

                CvInvoke.Imshow(windowName, mat);

                int key = CvInvoke.WaitKey(1);
                if (key == 27 || key == 'q' || key == 'Q') break;
                if (key == 'g' || key == 'G') showGrid = !showGrid;
                if (key == 'y' || key == 'Y') showYolo = !showYolo;
                if (key == 's' || key == 'S')
                {
                    string snapDir = Path.Combine(Path.GetTempPath(), "combined_live");
                    Directory.CreateDirectory(snapDir);
                    string snapPath = Path.Combine(snapDir,
                        $"live_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    CvInvoke.Imwrite(snapPath, mat);
                    Console.WriteLine($"Saved snapshot: {snapPath}");
                }

                if (frameCounter % 10 == 0)
                {
                    Console.WriteLine(
                        $"[Frame {frameCounter}] Capture: {captureTime}ms | " +
                        $"Grid: {gridTime}ms | YOLO: {yoloTime}ms");
                }

                CvInvoke.WaitKey(intervalMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[combined-live] Exception: {ex.Message}");
                CvInvoke.WaitKey(intervalMs);
            }
            finally
            {
                mat?.Dispose();
                bmp?.Dispose();
            }
        }

        CvInvoke.DestroyAllWindows();
        return 0;
    }

    private static void DrawYoloResults(Mat img, List<YoloPrediction> results)
    {
        foreach (var res in results)
        {
            MCvScalar color = res.ClassName.Contains("Blue") ? new MCvScalar(255, 0, 0) :
                              res.ClassName.Contains("Green") ? new MCvScalar(0, 255, 0) :
                              res.ClassName.Contains("Yellow") ? new MCvScalar(0, 255, 255) :
                              res.ClassName.Contains("Orange") ? new MCvScalar(0, 165, 255) :
                              new MCvScalar(255, 0, 255);

            CvInvoke.Rectangle(img, res.BoundingBox, color, 2);

            Point targetPoint = res.ClassName.StartsWith("Pin")
                ? res.BottomTip : res.Center;
            CvInvoke.Circle(img, targetPoint, 4, new MCvScalar(0, 0, 255), -1);

            string label = $"{res.ClassName} {res.Confidence:P0}";
            var org = new Point(res.BoundingBox.X,
                Math.Max(15, res.BoundingBox.Y - 5));
            PutTextWithOutline(img, label, org, 0.5, color, 1);
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

    private static void PutTextWithOutline(Mat img, string text, Point org,
        double scale, MCvScalar color, int thickness)
    {
        CvInvoke.PutText(img, text, org, FontFace.HersheySimplex, scale,
            new MCvScalar(0, 0, 0), thickness + 2);
        CvInvoke.PutText(img, text, org, FontFace.HersheySimplex, scale,
            color, thickness);
    }

    public class EWMA(double alpha = 0.25)
    {
        private readonly double alpha = Math.Clamp(alpha, 0.01, 1.0);
        private double? s;

        public double Update(double x)
        {
            if (!s.HasValue || !double.IsFinite(s.Value)) s = x;
            else s = this.alpha * x + (1 - this.alpha) * s.Value;
            return s.Value;
        }

        public double Value => s ?? double.NaN;
    }
}