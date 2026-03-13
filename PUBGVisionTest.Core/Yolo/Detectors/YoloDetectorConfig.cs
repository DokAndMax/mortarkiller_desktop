// Core/Yolo/Detectors/YoloDetectorConfig.cs
namespace PUBGVisionTest.Core.Yolo.Detectors;

/// <summary>
/// Конфігурація детектора — спільна для всіх бекендів.
/// </summary>
public record YoloDetectorConfig
{
    public string ModelPath { get; init; } = "";
    public string[] Labels { get; init; } = Array.Empty<string>();
    public int ExportImgsz { get; init; } = 640;
    public float ConfidenceThreshold { get; init; } = 0.5f;
    public float NmsThreshold { get; init; } = 0.4f;
    public YoloVersion YoloVersion { get; init; } = YoloVersion.V8_V11;

    // Тайлінг
    public bool IsSliced { get; init; } = false;
    public int SliceSize { get; init; } = 640;
    public float SliceOverlap { get; init; } = 0.2f;

    // Даунскейл скріншота перед детекцією
    public bool DownscaleScreenshot { get; init; } = false;
    public int ScreenshotDownscaleWidth { get; init; } = 0;
}