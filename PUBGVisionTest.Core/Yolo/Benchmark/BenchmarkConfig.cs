// Core/Yolo/Benchmark/BenchmarkConfig.cs
using PUBGVisionTest.Core.Yolo.Detectors;

namespace PUBGVisionTest.Core.Yolo.Benchmark;

/// <summary>
/// Конфігурація одного бенчмарк-прогону.
/// </summary>
public record BenchmarkRunConfig
{
    public string Name { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public DetectorBackend Backend { get; set; }
    public int TrainImgsz { get; set; }
    public int ExportImgsz { get; set; }
    public bool IsFp16 { get; set; }
    public bool DownscaleScreenshot { get; set; } = false;
    public int ScreenshotDownscaleWidth { get; set; } = 0;
    public YoloVersion YoloVersion { get; set; } = YoloVersion.V8_V11;
    public bool IsSliced { get; set; } = false;
    public int SliceSize { get; set; } = 640;
    public float SliceOverlap { get; set; } = 0.2f;
    public string[] Labels { get; set; } = Array.Empty<string>();
    public float ConfidenceThreshold { get; set; } = 0.5f;
    public float NmsThreshold { get; set; } = 0.4f;
    public int WarmupRuns { get; set; } = 3;
    public int BenchmarkRuns { get; set; } = 10;

    public string Approach
    {
        get
        {
            string suffix = IsSliced ? "(sliced)" : "";
            if (DownscaleScreenshot)
                return $"Train{TrainImgsz}->Export{ExportImgsz}->DS{ScreenshotDownscaleWidth}{suffix}";
            if (ExportImgsz != TrainImgsz)
                return $"Train{TrainImgsz}->Export{ExportImgsz}(re-export){suffix}";
            return $"Train{TrainImgsz}->Export{ExportImgsz}(native){suffix}";
        }
    }

    public override string ToString() =>
        $"{Name} | {Backend} | {Approach} | FP16:{IsFp16}";

    /// <summary>
    /// Конвертує в конфігурацію детектора.
    /// </summary>
    public YoloDetectorConfig ToDetectorConfig() => new()
    {
        ModelPath = ModelPath,
        Labels = Labels,
        ExportImgsz = ExportImgsz,
        ConfidenceThreshold = ConfidenceThreshold,
        NmsThreshold = NmsThreshold,
        YoloVersion = YoloVersion,
        IsSliced = IsSliced,
        SliceSize = SliceSize,
        SliceOverlap = SliceOverlap,
        DownscaleScreenshot = DownscaleScreenshot,
        ScreenshotDownscaleWidth = ScreenshotDownscaleWidth
    };
}