using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace mortarkiller;

// =======================================================
//  Enums & Models
// =======================================================

public enum YoloVersion
{
    V8,   // [1, 4+numClasses, numBoxes]
    V9,   // [1, 4+numClasses, numBoxes] — ідентичний V8
    V10,  // [1, numBoxes, 6] — NMS вбудований
    V11,  // [1, 4+numClasses, numBoxes] — ідентичний V8
    V12,  // [1, 4+numClasses, numBoxes] — ідентичний V8
    V26   // [1, 4+numClasses, numBoxes] — ідентичний V8
}

public class YoloPrediction
{
    public string ClassName { get; set; } = "";
    public float Confidence { get; set; }
    public Rectangle BoundingBox { get; set; }

    public Point Center => new(
        BoundingBox.X + BoundingBox.Width / 2,
        BoundingBox.Y + BoundingBox.Height / 2);

    public Point BottomTip => new(
        BoundingBox.X + BoundingBox.Width / 2,
        BoundingBox.Bottom);
}

/// <summary>
/// Інформація про letterbox-трансформацію для зворотного маппінгу координат
/// </summary>
public struct LetterboxInfo
{
    public float Scale;
    public int PadLeft;
    public int PadTop;
    public int OrigWidth;
    public int OrigHeight;
}

// =======================================================
//  YoloDetector
// =======================================================

public class YoloDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _labels;
    private int _imgsz;
    private readonly float _confThreshold;
    private readonly float _nmsThreshold;
    private readonly YoloVersion _yoloVersion;

    public YoloDetector(
        string modelPath,
        string[] labels,
        YoloVersion yoloVersion = YoloVersion.V8,
        int imgsz = 1280,
        float confThreshold = 0.5f,
        float nmsThreshold = 0.4f,
        bool useGpu = true)
    {
        _labels = labels;
        _imgsz = imgsz;
        _confThreshold = confThreshold;
        _nmsThreshold = nmsThreshold;
        _yoloVersion = yoloVersion;

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };

        if (useGpu)
        {
            try
            {
                options.AppendExecutionProvider_CUDA(deviceId: 0);
                Console.WriteLine("[YoloDetector] CUDA GPU enabled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YoloDetector] CUDA failed ({ex.Message}), using CPU");
                options.InterOpNumThreads = Environment.ProcessorCount;
                options.IntraOpNumThreads = Environment.ProcessorCount;
            }
        }
        else
        {
            options.InterOpNumThreads = Environment.ProcessorCount;
            options.IntraOpNumThreads = Environment.ProcessorCount;
            Console.WriteLine("[YoloDetector] CPU mode");
        }

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();

        var dims = _session.InputMetadata[_inputName].Dimensions;
        if (dims.Length == 4 && dims[2] > 0 && dims[3] > 0
            && (dims[2] != _imgsz || dims[3] != _imgsz))
        {
            Console.WriteLine(
                $"[YoloDetector] Model expects {dims[3]}x{dims[2]}, adjusting from {_imgsz}");
            _imgsz = dims[2];
        }

        Console.WriteLine(
            $"[YoloDetector] Version={_yoloVersion}, Input={_imgsz}x{_imgsz}, " +
            $"Labels=[{string.Join(", ", _labels)}]");
    }

    // ===================================================
    //  Головний метод детекції
    // ===================================================

    public List<YoloPrediction> Detect(Mat frame)
    {
        // 1. Letterbox
        using var letterboxed = ApplyLetterbox(frame, _imgsz, out var lbInfo);

        // 2. BGR -> RGB
        using var rgb = new Mat();
        CvInvoke.CvtColor(letterboxed, rgb, ColorConversion.Bgr2Rgb);

        // 3. Mat -> Tensor
        var tensor = MatToTensor(rgb, _imgsz);

        // 4. Інференс
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, tensor)
        };

        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        // 5. Парсинг: V10 має окремий формат, решта — однаковий
        return _yoloVersion switch
        {
            YoloVersion.V10 => ParseV10(outputTensor, lbInfo),
            _ => ParseV8(outputTensor, lbInfo)  // V8, V9, V11, V12, V26
        };
    }

    // ===================================================
    //  Letterbox
    // ===================================================

    private static Mat ApplyLetterbox(Mat source, int targetSize, out LetterboxInfo info)
    {
        int srcW = source.Width;
        int srcH = source.Height;

        float scale = Math.Min(
            (float)targetSize / srcW,
            (float)targetSize / srcH);

        int newW = (int)(srcW * scale);
        int newH = (int)(srcH * scale);

        int padLeft = (targetSize - newW) / 2;
        int padTop = (targetSize - newH) / 2;
        int padRight = targetSize - newW - padLeft;
        int padBottom = targetSize - newH - padTop;

        info = new LetterboxInfo
        {
            Scale = scale,
            PadLeft = padLeft,
            PadTop = padTop,
            OrigWidth = srcW,
            OrigHeight = srcH
        };

        using var resized = new Mat();
        CvInvoke.Resize(source, resized, new Size(newW, newH),
            0, 0, Inter.Linear);

        var padded = new Mat();
        CvInvoke.CopyMakeBorder(resized, padded,
            padTop, padBottom, padLeft, padRight,
            BorderType.Constant,
            new MCvScalar(114, 114, 114));

        return padded;
    }

    private static Rectangle ModelBoxToOriginal(
        float cx, float cy, float w, float h, LetterboxInfo info)
    {
        float x1 = cx - w / 2f - info.PadLeft;
        float y1 = cy - h / 2f - info.PadTop;

        float origX = x1 / info.Scale;
        float origY = y1 / info.Scale;
        float origW = w / info.Scale;
        float origH = h / info.Scale;

        origX = Math.Max(0, origX);
        origY = Math.Max(0, origY);
        origW = Math.Min(origW, info.OrigWidth - origX);
        origH = Math.Min(origH, info.OrigHeight - origY);

        return new Rectangle(
            (int)origX, (int)origY,
            (int)origW, (int)origH);
    }

    private static Rectangle ModelBoxToOriginal_XYXY(
        float x1, float y1, float x2, float y2, LetterboxInfo info)
    {
        x1 -= info.PadLeft;
        y1 -= info.PadTop;
        x2 -= info.PadLeft;
        y2 -= info.PadTop;

        float origX1 = x1 / info.Scale;
        float origY1 = y1 / info.Scale;
        float origX2 = x2 / info.Scale;
        float origY2 = y2 / info.Scale;

        origX1 = Math.Max(0, origX1);
        origY1 = Math.Max(0, origY1);
        origX2 = Math.Min(info.OrigWidth, origX2);
        origY2 = Math.Min(info.OrigHeight, origY2);

        return new Rectangle(
            (int)origX1, (int)origY1,
            (int)(origX2 - origX1), (int)(origY2 - origY1));
    }

    // ===================================================
    //  Парсери
    // ===================================================

    /// <summary>
    /// YOLOv8/v9/v11/v12/v26: [1, 4+numClasses, numBoxes]
    /// Транспонований формат — координати та класи по першій осі,
    /// бокси по другій.
    /// </summary>
    private List<YoloPrediction> ParseV8(
        Tensor<float> output, LetterboxInfo lbInfo)
    {
        int numBoxes = output.Dimensions[2];
        int numClasses = _labels.Length;

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

            if (maxScore >= _confThreshold)
            {
                float cx = output[0, 0, i];
                float cy = output[0, 1, i];
                float w = output[0, 2, i];
                float h = output[0, 3, i];

                var rect = ModelBoxToOriginal(cx, cy, w, h, lbInfo);

                if (rect.Width > 0 && rect.Height > 0)
                {
                    boxes.Add(rect);
                    scores.Add(maxScore);
                    classIds.Add(maxClassId);
                }
            }
        }

        if (boxes.Count == 0)
            return new List<YoloPrediction>();

        var indices = NonMaxSuppression(boxes, scores, _nmsThreshold);

        return indices.Select(idx => new YoloPrediction
        {
            ClassName = _labels[classIds[idx]],
            Confidence = scores[idx],
            BoundingBox = boxes[idx]
        }).ToList();
    }

    /// <summary>
    /// YOLOv10: [1, numBoxes, 6] — x1,y1,x2,y2,score,classId
    /// NMS вже вбудований у модель!
    /// </summary>
    private List<YoloPrediction> ParseV10(
        Tensor<float> output, LetterboxInfo lbInfo)
    {
        var predictions = new List<YoloPrediction>();
        int numBoxes = output.Dimensions[1];

        if (output.Dimensions.Length < 3 || output.Dimensions[2] < 6)
            return predictions;

        for (int i = 0; i < numBoxes; i++)
        {
            float x1 = output[0, i, 0];
            float y1 = output[0, i, 1];
            float x2 = output[0, i, 2];
            float y2 = output[0, i, 3];
            float score = output[0, i, 4];
            int classId = (int)output[0, i, 5];

            if (score < _confThreshold)
                continue;

            if (x1 == 0 && y1 == 0 && x2 == 0 && y2 == 0)
                continue;

            var rect = ModelBoxToOriginal_XYXY(x1, y1, x2, y2, lbInfo);

            if (rect.Width > 0 && rect.Height > 0
                && classId >= 0 && classId < _labels.Length)
            {
                predictions.Add(new YoloPrediction
                {
                    ClassName = _labels[classId],
                    Confidence = score,
                    BoundingBox = rect
                });
            }
        }

        return predictions;
    }

    // ===================================================
    //  Утиліти
    // ===================================================

    private static DenseTensor<float> MatToTensor(Mat rgbMat, int size)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });

        int totalBytes = size * size * 3;
        byte[] pixels = new byte[totalBytes];
        int step = (int)rgbMat.Step;

        if (step == size * 3)
        {
            Marshal.Copy(rgbMat.DataPointer, pixels, 0, totalBytes);
        }
        else
        {
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(
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
                tensor[0, 0, y, x] = pixels[offset] / 255f;
                tensor[0, 1, y, x] = pixels[offset + 1] / 255f;
                tensor[0, 2, y, x] = pixels[offset + 2] / 255f;
            }
        }

        return tensor;
    }

    private static List<int> NonMaxSuppression(
        List<Rectangle> boxes, List<float> scores, float nmsThreshold)
    {
        var result = new List<int>();
        var sorted = Enumerable.Range(0, scores.Count)
            .OrderByDescending(i => scores[i])
            .ToList();
        var suppressed = new HashSet<int>();

        for (int si = 0; si < sorted.Count; si++)
        {
            int i = sorted[si];
            if (suppressed.Contains(i)) continue;
            result.Add(i);

            for (int sj = si + 1; sj < sorted.Count; sj++)
            {
                int j = sorted[sj];
                if (suppressed.Contains(j)) continue;

                var inter = Rectangle.Intersect(boxes[i], boxes[j]);
                if (inter.IsEmpty) continue;

                float interArea = inter.Width * inter.Height;
                float unionArea =
                    (float)(boxes[i].Width * boxes[i].Height) +
                    (boxes[j].Width * boxes[j].Height) - interArea;

                if (unionArea > 0 && interArea / unionArea > nmsThreshold)
                    suppressed.Add(j);
            }
        }

        return result;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}