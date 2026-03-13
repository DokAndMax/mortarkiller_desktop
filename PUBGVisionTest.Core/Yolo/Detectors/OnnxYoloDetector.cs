// Core/Yolo/Detectors/OnnxYoloDetector.cs
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing;

namespace PUBGVisionTest.Core.Yolo.Detectors;

/// <summary>
/// YOLO-детектор на базі OnnxRuntime.
/// Підтримує CPU та CUDA GPU.
/// </summary>
public class OnnxYoloDetector : IYoloDetector
{
    private InferenceSession? _session;
    private YoloDetectorConfig _config = new();
    private string? _inputName;
    private readonly bool _useGpu;

    public string BackendName { get; }

    public OnnxYoloDetector(bool useGpu)
    {
        _useGpu = useGpu;
        BackendName = useGpu ? "OnnxRuntime (CUDA GPU)" : "OnnxRuntime (CPU)";
    }

    public void Initialize(YoloDetectorConfig config)
    {
        _config = config;

        if (!File.Exists(config.ModelPath))
            throw new FileNotFoundException($"Model not found: {config.ModelPath}");

        var options = CreateSessionOptions();
        _session = new InferenceSession(config.ModelPath, options);
        _inputName = _session.InputMetadata.Keys.First();

        // Перевірка реального input size
        var inputMeta = _session.InputMetadata[_inputName];
        var dims = inputMeta.Dimensions;
        if (dims.Length == 4 && dims[2] > 0 && dims[3] > 0)
        {
            int modelH = dims[2];
            if (modelH != config.ExportImgsz)
            {
                Console.WriteLine($"  WARNING: Model expects {dims[3]}x{modelH} " +
                    $"but config says ExportImgsz={config.ExportImgsz}. Adjusting...");
                _config = config with { ExportImgsz = modelH };
            }
        }
    }

    private SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };

        if (_useGpu)
        {
            options.AppendExecutionProvider_CUDA(deviceId: 0);
        }
        else
        {
            options.InterOpNumThreads = Environment.ProcessorCount;
            options.IntraOpNumThreads = Environment.ProcessorCount;
        }

        return options;
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
        if (_session == null)
            throw new InvalidOperationException("Not initialized");

        int origW = frame.Width;
        int origH = frame.Height;

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
                detections = detections.Select(d => new YoloPrediction
                {
                    ClassName = d.ClassName,
                    Confidence = d.Confidence,
                    BoundingBox = new Rectangle(
                        (int)(d.BoundingBox.X * upscale),
                        (int)(d.BoundingBox.Y * upscale),
                        (int)(d.BoundingBox.Width * upscale),
                        (int)(d.BoundingBox.Height * upscale))
                }).ToList();
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

        using var rgb = new Mat();
        CvInvoke.CvtColor(letterboxed, rgb, ColorConversion.Bgr2Rgb);

        var tensor = MatToTensor(rgb, modelInputSize);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName!, tensor)
        };

        using var results = _session!.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        return _config.YoloVersion switch
        {
            YoloVersion.V10 => ParseYoloV10Output(outputTensor, letterboxInfo),
            _ => ParseYoloV8Output(outputTensor, letterboxInfo)
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

            using var rgb = new Mat();
            CvInvoke.CvtColor(letterboxed, rgb, ColorConversion.Bgr2Rgb);

            var tensor = MatToTensor(rgb, modelInputSize);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName!, tensor)
            };

            using var results = _session!.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();

            var tileDetections = _config.YoloVersion switch
            {
                YoloVersion.V10 => ParseYoloV10Output(outputTensor, letterboxInfo),
                _ => ParseYoloV8Output(outputTensor, letterboxInfo)
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

        if (allBoxes.Count == 0) return new List<YoloPrediction>();

        return NmsHelper.ApplyPerClassNms(
            allBoxes, allScores, allClassNames, _config.NmsThreshold);
    }

    private static DenseTensor<float> MatToTensor(Mat rgbMat, int size)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });

        int totalBytes = size * size * 3;
        byte[] pixels = new byte[totalBytes];

        int step = (int)rgbMat.Step;

        if (step == size * 3)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                rgbMat.DataPointer, pixels, 0, totalBytes);
        }
        else
        {
            for (int y = 0; y < size; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    rgbMat.DataPointer + y * step,
                    pixels, y * size * 3, size * 3);
            }
        }

        for (int y = 0; y < size; y++)
        {
            int rowOffset = y * size * 3;
            for (int x = 0; x < size; x++)
            {
                int offset = rowOffset + x * 3;
                tensor[0, 0, y, x] = pixels[offset + 0] / 255f;
                tensor[0, 1, y, x] = pixels[offset + 1] / 255f;
                tensor[0, 2, y, x] = pixels[offset + 2] / 255f;
            }
        }

        return tensor;
    }

    private List<YoloPrediction> ParseYoloV8Output(
        Tensor<float> output, LetterboxInfo lbInfo)
    {
        int numBoxes = output.Dimensions[2];
        int numClasses = _config.Labels.Length;

        var boxes = new List<Rectangle>();
        var scores = new List<float>();
        var classIds = new List<int>();

        for (int i = 0; i < numBoxes; i++)
        {
            float maxScore = 0;
            int maxClassId = -1;

            for (int c = 0; c < numClasses; c++)
            {
                float score = output[0, 4 + c, i];
                if (score > maxScore)
                {
                    maxScore = score;
                    maxClassId = c;
                }
            }

            if (maxScore >= _config.ConfidenceThreshold)
            {
                float cx = output[0, 0, i];
                float cy = output[0, 1, i];
                float w = output[0, 2, i];
                float h = output[0, 3, i];

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

        var indices = NmsHelper.NonMaxSuppression(boxes, scores, _config.NmsThreshold);

        return indices.Select(idx => new YoloPrediction
        {
            ClassName = _config.Labels[classIds[idx]],
            Confidence = scores[idx],
            BoundingBox = boxes[idx]
        }).ToList();
    }

    private List<YoloPrediction> ParseYoloV10Output(
        Tensor<float> output, LetterboxInfo lbInfo)
    {
        var predictions = new List<YoloPrediction>();
        int numBoxes = output.Dimensions[1];
        int cols = output.Dimensions[2];
        if (cols < 6) return predictions;

        for (int i = 0; i < numBoxes; i++)
        {
            float score = output[0, i, 4];
            if (score < _config.ConfidenceThreshold) continue;

            float x1 = output[0, i, 0], y1 = output[0, i, 1];
            float x2 = output[0, i, 2], y2 = output[0, i, 3];
            int classId = (int)output[0, i, 5];

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

        return predictions;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}