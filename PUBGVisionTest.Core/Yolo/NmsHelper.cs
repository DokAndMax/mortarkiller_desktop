// Core/Yolo/NmsHelper.cs
using Emgu.CV.Dnn;
using System.Drawing;

namespace PUBGVisionTest.Core.Yolo;

/// <summary>
/// Спільні утиліти NMS для всіх детекторів.
/// </summary>
public static class NmsHelper
{
    /// <summary>
    /// Per-class NMS: окремо для кожного класу, щоб різні класи не придушували один одного.
    /// </summary>
    public static List<YoloPrediction> ApplyPerClassNms(
        List<Rectangle> boxes,
        List<float> scores,
        List<string> classNames,
        float nmsThreshold)
    {
        var result = new List<YoloPrediction>();

        var byClass = new Dictionary<string, List<int>>();
        for (int i = 0; i < boxes.Count; i++)
        {
            if (!byClass.ContainsKey(classNames[i]))
                byClass[classNames[i]] = new List<int>();
            byClass[classNames[i]].Add(i);
        }

        foreach (var kvp in byClass)
        {
            var indices = kvp.Value;
            var classBoxes = indices.Select(i => boxes[i]).ToArray();
            var classScores = indices.Select(i => scores[i]).ToArray();

            int[] nmsIndices = DnnInvoke.NMSBoxes(classBoxes, classScores, 0f, nmsThreshold);

            foreach (int ni in nmsIndices)
            {
                int originalIdx = indices[ni];
                result.Add(new YoloPrediction
                {
                    ClassName = classNames[originalIdx],
                    Confidence = scores[originalIdx],
                    BoundingBox = boxes[originalIdx]
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Стандартний NMS (без Emgu DnnInvoke, для OnnxRuntime бекенду).
    /// </summary>
    public static List<int> NonMaxSuppression(
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

                var intersection = Rectangle.Intersect(boxes[i], boxes[j]);
                if (intersection.IsEmpty) continue;

                float interArea = intersection.Width * intersection.Height;
                float unionArea = (float)(boxes[i].Width * boxes[i].Height)
                    + (boxes[j].Width * boxes[j].Height) - interArea;

                if (unionArea > 0 && interArea / unionArea > nmsThreshold)
                    suppressed.Add(j);
            }
        }

        return result;
    }
}