// Program.cs
using Emgu.CV;
using Emgu.CV.Cuda;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using mortarkiller.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace mortarkiller;

public enum ColorName
{ Green, Orange, Yellow, Blue }

// ------------------- Enums / basic types -------------------
public enum MarkerType
{ Player, Pin }

// ------------------- Mask & color helpers -------------------
public static class ColorOps
{
    public static Mat CreateHueOnlyMask(Mat hsv, double centerH, int dHPlus, int sMin, int vMin)
    {
        int lowH = (int)Math.Round(centerH) - dHPlus;
        int highH = (int)Math.Round(centerH) + dHPlus;

        var maskH = new Mat(hsv.Size, DepthType.Cv8U, 1);
        if (lowH < 0)
        {
            using var m1 = new Mat(); using var m2 = new Mat();
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(0, 0, 0)), new ScalarArray(new MCvScalar(highH, 255, 255)), m1);
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(180 + lowH, 0, 0)), new ScalarArray(new MCvScalar(180, 255, 255)), m2);
            CvInvoke.BitwiseOr(m1, m2, maskH);
        }
        else if (highH > 180)
        {
            using var m1 = new Mat(); using var m2 = new Mat();
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(lowH, 0, 0)), new ScalarArray(new MCvScalar(180, 255, 255)), m1);
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(0, 0, 0)), new ScalarArray(new MCvScalar(highH - 180, 255, 255)), m2);
            CvInvoke.BitwiseOr(m1, m2, maskH);
        }
        else
        {
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(lowH, 0, 0)), new ScalarArray(new MCvScalar(highH, 255, 255)), maskH);
        }

        using var sMask = new Mat(); using var vMask = new Mat();
        CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(0, sMin, 0)), new ScalarArray(new MCvScalar(180, 255, 255)), sMask);
        CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(0, 0, vMin)), new ScalarArray(new MCvScalar(180, 255, 255)), vMask);
        var hv = new Mat();
        CvInvoke.BitwiseAnd(maskH, sMask, hv);
        CvInvoke.BitwiseAnd(hv, vMask, hv);
        maskH.Dispose();
        return hv;
    }

    public static Mat CreateLabDistanceMask(Mat lab32f, MCvScalar centerLab, int thr)
    {
        using var center = new Mat(lab32f.Size, DepthType.Cv32F, 3);
        center.SetTo(centerLab);
        var diff = new Mat();
        CvInvoke.AbsDiff(lab32f, center, diff);
        var ch = diff.Split();
        var sq0 = new Mat(); var sq1 = new Mat(); var sq2 = new Mat();
        CvInvoke.Multiply(ch[0], ch[0], sq0);
        CvInvoke.Multiply(ch[1], ch[1], sq1);
        CvInvoke.Multiply(ch[2], ch[2], sq2);
        var sum = new Mat();
        CvInvoke.Add(sq0, sq1, sum);
        CvInvoke.Add(sum, sq2, sum);
        double thr2 = thr * thr;
        var mask = new Mat();
        CvInvoke.Threshold(sum, mask, thr2, 255, ThresholdType.BinaryInv);
        mask.ConvertTo(mask, DepthType.Cv8U);
        foreach (var m in ch) m.Dispose();
        sq0.Dispose(); sq1.Dispose(); sq2.Dispose(); sum.Dispose();
        return mask;
    }

    public static Mat CreateMask(Mat hsv, HSV center, int dH, int dS, int dV)
    {
        int Hc = (int)Math.Round(center.H);
        int Sc = (int)Math.Round(center.S);
        int Vc = (int)Math.Round(center.V);

        int lowH = Hc - dH;
        int highH = Hc + dH;
        int lowS = Utils.ClampInt(Sc - dS, 0, 255);
        int highS = Utils.ClampInt(Sc + dS, 0, 255);
        int lowV = Utils.ClampInt(Vc - dV, 0, 255);
        int highV = Utils.ClampInt(Vc + dV, 0, 255);

        var mask = new Mat(hsv.Size, DepthType.Cv8U, 1);
        if (lowH < 0)
        {
            using var m1 = new Mat();
            using var m2 = new Mat();
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(0, lowS, lowV)), new ScalarArray(new MCvScalar(highH, highS, highV)), m1);
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(180 + lowH, lowS, lowV)), new ScalarArray(new MCvScalar(180, highS, highV)), m2);
            CvInvoke.BitwiseOr(m1, m2, mask);
        }
        else if (highH > 180)
        {
            using var m1 = new Mat();
            using var m2 = new Mat();
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(lowH, lowS, lowV)), new ScalarArray(new MCvScalar(180, highS, highV)), m1);
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(0, lowS, lowV)), new ScalarArray(new MCvScalar(highH - 180, highS, highV)), m2);
            CvInvoke.BitwiseOr(m1, m2, mask);
        }
        else
        {
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(lowH, lowS, lowV)), new ScalarArray(new MCvScalar(highH, highS, highV)), mask);
        }
        return mask;
    }

    public static MCvScalar HsvCenterToLabScalar(HSV c)
    {
        using var hsv1 = new Mat(1, 1, DepthType.Cv8U, 3);
        hsv1.SetTo(new MCvScalar(c.H, c.S, c.V));
        using var bgr1 = new Mat();
        using var lab1 = new Mat();
        CvInvoke.CvtColor(hsv1, bgr1, ColorConversion.Hsv2Bgr);
        CvInvoke.CvtColor(bgr1, lab1, ColorConversion.Bgr2Lab);
        var d = (byte[,,])lab1.GetData();
        return new MCvScalar(d[0, 0, 0], d[0, 0, 1], d[0, 0, 2]);
    }

    public static void MorphAndBlurInPlace(Mat mask, int blurK, int openIter, int closeIter, int kernelSize = 3)
    {
        if (blurK >= 3 && blurK % 2 == 1)
            CvInvoke.GaussianBlur(mask, mask, new Size(blurK, blurK), 0);

        using var k3 = CvInvoke.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3), new Point(-1, -1));
        for (int i = 0; i < openIter; i++)
            CvInvoke.MorphologyEx(mask, mask, MorphOp.Open, k3, new Point(-1, -1), 1, BorderType.Reflect, default);
        for (int i = 0; i < closeIter; i++)
            CvInvoke.MorphologyEx(mask, mask, MorphOp.Close, k3, new Point(-1, -1), 1, BorderType.Reflect, default);

        if (kernelSize >= 5 && kernelSize % 2 == 1)
        {
            using var kL = CvInvoke.GetStructuringElement(MorphShapes.Ellipse, new Size(kernelSize, kernelSize), new Point(-1, -1));
            CvInvoke.MorphologyEx(mask, mask, MorphOp.Close, kL, new Point(-1, -1), 1, BorderType.Reflect, default);
        }
    }
}

public static class PreparedImageFactory
{
    public static PreparedImage FromBitmap(Bitmap bmp)
    {
        // Безпечно клонувати, щоб не залежати від життєвого циклу Image<>
        using var img = bmp.ToImage<Bgr, byte>();
        var bgr = img.Mat.Clone();

        var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);

        var lab = new Mat();
        CvInvoke.CvtColor(bgr, lab, ColorConversion.Bgr2Lab);

        var lab32f = new Mat();
        lab.ConvertTo(lab32f, DepthType.Cv32F);

        return new PreparedImage
        {
            Path = "live",
            Bgr = bgr,
            Hsv = hsv,
            Lab32F = lab32f,
            Width = bgr.Width,
            Height = bgr.Height
        };
    }
}

// ------------------- Utils -------------------
public static class Utils
{
    public static double AngleDeg(Point a, Point b, Point c)
    {
        double v1x = a.X - b.X, v1y = a.Y - b.Y;
        double v2x = c.X - b.X, v2y = c.Y - b.Y;
        double dot = v1x * v2x + v1y * v2y;
        double n1 = Math.Sqrt(v1x * v1x + v1y * v1y);
        double n2 = Math.Sqrt(v2x * v2x + v2y * v2y);
        if (n1 < 1e-6 || n2 < 1e-6) return 180;
        double cos = Clamp(dot / (n1 * n2), -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    public static MCvScalar BgrScalarFromColorName(ColorName cn)
    {
        return cn switch
        {
            ColorName.Green => new MCvScalar(68, 181, 73),
            ColorName.Orange => new MCvScalar(38, 98, 218),
            ColorName.Yellow => new MCvScalar(17, 229, 233),
            ColorName.Blue => new MCvScalar(217, 160, 58),
            _ => new MCvScalar(255, 255, 255)
        };
    }

    public static IEnumerable<byte> BytesOf(Mat m)
    {
        var a = m.GetData();
        if (a is byte[] a1) return a1;
        if (a is byte[,] a2) return a2.Cast<byte>();
        if (a is byte[,,] a3) return a3.Cast<byte>();
        throw new NotSupportedException($"Unsupported Mat data type: {a?.GetType().FullName}");
    }

    public static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    public static int ClampInt(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

    public static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static HSV HsvFromHex(string hex)
    {
        var c = ColorTranslator.FromHtml(hex);
        using var bgr = new Mat(1, 1, DepthType.Cv8U, 3);
        bgr.SetTo(new MCvScalar(c.B, c.G, c.R));
        using var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
        var data = (byte[,,])hsv.GetData();
        byte h = data[0, 0, 0];
        byte s = data[0, 0, 1];
        byte v = data[0, 0, 2];
        return new HSV(h, s, v);
    }

    public static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(v => v).ToArray();
        if (a.Length == 0) return 0;
        return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
    }

    public static double MedianCircular(List<double> hs)
    {
        if (hs.Count == 0) return 0;
        double sumX = 0, sumY = 0;
        foreach (var h in hs)
        {
            double ang = h / 180.0 * 2.0 * Math.PI;
            sumX += Math.Cos(ang);
            sumY += Math.Sin(ang);
        }
        double angMean = Math.Atan2(sumY, sumX);
        if (angMean < 0) angMean += 2 * Math.PI;
        double hMean = angMean * 180.0 / (2 * Math.PI);
        return hMean;
    }
}

public class BaseColorSpec
{
    public Dictionary<ColorName, HSV> PinCenters = [];
    public Dictionary<ColorName, HSV> PlayerCenters = [];
}

public class ConfigRoot
{
    public List<DatasetItem> items { get; set; } = [];
    public int stage1_iterations { get; set; } = 250;
    public int stage2_iterations { get; set; } = 120;
    public int threads { get; set; } = 0;
}

public class HSV(double h, double s, double v)
{
    public double H = h, S = s, V = v; // OpenCV HSV: H in [0..180], S,V in [0..255]

    public HSV Clone() => new(H, S, V);
}

// ------------------- Diagnostics -------------------
public class ImageDiagnostics
{
    public string path { get; set; } = "";
    public List<double> pinCandidateApexAngles { get; set; } = [];
    public List<double> pinCandidateAreas { get; set; } = [];
    public List<double> pinCandidateHeights { get; set; } = [];
    public Dictionary<string, int> pinMaskWhitePxByColor { get; set; } = [];
    public List<double> playerCandidateAreas { get; set; } = [];
    public List<double> playerCandidateRadii { get; set; } = [];
}

public class LiveDetections
{
    public int height { get; set; }
    public List<MarkerCoord> markers { get; set; } = [];
    public DateTime timestamp { get; set; } = DateTime.UtcNow;
    public int width { get; set; }
}

public class LiveMode(PlayerDetector players)
{
    private readonly PlayerDetector _players = players;

    public LiveDetections Run(Bitmap image, PlayerParams p)
    => Run(image, p, []);

    public LiveDetections Run(Bitmap image, PlayerParams p, params ColorName[] colors)
    {
        bool hasCuda = false;
        try { hasCuda = CudaInvoke.HasCuda; } catch { hasCuda = false; }

        using var imgBgr = image.ToImage<Bgr, byte>();
        using var bgr = imgBgr.Mat.Clone(); // важливо скопіювати
        using var hsv = new Mat();
        if (hasCuda)
        {
            using var gBgr = new GpuMat();
            using var gHsv = new GpuMat();
            gBgr.Upload(bgr);
            CudaInvoke.CvtColor(gBgr, gHsv, ColorConversion.Bgr2Hsv);
            gHsv.Download(hsv);
        }
        else
        {
            CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
        }

        var prepared = new PreparedImage
        {
            Bgr = bgr,
            Hsv = hsv,
            Width = bgr.Width,
            Height = bgr.Height
        };

        var preds = _players.Detect(prepared, p, out double avgR, new ImageDiagnostics(), colors);

        return new LiveDetections
        {
            timestamp = DateTime.UtcNow,
            width = prepared.Width,
            height = prepared.Height,
            markers = [.. preds.Select(pr => new MarkerCoord
                {
                    Type = pr.Type,
                    Color = pr.Color,
                    X = pr.Point.X,
                    Y = pr.Point.Y,
                    Score = pr.Score
                })]
        };
    }
}

public class MarkerCoord
{
    public ColorName Color { get; set; }
    public double Score { get; set; }
    public MarkerType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

// ------------------- Detectors -------------------
public class PlayerDetector(BaseColorSpec baseColors)
{
    private readonly BaseColorSpec _base = baseColors;

    public List<Prediction> Detect(PreparedImage img, PlayerParams p, out double avgRadius, ImageDiagnostics diag)
    {
        return Detect(img, p, out avgRadius, diag, Array.Empty<ColorName>());
    }

    public List<Prediction> Detect(PreparedImage img, PlayerParams p, out double avgRadius, ImageDiagnostics diag, params ColorName[] colors)
    {
        var colorsToUse = colors != null && colors.Length > 0
            ? colors.Distinct()
            : Enum.GetValues(typeof(ColorName)).Cast<ColorName>();

        var preds = new List<Prediction>();
        var radii = new List<double>();

        foreach (var col in colorsToUse)
        {
            if (!_base.PlayerCenters.TryGetValue(col, out var center)) continue;

            var c = center.Clone();
            c.H = (c.H + p.HueShift[col] + 180) % 180;

            using var mask = ColorOps.CreateMask(img.Hsv, c, p.dH, p.dS, p.dV);
            ColorOps.MorphAndBlurInPlace(mask, p.blur, p.open, p.close, 3);

            var best = DetectBestCircleByContour(mask, col, p, diag, out double rBest);
            if (best != null)
            {
                preds.Add(best);
                if (rBest > 0) radii.Add(rBest);
            }
            else if (p.useHough)
            {
                var hBest = DetectByHough(img, mask, col, p, radii);
                if (hBest != null) preds.Add(hBest);
            }
        }

        avgRadius = radii.Count > 0 ? radii.Average() : 0;
        return preds;
    }

    private Prediction DetectBestCircleByContour(Mat mask, ColorName col, PlayerParams p, ImageDiagnostics diag, out double bestR)
    {
        bestR = 0;
        using var contours = new VectorOfVectorOfPoint();
        CvInvoke.FindContours(mask, contours, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);

        Prediction best = null; double bestScore = double.MinValue;

        for (int i = 0; i < contours.Size; i++)
        {
            using var cnt = contours[i];
            double area = CvInvoke.ContourArea(cnt);
            if (area < p.minArea || area > p.maxArea) continue;
            var rect = CvInvoke.BoundingRectangle(cnt);
            double peri = CvInvoke.ArcLength(cnt, true);
            if (peri <= 1) continue;
            double circ = 4.0 * Math.PI * area / (peri * peri);
            if (circ < p.minCircularity) continue;

            var m = CvInvoke.Moments(cnt);
            if (Math.Abs(m.M00) < 1e-6) continue;
            int cx = (int)Math.Round(m.M10 / m.M00);
            int cy = (int)Math.Round(m.M01 / m.M00);
            double r = Math.Sqrt(area / Math.PI);
            double score = circ * Math.Sqrt(area);

            diag.playerCandidateAreas.Add(area);
            diag.playerCandidateRadii.Add(r);

            if (score > bestScore)
            {
                bestScore = score;
                best = new Prediction { Type = MarkerType.Player, Color = col, Point = new Point(cx, cy), Area = area, BBox = rect, Circularity = circ, Score = score };
                bestR = r;
            }
        }

        if (best != null && best.Score >= p.detectionScoreMin) return best;
        return null;
    }

    private Prediction DetectByHough(PreparedImage img, Mat colorMask, ColorName col, PlayerParams p, List<double> radii)
    {
        using var gray = new Mat();
        CvInvoke.CvtColor(img.Bgr, gray, ColorConversion.Bgr2Gray);
        using var masked = new Mat();
        CvInvoke.BitwiseAnd(gray, colorMask, masked);

        int avgR = (int)Math.Round(radii.Count > 0 ? radii.Average() : 11.0);
        int minR = Math.Max(6, avgR + p.hough_minR_pad);
        int maxR = Math.Min(40, avgR + p.hough_maxR_pad);

        CircleF[] circles = CvInvoke.HoughCircles(masked, HoughModes.Gradient,
            p.hough_dp, p.hough_minDist, p.hough_param1, p.hough_param2, minR, maxR);

        if (circles != null && circles.Length > 0)
        {
            var c = circles.OrderByDescending(cc => cc.Radius).First();
            double area = Math.PI * c.Radius * c.Radius;
            if (area < p.minArea || area > p.maxArea) return null;

            return new Prediction
            {
                Type = MarkerType.Player,
                Color = col,
                Point = new Point((int)Math.Round(c.Center.X), (int)Math.Round(c.Center.Y)),
                Area = area,
                Score = c.Radius
            };
        }
        return null;
    }
}

// ------------------- Params -------------------
public class PlayerParams
{
    public int blur, open, close;
    public double detectionScoreMin;
    public int dH, dS, dV;
    public double hough_dp = 1.2;

    public int hough_maxR_pad = +6;

    public double hough_minDist = 18;

    public int hough_minR_pad = -4;

    public double hough_param1 = 110;

    public double hough_param2 = 16;

    public Dictionary<ColorName, int> HueShift = new()
    { { ColorName.Green, 0 }, { ColorName.Orange, 0 }, { ColorName.Yellow, 0 }, { ColorName.Blue, 0 } };

    public double minArea, maxArea;
    public double minCircularity;

    // Hough fallback
    public bool useHough = true;

    public BaseColorSpec CalibratedColors { get; set; } = new();

    public PlayerParams Clone()
    {
        var cloned = new PlayerParams
        {
            dH = dH,
            dS = dS,
            dV = dV,
            HueShift = new Dictionary<ColorName, int>(HueShift),
            blur = blur,
            open = open,
            close = close,
            minCircularity = minCircularity,
            minArea = minArea,
            maxArea = maxArea,
            detectionScoreMin = detectionScoreMin,
            useHough = useHough,
            hough_dp = hough_dp,
            hough_minDist = hough_minDist,
            hough_param1 = hough_param1,
            hough_param2 = hough_param2,
            hough_minR_pad = hough_minR_pad,
            hough_maxR_pad = hough_maxR_pad
        };

        // Додано: глибока копія кольорів (щоб не посилатися на внутр. стани)
        if (CalibratedColors != null)
        {
            var bc = new BaseColorSpec
            {
                PlayerCenters = CalibratedColors.PlayerCenters?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) ?? new(),
                PinCenters = CalibratedColors.PinCenters?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) ?? new()
            };
            cloned.CalibratedColors = bc;
        }

        return cloned;
    }

    public override string ToString()
    {
        string hs = string.Join(",", HueShift.Select(kv => $"{kv.Key}:{kv.Value}"));
        return $"P: HSV d=({dH},{dS},{dV}), hueShift[{hs}], morph(blur={blur},open={open},close={close}), circ>={minCircularity:F2}, area=[{minArea:F0},{maxArea:F0}], score>={detectionScoreMin:F2}, Hough(dp={hough_dp:F1},p1={hough_param1:F0},p2={hough_param2:F0},Rpad=[{hough_minR_pad},{hough_maxR_pad}])";
    }
}

public class PlayersDiagnosticsRoot
{
    public double avg_player_error_px { get; set; }
    public double avg_player_radius { get; set; }
    public PlayerParams best_params { get; set; } = new();
    public List<ImageDiagnostics> per_image { get; set; } = [];
}

// ------------------- Prediction -------------------
public class Prediction
{
    public double ApexAngle;
    public double Area;
    public Rectangle BBox;
    public double Circularity;
    public ColorName Color;
    public Point Point;
    public double Score;
    public MarkerType Type;
    // players
    // pins
}

// ------------------- Prepared image -------------------
public class PreparedImage
{
    public Mat Bgr = new();
    public List<Point> GTPins = [];
    public List<Point> GTPlayers = [];
    public Mat Hsv = new();
    public Mat Lab32F = new();
    public string Path = "";
    public int Width, Height;
}

// ------------------- Trainer -------------------
public class Trainer
{
    private readonly BaseColorSpec _baseColors;
    private readonly ConfigRoot _cfg;
    private readonly bool _hasCuda;
    private readonly List<PreparedImage> _images = [];
    private readonly PlayerDetector _playerDetector;
    private readonly int _threads;

    public Trainer(ConfigRoot cfg)
    {
        _cfg = cfg;
        _hasCuda = CudaInvoke.HasCuda;
        _threads = cfg.threads <= 0 ? Environment.ProcessorCount : cfg.threads;

        _baseColors = new BaseColorSpec
        {
            PlayerCenters = new Dictionary<ColorName, HSV>
            {
                { ColorName.Green, Utils.HsvFromHex("#52a44f") },
                { ColorName.Orange, Utils.HsvFromHex("#dd7e44") },
                { ColorName.Yellow, Utils.HsvFromHex("#d0cb28") },
                { ColorName.Blue, Utils.HsvFromHex("#60a6c2") },
            },
            PinCenters = new Dictionary<ColorName, HSV>
            {
                { ColorName.Green, Utils.HsvFromHex("#44b549") },
                { ColorName.Orange, Utils.HsvFromHex("#da6226") },
                { ColorName.Yellow, Utils.HsvFromHex("#e9e511") },
                { ColorName.Blue, Utils.HsvFromHex("#3aa0d9") },
            }
        };

        _playerDetector = new PlayerDetector(_baseColors);
    }

    public PlayerDetector Players => _playerDetector;

    public void CalibrateColorsFromGT()
    {
        var playerSamples = Enum.GetValues(typeof(ColorName)).Cast<ColorName>().ToDictionary(c => c, c => new List<HSV>());
        var pinSamples = Enum.GetValues(typeof(ColorName)).Cast<ColorName>().ToDictionary(c => c, c => new List<HSV>());

        foreach (var img in _images)
        {
            foreach (var pt in img.GTPlayers)
            {
                var hsvMed = SampleMedianHSV(img.Hsv, pt, 5, 5);
                var nearest = NearestByHue(hsvMed, _baseColors.PlayerCenters);
                playerSamples[nearest].Add(hsvMed);
            }
            foreach (var pt in img.GTPins)
            {
                int yTop = Math.Max(0, pt.Y - 28);
                var roiPt = new Point(pt.X, (pt.Y + yTop) / 2);
                var hsvMed = SampleMedianHSV(img.Hsv, roiPt, 9, 15);
                var nearest = NearestByHue(hsvMed, _baseColors.PinCenters);
                pinSamples[nearest].Add(hsvMed);
            }
        }

        foreach (var c in Enum.GetValues(typeof(ColorName)).Cast<ColorName>())
        {
            if (playerSamples[c].Count > 0)
                _baseColors.PlayerCenters[c] = RobustHSVMedian(playerSamples[c], _baseColors.PlayerCenters[c].H);
            if (pinSamples[c].Count > 0)
                _baseColors.PinCenters[c] = RobustHSVMedian(pinSamples[c], _baseColors.PinCenters[c].H);
        }

        Console.WriteLine("Calibrated color centers (HSV):");
        foreach (var c in Enum.GetValues(typeof(ColorName)).Cast<ColorName>())
        {
            var p = _baseColors.PlayerCenters[c];
            var pin = _baseColors.PinCenters[c];
            Console.WriteLine($"  {c}: Player H={p.H:F1} S={p.S:F1} V={p.V:F1} | Pin H={pin.H:F1} S={pin.S:F1} V={pin.V:F1}");
        }

        static HSV SampleMedianHSV(Mat hsv, Point center, int rx, int ry)
        {
            int x0 = Math.Max(0, center.X - rx), x1 = Math.Min(hsv.Width - 1, center.X + rx);
            int y0 = Math.Max(0, center.Y - ry), y1 = Math.Min(hsv.Height - 1, center.Y + ry);
            using var roi = new Mat(hsv, new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
            var ch = roi.Split();
            var hVals = Utils.BytesOf(ch[0]).Select(b => (double)b).ToList();
            var sVals = Utils.BytesOf(ch[1]).Select(b => (double)b).ToList();
            var vVals = Utils.BytesOf(ch[2]).Select(b => (double)b).ToList();
            foreach (var m in ch) m.Dispose();
            double medH = Utils.MedianCircular(hVals);
            double medS = Utils.Median(sVals);
            double medV = Utils.Median(vVals);
            return new HSV(medH, medS, medV);
        }

        static ColorName NearestByHue(HSV sample, Dictionary<ColorName, HSV> centers)
        {
            double best = 1e9; ColorName bestC = centers.Keys.First();
            foreach (var kv in centers)
            {
                double d = Math.Abs(sample.H - kv.Value.H);
                d = Math.Min(d, 180 - d);
                if (d < best) { best = d; bestC = kv.Key; }
            }
            return bestC;
        }

        static HSV RobustHSVMedian(List<HSV> xs, double refH)
        {
            if (xs.Count == 0) return new HSV(refH, 128, 128);
            var Hs = xs.Select(z => z.H).ToList();
            var Ss = xs.Select(z => z.S).ToList();
            var Vs = xs.Select(z => z.V).ToList();
            return new HSV(Utils.MedianCircular(Hs), Utils.Median(Ss), Utils.Median(Vs));
        }
    }

    public BaseColorSpec GetCalibratedColorsCopy()
    {
        var bc = new BaseColorSpec
        {
            PlayerCenters = _baseColors.PlayerCenters.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
            PinCenters = _baseColors.PinCenters.ToDictionary(kv => kv.Key, kv => kv.Value.Clone())
        };
        return bc;
    }

    public void LoadImages()
    {
        Console.WriteLine($"CUDA available: {_hasCuda}");
        foreach (var it in _cfg.items)
        {
            if (!File.Exists(it.Path))
            {
                Console.WriteLine($"Missing file: {it.Path}");
                continue;
            }

            var bgr = new Mat(it.Path, ImreadModes.ColorBgr);
            var hsv = new Mat();
            var lab = new Mat();
            if (_hasCuda)
            {
                using var gBgr = new GpuMat();
                using var gHsv = new GpuMat();
                gBgr.Upload(bgr);
                CudaInvoke.CvtColor(gBgr, gHsv, ColorConversion.Bgr2Hsv);
                gHsv.Download(hsv);
            }
            else
            {
                CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
            }
            CvInvoke.CvtColor(bgr, lab, ColorConversion.Bgr2Lab);
            var lab32f = new Mat();
            lab.ConvertTo(lab32f, DepthType.Cv32F);

            var prepared = new PreparedImage
            {
                Path = it.Path,
                Bgr = bgr,
                Hsv = hsv,
                Lab32F = lab32f,
                Width = bgr.Width,
                Height = bgr.Height,
                GTPlayers = it.GtPlayers.Select(p => new Point(p.X, p.Y)).ToList(),
                GTPins = it.GtPins.Select(p => new Point(p.X, p.Y)).ToList()
            };
            _images.Add(prepared);
        }
        Console.WriteLine($"Loaded {_images.Count} image(s).");
    }

    public (PlayerParams best, double avgR) TrainPlayers(int N1, int N2)
    {
        Console.WriteLine("Train Players: Stage 1 (random search)...");
        var rng = new Random(123);
        var sw = Stopwatch.StartNew();
        var bestList = new List<EvalResultPlayers>();

        for (int i = 1; i <= N1; i++)
        {
            var ps = RandomPlayerParams(rng);
            var eval = EvaluatePlayers(ps);
            bestList.Add(eval);
            bestList = bestList.OrderBy(e => e.Score).Take(8).ToList();

            if (i % Math.Max(1, N1 / 20) == 0 || i == N1)
            {
                double pct = 100.0 * i / N1;
                double itPerSec = i / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
                double eta = (N1 - i) / Math.Max(1e-6, itPerSec);
                Console.WriteLine($"  {pct,5:F1}%  i={i}/{N1}, bestScore={bestList[0].Score:F1}, ETA~{eta:F1}s | AvgErr P={bestList[0].AvgErr:F1}px, avgR={bestList[0].AvgRadius:F2}px");
            }
        }
        Console.WriteLine("Top Stage1 (Players):");
        Console.WriteLine(bestList[0].Params.ToString());

        Console.WriteLine("Train Players: Stage 2 (local refinement)...");
        var seeds = bestList.Select(b => b.Params).ToList();
        var globalBest = bestList[0];
        sw.Restart();

        for (int i = 1; i <= N2; i++)
        {
            var baseIdx = rng.Next(0, seeds.Count);
            var cand = MutatePlayer(seeds[baseIdx], rng, scale: 1.0);
            var eval = EvaluatePlayers(cand);
            if (eval.Score < globalBest.Score)
            {
                globalBest = eval;
                seeds[baseIdx] = cand;
            }
            if (i % Math.Max(1, N2 / 20) == 0 || i == N2)
            {
                double pct = 100.0 * i / N2;
                double itPerSec = i / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
                double eta = (N2 - i) / Math.Max(1e-6, itPerSec);
                Console.WriteLine($"  {pct,5:F1}%  i={i}/{N2}, bestScore={globalBest.Score:F1}, ETA~{eta:F1}s | AvgErr P={globalBest.AvgErr:F1}px, avgR={globalBest.AvgRadius:F2}px");
            }
        }
        Console.WriteLine("Best Players params:");
        Console.WriteLine(globalBest.Params.ToString());

        return (globalBest.Params, globalBest.AvgRadius);
    }

    // ------------------- Visualization & Diagnostics -------------------
    public void VisualizeAndSave(PlayerParams pPlayers)
    {
        Directory.CreateDirectory("output");
        Directory.CreateDirectory("output/overlays");

        var playersDiag = new PlayersDiagnosticsRoot { best_params = pPlayers.Clone() };

        var allPlayerR = new List<double>();
        var allPlayerErr = new List<double>();

        foreach (var img in _images)
        {
            var diP = new ImageDiagnostics { path = img.Path };
            var predsPlayers = _playerDetector.Detect(img, pPlayers, out double rImg, diP);
            playersDiag.per_image.Add(diP);
            if (rImg > 0) allPlayerR.Add(rImg);

            var pPredPts = predsPlayers.Select(x => x.Point).ToList();
            MatchAndScore(pPredPts, img.GTPlayers, out double avgPErr);
            allPlayerErr.Add(avgPErr);

            // Overlay
            var canvas = img.Bgr.Clone();

            // draw GT
            foreach (var gt in img.GTPlayers)
                CvInvoke.DrawMarker(canvas, gt, new MCvScalar(220, 220, 220), MarkerTypes.TiltedCross, 22, 2);
            foreach (var gt in img.GTPins)
                CvInvoke.DrawMarker(canvas, gt, new MCvScalar(220, 220, 220), MarkerTypes.Cross, 22, 2);

            // predictions
            foreach (var pr in predsPlayers)
            {
                var color = Utils.BgrScalarFromColorName(pr.Color);
                CvInvoke.Circle(canvas, pr.Point, 10, color, 2);
                CvInvoke.PutText(canvas, $"P {pr.Color} circ={pr.Circularity:F2}", new Point(pr.Point.X + 12, pr.Point.Y - 12),
                    FontFace.HersheySimplex, 0.5, color, 1);
            }

            CvInvoke.PutText(canvas, $"Err: P={avgPErr:F1}px, avgR={rImg:F1}px",
                new Point(20, 30), FontFace.HersheySimplex, 0.8, new MCvScalar(20, 230, 20), 2);

            var outPath = Path.Combine("output/overlays", Path.GetFileNameWithoutExtension(img.Path) + "_overlay.png");
            CvInvoke.Imwrite(outPath, canvas);
            Console.WriteLine($"Saved overlay: {outPath}");
        }

        playersDiag.avg_player_radius = allPlayerR.Count > 0 ? allPlayerR.Average() : 0;
        playersDiag.avg_player_error_px = allPlayerErr.Count > 0 ? allPlayerErr.Average() : 0;

        File.WriteAllText("output/diagnostics_players.json", JsonSerializer.Serialize(playersDiag, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        Console.WriteLine("Saved: output/diagnostics_players.json");
    }

    // ------------------- Matching -------------------
    private static double MatchAndScore(List<Point> preds, List<Point> gts, out double avgErrorPx, double missPenalty = 180.0, double fpPenalty = 140.0)
    {
        var remainingPreds = new List<Point>(preds);
        var remainingGts = new List<Point>(gts);
        double total = 0.0;
        var errors = new List<double>();

        while (remainingPreds.Count > 0 && remainingGts.Count > 0)
        {
            double best = double.MaxValue;
            int bi = -1, bj = -1;
            for (int i = 0; i < remainingPreds.Count; i++)
            {
                for (int j = 0; j < remainingGts.Count; j++)
                {
                    double d = Utils.Dist(remainingPreds[i], remainingGts[j]);
                    if (d < best) { best = d; bi = i; bj = j; }
                }
            }
            total += best;
            errors.Add(best);
            remainingPreds.RemoveAt(bi);
            remainingGts.RemoveAt(bj);
        }

        total += remainingGts.Count * missPenalty;
        total += remainingPreds.Count * fpPenalty;

        avgErrorPx = errors.Count > 0 ? errors.Average() :
            remainingGts.Count > 0 ? missPenalty : remainingPreds.Count > 0 ? fpPenalty : 0;
        return total;
    }

    private EvalResultPlayers EvaluatePlayers(PlayerParams p)
    {
        double total = 0;
        var errs = new List<double>();
        var radii = new List<double>();
        var po = new ParallelOptions { MaxDegreeOfParallelism = _threads };
        object lockObj = new();

        Parallel.ForEach(_images, po, img =>
        {
            var diag = new ImageDiagnostics { path = img.Path };
            var preds = _playerDetector.Detect(img, p, out double rImg, diag);
            var pts = preds.Select(pp => pp.Point).ToList();
            double avgErrImg;
            double s = MatchAndScore(pts, img.GTPlayers, out avgErrImg, missPenalty: 180, fpPenalty: 140);
            lock (lockObj)
            {
                total += s;
                errs.Add(avgErrImg);
                if (rImg > 0) radii.Add(rImg);
            }
        });

        return new EvalResultPlayers
        {
            Params = p.Clone(),
            Score = total,
            AvgErr = errs.Count > 0 ? errs.Average() : 0,
            AvgRadius = radii.Count > 0 ? radii.Average() : 0
        };
    }

    private PlayerParams MutatePlayer(PlayerParams src, Random rng, double scale = 1.0)
    {
        int Jit(int v, int j, int lo, int hi) => Utils.ClampInt(v + (int)Math.Round((rng.NextDouble() * 2 - 1) * j * scale), lo, hi);
        double JitD(double v, double j, double lo, double hi) => Utils.Clamp(v + (rng.NextDouble() * 2 - 1) * j * scale, lo, hi);

        var p = src.Clone();
        p.dH = Jit(p.dH, 3, 2, 30);
        p.dS = Jit(p.dS, 12, 10, 150);
        p.dV = Jit(p.dV, 12, 10, 150);
        p.blur = new[] { 3, 5, 7 }[(Array.IndexOf(new[] { 3, 5, 7 }, p.blur) + (rng.Next(0, 2) == 0 ? -1 : 1) + 3) % 3];
        p.open = Jit(p.open, 1, 0, 4);
        p.close = Jit(p.close, 1, 0, 4);
        p.minCircularity = JitD(p.minCircularity, 0.05, 0.5, 0.98);
        p.minArea = JitD(p.minArea, 120, 50, 10000);
        p.maxArea = JitD(p.maxArea, 140, 400, 20000);
        p.detectionScoreMin = JitD(p.detectionScoreMin, 2, 4, 40);
        foreach (ColorName c in Enum.GetValues(typeof(ColorName)))
            p.HueShift[c] = Jit(p.HueShift[c], 3, -20, 20);

        p.hough_dp = JitD(p.hough_dp, 0.1, 0.8, 2.0);
        p.hough_minDist = JitD(p.hough_minDist, 2, 10, 40);
        p.hough_param1 = JitD(p.hough_param1, 8, 50, 180);
        p.hough_param2 = JitD(p.hough_param2, 2, 8, 30);
        p.hough_minR_pad = Jit(p.hough_minR_pad, 2, -12, 0);
        p.hough_maxR_pad = Jit(p.hough_maxR_pad, 2, 2, 16);

        if (p.maxArea < p.minArea) (p.maxArea, p.minArea) = (p.minArea, p.maxArea);
        return p;
    }

    private PlayerParams RandomPlayerParams(Random rng)
    {
        int RandIn(int a, int b) => rng.Next(a, b + 1);
        double RandD(double a, double b) => a + rng.NextDouble() * (b - a);

        var p = new PlayerParams
        {
            dH = RandIn(10, 20),
            dS = RandIn(30, 120),
            dV = RandIn(30, 150),
            blur = new[] { 3, 5, 7 }[rng.Next(0, 3)],
            open = RandIn(0, 3),
            close = RandIn(0, 3),
            minCircularity = RandD(0.68, 0.92),
            minArea = RandD(200, 1000),
            maxArea = RandD(800, 6500),
            detectionScoreMin = RandD(8, 20),
            useHough = true,
            hough_dp = RandD(1.0, 1.5),
            hough_minDist = RandD(14, 28),
            hough_param1 = RandD(80, 140),
            hough_param2 = RandD(12, 22),
            hough_minR_pad = rng.Next(-8, -2),
            hough_maxR_pad = rng.Next(4, 12)
        };
        foreach (ColorName c in Enum.GetValues(typeof(ColorName)))
            p.HueShift[c] = rng.Next(-10, 11);
        if (p.maxArea < p.minArea) (p.maxArea, p.minArea) = (p.minArea, p.maxArea);
        return p;
    }

    // ------------------- Training: Players -------------------
    private class EvalResultPlayers
    {
        public double AvgErr;
        public double AvgRadius;
        public PlayerParams Params = new();
        public double Score;
    }
}

// ------------------- Program -------------------
public static class ProgramMark
{
    public static int Run(string[] args)
    {
        try
        {
            string cfgPath = args != null && args.Length > 0 ? args[0] : "config.json";
            if (!File.Exists(cfgPath))
            {
                Console.WriteLine($"Config not found: {cfgPath}");
                return 1;
            }
            var cfgJson = File.ReadAllText(cfgPath);
            var cfg = JsonSerializer.Deserialize<ConfigRoot>(cfgJson);
            if (cfg == null || cfg.items == null || cfg.items.Count == 0)
            {
                Console.WriteLine("Empty config or no items.");
                return 1;
            }

            var trainer = new Trainer(cfg);
            trainer.LoadImages();
            trainer.CalibrateColorsFromGT();

            int N1 = Math.Max(10, cfg.stage1_iterations);
            int N2 = Math.Max(10, cfg.stage2_iterations);

            var (bestPlayers, avgR) = trainer.TrainPlayers(N1, N2);

            bestPlayers.CalibratedColors = trainer.GetCalibratedColorsCopy();

            File.WriteAllText("output/best_player_params.json", JsonSerializer.Serialize(bestPlayers, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            Console.WriteLine("Saved: output/best_player_params.json");

            trainer.VisualizeAndSave(bestPlayers);

            Console.WriteLine("Done.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error:\n" + ex);
            return 2;
        }
        return 0;
    }
}