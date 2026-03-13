// Core/Yolo/Detectors/EmguYoloDetector.cs
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Dnn;
using Emgu.CV.Structure;
using System.Drawing;
using Backend = Emgu.CV.Dnn.Backend;

namespace PUBGVisionTest.Core.Yolo.Detectors;

/// <summary>
/// YOLO-детектор на базі Emgu.CV DNN.
/// Підтримує CPU, CUDA GPU, CUDA FP16.
/// Підтримує YOLOv8/v11, letterbox, tiling, downscale.
/// </summary>
public class EmguYoloDetector : IYoloDetector
{
    private Net? _net;
    private YoloDetectorConfig _config = new();
    private readonly Backend _backend;
    private readonly Target _target;

    public string BackendName { get; }

    public EmguYoloDetector(DetectorBackend backend)
    {
        (_backend, _target, BackendName) = backend switch
        {
            DetectorBackend.EmguCpu => (Backend.OpenCV, Target.Cpu, "Emgu.CV (CPU)"),
            DetectorBackend.EmguGpu => (Backend.Cuda, Target.Cuda, "Emgu.CV (CUDA GPU)"),
            DetectorBackend.EmguGpuFp16 => (Backend.Cuda, Target.CudaFp16, "Emgu.CV (CUDA FP16)"),
            _ => throw new ArgumentException($"Unsupported Emgu backend: {backend}")
        };
    }

    public void Initialize(YoloDetectorConfig config)
    {
        _config = config;

        if (!File.Exists(config.ModelPath))
            throw new FileNotFoundException($"Model not found: {config.ModelPath}");

        _net = DnnInvoke.ReadNetFromONNX(config.ModelPath);
        _net.SetPreferableBackend(_backend);
        _net.SetPreferableTarget(_target);
    }

    public List<YoloPrediction> Detect(byte[] imageData)
    {
        using var mat = new Mat();
        CvInvoke.Imdecode(imageData, ImreadModes.AnyColor, mat);

        if (mat.IsEmpty)
            throw new ArgumentException("Cannot decode image data");

        return Detect(mat);
    }

    public List<YoloPrediction> Detect(Mat frame)
    {
        if (_net == null)
            throw new InvalidOperationException("Not initialized. Call Initialize() first.");

        int origW = frame.Width;
        int origH = frame.Height;

        // Крок 1: Опціональний даунскейл
        Mat workingFrame;
        int workingW, workingH;
        bool wasDownscaled = false;

        if (_config.DownscaleScreenshot
            && _config.ScreenshotDownscaleWidth > 0
            && _config.ScreenshotDownscaleWidth < origW)
        {
            float ratio = (float)_config.ScreenshotDownscaleWidth / origW;
            workingW = _config.ScreenshotDownscaleWidth;
            workingH = (int)(origH * ratio);
            workingFrame = new Mat();
            CvInvoke.Resize(frame, workingFrame, new Size(workingW, workingH));
            wasDownscaled = true;
        }
        else
        {
            workingFrame = frame;
            workingW = origW;
            workingH = origH;
        }

        try
        {
            List<YoloPrediction> detections;

            if (_config.IsSliced &&
                TilingHelper.NeedsTiling(workingW, workingH, _config.SliceSize))
            {
                detections = DetectSliced(workingFrame, workingW, workingH);
            }
            else
            {
                detections = DetectSingle(workingFrame, workingW, workingH);
            }

            if (wasDownscaled)
            {
                float upscale = (float)origW / workingW;
                detections = ScaleDetections(detections, upscale);
            }

            return detections;
        }
        finally
        {
            if (wasDownscaled)
                workingFrame.Dispose();
        }
    }

    private List<YoloPrediction> DetectSingle(Mat frame, int frameW, int frameH)
    {
        int modelInputSize = _config.ExportImgsz;

        using var letterboxed = LetterboxHelper.Apply(
            frame, modelInputSize, out var letterboxInfo);

        using var blob = DnnInvoke.BlobFromImage(
            letterboxed, 1.0 / 255.0,
            new Size(modelInputSize, modelInputSize),
            new MCvScalar(), swapRB: true, crop: false);

        _net!.SetInput(blob);
        using var output = _net.Forward();

        return _config.YoloVersion switch
        {
            YoloVersion.V10 => ParseYoloV10Output(output, letterboxInfo),
            _ => ParseYoloV8Output(output, letterboxInfo)
        };
    }

    private List<YoloPrediction> DetectSliced(Mat frame, int frameW, int frameH)
    {
        var tiles = TilingHelper.GenerateTiles(
            frameW, frameH, _config.SliceSize, _config.SliceOverlap);

        var allBoxes = new List<Rectangle>();
        var allScores = new List<float>();
        var allClassNames = new List<string>();

        int modelInputSize = _config.ExportImgsz;

        foreach (var tile in tiles)
        {
            using var tileRoi = new Mat(frame, tile);

            using var letterboxed = LetterboxHelper.Apply(
                tileRoi, modelInputSize, out var letterboxInfo);

            using var blob = DnnInvoke.BlobFromImage(
                letterboxed, 1.0 / 255.0,
                new Size(modelInputSize, modelInputSize),
                new MCvScalar(), swapRB: true, crop: false);

            _net!.SetInput(blob);
            using var output = _net.Forward();

            var tileDetections = _config.YoloVersion switch
            {
                YoloVersion.V10 => ParseYoloV10Output(output, letterboxInfo),
                _ => ParseYoloV8Output(output, letterboxInfo)
            };

            foreach (var det in tileDetections)
            {
                var shiftedBox = new Rectangle(
                    det.BoundingBox.X + tile.X,
                    det.BoundingBox.Y + tile.Y,
                    det.BoundingBox.Width,
                    det.BoundingBox.Height);

                int x1 = Math.Max(0, shiftedBox.X);
                int y1 = Math.Max(0, shiftedBox.Y);
                int x2 = Math.Min(frameW, shiftedBox.Right);
                int y2 = Math.Min(frameH, shiftedBox.Bottom);

                if (x2 > x1 && y2 > y1)
                {
                    allBoxes.Add(new Rectangle(x1, y1, x2 - x1, y2 - y1));
                    allScores.Add(det.Confidence);
                    allClassNames.Add(det.ClassName);
                }
            }
        }

        if (allBoxes.Count == 0)
            return [];

        return NmsHelper.ApplyPerClassNms(
            allBoxes, allScores, allClassNames, _config.NmsThreshold);
    }

    private List<YoloPrediction> ParseYoloV8Output(Mat output, LetterboxInfo lbInfo)
    {
        float[,,] data = (float[,,])output.GetData();
        int numClasses = _config.Labels.Length;
        int numBoxes = data.GetLength(2);

        var boxes = new List<Rectangle>();
        var scores = new List<float>();
        var classIds = new List<int>();

        for (int i = 0; i < numBoxes; i++)
        {
            float maxScore = 0;
            int maxClassId = -1;

            for (int c = 0; c < numClasses; c++)
            {
                float score = data[0, 4 + c, i];
                if (score > maxScore)
                {
                    maxScore = score;
                    maxClassId = c;
                }
            }

            if (maxScore >= _config.ConfidenceThreshold)
            {
                float cx = data[0, 0, i];
                float cy = data[0, 1, i];
                float w = data[0, 2, i];
                float h = data[0, 3, i];

                var rect = LetterboxHelper.ModelBoxToSource(cx, cy, w, h, lbInfo);

                if (rect.Width > 0 && rect.Height > 0)
                {
                    boxes.Add(rect);
                    scores.Add(maxScore);
                    classIds.Add(maxClassId);
                }
            }
        }

        if (boxes.Count == 0) return new List<YoloPrediction>();

        int[] indices = DnnInvoke.NMSBoxes(
            boxes.ToArray(), scores.ToArray(),
            _config.ConfidenceThreshold, _config.NmsThreshold);

        return indices.Select(idx => new YoloPrediction
        {
            ClassName = _config.Labels[classIds[idx]],
            Confidence = scores[idx],
            BoundingBox = boxes[idx]
        }).ToList();
    }

    private List<YoloPrediction> ParseYoloV10Output(Mat output, LetterboxInfo lbInfo)
    {
        var predictions = new List<YoloPrediction>();
        var rawData = output.GetData();

        if (rawData is float[,,] data3d)
            ParseV10_3D(data3d, lbInfo, predictions);
        else if (rawData is float[,] data2d)
            ParseV10_2D(data2d, lbInfo, predictions);

        return predictions;
    }

    private void ParseV10_3D(float[,,] data, LetterboxInfo lbInfo, List<YoloPrediction> predictions)
    {
        int numBoxes = data.GetLength(1);
        int cols = data.GetLength(2);
        if (cols < 6) return;

        for (int i = 0; i < numBoxes; i++)
        {
            float score = data[0, i, 4];
            if (score < _config.ConfidenceThreshold) continue;

            float x1 = data[0, i, 0], y1 = data[0, i, 1];
            float x2 = data[0, i, 2], y2 = data[0, i, 3];
            int classId = (int)data[0, i, 5];

            if (x1 == 0 && y1 == 0 && x2 == 0 && y2 == 0) continue;

            var rect = LetterboxHelper.ModelBoxToSource(
                (x1 + x2) / 2f, (y1 + y2) / 2f, x2 - x1, y2 - y1, lbInfo);

            if (rect.Width > 0 && rect.Height > 0
                && classId >= 0 && classId < _config.Labels.Length)
            {
                predictions.Add(new YoloPrediction
                {
                    ClassName = _config.Labels[classId],
                    Confidence = score,
                    BoundingBox = rect
                });
            }
        }
    }

    private void ParseV10_2D(float[,] data, LetterboxInfo lbInfo, List<YoloPrediction> predictions)
    {
        int numBoxes = data.GetLength(0);
        int cols = data.GetLength(1);
        if (cols < 6) return;

        for (int i = 0; i < numBoxes; i++)
        {
            float score = data[i, 4];
            if (score < _config.ConfidenceThreshold) continue;

            float x1 = data[i, 0], y1 = data[i, 1];
            float x2 = data[i, 2], y2 = data[i, 3];
            int classId = (int)data[i, 5];

            if (x1 == 0 && y1 == 0 && x2 == 0 && y2 == 0) continue;

            var rect = LetterboxHelper.ModelBoxToSource(
                (x1 + x2) / 2f, (y1 + y2) / 2f, x2 - x1, y2 - y1, lbInfo);

            if (rect.Width > 0 && rect.Height > 0
                && classId >= 0 && classId < _config.Labels.Length)
            {
                predictions.Add(new YoloPrediction
                {
                    ClassName = _config.Labels[classId],
                    Confidence = score,
                    BoundingBox = rect
                });
            }
        }
    }

    private static List<YoloPrediction> ScaleDetections(List<YoloPrediction> detections, float ratio)
    {
        return detections.Select(d => new YoloPrediction
        {
            ClassName = d.ClassName,
            Confidence = d.Confidence,
            BoundingBox = new Rectangle(
                (int)(d.BoundingBox.X * ratio),
                (int)(d.BoundingBox.Y * ratio),
                (int)(d.BoundingBox.Width * ratio),
                (int)(d.BoundingBox.Height * ratio))
        }).ToList();
    }

    public void Dispose()
    {
        _net?.Dispose();
        _net = null;
    }
}