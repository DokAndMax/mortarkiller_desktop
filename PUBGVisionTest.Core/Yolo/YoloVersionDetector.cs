// Core/Yolo/YoloVersionDetector.cs
using Microsoft.ML.OnnxRuntime;

namespace PUBGVisionTest.Core.Yolo;

public static class YoloVersionDetector
{
    public static YoloVersion Detect(string onnxPath, int numLabels)
    {
        using var session = new InferenceSession(onnxPath);
        var outputMeta = session.OutputMetadata.First().Value;
        var dims = outputMeta.Dimensions;

        if (dims.Length == 3)
        {
            if (dims[2] == 6) return YoloVersion.V10;
            if (dims[1] == 4 + numLabels) return YoloVersion.V8_V11;
            if (dims[2] <= 6 && dims[1] > 100) return YoloVersion.V10;
        }

        return YoloVersion.V8_V11;
    }
}