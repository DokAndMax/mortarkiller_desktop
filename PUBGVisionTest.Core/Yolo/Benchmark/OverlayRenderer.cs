// Core/Yolo/Benchmark/OverlayRenderer.cs
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System.Drawing;

namespace PUBGVisionTest.Core.Yolo.Benchmark;

public static class OverlayRenderer
{
    private static readonly MCvScalar ColorTP = new(0, 255, 0);
    private static readonly MCvScalar ColorFP = new(0, 0, 255);
    private static readonly MCvScalar ColorFN = new(0, 165, 255);
    private static readonly MCvScalar ColorExpected = new(255, 255, 0);
    private static readonly MCvScalar ColorWhite = new(255, 255, 255);
    private static readonly MCvScalar ColorBlack = new(0, 0, 0);

    public static void SaveAllOverlays(
        List<BenchmarkResult> results,
        Dictionary<string, byte[]> preloadedImages,
        List<TestCase> testCases,
        string outputDir)
    {
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        Console.WriteLine($"\nSaving overlay images to '{outputDir}'...");

        int saved = 0;
        foreach (var result in results.Where(r => r.Error == null))
        {
            foreach (var tcResult in result.TestCaseResults)
            {
                var testCase = testCases.FirstOrDefault(t => t.ImageName == tcResult.ImageName);
                if (testCase == null) continue;

                var imageData = preloadedImages[testCase.ImagePath];
                string safeName = SanitizeFileName(result.Config.Name);
                string outputPath = Path.Combine(outputDir,
                    $"{safeName}_{tcResult.ImageName}.png");

                try
                {
                    SaveOverlay(imageData, tcResult, testCase, result.Config, outputPath);
                    saved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Failed to save {outputPath}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"Saved {saved} overlay images.");
    }

    private static void SaveOverlay(
        byte[] imageData,
        TestCaseResult tcResult,
        TestCase testCase,
        BenchmarkRunConfig config,
        string outputPath)
    {
        using var original = new Mat();
        CvInvoke.Imdecode(imageData, ImreadModes.ColorBgr, original);
        if (original.IsEmpty) return;

        var matchedDetections = new HashSet<YoloPrediction>(
            tcResult.Matches
                .Where(m => m.IsCorrect && m.Matched != null)
                .Select(m => m.Matched!));

        foreach (var det in tcResult.Detections)
        {
            bool isTP = matchedDetections.Contains(det);
            var color = isTP ? ColorTP : ColorFP;
            int thickness = isTP ? 2 : 1;

            if (det.BoundingBox.Width > 0 && det.BoundingBox.Height > 0)
                CvInvoke.Rectangle(original, det.BoundingBox, color, thickness);

            DrawCross(original, det.Center, color, 8, 2);

            string label = $"{det.ClassName} {det.Confidence:F2}";
            var labelPos = new Point(
                Math.Max(0, det.BoundingBox.X),
                Math.Max(15, det.BoundingBox.Y - 5));

            int baseline = 0;
            var textSize = CvInvoke.GetTextSize(label, FontFace.HersheySimplex, 0.5, 1, ref baseline);
            var bgRect = new Rectangle(labelPos.X, labelPos.Y - textSize.Height - 2,
                textSize.Width + 4, textSize.Height + 6);
            CvInvoke.Rectangle(original, bgRect, color, -1);
            CvInvoke.PutText(original, label, new Point(labelPos.X + 2, labelPos.Y),
                FontFace.HersheySimplex, 0.5, ColorBlack, 1);
        }

        foreach (var exp in testCase.ExpectedObjects)
        {
            var match = tcResult.Matches.FirstOrDefault(m => m.Expected == exp);
            bool found = match?.IsCorrect ?? false;
            var expColor = found ? ColorTP : ColorFN;

            CvInvoke.Circle(original, exp.ExpectedPoint, exp.ToleranceRadius,
                expColor, 1, Emgu.CV.CvEnum.LineType.AntiAlias);

            DrawCross(original, exp.ExpectedPoint, ColorExpected, 12, 2);

            string expLabel = found
                ? $"OK: {exp.Label} (d={match!.DistancePx:F1}px)"
                : $"MISS: {exp.Label}";

            var textPos = new Point(
                Math.Max(0, exp.CenterX - 40),
                Math.Max(15, exp.CenterY - exp.ToleranceRadius - 8));

            DrawTextWithBg(original, expLabel, textPos, expColor);

            if (match?.Matched != null && match.IsCorrect)
            {
                Point detPoint = exp.UseBottomTip ? match.Matched.BottomTip : match.Matched.Center;
                CvInvoke.Line(original, exp.ExpectedPoint, detPoint,
                    ColorExpected, 1, Emgu.CV.CvEnum.LineType.AntiAlias);
            }
        }

        DrawInfoPanel(original, config, tcResult);
        CvInvoke.Imwrite(outputPath, original);
    }

    private static void DrawInfoPanel(Mat image, BenchmarkRunConfig config, TestCaseResult tcResult)
    {
        int panelHeight = 60;
        var panelRect = new Rectangle(0, 0, image.Width, panelHeight);
        using var overlay = image.Clone();
        CvInvoke.Rectangle(overlay, panelRect, new MCvScalar(0, 0, 0), -1);
        CvInvoke.AddWeighted(overlay, 0.6, image, 0.4, 0, image);

        CvInvoke.PutText(image, config.ToString(), new Point(10, 20),
            FontFace.HersheySimplex, 0.5, ColorWhite, 1);

        int tp = tcResult.Matches.Count(m => m.IsCorrect);
        int fn = tcResult.Matches.Count(m => !m.IsCorrect);
        int fp = tcResult.Detections.Count - tp;

        string line2 = $"Time: {tcResult.InferenceMs:F1}ms | " +
                       $"Detections: {tcResult.Detections.Count} | TP:{tp} FP:{fp} FN:{fn}";
        CvInvoke.PutText(image, line2, new Point(10, 45),
            FontFace.HersheySimplex, 0.5, ColorWhite, 1);
    }

    private static void DrawCross(Mat image, Point center, MCvScalar color, int size, int thickness)
    {
        CvInvoke.Line(image, new Point(center.X - size, center.Y),
            new Point(center.X + size, center.Y), color, thickness);
        CvInvoke.Line(image, new Point(center.X, center.Y - size),
            new Point(center.X, center.Y + size), color, thickness);
    }

    private static void DrawTextWithBg(Mat image, string text, Point pos, MCvScalar color)
    {
        int baseline = 0;
        var textSize = CvInvoke.GetTextSize(text, FontFace.HersheySimplex, 0.45, 1, ref baseline);
        var bgRect = new Rectangle(pos.X - 1, pos.Y - textSize.Height - 2,
            textSize.Width + 4, textSize.Height + 6);

        CvInvoke.Rectangle(image, bgRect, ColorBlack, -1);
        CvInvoke.Rectangle(image, bgRect, color, 1);
        CvInvoke.PutText(image, text, new Point(pos.X + 1, pos.Y),
            FontFace.HersheySimplex, 0.45, color, 1);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        if (name.Length > 80) name = name[..80];
        return name;
    }
}