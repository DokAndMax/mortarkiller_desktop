using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Point = System.Drawing.Point;

namespace PUBGVisionTest.Core.Helpers;

/// <summary>
/// Спільні утиліти для роботи з зображеннями.
/// </summary>
public static class ImageUtils
{
    /// <summary>
    /// Гарантує, що Mat має 3 канали (BGR).
    /// </summary>
    public static void EnsureBgr(ref Mat mat)
    {
        if (mat.NumberOfChannels == 4)
        {
            var bgr = new Mat();
            CvInvoke.CvtColor(mat, bgr, ColorConversion.Bgra2Bgr);
            mat.Dispose();
            mat = bgr;
        }
        else if (mat.NumberOfChannels == 1)
        {
            var bgr = new Mat();
            CvInvoke.CvtColor(mat, bgr, ColorConversion.Gray2Bgr);
            mat.Dispose();
            mat = bgr;
        }
    }

    /// <summary>
    /// Текст з обведенням (outline).
    /// </summary>
    public static void PutTextOutline(Mat img, string text, Point org,
        double scale, MCvScalar color, int thickness = 1)
    {
        CvInvoke.PutText(img, text, org, FontFace.HersheySimplex, scale,
            new MCvScalar(0, 0, 0), thickness + 2);
        CvInvoke.PutText(img, text, org, FontFace.HersheySimplex, scale,
            color, thickness);
    }
}