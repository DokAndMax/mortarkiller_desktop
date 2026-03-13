// Core/Yolo/Benchmark/ModelInfo.cs
namespace PUBGVisionTest.Core.Yolo.Benchmark;

public class ModelInfo
{
    public string Path { get; set; } = "";
    public int TrainImgsz { get; set; }
    public int ExportImgsz { get; set; }
    public bool IsFp16 { get; set; }
    public bool IsSliced { get; set; }
    public YoloVersion YoloVersion { get; set; } = YoloVersion.V8_V11;
    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);
}