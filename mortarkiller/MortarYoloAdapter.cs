// MortarKiller/MortarYoloAdapter.cs
using PUBGVisionTest.Core.Yolo;
using PUBGVisionTest.Core.Yolo.Detectors;
using System;
using System.Collections.Generic;

namespace mortarkiller;

/// <summary>
/// Тонка обгортка над IYoloDetector з PUBGVisionTest.Core.
/// Дає MortarKiller простий API без необхідності знати деталі.
/// </summary>
public sealed class MortarYoloAdapter : IDisposable
{
    private readonly IYoloDetector _detector;

    public MortarYoloAdapter(
        string modelPath,
        string[] labels,
        YoloVersion yoloVersion = YoloVersion.V8_V11,
        int imgsz = 640,
        float confThreshold = 0.5f,
        float nmsThreshold = 0.4f,
        bool useGpu = true)
    {
        var backend = useGpu ? DetectorBackend.EmguGpu : DetectorBackend.OnnxCpu;

        _detector = DetectorFactory.Create(backend);
        _detector.Initialize(new YoloDetectorConfig
        {
            ModelPath = modelPath,
            Labels = labels,
            ExportImgsz = imgsz,
            ConfidenceThreshold = confThreshold,
            NmsThreshold = nmsThreshold,
            YoloVersion = yoloVersion,
            // Тайлінг можна увімкнути якщо потрібно:
            IsSliced = true,
        });
    }

    /// <summary>
    /// Детекція на Mat — основний метод для MortarKiller.
    /// Сигнатура повністю збігається зі старим YoloDetector.Detect(Mat).
    /// </summary>
    public List<YoloPrediction> Detect(Emgu.CV.Mat frame)
        => _detector.Detect(frame);

    public void Dispose() => _detector.Dispose();
}