// Core/Yolo/Benchmark/QualityEvaluator.cs
using System.Drawing;

namespace PUBGVisionTest.Core.Yolo.Benchmark;

public static class QualityEvaluator
{
    public static List<MatchResult> Evaluate(
        List<YoloPrediction> detections,
        List<ExpectedObject> expected)
    {
        var matches = new List<MatchResult>();
        var usedDetections = new HashSet<int>();

        foreach (var exp in expected)
        {
            double bestDist = double.MaxValue;
            int bestIdx = -1;
            YoloPrediction? bestPred = null;

            for (int i = 0; i < detections.Count; i++)
            {
                if (usedDetections.Contains(i)) continue;

                var det = detections[i];

                if (!string.Equals(det.ClassName, exp.Label, StringComparison.OrdinalIgnoreCase))
                    continue;

                Point detPoint = exp.UseBottomTip ? det.BottomTip : det.Center;
                double dist = Distance(detPoint, exp.ExpectedPoint);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                    bestPred = det;
                }
            }

            bool isCorrect = bestPred != null && bestDist <= exp.ToleranceRadius;

            if (bestIdx >= 0 && isCorrect)
                usedDetections.Add(bestIdx);

            matches.Add(new MatchResult
            {
                Expected = exp,
                Matched = bestPred,
                DistancePx = bestPred != null ? bestDist : double.NaN,
                IsCorrect = isCorrect
            });
        }

        return matches;
    }

    public static int CountFalsePositives(
        List<YoloPrediction> detections,
        List<MatchResult> matches)
    {
        var matchedSet = matches
            .Where(m => m.IsCorrect && m.Matched != null)
            .Select(m => m.Matched)
            .ToHashSet();

        return detections.Count(d => !matchedSet.Contains(d));
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}