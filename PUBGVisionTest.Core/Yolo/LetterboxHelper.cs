// Core/Yolo/LetterboxHelper.cs
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System.Drawing;

namespace PUBGVisionTest.Core.Yolo;

/// <summary>
/// Letterbox preprocessing — як робить Ultralytics при тренуванні.
/// Зберігає aspect ratio, додає сірий padding (114,114,114).
/// </summary>
public static class LetterboxHelper
{
    private static readonly MCvScalar PadColor = new(114, 114, 114);

    public static Mat Apply(Mat source, int modelInputSize, out LetterboxInfo info)
    {
        int srcW = source.Width;
        int srcH = source.Height;

        float scale = Math.Min(
            (float)modelInputSize / srcW,
            (float)modelInputSize / srcH);

        int scaledW = (int)Math.Round(srcW * scale);
        int scaledH = (int)Math.Round(srcH * scale);

        float padX = (modelInputSize - scaledW) / 2f;
        float padY = (modelInputSize - scaledH) / 2f;

        info = new LetterboxInfo
        {
            SourceWidth = srcW,
            SourceHeight = srcH,
            ModelInputSize = modelInputSize,
            Scale = scale,
            PadX = padX,
            PadY = padY,
            ScaledWidth = scaledW,
            ScaledHeight = scaledH
        };

        var resized = new Mat();
        CvInvoke.Resize(source, resized, new Size(scaledW, scaledH),
            interpolation: Inter.Linear);

        var letterboxed = new Mat(modelInputSize, modelInputSize,
            source.Depth, source.NumberOfChannels);
        letterboxed.SetTo(PadColor);

        int top = (int)Math.Round(padY);
        int left = (int)Math.Round(padX);

        var roi = new Rectangle(left, top, scaledW, scaledH);
        using var roiMat = new Mat(letterboxed, roi);
        resized.CopyTo(roiMat);
        resized.Dispose();

        return letterboxed;
    }

    public static Rectangle ModelBoxToSource(
        float cx, float cy, float w, float h,
        LetterboxInfo info)
    {
        float srcCx = (cx - info.PadX) / info.Scale;
        float srcCy = (cy - info.PadY) / info.Scale;
        float srcW = w / info.Scale;
        float srcH = h / info.Scale;

        int rectX = Math.Max(0, (int)(srcCx - srcW / 2));
        int rectY = Math.Max(0, (int)(srcCy - srcH / 2));
        int rectW = Math.Min((int)srcW, info.SourceWidth - rectX);
        int rectH = Math.Min((int)srcH, info.SourceHeight - rectY);

        return new Rectangle(rectX, rectY, Math.Max(0, rectW), Math.Max(0, rectH));
    }
}