// Detection/ProcessFrameSource.cs
using PUBGVisionTest.Core.Capture;
using System.Drawing;

namespace mortarkiller.Detection;

/// <summary>
/// Захоплює кадри з вікна процесу через ScreenshotHelper.
/// </summary>
public sealed class ProcessFrameSource : IFrameSource
{
    private readonly string _processName;

    public ProcessFrameSource(string processName)
    {
        _processName = processName;
    }

    public FrameCaptureResult Capture()
    {
        var (frame, mode) = ScreenshotHelper.CaptureSmart(_processName);
        return new FrameCaptureResult(frame, mode);
    }

    public void Dispose() { /* нічого не тримаємо */ }
}