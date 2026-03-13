// Core/Yolo/Benchmark/BenchmarkResult.cs
namespace PUBGVisionTest.Core.Yolo.Benchmark;

public class BenchmarkResult
{
    public BenchmarkRunConfig Config { get; set; } = new();

    public double AvgInferenceMs { get; set; }
    public double MinInferenceMs { get; set; }
    public double MaxInferenceMs { get; set; }
    public double MedianInferenceMs { get; set; }
    public double FPS => AvgInferenceMs > 0 ? 1000.0 / AvgInferenceMs : 0;

    public int TotalExpectedObjects { get; set; }
    public int TotalDetectedCorrectly { get; set; }
    public int TotalFalsePositives { get; set; }
    public int TotalMissed { get; set; }

    public double Precision => TotalDetectedCorrectly + TotalFalsePositives > 0
        ? (double)TotalDetectedCorrectly / (TotalDetectedCorrectly + TotalFalsePositives) : 0;
    public double Recall => TotalExpectedObjects > 0
        ? (double)TotalDetectedCorrectly / TotalExpectedObjects : 0;
    public double F1 => Precision + Recall > 0
        ? 2 * Precision * Recall / (Precision + Recall) : 0;

    public double AvgCenterDistancePx { get; set; }
    public double AvgConfidence { get; set; }
    public List<TestCaseResult> TestCaseResults { get; set; } = new();
    public string? Error { get; set; }
}

public class TestCaseResult
{
    public string ImageName { get; set; } = "";
    public List<YoloPrediction> Detections { get; set; } = new();
    public List<MatchResult> Matches { get; set; } = new();
    public double InferenceMs { get; set; }
}

public class MatchResult
{
    public ExpectedObject Expected { get; set; } = new();
    public YoloPrediction? Matched { get; set; }
    public double DistancePx { get; set; }
    public bool IsCorrect { get; set; }
}