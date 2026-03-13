// Core/Yolo/TilingHelper.cs
using System.Drawing;

namespace PUBGVisionTest.Core.Yolo;

/// <summary>
/// Генерує сітку тайлів для sliced inference.
/// </summary>
public static class TilingHelper
{
    public static List<Rectangle> GenerateTiles(
        int imageWidth, int imageHeight,
        int sliceSize, float overlap)
    {
        var tiles = new List<Rectangle>();
        int step = Math.Max(1, (int)(sliceSize * (1.0f - overlap)));

        for (int y = 0; y < imageHeight; y += step)
        {
            for (int x = 0; x < imageWidth; x += step)
            {
                int tileX = Math.Min(x, Math.Max(0, imageWidth - sliceSize));
                int tileY = Math.Min(y, Math.Max(0, imageHeight - sliceSize));
                int tileW = Math.Min(sliceSize, imageWidth - tileX);
                int tileH = Math.Min(sliceSize, imageHeight - tileY);

                var tile = new Rectangle(tileX, tileY, tileW, tileH);

                if (!tiles.Any(t => t == tile))
                    tiles.Add(tile);
            }

            if (imageHeight <= sliceSize) break;
        }

        return tiles;
    }

    public static bool NeedsTiling(int imageWidth, int imageHeight, int sliceSize)
    {
        return imageWidth > sliceSize || imageHeight > sliceSize;
    }
}