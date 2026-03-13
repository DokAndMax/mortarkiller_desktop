// Core/Yolo/Detectors/IYoloDetector.cs
namespace PUBGVisionTest.Core.Yolo.Detectors;

/// <summary>
/// Уніфікований інтерфейс YOLO-детектора.
/// Працює з byte[] (закодоване зображення) або Mat (для live-режимів).
/// </summary>
public interface IYoloDetector : IDisposable
{
    string BackendName { get; }
    void Initialize(YoloDetectorConfig config);
    List<YoloPrediction> Detect(byte[] imageData);
    List<YoloPrediction> Detect(Emgu.CV.Mat frame);
}