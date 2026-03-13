// Core/Yolo/YoloPrediction.cs
using System.Drawing;

namespace PUBGVisionTest.Core.Yolo;

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