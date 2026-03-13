// Core/Yolo/Detectors/DetectorBackend.cs
namespace PUBGVisionTest.Core.Yolo.Detectors;

public enum DetectorBackend
{
    EmguCpu,
    EmguGpu,
    EmguGpuFp16,
    OnnxCpu,
    OnnxGpu
}