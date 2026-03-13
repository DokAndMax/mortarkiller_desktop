using Emgu.CV;
using Emgu.CV.Dnn;
using Emgu.CV.Structure;
using System.Drawing;

namespace PUBGVisionTest.Core.Yolo;

public class YoloDetector : IDisposable
{
    private readonly Net _net;
    private readonly string[] _labels;
    private readonly int _imgsz;
    private readonly float _confThreshold;
    private readonly float _nmsThreshold;
    private readonly TilingOptions _tiling;

    public YoloDetector(
        string modelPath,
        string[] labels,
        int imgsz = 1280,
        float confThreshold = 0.5f,
        float nmsThreshold = 0.4f,
        TilingOptions? tiling = null)
    {
        _net = DnnInvoke.ReadNetFromONNX(modelPath);
        _net.SetPreferableBackend(Emgu.CV.Dnn.Backend.Cuda);
        _net.SetPreferableTarget(Target.Cuda);

        _labels = labels;
        _imgsz = imgsz;
        _confThreshold = confThreshold;
        _nmsThreshold = nmsThreshold;
        _tiling = tiling ?? new TilingOptions();
    }

    /// <summary>
    /// Головний метод детекції. Якщо тайлінг увімкнено — нарізає зображення,
    /// прогонює кожен тайл, збирає результати та зливає дублікати.
    /// </summary>
    public List<YoloPrediction> Detect(Mat frame)
    {
        if (!_tiling.Enabled)
        {
            // Звичайна детекція без тайлінгу
            return DetectSingle(frame, offsetX: 0, offsetY: 0, frame.Width, frame.Height);
        }

        return DetectWithTiling(frame);
    }

    /// <summary>
    /// Детекція з тайлінгом: нарізає зображення на перекриваючі фрагменти,
    /// прогонює кожен через модель, зсуває координати назад на повне зображення
    /// і прибирає дублікати через NMS.
    /// </summary>
    private List<YoloPrediction> DetectWithTiling(Mat frame)
    {
        int imgW = frame.Width;
        int imgH = frame.Height;
        int tileSize = _tiling.TileSize;
        int step = (int)(tileSize * (1.0f - _tiling.Overlap));

        var allPredictions = new List<YoloPrediction>();

        // ——— 1. Прогін по тайлах ———
        int tileCount = 0;

        for (int yStart = 0; yStart < imgH; yStart += step)
        {
            for (int xStart = 0; xStart < imgW; xStart += step)
            {
                // Коригуємо вікно на краях, щоб тайл завжди був повного розміру
                int xEnd = Math.Min(xStart + tileSize, imgW);
                int yEnd = Math.Min(yStart + tileSize, imgH);

                // Зсуваємо початок назад, якщо тайл вийшов за край
                int actualXStart = Math.Max(0, xEnd - tileSize);
                int actualYStart = Math.Max(0, yEnd - tileSize);

                int tileW = xEnd - actualXStart;
                int tileH = yEnd - actualYStart;

                // Вирізаємо тайл
                var roi = new Rectangle(actualXStart, actualYStart, tileW, tileH);
                using var tile = new Mat(frame, roi);

                // Детекція на тайлі з зсувом координат
                var tilePredictions = DetectSingle(
                    tile,
                    offsetX: actualXStart,
                    offsetY: actualYStart,
                    originalWidth: imgW,
                    originalHeight: imgH);

                allPredictions.AddRange(tilePredictions);
                tileCount++;
            }
        }

        // ——— 2. (Опційно) Прогін повного зображення для великих об'єктів ———
        if (_tiling.IncludeFullImage)
        {
            var fullPredictions = DetectSingle(frame, offsetX: 0, offsetY: 0, imgW, imgH);
            allPredictions.AddRange(fullPredictions);
        }

        // ——— 3. Глобальний NMS для злиття дублікатів між тайлами ———
        return ApplyGlobalNms(allPredictions, _tiling.MergeIouThreshold);
    }

    /// <summary>
    /// Детекція на одному зображенні (або тайлі).
    /// offsetX/offsetY — зсув тайлу відносно повного зображення (0,0 для повного).
    /// </summary>
    private List<YoloPrediction> DetectSingle(
        Mat frame,
        int offsetX,
        int offsetY,
        int originalWidth,
        int originalHeight)
    {
        using var blob = DnnInvoke.BlobFromImage(
            frame, 1.0 / 255.0, new Size(_imgsz, _imgsz),
            new MCvScalar(), swapRB: true, crop: false);

        _net.SetInput(blob);
        using var output = _net.Forward();

        float[,,] data = (float[,,])output.GetData();
        int numClasses = _labels.Length;
        int numBoxes = data.GetLength(2);

        var boxes = new List<Rectangle>();
        var scores = new List<float>();
        var classIds = new List<int>();

        // Масштаб: модель бачить imgsz×imgsz, а тайл має frame.Width × frame.Height
        float scaleX = (float)frame.Width / _imgsz;
        float scaleY = (float)frame.Height / _imgsz;

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

            if (maxScore >= _confThreshold)
            {
                float cx = data[0, 0, i];
                float cy = data[0, 1, i];
                float w = data[0, 2, i];
                float h = data[0, 3, i];

                // Координати в просторі тайлу → в просторі повного зображення
                int rectX = (int)((cx - w / 2) * scaleX) + offsetX;
                int rectY = (int)((cy - h / 2) * scaleY) + offsetY;
                int rectW = (int)(w * scaleX);
                int rectH = (int)(h * scaleY);

                // Clamp до меж повного зображення
                rectX = Math.Max(0, rectX);
                rectY = Math.Max(0, rectY);
                if (rectX + rectW > originalWidth) rectW = originalWidth - rectX;
                if (rectY + rectH > originalHeight) rectH = originalHeight - rectY;

                if (rectW > 0 && rectH > 0)
                {
                    boxes.Add(new Rectangle(rectX, rectY, rectW, rectH));
                    scores.Add(maxScore);
                    classIds.Add(maxClassId);
                }
            }
        }

        // NMS для одного тайлу/зображення
        if (boxes.Count == 0)
            return [];

        int[] indices = DnnInvoke.NMSBoxes(
            boxes.ToArray(), scores.ToArray(), _confThreshold, _nmsThreshold);

        var predictions = new List<YoloPrediction>();
        foreach (int idx in indices)
        {
            predictions.Add(new YoloPrediction
            {
                ClassName = _labels[classIds[idx]],
                Confidence = scores[idx],
                BoundingBox = boxes[idx]
            });
        }

        return predictions;
    }

    /// <summary>
    /// Глобальний NMS: зливає дублікати, що виникли на перетинах тайлів.
    /// Працює окремо для кожного класу.
    /// </summary>
    private List<YoloPrediction> ApplyGlobalNms(
        List<YoloPrediction> predictions, float iouThreshold)
    {
        if (predictions.Count == 0)
            return predictions;

        var result = new List<YoloPrediction>();

        // NMS окремо для кожного класу
        var byClass = predictions
            .GroupBy(p => p.ClassName)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (className, classPreds) in byClass)
        {
            var boxes = classPreds.Select(p => p.BoundingBox).ToArray();
            var scores = classPreds.Select(p => p.Confidence).ToArray();

            int[] indices = DnnInvoke.NMSBoxes(
                boxes, scores, _confThreshold, iouThreshold);

            foreach (int idx in indices)
            {
                result.Add(classPreds[idx]);
            }
        }

        return result;
    }

    public void Dispose() => _net?.Dispose();
}