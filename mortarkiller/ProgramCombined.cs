using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static mortarkiller.GridScaleDetector;
using Mat = Emgu.CV.Mat;

namespace mortarkiller;

public static class ProgramCombined
{

    // Виклик: combined-live <grid_params.json> <pin_best_params.json> <players_params.json> "Process Name" [--interval=500]
    public static async Task<int> MainCombined(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  combined-live <grid_params.json> <pin_best_params.json> <players_params.json> \"Process Name\" [--interval=500]");
            return 1;
        }

        string gridParamsPath = args[0];
        string pinParamsPath = args[1];
        string playersParamsPath = args[2];
        string processName = args[3];

        int intervalMs = 200;
        foreach (var a in args.Skip(4))
        {
            if (a.StartsWith("--interval=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a.AsSpan(11), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                intervalMs = Math.Max(0, v);
            }
        }

        if (!File.Exists(gridParamsPath)) { Console.WriteLine($"Grid params not found: {gridParamsPath}"); return 1; }
        if (!File.Exists(pinParamsPath)) { Console.WriteLine($"Pin params not found: {pinParamsPath}"); return 1; }
        if (!File.Exists(playersParamsPath)) { Console.WriteLine($"Players params not found: {playersParamsPath}"); return 1; }

        // 1) Параметри
        var gridParams = JsonSerializer.Deserialize<DetectorParams>(File.ReadAllText(gridParamsPath), JsonOptions());
        if (gridParams == null) { Console.WriteLine("Не вдалося прочитати grid-параметри."); return 1; }

        var pinParams = ParamsIO.LoadFromBestParamsJson(pinParamsPath);

        var playersParams = JsonSerializer.Deserialize<PlayerParams>(File.ReadAllText(playersParamsPath), JsonOptions());
        if (playersParams == null) { Console.WriteLine("Не вдалося прочитати players-параметри."); return 1; }

        // 2) Детектори
        string dirOfPinParams = Path.GetDirectoryName(Path.GetFullPath(pinParamsPath)) ?? ".";
        string templatesDir = Path.Combine(dirOfPinParams, "best_pin_masks");
        using var pinTemplates = Directory.Exists(templatesDir) ? TemplateLibrary.LoadFromDir(templatesDir) : null;
        var pinDetector = new PinDetector(templates: pinTemplates);

        var playerDetector = new PlayerDetector(playersParams.CalibratedColors);
        var playerLive = new LiveMode(playerDetector);

        CvInvoke.NumThreads = Math.Max(1, Environment.ProcessorCount - 1);

        Console.WriteLine($"[combined-live] process=\"{processName}\", interval={intervalMs} ms");
        Console.WriteLine("Клавіші: ESC/Q – вихід; G/P/M – toggle overlays; S – save snapshot.");

        // 3) Стан UI
        bool showGrid = true, showPins = true, showMarks = true;
        var ewma = new EWMA(alpha: 0.25);
        var fpsSw = Stopwatch.StartNew();
        int frameCounter = 0;

        while (true)
        {
            Bitmap bmp = null;
            Mat mat = null;

            try
            {
                // 4) Захоплення вікна
                bmp = ScreenshotHelper.CaptureWindow(processName);
                if (bmp == null)
                {
                    Console.WriteLine($"[{DateTime.Now:T}] Процес \"{processName}\" не знайдено або вікно процесу мінімізовано. Повтор через {intervalMs} мс ...");
                    await Task.Delay(intervalMs);
                    continue;
                }

                mat = bmp.ToMat();
                EnsureBgr(ref mat);

                // 5) GridScaleDetector
                bool produceDebug = true;
                var gridRes = Detect100m(mat, gridParams, produceDebug, priorPeriodPx: null);

                double? pxPer100 = null;
                if (gridRes.Success && double.IsFinite(gridRes.PxPer100m) && gridRes.PxPer100m > 0)
                    pxPer100 = ewma.Update(gridRes.PxPer100m);

                // 6) PlayerMarkDetector (з Bitmap)
                var markRes = playerLive.Run(bmp, playersParams); // повертає LiveDetections з markers

                // 7) PinDetector (з Mat)
                var pinRes = pinDetector.DetectFromMat(mat, "live-combined", pinParams);

                // 8) Візуалізація
                // - Grid overlay
                if (showGrid) Overlay.DrawGridOverlay(mat, gridRes);

                // - Pins overlay
                if (showPins) Reporter.DrawDetectionsOnImage(mat, pinRes);

                // - Marks overlay
                if (showMarks) DrawMarkers(mat, markRes);

                // Заголовок + статистика
                frameCounter++;
                double fps = frameCounter / Math.Max(1e-6, fpsSw.Elapsed.TotalSeconds);
                string gridInfo = pxPer100.HasValue ? $"{pxPer100.Value:F1} px/100m" :
                                    gridRes.Success ? $"{gridRes.PxPer100m:F1} px/100m" : "FAIL";
                string stats = $"Grid: {gridInfo} | Pins: {pinRes.Predictions.Count} | Marks: {markRes.markers.Count} | FPS~{fps:F1}";
                PutTextWithOutline(mat, stats, new Point(10, 22), 0.6, new MCvScalar(255, 255, 255), 1);

                CvInvoke.Imshow("Combined Live (Grid + Marks + Pins)", mat);
                int key = CvInvoke.WaitKey(1);
                if (key == 27 || key == 'q' || key == 'Q') break;
                if (key == 'g' || key == 'G') showGrid = !showGrid;
                if (key == 'p' || key == 'P') showPins = !showPins;
                if (key == 'm' || key == 'M') showMarks = !showMarks;
                if (key == 's' || key == 'S')
                {
                    string snapDir = Path.Combine(Path.GetTempPath(), "combined_live");
                    Directory.CreateDirectory(snapDir);
                    string snapPath = Path.Combine(snapDir, $"live_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    CvInvoke.Imwrite(snapPath, mat);
                    Console.WriteLine($"Saved snapshot: {snapPath}");
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

    private static void DrawMarkers(Mat img, LiveDetections dets)
    {
        if (dets?.markers == null || dets.markers.Count == 0) return;

        foreach (var m in dets.markers)
        {
            var color = Utils.BgrScalarFromColorName(m.Color);
            var p = new Point(m.X, m.Y);

            // коло мітки
            CvInvoke.Circle(img, p, 6, color, 2);

            // підпис
            string label = $"{m.Type} {m.Color} * {m.Score:0.00}";
            var org = new Point(p.X + 8, Math.Max(15, p.Y - 8));
            PutTextWithOutline(img, label, org, 0.5, color, 1);
        }
    }

    private static void PutTextWithOutline(Mat img, string text, Point org, double scale, MCvScalar color, int thickness)
    {
        CvInvoke.PutText(img, text, org, FontFace.HersheySimplex, scale, new MCvScalar(0, 0, 0), thickness + 2);
        CvInvoke.PutText(img, text, org, FontFace.HersheySimplex, scale, color, thickness);
    }

    private static JsonSerializerOptions JsonOptions(bool indented = false) => new()
    {
        IncludeFields = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true
    };

    public class EWMA(double alpha = 0.25)
    {
        private readonly double alpha = Math.Clamp(alpha, 0.01, 1.0);
        private double? s;

        public double Update(double x)
        {
            if (!s.HasValue || !double.IsFinite(s.Value)) s = x;
            else s = alpha * x + (1 - alpha) * s.Value;
            return s.Value;
        }

        public double Value => s ?? double.NaN;
        public void Reset(double? seed = null) { s = seed; }
    }
}