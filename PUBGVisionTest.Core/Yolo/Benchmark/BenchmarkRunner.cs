// Core/Yolo/Benchmark/BenchmarkRunner.cs
using PUBGVisionTest.Core.Yolo.Detectors;
using System.Diagnostics;

namespace PUBGVisionTest.Core.Yolo.Benchmark;

/// <summary>
/// Виконує бенчмарк-прогін для одної конфігурації.
/// </summary>
public static class BenchmarkRunner
{
    public static BenchmarkResult Run(
        BenchmarkRunConfig config,
        List<TestCase> testCases,
        Dictionary<string, byte[]> preloaded)
    {
        var result = new BenchmarkResult { Config = config };

        IYoloDetector detector;
        try
        {
            detector = DetectorFactory.Create(config.Backend);
            detector.Initialize(config.ToDetectorConfig());
        }
        catch (Exception ex)
        {
            result.Error = $"Init failed: {ex.Message}";
            return result;
        }

        try
        {
            // Warmup
            for (int w = 0; w < config.WarmupRuns; w++)
            {
                foreach (var tc in testCases)
                {
                    try { detector.Detect(preloaded[tc.ImagePath]); }
                    catch { }
                }
            }

            // Benchmark
            var allTimings = new List<double>();

            for (int run = 0; run < config.BenchmarkRuns; run++)
            {
                foreach (var tc in testCases)
                {
                    var sw = Stopwatch.StartNew();
                    List<YoloPrediction> detections;
                    try
                    {
                        detections = detector.Detect(preloaded[tc.ImagePath]);
                    }
                    catch (Exception ex)
                    {
                        result.Error = $"Detect failed: {ex.Message}";
                        return result;
                    }
                    sw.Stop();

                    allTimings.Add(sw.Elapsed.TotalMilliseconds);

                    // Зберігаємо тільки останній прогін
                    if (run == config.BenchmarkRuns - 1)
                    {
                        var tcr = new TestCaseResult
                        {
                            ImageName = tc.ImageName,
                            Detections = detections,
                            InferenceMs = sw.Elapsed.TotalMilliseconds
                        };

                        if (tc.HasAnnotations)
                            tcr.Matches = QualityEvaluator.Evaluate(detections, tc.ExpectedObjects);

                        result.TestCaseResults.Add(tcr);
                    }
                }
            }

            allTimings.Sort();
            result.AvgInferenceMs = allTimings.Average();
            result.MinInferenceMs = allTimings.Min();
            result.MaxInferenceMs = allTimings.Max();
            result.MedianInferenceMs = allTimings[allTimings.Count / 2];

            AggregateQuality(result);
        }
        catch (Exception ex)
        {
            result.Error = $"Runtime: {ex.Message}";
        }
        finally
        {
            detector.Dispose();
        }

        return result;
    }

    private static void AggregateQuality(BenchmarkResult result)
    {
        int totalExpected = 0, totalTP = 0, totalFP = 0;
        var distances = new List<double>();
        var confidences = new List<double>();

        foreach (var tc in result.TestCaseResults)
        {
            totalExpected += tc.Matches.Count;
            totalTP += tc.Matches.Count(m => m.IsCorrect);
            totalFP += QualityEvaluator.CountFalsePositives(tc.Detections, tc.Matches);

            foreach (var m in tc.Matches.Where(m => m.IsCorrect && m.Matched != null))
            {
                if (!double.IsNaN(m.DistancePx)) distances.Add(m.DistancePx);
                confidences.Add(m.Matched!.Confidence);
            }
        }

        result.TotalExpectedObjects = totalExpected;
        result.TotalDetectedCorrectly = totalTP;
        result.TotalFalsePositives = totalFP;
        result.TotalMissed = totalExpected - totalTP;
        result.AvgCenterDistancePx = distances.Count > 0 ? distances.Average() : 0;
        result.AvgConfidence = confidences.Count > 0 ? confidences.Average() : 0;
    }
}