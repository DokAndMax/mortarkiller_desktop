namespace PUBGVisionTest.Core.Yolo;

/// <summary>
/// Налаштування тайлінгу (нарізки зображення на фрагменти).
/// </summary>
public class TilingOptions
{
    /// <summary>Чи увімкнено тайлінг.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Розмір тайлу в пікселях (ширина = висота).</summary>
    public int TileSize { get; set; } = 640;

    /// <summary>Перекриття між тайлами (0.0 – 1.0). Типово 0.2 = 20%.</summary>
    public float Overlap { get; set; } = 0.2f;

    /// <summary>
    /// Чи додатково прогнати повне зображення (зменшене до imgsz).
    /// Це допомагає ловити великі об'єкти, які можуть бути розрізані тайлами.
    /// </summary>
    public bool IncludeFullImage { get; set; } = true;

    /// <summary>
    /// IoU поріг для злиття дублікатів між тайлами.
    /// </summary>
    public float MergeIouThreshold { get; set; } = 0.5f;
}