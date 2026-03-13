// Detection/IFrameSource.cs
using PUBGVisionTest.Core.Capture;
using System;
using System.Drawing;

namespace mortarkiller.Detection;

/// <summary>
/// Абстракція над захопленням кадрів — легко замінити на файл/відео для тестів.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>
    /// Захоплює кадр. Повертає null, якщо вікно мінімізоване/недоступне.
    /// Caller відповідає за Dispose bitmap'а.
    /// </summary>
    FrameCaptureResult Capture();
}

public sealed record FrameCaptureResult(
    Bitmap? Frame,
    WindowMode Mode);