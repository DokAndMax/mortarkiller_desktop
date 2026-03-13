// Core/Yolo/Benchmark/ReportGenerator.cs
using System.Text;

namespace PUBGVisionTest.Core.Yolo.Benchmark;

public static class ReportGenerator
{
    private static double CalculateCompositeScore(BenchmarkResult r, double maxMs, double maxDist)
    {
        double speedScore = 1.0 - Math.Clamp(r.AvgInferenceMs / maxMs, 0, 1);
        double distScore = maxDist > 0
            ? 1.0 - Math.Clamp(r.AvgCenterDistancePx / maxDist, 0, 1) : 1.0;
        double confScore = r.AvgConfidence;

        return r.F1 * 40.0 + r.Recall * 20.0 + r.Precision * 15.0 +
               speedScore * 15.0 + confScore * 5.0 + distScore * 5.0;
    }

    public static void PrintConsoleReport(List<BenchmarkResult> results)
    {
        var successful = results.Where(r => r.Error == null).ToList();
        var failed = results.Where(r => r.Error != null).ToList();

        var line = new string('=', 135);

        Console.WriteLine();
        Console.WriteLine(line);
        Console.WriteLine("  YOLO BENCHMARK RESULTS");
        Console.WriteLine($"  Successful: {successful.Count} | Failed: {failed.Count} | Total: {results.Count}");
        Console.WriteLine(line);

        if (successful.Count > 0)
        {
            double maxMs = successful.Max(r => r.AvgInferenceMs);
            double maxDist = successful.Where(r => r.AvgCenterDistancePx > 0)
                .Select(r => r.AvgCenterDistancePx).DefaultIfEmpty(1.0).Max();

            var scored = successful.Select(r => new
            {
                Result = r,
                Score = CalculateCompositeScore(r, maxMs, maxDist),
                TotalDet = r.TestCaseResults.Sum(t => t.Detections.Count)
            }).ToList();

            var perfectGroup = scored.Where(s => s.Result.Precision >= 1.0 && s.Result.Recall >= 1.0)
                .OrderByDescending(s => s.Score).ToList();
            var restGroup = scored.Where(s => !(s.Result.Precision >= 1.0 && s.Result.Recall >= 1.0))
                .OrderByDescending(s => s.Score).ToList();

            if (perfectGroup.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n  🏆 PERFECT DETECTIONS — {perfectGroup.Count} configs\n");
                Console.ResetColor();

                PrintTableHeader();
                Console.WriteLine(new string('-', 135));

                int rank = 1;
                foreach (var s in perfectGroup)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    PrintTableRow(rank++, s.Result, s.Score, s.TotalDet);
                    Console.ResetColor();
                }
                Console.WriteLine(new string('-', 135));
            }

            if (restGroup.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n  📊 OTHER RESULTS — {restGroup.Count} configs\n");
                Console.ResetColor();

                PrintTableHeader();
                Console.WriteLine(new string('-', 135));

                int rank = 1;
                foreach (var s in restGroup)
                {
                    Console.ForegroundColor = s.Result.F1 >= 0.7 ? ConsoleColor.Yellow
                        : s.Result.F1 > 0 ? ConsoleColor.Red : ConsoleColor.DarkGray;
                    PrintTableRow(rank++, s.Result, s.Score, s.TotalDet);
                    Console.ResetColor();
                }
                Console.WriteLine(new string('-', 135));
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n  Score: F1×40 + Recall×20 + Precision×15 + Speed×15 + Conf×5 + Accuracy×5 (max=100)");
            Console.ResetColor();
        }

        if (failed.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  FAILED ({failed.Count}):");
            Console.ResetColor();
            foreach (var f in failed.Take(10))
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"    {Truncate(f.Config.Name, 60)}: {Truncate(f.Error ?? "", 70)}");
                Console.ResetColor();
            }
        }

        Console.WriteLine(line);
    }

    private static void PrintTableHeader()
    {
        Console.WriteLine(" {0,4} | {1,-72} | {2,5} | {3,7} | {4,6} | {5,6} | {6,6} | {7,6} | {8,7} | {9,5} | {10,4}",
            "#", "Configuration", "Score", "Avg ms", "FPS", "Prec", "Recall", "F1", "AvgDst", "Conf", "Det");
    }

    private static void PrintTableRow(int rank, BenchmarkResult r, double score, int totalDet)
    {
        Console.WriteLine(" {0,4} | {1,-72} | {2,5:F1} | {3,7:F1} | {4,6:F1} | {5,6:F3} | {6,6:F3} | {7,6:F3} | {8,7:F1} | {9,5:F3} | {10,4}",
            rank, Truncate(r.Config.ToString(), 72), score,
            r.AvgInferenceMs, r.FPS, r.Precision, r.Recall, r.F1,
            r.AvgCenterDistancePx, r.AvgConfidence, totalDet);
    }

    public static void SaveCsv(List<BenchmarkResult> results, string path)
    {
        var successful = results.Where(r => r.Error == null).ToList();
        double maxMs = successful.Count > 0 ? successful.Max(r => r.AvgInferenceMs) : 1;
        double maxDist = successful.Where(r => r.AvgCenterDistancePx > 0)
            .Select(r => r.AvgCenterDistancePx).DefaultIfEmpty(1.0).Max();

        var sb = new StringBuilder();
        sb.AppendLine("Config,Approach,Backend,TrainImgsz,ExportImgsz,DownscaleW,FP16," +
            "Score,AvgMs,MinMs,MaxMs,MedianMs,FPS," +
            "Precision,Recall,F1,AvgDistPx,AvgConf,TP,FP,FN,TotalDet,Error");

        foreach (var r in results)
        {
            int totalDet = r.TestCaseResults.Sum(t => t.Detections.Count);
            double score = r.Error == null ? CalculateCompositeScore(r, maxMs, maxDist) : 0;

            sb.AppendLine(string.Join(",",
                Q(r.Config.Name), Q(r.Config.Approach), r.Config.Backend,
                r.Config.TrainImgsz, r.Config.ExportImgsz,
                r.Config.DownscaleScreenshot ? r.Config.ScreenshotDownscaleWidth : 0,
                r.Config.IsFp16, score.ToString("F2"),
                r.AvgInferenceMs.ToString("F2"), r.MinInferenceMs.ToString("F2"),
                r.MaxInferenceMs.ToString("F2"), r.MedianInferenceMs.ToString("F2"),
                r.FPS.ToString("F1"), r.Precision.ToString("F4"), r.Recall.ToString("F4"),
                r.F1.ToString("F4"), r.AvgCenterDistancePx.ToString("F2"),
                r.AvgConfidence.ToString("F4"), r.TotalDetectedCorrectly,
                r.TotalFalsePositives, r.TotalMissed, totalDet,
                Q(r.Error ?? "")));
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"CSV saved: {path}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";

    private static string Q(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
}