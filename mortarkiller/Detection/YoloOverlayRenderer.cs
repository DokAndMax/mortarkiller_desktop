// Detection/YoloOverlayRenderer.cs
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using PUBGVisionTest.Core.Yolo;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace mortarkiller.Detection;

/// <summary>
/// Малює YOLO-предікшни на Mat — для дебагу та оверлеїв.
/// </summary>
public static class YoloOverlayRenderer
{
    public static void Draw(Mat img, List<YoloPrediction>? predictions)
    {
        if (predictions == null || predictions.Count == 0) return;

        foreach (var pred in predictions)
        {
            MCvScalar color;
            if (pred.ClassName.StartsWith("pin_", StringComparison.OrdinalIgnoreCase))
                color = new MCvScalar(0, 0, 255);
            else if (pred.ClassName.StartsWith("player_", StringComparison.OrdinalIgnoreCase))
                color = new MCvScalar(255, 200, 0);
            else
                color = new MCvScalar(200, 200, 200);

            CvInvoke.Rectangle(img, pred.BoundingBox, color, 2);

            Point anchor = pred.ClassName.StartsWith("pin_", StringComparison.OrdinalIgnoreCase)
                ? pred.BottomTip
                : pred.Center;
            CvInvoke.Circle(img, anchor, 4, color, -1);

            string label = $"{pred.ClassName} {pred.Confidence:F2}";
            var textOrg = new Point(pred.BoundingBox.X,
                Math.Max(15, pred.BoundingBox.Y - 5));
            CvInvoke.PutText(img, label, textOrg, FontFace.HersheySimplex, 0.45,
                new MCvScalar(0, 0, 0), 2, LineType.AntiAlias);
            CvInvoke.PutText(img, label, textOrg, FontFace.HersheySimplex, 0.45,
                color, 1, LineType.AntiAlias);
        }
    }
}