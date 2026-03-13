// Core/Yolo/YoloVersion.cs
namespace PUBGVisionTest.Core.Yolo;

public enum YoloVersion
{
    V8_V11,  // [1, 4+classes, boxes] — потрібен NMS
    V10      // [1, boxes, 6] — вбудований NMS
}