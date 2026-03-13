// Core/Yolo/Benchmark/TestCase.cs
using System.Drawing;
using System.Text.Json.Serialization;

namespace PUBGVisionTest.Core.Yolo.Benchmark;

public class ExpectedObject
{
    public string Label { get; set; } = "";
    public int CenterX { get; set; }
    public int CenterY { get; set; }
    public int ToleranceRadius { get; set; } = 50;
    public bool UseBottomTip { get; set; } = false;

    [JsonIgnore]
    public Point ExpectedPoint => new(CenterX, CenterY);
}

public class TestCase
{
    public string ImagePath { get; set; } = "";
    public string ImageName => Path.GetFileNameWithoutExtension(ImagePath);
    public List<ExpectedObject> ExpectedObjects { get; set; } = new();
    public bool HasAnnotations => ExpectedObjects.Count > 0;
}

public class TestCaseAnnotation
{
    public List<ExpectedObject> ExpectedObjects { get; set; } = new();
}