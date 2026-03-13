// Core/Yolo/LetterboxInfo.cs
namespace PUBGVisionTest.Core.Yolo;

/// <summary>
/// Інформація про letterbox трансформацію для коректного
/// маппінгу координат назад до оригінального зображення.
/// </summary>
public class LetterboxInfo
{
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int ModelInputSize { get; set; }
    public float Scale { get; set; }
    public float PadX { get; set; }
    public float PadY { get; set; }
    public int ScaledWidth { get; set; }
    public int ScaledHeight { get; set; }

    public (float x, float y) ModelToSource(float mx, float my)
    {
        float sx = (mx - PadX) / Scale;
        float sy = (my - PadY) / Scale;
        return (sx, sy);
    }

    public (float w, float h) ModelToSourceSize(float mw, float mh)
    {
        return (mw / Scale, mh / Scale);
    }
}