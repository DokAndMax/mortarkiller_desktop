using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using mortarkiller.Model;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace mortarkiller;

// ===========================
// Parameters to learn
// ===========================
public enum PinColor
{
    Green = 0,
    Orange = 1,
    Yellow = 2,
    Blue = 3,
}

// ===========================
// Template strategy for training
// ===========================
public enum TemplateUseStrategy
{
    None,               // не використовувати шаблони під час детекції
    BuildPerCandidate,  // будувати шаблони під кожного кандидата (як було раніше)
    FixedProvided       // використовувати фіксовані (надані) шаблони для всіх кандидатів
}

// ===========================
// Debugger (intermediate dumps)
// ===========================
public static class DebuggerViz
{
    private static readonly JsonSerializerOptions CachedJsonSerializerOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true
    };

    private static readonly object CsvLock = new();

    public static void DumpIntermediatesFor(TrainingConfig config, ParameterSet p, TemplateLibrary templates)
    {
        var dbg = config.Debug;
        if (dbg == null || !dbg.Enabled) return;

        string dumpDir = Util.EnsureDir(string.IsNullOrEmpty(dbg.DumpDir) ? Path.Combine("output", "debug") : dbg.DumpDir);
        Console.WriteLine("Debug dump of intermediates:");
        var pb = new ProgressBar(config.Items.Count, "Debug");

        foreach (var item in config.Items)
        {
            if (!ShouldDumpImage(item.Path, dbg.DumpImages)) { pb.Tick(); continue; }
            try { DumpOne(item.Path, p, dumpDir, dbg, templates); }
            catch (Exception ex) { Console.WriteLine($"[Debug] {item.Path}: {ex.Message}"); }
            pb.Tick();
        }
        pb.Done();
        Console.WriteLine($"Debug saved to: {dumpDir}");
    }

    private static void AppendDebugCsv(string csvPath, object metaObj)
    {
        var json = JsonSerializer.SerializeToElement(metaObj);
        bool writeHeader = !File.Exists(csvPath);

        string line = string.Join(",",
            Escape(json.GetProperty("image").GetString()),
            Escape(json.GetProperty("color").GetString()),
            json.GetProperty("params").GetProperty("hue_center").GetInt32(),
            json.GetProperty("params").GetProperty("hue_tol").GetInt32(),
            json.GetProperty("params").GetProperty("sat_min").GetInt32(),
            json.GetProperty("params").GetProperty("val_min").GetInt32(),
            json.GetProperty("params").GetProperty("scale").GetDouble().ToString(CultureInfo.InvariantCulture),
            json.GetProperty("params").GetProperty("open_size").GetInt32(),
            json.GetProperty("params").GetProperty("close_size").GetInt32(),
            json.GetProperty("params").GetProperty("blur").GetInt32(),
            json.GetProperty("params").GetProperty("erode_iter").GetInt32(),
            json.GetProperty("params").GetProperty("dilate_iter").GetInt32(),
            json.GetProperty("params").GetProperty("min_area").GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
            json.GetProperty("params").GetProperty("max_area").GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
            json.GetProperty("ambiguous_raw_px").GetInt32(),
            json.GetProperty("mask_counts").GetProperty("raw").GetInt32(),
            json.GetProperty("mask_counts").GetProperty("blur").GetInt32(),
            json.GetProperty("mask_counts").GetProperty("open").GetInt32(),
            json.GetProperty("mask_counts").GetProperty("close").GetInt32(),
            json.GetProperty("mask_counts").GetProperty("erode_dilate").GetInt32(),
            json.GetProperty("contours").GetProperty("total").GetInt32(),
            json.GetProperty("contours").GetProperty("pass_area").GetInt32(),
            json.GetProperty("contours").GetProperty("pass_area_and_aspect").GetInt32(),
            json.GetProperty("contours").GetProperty("rejected_small").GetInt32(),
            json.GetProperty("contours").GetProperty("rejected_big").GetInt32(),
            json.GetProperty("contours").GetProperty("rejected_aspect").GetInt32(),
            json.GetProperty("best_found").GetBoolean() ? 1 : 0
        );

        lock (CsvLock)
        {
            using var sw = new StreamWriter(csvPath, append: true);
            if (writeHeader)
            {
                sw.WriteLine("image,color,hue_center,hue_tol,sat_min,val_min,scale,k_open,k_close,blur,erode_iter,dilate_iter,min_area,max_area,ambiguous_raw_px,mask_nz_raw_pre,mask_nz_blur,mask_nz_open,mask_nz_close,mask_nz_ed,contours_total,contours_pass_area,contours_pass_area_aspect,reject_small,reject_big,reject_aspect,best_found");
            }
            sw.WriteLine(line);
        }
    }

    private static void DumpOne(string path, ParameterSet p, string dumpDir, DebugOptions dbg, TemplateLibrary templates)
    {
        using var src = CvInvoke.Imread(path, ImreadModes.AnyColor);
        if (src.IsEmpty) return;

        Size sz = src.Size;
        int imgArea = sz.Width * sz.Height;

        string baseDir = Util.EnsureDir(Path.Combine(dumpDir, Path.GetFileNameWithoutExtension(path)));

        using var hsv = new Mat();
        CvInvoke.CvtColor(src, hsv, ColorConversion.Bgr2Hsv);

        if (dbg.SaveHsvChannels)
        {
            using var mv = new VectorOfMat();
            CvInvoke.Split(hsv, mv);
            CvInvoke.Imwrite(Path.Combine(baseDir, "00_H.png"), mv[0]);
            CvInvoke.Imwrite(Path.Combine(baseDir, "01_S.png"), mv[1]);
            CvInvoke.Imwrite(Path.Combine(baseDir, "02_V.png"), mv[2]);
        }

        int kOpen = Util.ToOdd(Math.Max(1, (int)Math.Round(p.OpenSize * p.Scale)));
        int kClose = Util.ToOdd(Math.Max(1, (int)Math.Round(p.CloseSize * p.Scale)));
        int blur = Util.ToOdd(Math.Max(1, (int)Math.Round(p.Blur * p.Scale)));

        using var kernelOpen = CvInvoke.GetStructuringElement(MorphShapes.Ellipse, new Size(kOpen, kOpen), new Point(-1, -1));
        using var kernelClose = CvInvoke.GetStructuringElement(MorphShapes.Ellipse, new Size(kClose, kClose), new Point(-1, -1));

        double minArea = Math.Max(10, p.MinAreaRatio * imgArea);
        double maxArea = Math.Max(minArea * 2, p.MaxAreaRatio * imgArea);

        var rawMasks = new Mat[4];
        for (int ci = 0; ci < 4; ci++)
            rawMasks[ci] = PinDetector.CreateMaskCpu(hsv, p.Colors[ci]);

        int ambiguousPx;
        using (var any = new Mat(hsv.Size, DepthType.Cv8U, 1))
        using (var amb = new Mat(hsv.Size, DepthType.Cv8U, 1))
        {
            any.SetTo(new MCvScalar(0)); amb.SetTo(new MCvScalar(0));
            for (int i = 0; i < 4; i++)
            {
                using var overlap = new Mat();
                CvInvoke.BitwiseAnd(any, rawMasks[i], overlap);
                CvInvoke.BitwiseOr(amb, overlap, amb);
                CvInvoke.BitwiseOr(any, rawMasks[i], any);
            }
            ambiguousPx = CvInvoke.CountNonZero(amb);
            CvInvoke.Imwrite(Path.Combine(baseDir, "09_overlap_ambiguous.png"), amb);
        }

        // ЖОДНИХ ексклюзивних масок — працюємо напряму з rawMasks
        for (int ci = 0; ci < 4; ci++)
        {
            var color = (PinColor)ci;
            if (!ShouldDumpColor(color, dbg.DumpColors)) continue;

            var cp = p.Colors[ci];
            string colorDir = Util.EnsureDir(Path.Combine(baseDir, color.ToString()));

            using var maskRaw = rawMasks[ci].Clone();
            CvInvoke.Imwrite(Path.Combine(colorDir, "08_mask_raw.png"), maskRaw);

            using var maskBlur = new Mat();
            if (blur > 1)
            {
                CvInvoke.GaussianBlur(maskRaw, maskBlur, new Size(blur, blur), 0);
                CvInvoke.Threshold(maskBlur, maskBlur, 127, 255, ThresholdType.Binary);
            }
            else maskRaw.CopyTo(maskBlur);

            using var maskOpen = new Mat();
            if (kOpen > 1) CvInvoke.MorphologyEx(maskBlur, maskOpen, MorphOp.Open, kernelOpen, new Point(-1, -1), 1, BorderType.Reflect, default);
            else maskBlur.CopyTo(maskOpen);

            int rawComponents = Util.CountConnected(maskRaw);
            bool doClose = rawComponents <= p.CloseIfComponentsLE;

            using var maskClose = new Mat();
            if (kClose > 1 && doClose) CvInvoke.MorphologyEx(maskOpen, maskClose, MorphOp.Close, kernelClose, new Point(-1, -1), 1, BorderType.Reflect, default);
            else maskOpen.CopyTo(maskClose);

            using var maskED = new Mat();
            maskClose.CopyTo(maskED);
            if (p.ErodeIterations > 0) CvInvoke.Erode(maskED, maskED, null, new Point(-1, -1), p.ErodeIterations, BorderType.Reflect, default);
            if (p.DilateIterations > 0) CvInvoke.Dilate(maskED, maskED, null, new Point(-1, -1), p.DilateIterations, BorderType.Reflect, default);

            int nzRaw = CvInvoke.CountNonZero(maskRaw);
            int nzBlur = CvInvoke.CountNonZero(maskBlur);
            int nzOpen = CvInvoke.CountNonZero(maskOpen);
            int nzClose = CvInvoke.CountNonZero(maskClose);
            int nzED = CvInvoke.CountNonZero(maskED);

            var colorBgr = OverlayColor(color);

            if (dbg.SaveStages)
            {
                using var ovRaw = OverlayMaskOnImage(src, maskRaw, colorBgr, 0.45);
                CvInvoke.Imwrite(Path.Combine(colorDir, "10_overlay_raw.png"), ovRaw);

                using var ovBlur = OverlayMaskOnImage(src, maskBlur, colorBgr, 0.45);
                CvInvoke.Imwrite(Path.Combine(colorDir, "11_overlay_blur.png"), ovBlur);

                using var ovOpen = OverlayMaskOnImage(src, maskOpen, colorBgr, 0.45);
                CvInvoke.Imwrite(Path.Combine(colorDir, "12_overlay_open.png"), ovOpen);

                using var ovClose = OverlayMaskOnImage(src, maskClose, colorBgr, 0.45);
                CvInvoke.Imwrite(Path.Combine(colorDir, "13_overlay_close.png"), ovClose);

                using var ovED = OverlayMaskOnImage(src, maskED, colorBgr, 0.45);
                CvInvoke.Imwrite(Path.Combine(colorDir, "14_overlay_erode_dilate.png"), ovED);
            }

            using var contours = new VectorOfVectorOfPoint();
            CvInvoke.FindContours(maskED, contours, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);

            int total = contours.Size;
            int passArea = 0, passAspect = 0;
            int rejSmall = 0, rejBig = 0, rejAspect = 0;

            using var drawAll = src.Clone();
            double bestArea = -1; int bestIdx = -1;

            for (int i = 0; i < total; i++)
            {
                double area = CvInvoke.ContourArea(contours[i]);
                var rect = CvInvoke.BoundingRectangle(contours[i]);
                double aspect = rect.Height > 0 ? (double)rect.Height / Math.Max(1, rect.Width) : 0;

                bool areaOk = area >= minArea && area <= maxArea;
                if (areaOk) passArea++;
                double aspectMin = p.AspectMin;
                bool aspectOk = aspect >= aspectMin;
                if (areaOk && aspectOk) passAspect++;

                string reason;
                MCvScalar col;
                if (!areaOk)
                {
                    if (area < minArea) { rejSmall++; reason = "tooSmall"; col = new MCvScalar(0, 255, 255); }
                    else { rejBig++; reason = "tooBig"; col = new MCvScalar(0, 0, 255); }
                }
                else if (!aspectOk) { rejAspect++; reason = "aspectFail"; col = new MCvScalar(255, 0, 0); }
                else { reason = "pass"; col = new MCvScalar(0, 255, 0); if (area > bestArea) { bestArea = area; bestIdx = i; } }

                if (dbg.SaveRejectedContours || reason == "pass")
                {
                    CvInvoke.DrawContours(drawAll, contours, i, col, 2);
                    CvInvoke.Rectangle(drawAll, rect, col, 1);
                    CvInvoke.PutText(drawAll, $"{i}:{reason} A={area:0} asp={aspect:0.00}", new Point(rect.X, Math.Max(10, rect.Y - 3)), FontFace.HersheySimplex, 0.4, col, 1);
                }
            }

            using var drawBest = src.Clone();
            if (bestIdx >= 0)
            {
                var tip = PinDetector.FindBottomTip(contours[bestIdx], sz);
                var r = CvInvoke.BoundingRectangle(contours[bestIdx]);
                CvInvoke.DrawContours(drawBest, contours, bestIdx, new MCvScalar(0, 255, 0), 2);
                CvInvoke.Rectangle(drawBest, r, new MCvScalar(0, 255, 0), 2);
                CvInvoke.Circle(drawBest, tip, 6, new MCvScalar(0, 0, 255), 2);
                CvInvoke.PutText(drawBest, $"BEST area={bestArea:0}", new Point(r.X, Math.Max(10, r.Y - 10)), FontFace.HersheySimplex, 0.6, new MCvScalar(0, 255, 0), 2);
            }
            else
            {
                CvInvoke.PutText(drawBest, "NO CONTOUR PASSED FILTERS", new Point(30, 30), FontFace.HersheySimplex, 0.8, new MCvScalar(0, 0, 255), 2);
            }

            using var filterAll = src.Clone();
            using var filterPass = src.Clone();

            int bestIdxTpl = -1; double bestTplScore = double.NegativeInfinity;
            int bestIdxArea = -1; double bestAreaLoc = -1;

            for (int i = 0; i < contours.Size; i++)
            {
                double area = CvInvoke.ContourArea(contours[i]);
                var rect = CvInvoke.BoundingRectangle(contours[i]);
                double aspect = rect.Height > 0 ? (double)rect.Height / Math.Max(1, rect.Width) : 0;

                string reason;
                MCvScalar col;
                bool areaOk = area >= minArea && area <= maxArea;
                double aspectMin = p.AspectMin;
                bool aspectOk = aspect >= aspectMin;
                if (!areaOk)
                {
                    if (area < minArea) { reason = "tooSmall"; col = new MCvScalar(0, 255, 255); }
                    else { reason = "tooBig"; col = new MCvScalar(0, 0, 255); }
                }
                else if (!aspectOk) { reason = "aspectFail"; col = new MCvScalar(255, 0, 0); }
                else { reason = "pass"; col = new MCvScalar(0, 200, 0); }

                double tplScore = -1, iouF = 0, iouE = 0;
                if (templates?.Has == true)
                {
                    using var norm = ShapeMatch.NormalizeFromContour(maskED, contours[i], TemplateLibrary.CanonW, TemplateLibrary.CanonH, sz);
                    (tplScore, iouF, iouE) = ShapeMatch.ScoreAgainstTemplates(norm, templates, color, p.TplEdgeWeight);
                    if (reason == "pass" && tplScore < p.TplMinScore)
                    {
                        reason = "tplReject";
                        col = new MCvScalar(80, 80, 80);
                    }

                    if (tplScore > bestTplScore && reason == "pass")
                    {
                        bestTplScore = tplScore;
                        bestIdxTpl = i;
                    }
                }

                if (area > bestAreaLoc && reason == "pass")
                {
                    bestAreaLoc = area;
                    bestIdxArea = i;
                }

                CvInvoke.DrawContours(filterAll, contours, i, col, 2);
                CvInvoke.Rectangle(filterAll, rect, col, 2);

                string msg = $"#{i} {reason} A={area:0} asp={aspect:0.00}";
                if (tplScore >= 0)
                    msg += $" tpl={tplScore:0.00} (e={iouE:0.00}/f={iouF:0.00})";
                CvInvoke.PutText(filterAll, msg, new Point(rect.X, Math.Max(10, rect.Y - 5)), FontFace.HersheySimplex, 0.45, col, 1);
            }

            int bestIdxForViz = (templates?.Has == true && bestIdxTpl >= 0) ? bestIdxTpl : bestIdxArea;
            if (bestIdxForViz >= 0)
            {
                var r = CvInvoke.BoundingRectangle(contours[bestIdxForViz]);
                CvInvoke.DrawContours(filterPass, contours, bestIdxForViz, new MCvScalar(0, 255, 0), 3);
                CvInvoke.Rectangle(filterPass, r, new MCvScalar(0, 255, 0), 3);
                CvInvoke.PutText(filterPass, "BEST", new Point(r.X, Math.Max(10, r.Y - 8)), FontFace.HersheySimplex, 0.6, new MCvScalar(0, 255, 0), 2);
            }
            else
            {
                CvInvoke.PutText(filterPass, "NO PASS", new Point(30, 30), FontFace.HersheySimplex, 0.8, new MCvScalar(0, 0, 255), 2);
            }

            CvInvoke.Imwrite(Path.Combine(colorDir, "15_filter_stage.png"), filterAll);
            CvInvoke.Imwrite(Path.Combine(colorDir, "16_filter_stage_pass.png"), filterPass);

            if (dbg.SavePinMasks)
            {
                string metaPinPath = Path.Combine(colorDir, "meta_pin.json");

                if (bestIdxForViz >= 0)
                {
                    var pts = contours[bestIdxForViz].ToArray();
                    using var cntCopy = new VectorOfPoint(pts);
                    using var vv = new VectorOfVectorOfPoint();
                    vv.Push(cntCopy);

                    using var pinMaskFull = new Mat(sz, DepthType.Cv8U, 1);
                    pinMaskFull.SetTo(new MCvScalar(0));
                    CvInvoke.DrawContours(pinMaskFull, vv, -1, new MCvScalar(255), -1);

                    var r = CvInvoke.BoundingRectangle(contours[bestIdxForViz]);
                    var rClamped = Rectangle.Intersect(r, new Rectangle(Point.Empty, sz));
                    using var pinMaskRoi = new Mat(pinMaskFull, rClamped);

                    using var pinMaskCanon = ShapeMatch.NormalizeFromContour(
                        maskED, contours[bestIdxForViz],
                        TemplateLibrary.CanonW, TemplateLibrary.CanonH, sz
                    );

                    string fnFull = Path.Combine(colorDir, "17_pin_mask_full.png");
                    string fnRoi = Path.Combine(colorDir, "17_pin_mask_roi.png");
                    string fnCanon = Path.Combine(colorDir, "18_pin_mask_canon.png");

                    CvInvoke.Imwrite(fnFull, pinMaskFull);
                    CvInvoke.Imwrite(fnRoi, pinMaskRoi);
                    CvInvoke.Imwrite(fnCanon, pinMaskCanon);

                    var tip = PinDetector.FindBottomTip(contours[bestIdxForViz], sz);

                    var pinMeta = new
                    {
                        image = path,
                        color = color.ToString(),
                        best_idx = bestIdxForViz,
                        bbox = new { x = rClamped.X, y = rClamped.Y, w = rClamped.Width, h = rClamped.Height },
                        tip = new { x = tip.X, y = tip.Y },
                        mask_full = fnFull,
                        mask_roi = fnRoi,
                        mask_canon = fnCanon,
                        canon_size = new { w = TemplateLibrary.CanonW, h = TemplateLibrary.CanonH }
                    };
                    File.WriteAllText(metaPinPath, JsonSerializer.Serialize(pinMeta, CachedJsonSerializerOptions));
                }
                else
                {
                    var pinMeta = new
                    {
                        image = path,
                        color = color.ToString(),
                        best_idx = -1,
                        message = "no pin passed filters"
                    };
                    File.WriteAllText(metaPinPath, JsonSerializer.Serialize(pinMeta, CachedJsonSerializerOptions));
                }
            }

            if (dbg.TileComposite)
            {
                using var grid = MakeComposite(
                [
                src,
                GrayToBgr(maskRaw),
                GrayToBgr(maskBlur),
                GrayToBgr(maskOpen),
                GrayToBgr(maskClose),
                GrayToBgr(maskED),
                drawAll,
                drawBest
            ], 4);
                CvInvoke.Imwrite(Path.Combine(colorDir, "00_composite.png"), grid);
            }

            var meta = new
            {
                image = path,
                color = color.ToString(),
                ambiguous_raw_px = ambiguousPx,
                @params = new
                {
                    scale = p.Scale,
                    blur,
                    open_size = kOpen,
                    close_size = kClose,
                    erode_iter = p.ErodeIterations,
                    dilate_iter = p.DilateIterations,
                    min_area_ratio = p.MinAreaRatio,
                    max_area_ratio = p.MaxAreaRatio,
                    min_area = minArea,
                    max_area = maxArea,
                    hue_center = cp.HueCenter,
                    hue_tol = cp.HueTol,
                    sat_min = cp.SatMin,
                    val_min = cp.ValMin
                },
                mask_counts = new
                {
                    raw = nzRaw,
                    blur = nzBlur,
                    open = nzOpen,
                    close = nzClose,
                    erode_dilate = nzED
                },
                contours = new { total, pass_area = passArea, pass_area_and_aspect = passAspect, rejected_small = rejSmall, rejected_big = rejBig, rejected_aspect = rejAspect },
                best_found = bestIdx >= 0
            };
            File.WriteAllText(Path.Combine(colorDir, "meta.json"), JsonSerializer.Serialize(meta, CachedJsonSerializerOptions));

            AppendDebugCsv(Path.Combine(dumpDir, "diagnostics_intermediate.csv"), meta);
        }

        for (int ci = 0; ci < 4; ci++) rawMasks[ci]?.Dispose();
    }

    private static string Escape(string s) => s != null && s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s ?? "";

    private static Mat GrayToBgr(Mat gray)
    {
        var bgr = new Mat();
        CvInvoke.CvtColor(gray, bgr, ColorConversion.Gray2Bgr);
        return bgr;
    }

    private static Mat MakeComposite(Mat[] mats, int cols)
    {
        int rows = (int)Math.Ceiling(mats.Length / (double)cols);
        var rowMats = new List<Mat>();
        for (int r = 0; r < rows; r++)
        {
            using var rowVec = new VectorOfMat();
            for (int c = 0; c < cols; c++)
            {
                int idx = r * cols + c;
                rowVec.Push(mats[Math.Min(idx, mats.Length - 1)]);
            }
            var row = new Mat();
            CvInvoke.HConcat(rowVec, row);
            rowMats.Add(row);
        }

        using var colVec = new VectorOfMat(rowMats.ToArray());
        var result = new Mat();
        CvInvoke.VConcat(colVec, result);

        foreach (var r in rowMats) r.Dispose();
        return result;
    }

    private static MCvScalar OverlayColor(PinColor c) => c switch
    {
        PinColor.Green => new MCvScalar(73, 181, 68),
        PinColor.Orange => new MCvScalar(38, 98, 218),
        PinColor.Yellow => new MCvScalar(17, 229, 233),
        PinColor.Blue => new MCvScalar(217, 160, 58),
        _ => new MCvScalar(0, 0, 255)
    };

    private static Mat OverlayMaskOnImage(Mat imgBgr, Mat mask8u, MCvScalar colorBgr, double alpha = 0.45)
    {
        var res = imgBgr.Clone();
        using var colorImg = new Mat(imgBgr.Size, DepthType.Cv8U, 3);
        colorImg.SetTo(colorBgr);

        using var tinted = new Mat();
        CvInvoke.AddWeighted(imgBgr, 1.0 - alpha, colorImg, alpha, 0, tinted);
        tinted.CopyTo(res, mask8u);
        return res;
    }

    private static bool ShouldDumpColor(PinColor c, List<string> filters)
    {
        if (filters == null || filters.Count == 0) return true;
        return filters.Any(fc => string.Equals(fc, c.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldDumpImage(string path, List<string> filters)
    {
        if (filters == null || filters.Count == 0) return true;
        string file = Path.GetFileName(path);
        return filters.Any(f => file.Equals(f, StringComparison.OrdinalIgnoreCase) || path.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}

// ===========================
// Evaluator (assignment & scoring)
// ===========================
public static class Evaluator
{
    private const double DistCapPx = 120.0;

    private const double FalsePosPenaltyPx = 80.0;

    // було 160
    private const double MatchRewardPx = 40.0;

    // Налаштовувані ваги/пороги для скору
    private const double MaxMatchDistPx = 90.0;     // > не матчимо

    // кап на вклад відстані
    private const double MissingPenaltyPx = 300.0;  // було 220

    // бонус за кожний TP

    public static CandidateResult EvaluateDataset(
        IList<DatasetItem> items,
        ParameterSet p,
        Func<string, ParameterSet, DetectionResultPin> detector,
        Action? onProgress = null)
    {
        var result = new CandidateResult
        {
            Params = p,
            Score = 0.0
        };

        var perImage = new List<PerImageEval>();
        var allDistances = new List<double>();
        int totalMissing = 0;
        int totalFp = 0;
        int totalMatched = 0;
        double scoreSum = 0.0;

        foreach (var item in items)
        {
            var det = detector(item.Path, p);
            if (!string.IsNullOrEmpty(det.Error))
            {
                var bad = new PerImageEval { Path = item.Path, Predictions = new(), Gt = item.GtPins.Select(g => new Point(g.X, g.Y)).ToList(), Distances = Array.Empty<double>(), Missing = item.GtPins.Count, FalsePos = 0 };
                perImage.Add(bad);
                scoreSum += item.GtPins.Count * MissingPenaltyPx;
                totalMissing += item.GtPins.Count;
                onProgress?.Invoke();
                continue;
            }

            var gtPts = item.GtPins.Select(g => new Point(g.X, g.Y)).ToList();
            var (score, eval) = EvaluateOne(item.Path, det.Predictions, gtPts);
            eval.ContoursDiag.AddRange(det.ContourDiagnostics);
            perImage.Add(eval);
            scoreSum += score;

            totalMissing += eval.Missing;
            totalFp += eval.FalsePos;
            totalMatched += eval.Distances.Length;
            allDistances.AddRange(eval.Distances);

            onProgress?.Invoke();
        }

        result.Score = scoreSum;
        result.PerImageDetails = perImage;
        result.MatchedTotal = totalMatched;
        result.MissingTotal = totalMissing;
        result.FalsePositiveTotal = totalFp;
        result.MeanDistance = allDistances.Count > 0 ? allDistances.Average() : 0.0;
        result.MedianDistance = allDistances.Count > 0 ? allDistances.OrderBy(x => x).ElementAt(allDistances.Count / 2) : 0.0;

        double sumDist = allDistances.Sum(d => Math.Min(d, 120.0)); // той же DistCapPx
        double missCost = result.MissingTotal * 300.0;
        double fpCost = result.FalsePositiveTotal * 80.0;
        double matchBonus = result.MatchedTotal * 40.0;
        Console.WriteLine($"[Breakdown] sumDist={sumDist:0.##}, miss={missCost:0}, fp={fpCost:0}, bonus=-{matchBonus:0}, total={result.Score:0.##}");

        return result;
    }

    public static (double score, PerImageEval eval) EvaluateOne(string path, List<Point> preds, List<Point> gt)
    {
        var eval = new PerImageEval
        {
            Path = path,
            Predictions = preds,
            Gt = gt
        };

        int n = preds.Count;
        int m = gt.Count;

        if (n == 0 && m == 0)
        {
            eval.Distances = [];
            eval.Missing = 0;
            eval.FalsePos = 0;
            return (0.0, eval);
        }

        var used = new bool[n];
        var dists = new List<double>();
        double best = double.PositiveInfinity;
        List<double>? bestMatched = null;
        int bestFp = 0;

        void Recurse(int gi, double cost, List<double> matchedDists)
        {
            if (cost >= best) return;

            if (gi == m)
            {
                int usedCount = used.Count(u => u);
                int fp = n - usedCount;

                // додаємо штраф за FP і бонус за матчі
                double total = cost + fp * FalsePosPenaltyPx - matchedDists.Count * MatchRewardPx;
                if (total < best)
                {
                    best = total;
                    bestMatched = [.. matchedDists];
                    bestFp = fp;
                }
                return;
            }

            // варіант: пропустити цю GT
            Recurse(gi + 1, cost + MissingPenaltyPx, matchedDists);

            // варіант: зіставити з предиктом
            for (int pi = 0; pi < n; pi++)
            {
                if (used[pi]) continue;
                double d = Util.Euclid(preds[pi], gt[gi]);

                // занадто далекі пари не матчимо
                if (d > MaxMatchDistPx) continue;

                double eff = Math.Min(d, DistCapPx);

                used[pi] = true;
                matchedDists.Add(d); // зберігаємо реальну відстань для діагностики/середніх
                Recurse(gi + 1, cost + eff, matchedDists);
                matchedDists.RemoveAt(matchedDists.Count - 1);
                used[pi] = false;
            }
        }

        Recurse(0, 0.0, dists);

        eval.Distances = bestMatched?.ToArray() ?? Array.Empty<double>();
        int matchedCount = eval.Distances.Length;
        eval.Missing = Math.Max(0, m - matchedCount);
        eval.FalsePos = bestFp;

        // best вже містить штрафи/бонуси; його і повертаємо як score
        return (best, eval);
    }
}

// ===========================
// Params IO (load from best_params.json)
// ===========================
public static class ParamsIO
{
    public static ParameterSet LoadFromBestParamsJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        if (!root.TryGetProperty("parameters", out var pEl))
            throw new InvalidOperationException("Invalid best_params.json: 'parameters' node missing.");

        var p = new ParameterSet
        {
            Scale = (float)pEl.GetProperty("scale").GetDouble(),
            Blur = pEl.GetProperty("blur").GetInt32(),
            OpenSize = pEl.GetProperty("open_size").GetInt32(),
            CloseSize = pEl.GetProperty("close_size").GetInt32(),
            ErodeIterations = pEl.GetProperty("erode_iter").GetInt32(),
            DilateIterations = pEl.GetProperty("dilate_iter").GetInt32(),
            MinAreaRatio = (float)pEl.GetProperty("min_area_ratio").GetDouble(),
            MaxAreaRatio = (float)pEl.GetProperty("max_area_ratio").GetDouble(),
            Colors = new ColorParams[4]
        };

        p.TplMinScore = pEl.TryGetProperty("tpl_min_score", out var v1) ? v1.GetDouble() : 0.45;
        p.AspectMin = pEl.TryGetProperty("aspect_min", out var v2) ? v2.GetDouble() : 1.05;
        p.NmsRadius = pEl.TryGetProperty("nms_radius", out var v3) ? v3.GetDouble() : 22.0;
        p.TplEdgeWeight = pEl.TryGetProperty("tpl_edge_weight", out var v4) ? v4.GetDouble() : 0.70;
        p.TemplateBuildFillFrac = pEl.TryGetProperty("tpl_build_fill_frac", out var v5) ? v5.GetDouble() : 0.50;
        p.TemplateBuildEdgeFrac = pEl.TryGetProperty("tpl_build_edge_frac", out var v6) ? v6.GetDouble() : 0.45;
        p.CloseIfComponentsLE = pEl.TryGetProperty("close_if_components_le", out var v7) ? v7.GetInt32() : 1;

        foreach (var colorEl in pEl.GetProperty("colors").EnumerateArray())
        {
            string colorName = colorEl.GetProperty("color").GetString();
            if (!Enum.TryParse<PinColor>(colorName, true, out var c))
                continue;
            p.Colors[(int)c] = new ColorParams
            {
                HueCenter = colorEl.GetProperty("hue_center").GetInt32(),
                HueTol = colorEl.GetProperty("hue_tol").GetInt32(),
                SatMin = colorEl.GetProperty("sat_min").GetInt32(),
                ValMin = colorEl.GetProperty("val_min").GetInt32()
            };
        }

        for (int i = 0; i < p.Colors.Length; i++)
        {
            if (p.Colors[i] == null) p.Colors[i] = new ColorParams { HueCenter = 0, HueTol = 20, SatMin = 60, ValMin = 60 };
        }
        return p;
    }
}

// ===========================
// Visualization & reports
// ===========================
public static class Reporter
{
    public static void DrawDetectionsOnImage(Mat img, DetectionResultPin det)
    {
        var usedDiag = det.ContourDiagnostics.Where(d => d.UsedAsPrediction).ToList();
        var map = usedDiag.ToDictionary(d => d.Tip, d => d);
        foreach (var pr in det.Predictions)
        {
            if (map.TryGetValue(pr, out var d))
            {
                var (tag, col) = StyleOf(d.Color);
                CvInvoke.Circle(img, pr, 7, col, 3);
                CvInvoke.PutText(img, tag, new Point(pr.X + 9, pr.Y + 4), FontFace.HersheySimplex, 0.6, col, 2);
            }
            else
            {
                CvInvoke.Circle(img, pr, 7, new MCvScalar(0, 0, 255), 3);
            }
        }
    }

    public static void SaveBestParams(string outDir, CandidateResult best, string templatesRelDir = "best_pin_masks")
    {
        var outObj = new
        {
            score = best.Score,
            matched = best.MatchedTotal,
            missing = best.MissingTotal,
            false_positive = best.FalsePositiveTotal,
            mean_distance = best.MeanDistance,
            median_distance = best.MedianDistance,
            templates_dir = templatesRelDir,
            parameters = new
            {
                scale = best.Params.Scale,
                blur = best.Params.Blur,
                open_size = best.Params.OpenSize,
                close_size = best.Params.CloseSize,
                erode_iter = best.Params.ErodeIterations,
                dilate_iter = best.Params.DilateIterations,
                min_area_ratio = best.Params.MinAreaRatio,
                max_area_ratio = best.Params.MaxAreaRatio,

                // NEW
                tpl_min_score = best.Params.TplMinScore,
                aspect_min = best.Params.AspectMin,
                nms_radius = best.Params.NmsRadius,
                tpl_edge_weight = best.Params.TplEdgeWeight,
                tpl_build_fill_frac = best.Params.TemplateBuildFillFrac,
                tpl_build_edge_frac = best.Params.TemplateBuildEdgeFrac,
                close_if_components_le = best.Params.CloseIfComponentsLE,

                colors = best.Params.Colors.Select((c, idx) => new
                {
                    color = ((PinColor)idx).ToString(),
                    hue_center = c.HueCenter,
                    hue_tol = c.HueTol,
                    sat_min = c.SatMin,
                    val_min = c.ValMin
                }).ToArray()
            }
        };

        var json = JsonSerializer.Serialize(outObj, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        File.WriteAllText(Path.Combine(outDir, "best_params.json"), json);
    }

    public static void SaveBestPinMasks(string outDir, TemplateLibrary templates)
    {
        string dir = Util.EnsureDir(Path.Combine(outDir, "best_pin_masks"));
        templates.SaveToDir(dir);
        Console.WriteLine($"Saved best pin masks to: {dir}");
    }

    public static void SaveDiagnosticsCsv(string outDir, CandidateResult best)
    {
        var path = Path.Combine(outDir, "diagnostics_scale.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("image,color,tip_x,tip_y,matched_gt_x,matched_gt_y,dx,dy,distance,contour_area,box_w,box_h,aspect,tip_angle,mask_nonzero_raw,mask_nonzero,img_area,kernel_open,kernel_close,scale,used_as_prediction,reason,template_score,template_iou_fill,template_iou_edge");

        foreach (var per in best.PerImageDetails)
        {
            var preds = per.Predictions.ToList();
            var gts = per.Gt.ToList();

            var matches = new List<(int predIdx, int gtIdx)>();
            var usedPred = new bool[preds.Count];
            var usedGt = new bool[gts.Count];

            var pairs = new List<(int pi, int gi, double d)>();
            for (int i = 0; i < preds.Count; i++)
                for (int j = 0; j < gts.Count; j++)
                    pairs.Add((i, j, Util.Euclid(preds[i], gts[j])));
            foreach (var pr in pairs.OrderBy(x => x.d))
            {
                if (!usedPred[pr.pi] && !usedGt[pr.gi])
                {
                    usedPred[pr.pi] = usedGt[pr.gi] = true;
                    matches.Add((pr.pi, pr.gi));
                }
            }

            foreach (var cd in per.ContoursDiag)
            {
                var tip = cd.Tip;
                int matchIndex = matches.FindIndex(m => m.predIdx < preds.Count && preds[m.predIdx] == tip);
                Point? gt = null;
                double dx = 0, dy = 0, dist = 0;
                if (matchIndex >= 0)
                {
                    var g = gts[matches[matchIndex].gtIdx];
                    gt = g;
                    dx = tip.X - g.X;
                    dy = tip.Y - g.Y;
                    dist = Math.Sqrt(dx * dx + dy * dy);
                }

                sw.WriteLine(string.Join(",",
                    per.Path,
                    cd.Color,
                    tip.X, tip.Y,
                    gt?.X ?? -1, gt?.Y ?? -1,
                    dx.ToString("0.###", CultureInfo.InvariantCulture),
                    dy.ToString("0.###", CultureInfo.InvariantCulture),
                    dist.ToString("0.###", CultureInfo.InvariantCulture),
                    cd.Area,
                    cd.BBoxW,
                    cd.BBoxH,
                    cd.Aspect.ToString("0.###", CultureInfo.InvariantCulture),
                    cd.TipAngle.ToString("0.###", CultureInfo.InvariantCulture),
                    cd.MaskNonZeroRaw,
                    cd.MaskNonZero,
                    cd.ImgArea,
                    cd.KernelOpen,
                    cd.KernelClose,
                    cd.Scale.ToString("0.###", CultureInfo.InvariantCulture),
                    cd.UsedAsPrediction ? 1 : 0,
                    cd.RejectReason ?? "",
                    cd.TemplateScore.ToString("0.###", CultureInfo.InvariantCulture),
                    cd.TemplateIoUFill.ToString("0.###", CultureInfo.InvariantCulture),
                    cd.TemplateIoUEdge.ToString("0.###", CultureInfo.InvariantCulture)
                ));
            }
        }
    }

    public static void SaveVisualizations(string outDir, CandidateResult best)
    {
        foreach (var per in best.PerImageDetails)
        {
            using var img = CvInvoke.Imread(per.Path, ImreadModes.AnyColor);
            if (img.IsEmpty) continue;

            foreach (var d in per.ContoursDiag.Where(d => !d.UsedAsPrediction && d.RejectReason == "suppressedNMS"))
            {
                var pt = d.Tip;
                CvInvoke.Circle(img, pt, 6, new MCvScalar(180, 180, 180), 1);
                CvInvoke.PutText(img, "suppressed", new Point(pt.X + 8, pt.Y - 8), FontFace.HersheySimplex, 0.4, new MCvScalar(170, 170, 170), 1);
            }

            foreach (var g in per.Gt)
            {
                CvInvoke.DrawMarker(img, g, new MCvScalar(0, 255, 0), MarkerTypes.Cross, 17, 3);
            }

            var usedDiag = per.ContoursDiag.Where(d => d.UsedAsPrediction).ToList();
            var map = usedDiag.ToDictionary(d => d.Tip, d => d);
            foreach (var pr in per.Predictions)
            {
                if (map.TryGetValue(pr, out var d))
                {
                    var (tag, col) = StyleOf(d.Color);
                    CvInvoke.Circle(img, pr, 7, col, 3);
                    CvInvoke.PutText(img, tag, new Point(pr.X + 9, pr.Y + 4), FontFace.HersheySimplex, 0.6, col, 2);
                }
                else
                {
                    CvInvoke.Circle(img, pr, 7, new MCvScalar(0, 0, 255), 3);
                }
            }

            var preds = per.Predictions.ToList();
            var gts = per.Gt.ToList();
            var pairs = new List<(int pi, int gi, double d)>();
            var usedPred = new bool[preds.Count];
            var usedGt = new bool[gts.Count];
            for (int i = 0; i < preds.Count; i++)
                for (int j = 0; j < gts.Count; j++)
                    pairs.Add((i, j, Util.Euclid(preds[i], gts[j])));
            foreach (var pr in pairs.OrderBy(x => x.d))
            {
                if (!usedPred[pr.pi] && !usedGt[pr.gi])
                {
                    usedPred[pr.pi] = usedGt[pr.gi] = true;
                    var a = preds[pr.pi]; var b = gts[pr.gi];
                    CvInvoke.Line(img, a, b, new MCvScalar(255, 0, 0), 2);
                }
            }

            var saveName = Path.Combine(outDir, Path.GetFileNameWithoutExtension(per.Path) + "_annotated.png");
            CvInvoke.Imwrite(saveName, img);
        }
    }

    public static (string tag, MCvScalar bgr) StyleOf(PinColor c) => c switch
    {
        PinColor.Green => ("G", new MCvScalar(73, 181, 68)),
        PinColor.Orange => ("O", new MCvScalar(38, 98, 218)),
        PinColor.Yellow => ("Y", new MCvScalar(17, 229, 233)),
        PinColor.Blue => ("B", new MCvScalar(217, 160, 58)),
        _ => ("?", new MCvScalar(0, 0, 255))
    };
}

// ===========================
// Sampler for parameters
// ===========================
public static class Sampler
{
    private static readonly (PinColor color, string hex)[] ColorHexes =
    {
        (PinColor.Green,  "#44b549"),
        (PinColor.Orange, "#da6226"),
        (PinColor.Yellow, "#e9e511"),
        (PinColor.Blue,   "#3aa0d9"),
    };

    public static ParameterSet CreateBaselineFromPalette()
    {
        var colors = new ColorParams[4];
        foreach (var (col, hex) in ColorHexes)
        {
            var (h, s, v) = Util.HexToHsvOpenCv(hex);
            colors[(int)col] = new ColorParams
            {
                HueCenter = h,
                HueTol = 8,     // старт розумний і вузький
                SatMin = 96,    // як у ваших коментах
                ValMin = 121
            };
        }

        return new ParameterSet
        {
            Scale = 1.4f,
            Blur = 1,
            OpenSize = 3,
            CloseSize = 7,
            ErodeIterations = 0,
            DilateIterations = 0,
            MinAreaRatio = 1e-5f,
            MaxAreaRatio = 0.02f,
            Colors = colors,

            // NEW defaults
            TplMinScore = 0.45,
            AspectMin = 1.05,
            NmsRadius = 22.0,
            TplEdgeWeight = 0.70,
            TemplateBuildFillFrac = 0.50,
            TemplateBuildEdgeFrac = 0.45,
            CloseIfComponentsLE = 1
        };
    }

    public static ParameterSet CreateRandomInitial(Random rng)
    {
        var colors = new ColorParams[4];
        foreach (var (col, hex) in ColorHexes)
        {
            var (h, s, v) = Util.HexToHsvOpenCv(hex);
            //colors[(int)col] = new ColorParams
            //{
            //    HueCenter = h,
            //    HueTol = 6,
            //    SatMin = 96,
            //    ValMin = 121,
            //};

            colors[(int)col] = new ColorParams
            {
                HueCenter = h,
                HueTol = rng.Next(3, 28),
                SatMin = rng.Next(60, 170),
                ValMin = rng.Next(60, 170),
            };
        }

        //return new ParameterSet
        //{
        //    Scale = 1.6615417f,
        //    Blur = 1,
        //    OpenSize = 2,
        //    CloseSize = 7,
        //    ErodeIterations = 0,
        //    DilateIterations = 0,
        //    MinAreaRatio = 0,
        //    MaxAreaRatio = 1f,
        //    Colors = colors
        //};

        return new ParameterSet
        {
            Scale = (float)(0.6 + rng.NextDouble() * 2.2),
            Blur = new[] { 1, 3, 5, 7 }[rng.Next(0, 4)],
            OpenSize = rng.Next(1, 10),
            CloseSize = rng.Next(1, 10),
            ErodeIterations = rng.Next(0, 3),
            DilateIterations = rng.Next(0, 3),
            MinAreaRatio = (float)(1e-5 + rng.NextDouble() * 0.003),
            MaxAreaRatio = (float)(0.001 + rng.NextDouble() * 0.02),
            Colors = colors,

            // NEW randomized
            TplMinScore = 0.35 + rng.NextDouble() * 0.35,          // [0.35..0.70]
            AspectMin = 1.0 + rng.NextDouble() * 0.2,              // [1.00..1.20]
            NmsRadius = 16.0 + rng.NextDouble() * 16.0,            // [16..32]
            TplEdgeWeight = 0.55 + rng.NextDouble() * 0.3,         // [0.55..0.85]
            TemplateBuildFillFrac = 0.40 + rng.NextDouble() * 0.2, // [0.40..0.60]
            TemplateBuildEdgeFrac = 0.35 + rng.NextDouble() * 0.2, // [0.35..0.55]
            CloseIfComponentsLE = rng.Next(0, 3)                   // {0,1,2}
        };
    }

    public static ParameterSet Mutate(ParameterSet best, Random rng)
    {
        var p = best.Clone();
        double g = 0.35;

        //p.Scale = 1.6615417f;
        //p.Blur = 1;
        //p.OpenSize = 2;
        //p.CloseSize = 7;
        //p.ErodeIterations = 0;
        //p.DilateIterations = 0;
        //p.MinAreaRatio = 0f;
        //p.MaxAreaRatio = 1f;

        p.Scale = Clamp((float)(p.Scale * Math.Exp(Normal(rng, 0, g * 0.2))), 0.5f, 3.0f);
        p.Blur = OddClamp((int)Math.Round(p.Blur + Normal(rng, 0, 2)), 1, 11);
        p.OpenSize = (int)Clamp(Math.Round(p.OpenSize + Normal(rng, 0, 2)), 1, 15);
        p.CloseSize = (int)Clamp(Math.Round(p.CloseSize + Normal(rng, 0, 2)), 1, 15);
        p.ErodeIterations = (int)Clamp(Math.Round(p.ErodeIterations + Normal(rng, 0, 1)), 0, 4);
        p.DilateIterations = (int)Clamp(Math.Round(p.DilateIterations + Normal(rng, 0, 1)), 0, 4);
        p.MinAreaRatio = Clamp(p.MinAreaRatio * Math.Exp(Normal(rng, 0, g * 0.3)), 1e-6f, 0.01f);
        p.MaxAreaRatio = Clamp(p.MaxAreaRatio * Math.Exp(Normal(rng, 0, g * 0.3)), 0.0005f, 0.05f);

        foreach (var cp in p.Colors)
        {
            //cp.HueTol = 6;
            //cp.SatMin = 96;
            //cp.ValMin = 121;
            cp.HueTol = (int)Clamp(Math.Round(cp.HueTol + Normal(rng, 0, 3)), 3, 40);
            cp.SatMin = (int)Clamp(Math.Round(cp.SatMin + Normal(rng, 0, 12)), 30, 220);
            cp.ValMin = (int)Clamp(Math.Round(cp.ValMin + Normal(rng, 0, 12)), 30, 220);
        }

        p.TplMinScore = Math.Max(0.0, Math.Min(1.0, p.TplMinScore + Normal(rng, 0, 0.04)));
        p.AspectMin = Math.Max(0.9, Math.Min(1.6, p.AspectMin + Normal(rng, 0, 0.03)));
        p.NmsRadius = Math.Max(8.0, Math.Min(50.0, p.NmsRadius + Normal(rng, 0, 2.5)));
        p.TplEdgeWeight = Math.Max(0.0, Math.Min(1.0, p.TplEdgeWeight + Normal(rng, 0, 0.04)));
        p.TemplateBuildFillFrac = Math.Max(0.3, Math.Min(0.8, p.TemplateBuildFillFrac + Normal(rng, 0, 0.03)));
        p.TemplateBuildEdgeFrac = Math.Max(0.3, Math.Min(0.8, p.TemplateBuildEdgeFrac + Normal(rng, 0, 0.03)));
        p.CloseIfComponentsLE = (int)Clamp(Math.Round(p.CloseIfComponentsLE + Normal(rng, 0, 0.6)), 0, 2);

        return p;
    }

    private static float Clamp(double v, float lo, float hi) => (float)Math.Max(lo, Math.Min(hi, v));

    private static double Normal(Random rng, double mean, double std)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + std * randStdNormal;
    }

    private static int OddClamp(int v, int lo, int hi)
    {
        v = Math.Max(lo, Math.Min(hi, v));
        return Util.ToOdd(v);
    }
}

public static class ShapeMatch
{
    public static double IoU(Mat a, Mat b)
    {
        using var inter = new Mat(); CvInvoke.BitwiseAnd(a, b, inter);
        using var uni = new Mat(); CvInvoke.BitwiseOr(a, b, uni);
        double iu = CvInvoke.CountNonZero(inter);
        double uu = Math.Max(1, CvInvoke.CountNonZero(uni));
        return iu / uu;
    }

    public static Mat MorphGradient(Mat bin)
    {
        var grad = new Mat(bin.Size, DepthType.Cv8U, 1);
        using var dil = new Mat();
        using var ero = new Mat();
        using var k = CvInvoke.GetStructuringElement(MorphShapes.Rectangle, new Size(3, 3), new Point(-1, -1));
        CvInvoke.Dilate(bin, dil, k, new Point(-1, -1), 1, BorderType.Reflect, default);
        //CvInvoke.Imwrite($"21.png", dil);
        //CvInvoke.Imwrite($"22.png", k);
        CvInvoke.Erode(bin, ero, k, new Point(-1, -1), 1, BorderType.Reflect, default);
        //CvInvoke.Imwrite($"23.png", ero);
        CvInvoke.Subtract(dil, ero, grad);
        //CvInvoke.Imwrite($"24.png", grad);
        CvInvoke.Threshold(grad, grad, 0, 255, ThresholdType.Binary);
        //CvInvoke.Imwrite($"25.png", grad);
        return grad;
    }

    public static Mat NormalizeFromContour(Mat maskED, VectorOfPoint contour, int outW, int outH, Size imgSize)
    {
        var rect = CvInvoke.BoundingRectangle(contour);
        var tip = PinDetector.FindBottomTip(contour, imgSize);
        return NormalizeRoiWithTip(maskED, rect, tip, outW, outH);
    }

    // Переносить ROI так, щоб tip опинився по центру внизу, масштабує до CanonW x CanonH
    public static Mat NormalizeRoiWithTip(Mat fullMask, Rectangle roi, Point tip, int outW, int outH)
    {
        var boundedRoi = Rectangle.Intersect(roi, new Rectangle(Point.Empty, fullMask.Size));
        using var patch = new Mat(fullMask, boundedRoi);
        //CvInvoke.Imwrite($"18.png", patch);

        var resized = new Mat();
        CvInvoke.Resize(patch, resized, new Size(outW, outH), 0, 0, Inter.Linear);
        //CvInvoke.Imwrite($"19.png", resized);

        double sx = (double)outW / Math.Max(1, boundedRoi.Width);
        double sy = (double)outH / Math.Max(1, boundedRoi.Height);
        int tipX = (int)Math.Round((tip.X - boundedRoi.X) * sx);
        int tipY = (int)Math.Round((tip.Y - boundedRoi.Y) * sy);

        int dx = outW / 2 - tipX;
        int dy = (outH - 2) - tipY;

        var outAligned = new Mat(outH, outW, DepthType.Cv8U, 1);
        outAligned.SetTo(new MCvScalar(0));

        using var M = new Matrix<float>(2, 3);
        M[0, 0] = 1; M[0, 1] = 0; M[0, 2] = dx;
        M[1, 0] = 0; M[1, 1] = 1; M[1, 2] = dy;

        CvInvoke.WarpAffine(
            resized, outAligned, M, new Size(outW, outH),
            Inter.Nearest, Warp.Default, BorderType.Constant, new MCvScalar(0)
        );
        //CvInvoke.Imwrite($"20.png", outAligned);
        resized.Dispose();
        return outAligned;
    }

    public static (double score, double iouFill, double iouEdge)
        ScoreAgainstTemplates(Mat normPatch, TemplateLibrary tpl, PinColor c, double edgeWeight)
    {
        var tplFill = tpl.GetFill();
        var tplEdge = tpl.GetEdge();
        using var edge = MorphGradient(normPatch);

        double iouFill = IoU(normPatch, tplFill);
        double iouEdge = IoU(edge, tplEdge);

        edgeWeight = Math.Max(0.0, Math.Min(1.0, edgeWeight));
        double score = edgeWeight * iouEdge + (1.0 - edgeWeight) * iouFill;
        return (score, iouFill, iouEdge);
    }

    // Back-compat (0.7 як раніше)
    public static (double score, double iouFill, double iouEdge)
        ScoreAgainstTemplates(Mat normPatch, TemplateLibrary tpl, PinColor c)
        => ScoreAgainstTemplates(normPatch, tpl, c, 0.7);
}

// ===========================
// Utilities
// ===========================
public static class Util
{
    public static Random GlobalRng = new(1337);

    public static Point ClampPoint(Point p, Size imgSize)
    {
        int x = Math.Max(0, Math.Min(imgSize.Width - 1, p.X));
        int y = Math.Max(0, Math.Min(imgSize.Height - 1, p.Y));
        return new Point(x, y);
    }

    public static int CountConnected(Mat binaryMask)
    {
        // expects 0/255 8UC1
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = CvInvoke.ConnectedComponentsWithStats(binaryMask, labels, stats, centroids,
            LineType.EightConnected, DepthType.Cv32S);
        // n includes background label 0
        return Math.Max(0, n - 1);
    }

    public static string EnsureDir(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static double Euclid(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static (int hue, int sat, int val) HexToHsvOpenCv(string hex)
    {
        if (hex.StartsWith('#')) hex = hex[1..];
        var r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
        var g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
        var b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
        using var mat = new Mat(1, 1, DepthType.Cv8U, 3);
        mat.SetTo(new MCvScalar(b, g, r)); // BGR order
        using var hsv = new Mat();
        CvInvoke.CvtColor(mat, hsv, ColorConversion.Bgr2Hsv);
        var data = (byte[,,])hsv.GetData();
        byte H = data[0, 0, 0];
        byte S = data[0, 0, 1];
        byte V = data[0, 0, 2];
        return (H, S, V);
    }

    public static int ToOdd(int x)
    {
        if (x <= 1) return 1;
        return x % 2 == 1 ? x : x + 1;
    }
}

public class CandidateResult
{
    public int FalsePositiveTotal;
    public int MatchedTotal;
    public double MeanDistance;

    // over matched
    public double MedianDistance;

    public int MissingTotal;
    public ParameterSet Params;

    // diagnostic
    public List<PerImageEval> PerImageDetails = [];

    public double Score;              // lower is better

    // Нове: шаблони, які були використані при оцінці найкращого кандидата
    public TemplateLibrary? TemplatesUsed;
}

public class ColorParams
{
    public int HueCenter; // 0..179 (OpenCV HSV)
    public int HueTol;    // 3..40
    public int SatMin;    // 0..255
    public int ValMin;    // 0..255

    public ColorParams Clone()
    {
        return new ColorParams { HueCenter = HueCenter, HueTol = HueTol, SatMin = SatMin, ValMin = ValMin };
    }
}

public class ContourDiag
{
    public int Area;
    public double Aspect;
    public int BBoxH;
    public int BBoxW;
    public PinColor Color;
    public int ImgArea;
    public int KernelClose;
    public int KernelOpen;
    public int MaskNonZero;
    public int MaskNonZeroRaw;
    public string? RejectReason;
    public float Scale;
    public double TemplateIoUEdge;
    public double TemplateIoUFill;

    // "pass","tooSmall","tooBig","aspectFail","recoveredFromRaw","allRejected","suppressedNMS"
    public double TemplateScore;

    // exclusive raw nonzero
    public Point Tip;

    public double TipAngle;
    public bool UsedAsPrediction;
}

// ===========================
// Data models for config/IO
// ===========================
public class DebugOptions
{
    [JsonPropertyName("dump_colors")] public List<string> DumpColors { get; set; } = new();
    [JsonPropertyName("dump_dir")] public string DumpDir { get; set; } = "output/debug";
    [JsonPropertyName("dump_images")] public List<string> DumpImages { get; set; } = new();
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("max_rejected_per_color")] public int MaxRejectedPerColor { get; set; } = 50;

    [JsonPropertyName("save_hsv_channels")] public bool SaveHsvChannels { get; set; } = true;

    [JsonPropertyName("save_pin_masks")] public bool SavePinMasks { get; set; } = true;

    [JsonPropertyName("save_rejected_contours")] public bool SaveRejectedContours { get; set; } = true;

    // e.g. ["Yellow"]
    [JsonPropertyName("save_stages")] public bool SaveStages { get; set; } = true;

    [JsonPropertyName("tile_composite")] public bool TileComposite { get; set; } = true;
}

public class DetectionResultPin
{
    public List<ContourDiag> ContourDiagnostics = [];
    public string? Error;
    public string? Path;
    public List<Point> Predictions = [];
}

public class GtPin
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
}

public class ParameterSet
{
    public int Blur;
    public int CloseSize;
    public ColorParams[]? Colors;
    public int DilateIterations;
    public int ErodeIterations;
    public float MaxAreaRatio;
    public float MinAreaRatio;
    public int OpenSize;
    public float Scale;

    // NEW: tunables for templates/NMS/aspect/close gating
    public double TplMinScore = 0.45;            // 0..1, було 0.45 в коді
    public double AspectMin = 1.05;              // було 1.05
    public double NmsRadius = 22.0;              // px, було 22.0
    public double TplEdgeWeight = 0.70;          // 0..1; вага IoU по ребру в скорі шаблону (fill = 1 - edge)
    public double TemplateBuildFillFrac = 0.50;  // 0..1; поріг під час побудови fill
    public double TemplateBuildEdgeFrac = 0.45;  // 0..1; поріг під час побудови edge
    public int CloseIfComponentsLE = 1;          // close, якщо кількість компонент <= цього значення

    public ParameterSet Clone()
    {
        return new ParameterSet
        {
            Scale = Scale,
            Blur = Blur,
            OpenSize = OpenSize,
            CloseSize = CloseSize,
            ErodeIterations = ErodeIterations,
            DilateIterations = DilateIterations,
            MinAreaRatio = MinAreaRatio,
            MaxAreaRatio = MaxAreaRatio,
            Colors = [.. Colors.Select(c => c.Clone())],

            // NEW
            TplMinScore = TplMinScore,
            AspectMin = AspectMin,
            NmsRadius = NmsRadius,
            TplEdgeWeight = TplEdgeWeight,
            TemplateBuildFillFrac = TemplateBuildFillFrac,
            TemplateBuildEdgeFrac = TemplateBuildEdgeFrac,
            CloseIfComponentsLE = CloseIfComponentsLE
        };
    }
}


public class PerImageEval
{
    // Additional diagnostics
    public List<ContourDiag> ContoursDiag = [];

    public double[]? Distances;
    public int FalsePos;
    public List<Point> Gt = [];

    // matched distances (after optimal assignment), length = matched count
    public int Missing;

    public string? Path;
    public List<Point> Predictions = [];
    // gt unmatched
    // preds unmatched

    // per color (0..3) chosen contour diag if any
}

// ===========================
// Detector (CPU)
// ===========================
public class PinDetector(TemplateLibrary? templates = null)
{
    private static readonly PinColor[] ColorsAll =
    [PinColor.Green, PinColor.Orange, PinColor.Yellow, PinColor.Blue];

    private static readonly PinColor[][] ColorsSingle =
    [
        [PinColor.Green],
        [PinColor.Orange],
        [PinColor.Yellow],
        [PinColor.Blue],
    ];

    private readonly TemplateLibrary? _templates = templates; // can be null

    public DetectionResultPin DetectForImage(string path, ParameterSet p)
    {
        using var src = CvInvoke.Imread(path, ImreadModes.AnyColor);
        if (src.IsEmpty) return new DetectionResultPin { Path = path, Error = $"Failed to read image: {path}" };
        return DetectInternal(src, path, p, ColorsAll);
    }

    public DetectionResultPin DetectForImage(string path, ParameterSet p, PinColor color)
    {
        using var src = CvInvoke.Imread(path, ImreadModes.AnyColor);
        if (src.IsEmpty) return new DetectionResultPin { Path = path, Error = $"Failed to read image: {path}" };
        return DetectInternal(src, path, p, ColorsSingle[(int)color]);
    }

    public DetectionResultPin DetectForImage(string path, ParameterSet p, IEnumerable<PinColor> colors)
    {
        using var src = CvInvoke.Imread(path, ImreadModes.AnyColor);
        if (src.IsEmpty) return new DetectionResultPin { Path = path, Error = $"Failed to read image: {path}" };
        var arr = colors is PinColor[] a ? a : colors.ToArray();
        return DetectInternal(src, path, p, arr);
    }

    public DetectionResultPin DetectFromMat(Mat srcBgr, string label, ParameterSet p)
    => DetectInternal(srcBgr, label, p, ColorsAll);

    public DetectionResultPin DetectFromMat(Mat srcBgr, string label, ParameterSet p, PinColor color)
        => DetectInternal(srcBgr, label, p, ColorsSingle[(int)color]);

    public DetectionResultPin DetectFromMat(Mat srcBgr, string label, ParameterSet p, IEnumerable<PinColor> colors)
    {
        var arr = colors is PinColor[] a ? a : colors.ToArray();
        return DetectInternal(srcBgr, label, p, arr);
    }

    internal static Mat CreateMaskCpu(Mat hsv, ColorParams cp)
    {
        int hLo = cp.HueCenter - cp.HueTol;
        int hHi = cp.HueCenter + cp.HueTol;
        Mat mask = new();
        if (hLo >= 0 && hHi <= 179)
        {
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(hLo, cp.SatMin, cp.ValMin)),
                                 new ScalarArray(new MCvScalar(hHi, 255, 255)), mask);
        }
        else
        {
            int low1 = (hLo % 180 + 180) % 180;
            int high1 = 179;
            int low2 = 0;
            int high2 = (hHi % 180 + 180) % 180;

            Mat m1 = new();
            Mat m2 = new();
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(low1, cp.SatMin, cp.ValMin)),
                                 new ScalarArray(new MCvScalar(high1, 255, 255)), m1);
            CvInvoke.InRange(hsv, new ScalarArray(new MCvScalar(low2, cp.SatMin, cp.ValMin)),
                                 new ScalarArray(new MCvScalar(high2, 255, 255)), m2);
            CvInvoke.BitwiseOr(m1, m2, mask);
            m1.Dispose(); m2.Dispose();
        }
        return mask;
    }

    // "Гостряк" — найнижча точка контуру.
    internal static Point FindBottomTip(VectorOfPoint contour, Size imgSize)
    {
        var pts = contour.ToArray();
        int maxY = int.MinValue;
        List<Point> bottoms = [];
        foreach (var p in pts)
        {
            if (p.Y > maxY)
            {
                maxY = p.Y;
                bottoms.Clear();
                bottoms.Add(p);
            }
            else if (p.Y == maxY)
            {
                bottoms.Add(p);
            }
        }

        if (bottoms.Count == 1) return Util.ClampPoint(bottoms[0], imgSize);
        double avgX = pts.Average(pt => pt.X);
        Point best = bottoms.OrderBy(p => Math.Abs(p.X - avgX)).First();
        return Util.ClampPoint(best, imgSize);
    }

    private static double AngleDeg(Point a, Point b, Point c)
    {
        double ux = a.X - b.X, uy = a.Y - b.Y;
        double vx = c.X - b.X, vy = c.Y - b.Y;
        double du = Math.Sqrt(ux * ux + uy * uy) + 1e-9;
        double dv = Math.Sqrt(vx * vx + vy * vy) + 1e-9;
        double cosA = (ux * vx + uy * vy) / (du * dv);
        cosA = Math.Max(-1, Math.Min(1, cosA));
        return Math.Acos(cosA) * 180.0 / Math.PI;
    }

    private static List<int> ComputeNmsKeepIndices(List<Point> preds, List<ContourDiag> diags, double radius)
    {
        if (preds.Count <= 1) return Enumerable.Range(0, preds.Count).ToList();

        double Score(ContourDiag d) => 0.6 * d.Aspect + 0.0015 * d.Area - 0.01 * d.TipAngle;

        var order = Enumerable.Range(0, preds.Count).OrderByDescending(i => Score(diags[i])).ToList();
        var taken = new bool[preds.Count];
        var keepIdx = new List<int>();

        foreach (var i in order)
        {
            if (taken[i]) continue;
            keepIdx.Add(i);
            for (int j = 0; j < preds.Count; j++)
            {
                if (taken[j] || j == i) continue;
                double dx = preds[i].X - preds[j].X;
                double dy = preds[i].Y - preds[j].Y;
                if (dx * dx + dy * dy <= radius * radius)
                    taken[j] = true;
            }
        }
        keepIdx.Sort();
        return keepIdx;
    }

    private static double ComputeTipAngleDeg(VectorOfPoint contour, Point tip)
    {
        var pts = contour.ToArray();
        int idx = Array.FindIndex(pts, p => p == tip);
        if (idx < 0)
        {
            idx = Array.FindIndex(pts, p => Math.Abs(p.X - tip.X) <= 1 && Math.Abs(p.Y - tip.Y) <= 1);
        }
        if (idx < 0) return 180.0;

        int step = Math.Max(1, pts.Length / 50);
        var a = pts[(idx - step + pts.Length) % pts.Length];
        var b = tip;
        var c = pts[(idx + step) % pts.Length];

        return AngleDeg(a, b, c);
    }

    private static (Point tip, ContourDiag diag)? TryRecoverFromRawMerge(
        Mat maskRaw,
        Size imgSize,
        List<(VectorOfPoint cnt, Rectangle rect)> areaOkButAspectFail,
        double minArea,
        double maxArea,
        double aspectMin,
        TemplateLibrary templates,
        PinColor color
    )
    {
        foreach (var (cnt, rect) in areaOkButAspectFail)
        {
            var roi = Rectangle.Intersect(rect, new Rectangle(Point.Empty, imgSize));
            if (roi.Width < 3 || roi.Height < 3) continue;

            using var rawRoi = new Mat(maskRaw, roi);
            using var sub = new VectorOfVectorOfPoint();
            CvInvoke.FindContours(rawRoi, sub, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);

            double bestScore = double.NegativeInfinity;
            (Point tip, ContourDiag diag)? best = null;

            for (int i = 0; i < sub.Size; i++)
            {
                var pts = sub[i].ToArray();
                for (int k = 0; k < pts.Length; k++) pts[k] = new Point(pts[k].X + roi.X, pts[k].Y + roi.Y);
                using var translated = new VectorOfPoint(pts);

                double area = CvInvoke.ContourArea(translated);
                if (area < minArea || area > maxArea) continue;

                var r = CvInvoke.BoundingRectangle(translated);
                double aspect = r.Height > 0 ? (double)r.Height / Math.Max(1, r.Width) : 0;
                if (aspect < aspectMin) continue;

                var tip = FindBottomTip(translated, imgSize);
                double tipAngle = ComputeTipAngleDeg(translated, tip);

                double tplScore = -1, iouFill = 0, iouEdge = 0;
                if (templates != null && templates.Has)
                {
                    using var roiMask = new Mat(maskRaw.Size, DepthType.Cv8U, 1);
                    roiMask.SetTo(new MCvScalar(0));
                    using var vv = new VectorOfVectorOfPoint();
                    vv.Push(translated);
                    CvInvoke.DrawContours(roiMask, vv, -1, new MCvScalar(255), -1);

                    using var norm = ShapeMatch.NormalizeRoiWithTip(
                        roiMask, CvInvoke.BoundingRectangle(translated), tip,
                        TemplateLibrary.CanonW, TemplateLibrary.CanonH
                    );
                    //(tplScore, iouFill, iouEdge) = ShapeMatch.ScoreAgainstTemplates(norm, templates, color, p.TplEdgeWeight);
                }
                double score = (-tipAngle + 20.0 * Math.Min(aspect, 2.0)) + (tplScore > 0 ? 50.0 * tplScore : 0.0);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = (tip, new ContourDiag
                    {
                        Area = (int)Math.Round(area),
                        BBoxW = r.Width,
                        BBoxH = r.Height,
                        Aspect = aspect,
                        TipAngle = tipAngle,
                        Tip = tip,
                        RejectReason = "recoveredFromRaw"
                    });
                }
            }

            if (best.HasValue) return best;
        }
        return null;
    }

    private DetectionResultPin DetectInternal(Mat srcBgr, string label, ParameterSet p, IReadOnlyList<PinColor> activeColors)
    {
        var result = new DetectionResultPin { Path = label };
        if (srcBgr == null || srcBgr.IsEmpty)
        {
            result.Error = $"Empty Mat for: {label}";
            return result;
        }

        Size sz = srcBgr.Size;
        int imgArea = sz.Width * sz.Height;

        using Mat hsvMat = new();

        CvInvoke.CvtColor(srcBgr, hsvMat, ColorConversion.Bgr2Hsv);

        int kOpen = Math.Max(1, (int)Math.Round(p.OpenSize * p.Scale));
        int kClose = Math.Max(1, (int)Math.Round(p.CloseSize * p.Scale));
        kOpen = Util.ToOdd(kOpen);
        kClose = Util.ToOdd(kClose);
        var kernelOpen = CvInvoke.GetStructuringElement(MorphShapes.Ellipse, new Size(kOpen, kOpen), new Point(-1, -1));
        var kernelClose = CvInvoke.GetStructuringElement(MorphShapes.Ellipse, new Size(kClose, kClose), new Point(-1, -1));

        int blur = Util.ToOdd(Math.Max(1, (int)Math.Round(p.Blur * p.Scale)));

        double minArea = Math.Max(10, p.MinAreaRatio * imgArea);
        double maxArea = Math.Max(minArea * 2, p.MaxAreaRatio * imgArea);

        var predictions = new List<Point>();
        var diag = new List<ContourDiag>();

        var masks = new Dictionary<PinColor, Mat>(activeColors.Count);
        foreach (var col in activeColors)
            masks[col] = CreateMaskCpu(hsvMat, p.Colors[(int)col]);

        foreach (var col in activeColors)
        {
            using var maskCpu = masks[col].Clone();

            int rawNonZero = CvInvoke.CountNonZero(maskCpu);
            int rawComponents = Util.CountConnected(maskCpu);

            if (blur > 1)
            {
                CvInvoke.GaussianBlur(maskCpu, maskCpu, new Size(blur, blur), 0);
                //CvInvoke.Imwrite("test1.png", maskCpu);
                CvInvoke.Threshold(maskCpu, maskCpu, 127, 255, ThresholdType.Binary);
                //CvInvoke.Imwrite("test2.png", maskCpu);
            }

            if (kOpen > 1)
                CvInvoke.MorphologyEx(maskCpu, maskCpu, MorphOp.Open, kernelOpen, new Point(-1, -1), 1, BorderType.Reflect, default);
            //CvInvoke.Imwrite("test3.png", maskCpu);
            bool doClose = rawComponents <= p.CloseIfComponentsLE;
            if (kClose > 1 && doClose)
                CvInvoke.MorphologyEx(maskCpu, maskCpu, MorphOp.Close, kernelClose, new Point(-1, -1), 1, BorderType.Reflect, default);
            //CvInvoke.Imwrite("test4.png", maskCpu);
            if (p.ErodeIterations > 0)
                CvInvoke.Erode(maskCpu, maskCpu, null, new Point(-1, -1), p.ErodeIterations, BorderType.Reflect, default);
            //CvInvoke.Imwrite("test5.png", maskCpu);
            if (p.DilateIterations > 0)
                CvInvoke.Dilate(maskCpu, maskCpu, null, new Point(-1, -1), p.DilateIterations, BorderType.Reflect, default);
            //CvInvoke.Imwrite("test6.png", maskCpu);
            using var contours = new VectorOfVectorOfPoint();
            CvInvoke.FindContours(maskCpu, contours, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);
            //CvInvoke.Imwrite("test7.png", maskCpu);
            int maskNonZero = CvInvoke.CountNonZero(maskCpu);
            ContourDiag? bestDiag = null;

            double bestArea = -1;
            int bestIdx = -1;

            var areaOkButAspectFail = new List<(VectorOfPoint cnt, Rectangle rect)>();

            for (int i = 0; i < contours.Size; i++)
            {
                double area = CvInvoke.ContourArea(contours[i]);
                if (area < minArea || area > maxArea) continue;

                var rect = CvInvoke.BoundingRectangle(contours[i]);
                double aspect = rect.Height > 0 ? (double)rect.Height / Math.Max(1, rect.Width) : 0;
                double aspectMin = p.AspectMin;
                bool aspectOk = aspect >= aspectMin;

                double tplScore = -1, iouFill = 0, iouEdge = 0;
                if (_templates?.Has == true)
                {
                    using var norm = ShapeMatch.NormalizeFromContour(maskCpu, contours[i], TemplateLibrary.CanonW, TemplateLibrary.CanonH, sz);
                    (tplScore, iouFill, iouEdge) = ShapeMatch.ScoreAgainstTemplates(norm, _templates, col, p.TplEdgeWeight);
                    if (tplScore < p.TplMinScore) { aspectOk = false; }
                }

                if (!aspectOk)
                {
                    areaOkButAspectFail.Add((new VectorOfPoint(contours[i].ToArray()), rect));
                    continue;
                }

                bool better;
                if (_templates?.Has == true)
                {
                    if (bestDiag == null || tplScore > bestDiag.TemplateScore) better = true; else better = false;
                }
                else
                {
                    if (area > bestArea) better = true; else better = false;
                }

                if (better)
                {
                    bestArea = area;
                    bestIdx = i;

                    var tip = FindBottomTip(contours[i], sz);
                    double tipAngle = ComputeTipAngleDeg(contours[i], tip);

                    bestDiag = new ContourDiag
                    {
                        Color = col,
                        Area = (int)Math.Round(area),
                        BBoxW = rect.Width,
                        BBoxH = rect.Height,
                        Aspect = aspect,
                        TipAngle = tipAngle,
                        KernelOpen = kOpen,
                        KernelClose = doClose ? kClose : 0,
                        Scale = p.Scale,
                        MaskNonZero = maskNonZero,
                        ImgArea = imgArea,
                        Tip = tip,
                        UsedAsPrediction = true,
                        RejectReason = "pass",
                        TemplateScore = tplScore,
                        TemplateIoUFill = iouFill,
                        TemplateIoUEdge = iouEdge
                    };
                }
            }

            if (bestIdx >= 0)
            {
                predictions.Add(bestDiag.Tip);
                diag.Add(bestDiag);
            }
            else
            {
                //var recovered = TryRecoverFromRawMerge(
                //            maskRaw, sz, areaOkButAspectFail, minArea, maxArea, aspectMin: 1.05,
                //            _templates, (PinColor)c
                //        );
                //if (recovered.HasValue)
                //{
                //    var (tip, rd) = recovered.Value;
                //    rd.Color = (PinColor)c;
                //    rd.KernelOpen = kOpen;
                //    rd.KernelClose = doClose ? kClose : 0;
                //    rd.Scale = p.Scale;
                //    rd.MaskNonZero = maskNonZero;
                //    rd.MaskNonZeroRaw = rawNonZero;
                //    rd.ImgArea = imgArea;
                //    rd.UsedAsPrediction = true;

                //    predictions.Add(tip);
                //    diagByColor.Add(rd);
                //}
                //else
                //{
                diag.Add(new ContourDiag
                {
                    Color = col,
                    Area = 0,
                    BBoxW = 0,
                    BBoxH = 0,
                    Aspect = 0,
                    TipAngle = 180.0,
                    KernelOpen = kOpen,
                    KernelClose = doClose ? kClose : 0,
                    Scale = p.Scale,
                    MaskNonZero = maskNonZero,
                    MaskNonZeroRaw = rawNonZero,
                    ImgArea = imgArea,
                    UsedAsPrediction = false,
                    RejectReason = "allRejected"
                });
                //}
            }

            maskCpu.Dispose();
        }

        // NMS
        var predAligned = new List<Point>();
        var diagAligned = new List<ContourDiag>();
        var mapAlignedToGlobal = new List<int>();
        for (int i = 0; i < diag.Count; i++)
        {
            if (diag[i].UsedAsPrediction)
            {
                predAligned.Add(diag[i].Tip);
                diagAligned.Add(diag[i]);
                mapAlignedToGlobal.Add(i);
            }
        }
        if (predAligned.Count > 1)
        {
            var keepIdxAligned = ComputeNmsKeepIndices(predAligned, diagAligned, p.NmsRadius);
            var newPreds = new List<Point>();
            for (int ai = 0; ai < predAligned.Count; ai++)
            {
                int gi = mapAlignedToGlobal[ai];
                bool keep = keepIdxAligned.Contains(ai);
                diag[gi].UsedAsPrediction = keep;
                if (!keep)
                {
                    diag[gi].RejectReason = "suppressedNMS";
                }
                else
                {
                    newPreds.Add(predAligned[ai]);
                }
            }
            predictions = newPreds;
        }

        result.Predictions = predictions;
        result.ContourDiagnostics = diag;

        kernelOpen?.Dispose();
        kernelClose?.Dispose();
        foreach (var m in masks.Values) m.Dispose();

        return result;
    }
}

// ===========================
// Progress reporting
// ===========================
public class ProgressBar(long total, string label)
{
    public long candidatesDone = 0;
    private readonly string _label = label;
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly long _total = total;
    private long _done = 0;
    private long _lastPrint = 0;
    // додайте це поруч з ProgressBar

    public void Done()
    {
        Tick();
        Console.WriteLine();
    }

    public void Tick()
    {
        var d = Interlocked.Increment(ref _done);
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastPrint) > Stopwatch.Frequency / 5)
        {
            lock (_lock)
            {
                var elapsed = _sw.Elapsed;
                double frac = Math.Max(1e-9, Math.Min(1.0, (double)d / _total));
                double etaSec = elapsed.TotalSeconds * (1.0 / frac - 1.0);
                Console.Write($"\r{_label} {d}/{_total} ({frac * 100:0.0}%) ETA {etaSec:0}s    ");
                _lastPrint = Stopwatch.GetTimestamp();
            }
        }
    }
}

public class TemplateLibrary : IDisposable
{
    public const int CanonH = 96;
    public const int CanonW = 76;
    private Mat _edgeBin;   // єдиний 8UC1, 0/255
    private Mat _fillBin;   // єдиний 8UC1, 0/255
    private bool _has;

    public bool Has => _has;

    public static TemplateLibrary Build(IList<DatasetItem> items, ParameterSet p)
    {
        var tpl = new TemplateLibrary();

        // Один акумулятор на всі кольори
        var sumFillAll = new Mat(CanonH, CanonW, DepthType.Cv32F, 1); sumFillAll.SetTo(new MCvScalar(0));
        var sumEdgeAll = new Mat(CanonH, CanonW, DepthType.Cv32F, 1); sumEdgeAll.SetTo(new MCvScalar(0));
        int countAll = 0;

        foreach (var item in items)
        {
            using var src = CvInvoke.Imread(item.Path, ImreadModes.AnyColor);
            if (src.IsEmpty) continue;

            using var hsv = new Mat();
            CvInvoke.CvtColor(src, hsv, ColorConversion.Bgr2Hsv);

            // 1) raw masks
            var rawMasks = new Mat[4];
            for (int c = 0; c < 4; c++) rawMasks[c] = PinDetector.CreateMaskCpu(hsv, p.Colors[c]);

            // 2) фінальні маски напряму з raw
            var finalMasks = new Mat[4];
            BuildFinalMasks(p, rawMasks, finalMasks, src.Size);

            // Пороги за площею/аспектом
            double imgArea = src.Size.Width * src.Size.Height;
            double minArea = Math.Max(10, p.MinAreaRatio * imgArea);
            double maxArea = Math.Max(minArea * 2, p.MaxAreaRatio * imgArea);

            foreach (var gt in item.GtPins)
            {
                var pt = new Point(gt.X, gt.Y);

                VectorOfPoint bestCnt = null;
                Rectangle bestRoi = Rectangle.Empty;
                double bestArea = -1;

                for (int c = 0; c < 4; c++)
                {
                    using var labels = new Mat();
                    using var stats = new Mat();
                    using var centroids = new Mat();
                    CvInvoke.ConnectedComponentsWithStats(finalMasks[c], labels, stats, centroids, LineType.EightConnected, DepthType.Cv32S);
                    var labArr = (int[,])labels.GetData(true);

                    int lbl = (pt.Y >= 0 && pt.Y < labels.Rows && pt.X >= 0 && pt.X < labels.Cols) ? labArr[pt.Y, pt.X] : 0;
                    if (lbl <= 0)
                    {
                        for (int dy = -2; dy <= 2 && lbl <= 0; dy++)
                            for (int dx = -2; dx <= 2 && lbl <= 0; dx++)
                            {
                                int ny = pt.Y + dy, nx = pt.X + dx;
                                if (ny >= 0 && ny < labels.Rows && nx >= 0 && nx < labels.Cols)
                                    lbl = Math.Max(lbl, labArr[ny, nx]);
                            }
                    }
                    if (lbl <= 0) continue;

                    var statsArr = (int[,])stats.GetData(true);
                    int x = statsArr[lbl, (int)ConnectedComponentsTypes.Left];
                    int y = statsArr[lbl, (int)ConnectedComponentsTypes.Top];
                    int w = statsArr[lbl, (int)ConnectedComponentsTypes.Width];
                    int h = statsArr[lbl, (int)ConnectedComponentsTypes.Height];
                    var roi = Rectangle.Intersect(new Rectangle(x, y, w, h), new Rectangle(Point.Empty, src.Size));
                    if (roi.Width < 3 || roi.Height < 3) continue;

                    using var labelsRoi = new Mat(labels, roi);
                    using var eq = new Mat();
                    CvInvoke.Compare(labelsRoi, new ScalarArray(new MCvScalar(lbl)), eq, CmpType.Equal); // 255 – наша компонента
                    using var cnts = new VectorOfVectorOfPoint();
                    CvInvoke.FindContours(eq, cnts, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);
                    if (cnts.Size == 0) continue;

                    var cnt = cnts[0].ToArray();
                    for (int i = 0; i < cnt.Length; i++) cnt[i] = new Point(cnt[i].X + roi.X, cnt[i].Y + roi.Y);
                    using var cntGlobal = new VectorOfPoint(cnt);

                    double area = CvInvoke.ContourArea(cntGlobal);
                    if (area < minArea || area > maxArea) continue;

                    var rect = CvInvoke.BoundingRectangle(cntGlobal);
                    double aspect = rect.Height > 0 ? (double)rect.Height / Math.Max(1, rect.Width) : 0;
                    if (aspect < 1.05) continue;

                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestRoi = rect;
                        bestCnt?.Dispose();
                        bestCnt = new VectorOfPoint(cnt);
                    }
                }

                if (bestArea <= 0 || bestCnt == null) continue;

                var tip = PinDetector.FindBottomTip(bestCnt, src.Size);

                // union усіх finalMasks
                using var unionMask = new Mat(src.Size, DepthType.Cv8U, 1);
                unionMask.SetTo(new MCvScalar(0));
                for (int c = 0; c < 4; c++) CvInvoke.BitwiseOr(unionMask, finalMasks[c], unionMask);

                using var patch = ShapeMatch.NormalizeRoiWithTip(unionMask, bestRoi, tip, CanonW, CanonH);
                using var edge = ShapeMatch.MorphGradient(patch);

                AddTo(sumFillAll, patch);
                AddTo(sumEdgeAll, edge);
                countAll++;

                bestCnt.Dispose();
            }

            for (int c = 0; c < 4; c++) { rawMasks[c]?.Dispose(); finalMasks[c]?.Dispose(); }
        }

        if (countAll > 0)
        {
            var fill = ThresholdToBinary(sumFillAll, (float)(p.TemplateBuildFillFrac * countAll));
            var edge = ThresholdToBinary(sumEdgeAll, (float)(p.TemplateBuildEdgeFrac * countAll));

            tpl._fillBin = fill.Clone();
            tpl._edgeBin = edge.Clone();
            tpl._has = true;

            fill.Dispose();
            edge.Dispose();
        }

        sumFillAll.Dispose();
        sumEdgeAll.Dispose();
        return tpl;

        static void AddTo(Mat sum, Mat bin8u)
        {
            using var f = new Mat();
            bin8u.ConvertTo(f, DepthType.Cv32F, 1.0 / 255.0);
            CvInvoke.Accumulate(f, sum);
        }

        static Mat ThresholdToBinary(Mat sum, float thr)
        {
            var out8 = new Mat(sum.Size, DepthType.Cv8U, 1);
            using var tmp = new Mat();
            CvInvoke.Threshold(sum, tmp, thr, 1.0, ThresholdType.Binary);
            tmp.ConvertTo(out8, DepthType.Cv8U, 255.0);
            return out8;
        }

        static void BuildFinalMasks(ParameterSet p, Mat[] inputMasks, Mat[] outFinal, Size sz)
        {
            int kOpen = Util.ToOdd(Math.Max(1, (int)Math.Round(p.OpenSize * p.Scale)));
            int kClose = Util.ToOdd(Math.Max(1, (int)Math.Round(p.CloseSize * p.Scale)));
            int blur = Util.ToOdd(Math.Max(1, (int)Math.Round(p.Blur * p.Scale)));

            using var kernelOpen = CvInvoke.GetStructuringElement(MorphShapes.Ellipse, new Size(kOpen, kOpen), new Point(-1, -1));
            using var kernelClose = CvInvoke.GetStructuringElement(MorphShapes.Ellipse, new Size(kClose, kClose), new Point(-1, -1));

            for (int c = 0; c < 4; c++)
            {
                outFinal[c] = inputMasks[c].Clone();

                if (blur > 1)
                {
                    CvInvoke.GaussianBlur(outFinal[c], outFinal[c], new Size(blur, blur), 0);
                    CvInvoke.Threshold(outFinal[c], outFinal[c], 127, 255, ThresholdType.Binary);
                }

                if (kOpen > 1) CvInvoke.MorphologyEx(outFinal[c], outFinal[c], MorphOp.Open, kernelOpen, new Point(-1, -1), 1, BorderType.Reflect, default);
                int comps = Util.CountConnected(outFinal[c]);
                if (kClose > 1 && comps <= p.CloseIfComponentsLE) CvInvoke.MorphologyEx(outFinal[c], outFinal[c], MorphOp.Close, kernelClose, new Point(-1, -1), 1, BorderType.Reflect, default);
                if (p.ErodeIterations > 0) CvInvoke.Erode(outFinal[c], outFinal[c], null, new Point(-1, -1), p.ErodeIterations, BorderType.Reflect, default);
                if (p.DilateIterations > 0) CvInvoke.Dilate(outFinal[c], outFinal[c], null, new Point(-1, -1), p.DilateIterations, BorderType.Reflect, default);
            }
        }
    }

    public static TemplateLibrary LoadFromDir(string dir)
    {
        var tpl = new TemplateLibrary();

        // 1) Спробувати «одну маску»
        string commonFill = Path.Combine(dir, "common_fill.png");
        string commonEdge = Path.Combine(dir, "common_edge.png");
        if (File.Exists(commonFill) && File.Exists(commonEdge))
        {
            var fill = CvInvoke.Imread(commonFill, ImreadModes.Grayscale);
            var edge = CvInvoke.Imread(commonEdge, ImreadModes.Grayscale);
            if (fill.Size.Width != CanonW || fill.Size.Height != CanonH)
                CvInvoke.Resize(fill, fill, new Size(CanonW, CanonH), 0, 0, Inter.Nearest);
            if (edge.Size.Width != CanonW || edge.Size.Height != CanonH)
                CvInvoke.Resize(edge, edge, new Size(CanonW, CanonH), 0, 0, Inter.Nearest);

            tpl._fillBin = fill;
            tpl._edgeBin = edge;
            tpl._has = true;
            return tpl;
        }

        // 2) Legacy-файли по кольорах — беремо першу знайдену пару
        foreach (PinColor c in Enum.GetValues(typeof(PinColor)))
        {
            string name = c.ToString().ToLowerInvariant();
            string fillPath = Path.Combine(dir, $"{name}_fill.png");
            string edgePath = Path.Combine(dir, $"{name}_edge.png");
            if (File.Exists(fillPath) && File.Exists(edgePath))
            {
                var fill = CvInvoke.Imread(fillPath, ImreadModes.Grayscale);
                var edge = CvInvoke.Imread(edgePath, ImreadModes.Grayscale);
                if (fill.Size.Width != CanonW || fill.Size.Height != CanonH)
                    CvInvoke.Resize(fill, fill, new Size(CanonW, CanonH), 0, 0, Inter.Nearest);
                if (edge.Size.Width != CanonW || edge.Size.Height != CanonH)
                    CvInvoke.Resize(edge, edge, new Size(CanonW, CanonH), 0, 0, Inter.Nearest);

                tpl._fillBin = fill;
                tpl._edgeBin = edge;
                tpl._has = true;
                break;
            }
        }
        return tpl;
    }

    // Нове: клонування шаблонів (для безпечного використання в паралелі)
    public TemplateLibrary Clone()
    {
        var t = new TemplateLibrary();
        if (_has)
        {
            t._fillBin = _fillBin?.Clone();
            t._edgeBin = _edgeBin?.Clone();
            t._has = true;
        }
        return t;
    }

    public void Dispose()
    {
        _fillBin?.Dispose();
        _edgeBin?.Dispose();
    }

    public Mat GetEdge() => _edgeBin;

    public Mat GetFill() => _fillBin;

    public void SaveToDir(string dir)
    {
        Util.EnsureDir(dir);
        if (!_has || _fillBin == null || _edgeBin == null) return;

        string fillPath = Path.Combine(dir, "common_fill.png");
        string edgePath = Path.Combine(dir, "common_edge.png");
        CvInvoke.Imwrite(fillPath, _fillBin);
        CvInvoke.Imwrite(edgePath, _edgeBin);

        var meta = new
        {
            mode = "single",
            fill = fillPath,
            edge = edgePath,
            canon_w = CanonW,
            canon_h = CanonH
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public class TrainingConfig
{
    [JsonPropertyName("debug")] public DebugOptions Debug { get; set; } = new();
    [JsonPropertyName("items")] public List<DatasetItem> Items { get; set; } = [];
    [JsonPropertyName("stage1_iterations")] public int Stage1Iterations { get; set; } = 250;
    [JsonPropertyName("stage2_iterations")] public int Stage2Iterations { get; set; } = 120;
    [JsonPropertyName("threads")] public int Threads { get; set; } = 0;
}

// ===========================
// Program
// ===========================
public class ProgramPin
{
    public static async Task<CandidateResult> EvaluateCandidatesParallel(
        IList<DatasetItem> items,
        IList<ParameterSet> candidates,
        string label,
        int threads,
        TemplateUseStrategy tplStrategy = TemplateUseStrategy.BuildPerCandidate,
        TemplateLibrary fixedTemplates = null)
    {
        var best = new CandidateResult { Score = double.PositiveInfinity, Params = candidates.First() };
        var lockBest = new object();

        long totalSteps = candidates.Count * (long)items.Count;
        var pb = new ProgressBar(totalSteps, label);

        var po = new ParallelOptions { MaxDegreeOfParallelism = threads };
        await Task.Run(() =>
        {
            Parallel.ForEach(candidates, po, candidate =>
            {
                TemplateLibrary tlibLocal = null;
                try
                {
                    // Обрати шаблони за стратегією
                    switch (tplStrategy)
                    {
                        case TemplateUseStrategy.None:
                            tlibLocal = null;
                            break;

                        case TemplateUseStrategy.FixedProvided:
                            tlibLocal = fixedTemplates?.Clone(); // клон для потокобезпеки
                            break;

                        case TemplateUseStrategy.BuildPerCandidate:
                        default:
                            tlibLocal = TemplateLibrary.Build(items, candidate);
                            break;
                    }

                    var detector = new PinDetector(tlibLocal);
                    var res = Evaluator.EvaluateDataset(items, candidate,
                        (path, p) => detector.DetectForImage(path, p),
                        onProgress: pb.Tick);

                    // Оновити best, зберігши шаблони, що використовувались
                    lock (lockBest)
                    {
                        if (res.Score < best.Score)
                        {
                            // звільнити попередні шаблони best
                            best.TemplatesUsed?.Dispose();
                            best = res;
                            best.TemplatesUsed = tlibLocal?.Clone(); // копія того, що ми використовували
                        }
                    }
                }
                finally
                {
                    // ПІСЛЯ КОЖНОГО КАНДИДАТА — друк best_score:
                    long done = Interlocked.Increment(ref pb.candidatesDone);

                    double curBest; int matched, miss, fp; double mean, med;
                    lock (lockBest)
                    {
                        curBest = best.Score;
                        matched = best.MatchedTotal;
                        miss = best.MissingTotal;
                        fp = best.FalsePositiveTotal;
                        mean = best.MeanDistance;
                        med = best.MedianDistance;
                    }

                    Console.WriteLine($"{label} candidate {done}/{candidates.Count} -> best_score={curBest:0.###} (matched={matched}, miss={miss}, fp={fp}, mean={mean:0.##}, median={med:0.##})");

                    // звільнити локальні
                    tlibLocal?.Dispose();
                }
            });
        });

        pb.Done();
        return best;
    }

    public static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            // Modes: train (default, first arg is config.json), detect, live
            string mode = args[0].ToLowerInvariant();
            if (mode == "detect")
            {
                return await RunDetectMode([.. args.Skip(1)]);
            }
            else if (mode == "live")
            {
                return await RunLiveMode([.. args.Skip(1)]);
            }
            else if (mode == "train")
            {
                // Backward-compatible: first arg is config.json
                return await RunTrainMode([.. args.Skip(1)]);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex);
            return 2;
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  PinDetectorTrainer.exe train config.json");
        Console.WriteLine("  PinDetectorTrainer.exe detect output/best_params.json <image_or_dir> [more images/dirs]");
        Console.WriteLine("  PinDetectorTrainer.exe live output/best_params.json \"Process Name\"");
    }

    private static async Task<int> RunDetectMode(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: PinDetectorTrainer.exe detect output/best_params.json <image_or_dir> [more...]");
            return 1;
        }

        string paramsPath = args[0];
        if (!File.Exists(paramsPath))
        {
            Console.WriteLine($"best_params.json not found: {paramsPath}");
            return 1;
        }

        var p = ParamsIO.LoadFromBestParamsJson(paramsPath);

        string dirOfParams = Path.GetDirectoryName(Path.GetFullPath(paramsPath)) ?? ".";
        string templatesDir = Path.Combine(dirOfParams, "best_pin_masks");

        using var tpl = Directory.Exists(templatesDir) ? TemplateLibrary.LoadFromDir(templatesDir) : null;
        var detector = new PinDetector(templates: tpl);

        string outDir = Util.EnsureDir(Path.Combine(dirOfParams, "detect_viz"));
        var inputs = args.Skip(1).ToArray();
        int processed = 0;

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var files = Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var f in files)
                {
                    using var img = CvInvoke.Imread(f, ImreadModes.AnyColor);
                    if (img.IsEmpty) { Console.WriteLine($"Skip empty: {f}"); continue; }
                    var det = detector.DetectFromMat(img, f, p);
                    Reporter.DrawDetectionsOnImage(img, det);
                    string savePath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "_det.png");
                    CvInvoke.Imwrite(savePath, img);
                    Console.WriteLine($"{f}: {det.Predictions.Count} pins -> {savePath}");
                    processed++;
                }
            }
            else if (File.Exists(input))
            {
                using var img = CvInvoke.Imread(input, ImreadModes.AnyColor);
                if (img.IsEmpty) { Console.WriteLine($"Skip empty: {input}"); continue; }
                var det = detector.DetectFromMat(img, input, p);
                Reporter.DrawDetectionsOnImage(img, det);
                string savePath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "_det.png");
                CvInvoke.Imwrite(savePath, img);
                Console.WriteLine($"{input}: {det.Predictions.Count} pins -> {savePath}");
                processed++;
            }
            else
            {
                Console.WriteLine($"Not found: {input}");
            }
        }

        var config = JsonSerializer.Deserialize<TrainingConfig>(await File.ReadAllTextAsync(@"C:\Users\Maks\source\repos\WinFormsApp1\WinFormsApp1\bin\x64\Debug\net8.0-windows\screenshot2\manifest.json"));
        var stage1Candidates = new List<ParameterSet>();
        stage1Candidates.Add(p); // Just evaluate the provided params on the dataset
        var best = await EvaluateCandidatesParallel(config.Items, stage1Candidates, "Stage 1", 1);
        string visDir = Util.EnsureDir(Path.Combine(outDir, "viz"));
        using var bestTemplates = TemplateLibrary.Build(config.Items, best.Params);
        Reporter.SaveBestPinMasks(outDir, bestTemplates);
        Reporter.SaveDiagnosticsCsv(outDir, best);
        Reporter.SaveVisualizations(visDir, best);
        DebuggerViz.DumpIntermediatesFor(config, best.Params, bestTemplates);
        Console.WriteLine($"Detect done. Images processed: {processed}. Output: {outDir}");
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> RunLiveMode(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: PinDetectorTrainer.exe live output/best_params.json \"Process Name\"");
            return 1;
        }

        string paramsPath = args[0];
        string processName = args[1];

        if (!File.Exists(paramsPath))
        {
            Console.WriteLine($"best_params.json not found: {paramsPath}");
            return 1;
        }

        var p = ParamsIO.LoadFromBestParamsJson(paramsPath);
        string dirOfParams = Path.GetDirectoryName(Path.GetFullPath(paramsPath)) ?? ".";
        string templatesDir = Path.Combine(dirOfParams, "best_pin_masks");

        using var tpl = Directory.Exists(templatesDir) ? TemplateLibrary.LoadFromDir(templatesDir) : null;
        var detector = new PinDetector(templates: tpl);

        Console.WriteLine($"Live mode: capturing window \"{processName}\". Press 'q' or ESC to exit.");

        var sw = new Stopwatch();
        int frames = 0;
        sw.Start();

        while (true)
        {
            using var bmp = ScreenshotHelper.CaptureWindow(processName);
            if (bmp == null)
            {
                Console.WriteLine("Window not found or capture failed. Retrying in 1s...");
                await Task.Delay(1000);
                continue;
            }

            using var frame = bmp.ToMat();

            var det = detector.DetectFromMat(frame, "live", p);
            Reporter.DrawDetectionsOnImage(frame, det);

            CvInvoke.Imshow("Live Pin Detector", frame);
            int key = CvInvoke.WaitKey(1);
            if (key == 27 || key == 'q' || key == 'Q') break;

            frames++;
            if (frames % 30 == 0)
            {
                double fps = frames / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                Console.Write($"\rFPS: {fps:0.0}    ");
            }
        }

        CvInvoke.DestroyAllWindows();
        return 0;
    }

    private static async Task<int> RunTrainMode(string[] args)
    {
        string configPath = args[0];
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config not found: {configPath}");
            return 1;
        }

        var config = JsonSerializer.Deserialize<TrainingConfig>(await File.ReadAllTextAsync(configPath));
        if (config == null || config.Items == null || config.Items.Count == 0)
        {
            Console.WriteLine("Invalid config or empty dataset.");
            return 1;
        }

        int threads = config.Threads > 0 ? config.Threads : Math.Max(1, Environment.ProcessorCount - 1);
        Console.WriteLine($"Threads: {threads}");

        string outDir = Util.EnsureDir("output");
        string visDir = Util.EnsureDir(Path.Combine(outDir, "viz"));

        // Stage 1: без шаблонів у детекторі
        // Smart Stage 1: локальний пошук без шаблонів
        var trainer = new SmartTrainer(config.Items, threads);

        // Обмежимо «кількість проходів» з Stage1Iterations (щоб не ганяти 250 разів)
        int maxPasses1 = Math.Min(20, Math.Max(8, config.Stage1Iterations / 20));

        var baseline = Sampler.CreateBaselineFromPalette();
        var stage1Best = await trainer.Optimize(
            baseline,
            label: "Stage 1 (smart, no templates)",
            tplStrategy: TemplateUseStrategy.BuildPerCandidate,
            fixedTemplates: null,
            maxPasses: maxPasses1
        );

        Console.WriteLine($"Best after Stage 1: score={stage1Best.Score:0.###} matched={stage1Best.MatchedTotal} miss={stage1Best.MissingTotal} fp={stage1Best.FalsePositiveTotal} meanDist={stage1Best.MeanDistance:0.##}");

        // Побудувати шаблони за найкращими параметрами Stage 1
        // Побудувати шаблони за найкращими параметрами Stage 1
        using var stage1Templates = TemplateLibrary.Build(config.Items, stage1Best.Params);
        Console.WriteLine("Templates built from Stage 1 best parameters.");

        // Smart Stage 2: локальний пошук з фіксованими шаблонами
        int maxPasses2 = Math.Min(15, Math.Max(6, config.Stage2Iterations / 20));
        var stage2Best = await trainer.Optimize(
            stage1Best.Params,
            label: "Stage 2 (smart, fixed templates)",
            tplStrategy: TemplateUseStrategy.BuildPerCandidate,
            fixedTemplates: stage1Templates,
            maxPasses: maxPasses2
        );

        // Обрати загальний найкращий
        var best = stage2Best.Score < stage1Best.Score ? stage2Best : stage1Best;

        // 1) Перебудувати шаблони з фінальними найкращими параметрами
        using var finalTemplates = TemplateLibrary.Build(config.Items, best.Params);

        // 2) (Опційно, але рекомендовано) Переоцінити датасет з цими фінальними шаблонами,
        //    щоб метрики/CSV відповідали саме цим маскам:
        var finalDetector = new PinDetector(finalTemplates);
        var finalBest = Evaluator.EvaluateDataset(
            config.Items,
            best.Params,
            (path, p) => finalDetector.DetectForImage(path, p)
        );
        // 3) Логи про фінальний результат
        Console.WriteLine($"Final (rebuilt) score={finalBest.Score:0.###}; matched={finalBest.MatchedTotal}; miss={finalBest.MissingTotal}; fp={finalBest.FalsePositiveTotal}; meanDist={finalBest.MeanDistance:0.##}; medianDist={finalBest.MedianDistance:0.##}");

        // 4) Збереження фінальних артефактів
        Reporter.SaveBestParams(outDir, finalBest, templatesRelDir: "best_pin_masks");
        Reporter.SaveBestPinMasks(outDir, finalTemplates);
        Reporter.SaveDiagnosticsCsv(outDir, finalBest);
        Reporter.SaveVisualizations(visDir, finalBest);

        // Debug з фінальними шаблонами
        if (config.Debug != null && config.Debug.Enabled)
        {
            DebuggerViz.DumpIntermediatesFor(config, finalBest.Params, finalTemplates);
        }

        Console.WriteLine("Done. See:");
        Console.WriteLine($" - {Path.Combine(outDir, "best_params.json")}");
        Console.WriteLine($" - {Path.Combine(outDir, "best_pin_masks")}");
        Console.WriteLine($" - {Path.Combine(outDir, "diagnostics_scale.csv")}");
        Console.WriteLine($" - {visDir} for annotated images");

        // Прибрати зайві шаблони, щоб не текла пам’ять
        stage2Best.TemplatesUsed?.Dispose();
        stage1Best.TemplatesUsed?.Dispose();

        return 0;
    }

    private sealed class SmartTrainer(IList<DatasetItem> items, int threads)
    {
        private readonly IList<DatasetItem> _items = items;
        private readonly int _threads = threads;

        public async Task<CandidateResult> Optimize(
            ParameterSet start,
            string label,
            TemplateUseStrategy tplStrategy,
            TemplateLibrary fixedTemplates = null,
            int maxPasses = 12,
            double improveTol = 1e-6
        )
        {
            // Оцінимо базовий варіант
            var init = await ProgramPin.EvaluateCandidatesParallel(
                _items,
                new List<ParameterSet> { start },
                $"{label} Init",
                _threads,
                tplStrategy,
                fixedTemplates
            );
            var best = init;

            var steps = new Steps();

            for (int pass = 1; pass <= maxPasses; pass++)
            {
                bool improved = false;

                // 1) Scale + Blur
                {
                    var cands = BuildScaleBlurNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] scale/blur",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score)
                    {
                        best.TemplatesUsed?.Dispose();
                        best = res;
                        improved = true;
                    }
                    FlushTemplates(cands);
                }

                // 2) Open/Close
                {
                    var cands = BuildOpenCloseNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] open/close",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score)
                    {
                        best.TemplatesUsed?.Dispose();
                        best = res;
                        improved = true;
                    }
                    FlushTemplates(cands);
                }

                // 3) Erode/Dilate
                {
                    var cands = BuildErodeDilateNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] erode/dilate",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score)
                    {
                        best.TemplatesUsed?.Dispose();
                        best = res;
                        improved = true;
                    }
                    FlushTemplates(cands);
                }

                // 4) Min/Max area
                {
                    var cands = BuildAreaNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] area",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score)
                    {
                        best.TemplatesUsed?.Dispose();
                        best = res;
                        improved = true;
                    }
                    FlushTemplates(cands);
                }

                // 5) По кольорах (HueTol, SatMin, ValMin)
                for (int ci = 0; ci < 4; ci++)
                {
                    var cands = BuildColorNeighbors(best.Params, steps, (PinColor)ci);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] color {(PinColor)ci}",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score)
                    {
                        best.TemplatesUsed?.Dispose();
                        best = res;
                        improved = true;
                    }
                    FlushTemplates(cands);
                }

                // 6) NMS radius
                {
                    var cands = BuildNmsNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] nms radius",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score) { best.TemplatesUsed?.Dispose(); best = res; improved = true; }
                    FlushTemplates(cands);
                }

                // 7) Aspect min
                {
                    var cands = BuildAspectNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] aspect min",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score) { best.TemplatesUsed?.Dispose(); best = res; improved = true; }
                    FlushTemplates(cands);
                }

                // 8) Template min score
                {
                    var cands = BuildTplScoreNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] tpl score",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score) { best.TemplatesUsed?.Dispose(); best = res; improved = true; }
                    FlushTemplates(cands);
                }

                // 9) Template edge weight
                {
                    var cands = BuildTplWeightNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] tpl edge weight",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score) { best.TemplatesUsed?.Dispose(); best = res; improved = true; }
                    FlushTemplates(cands);
                }

                // 10) Template build thresholds
                {
                    var cands = BuildTplBuildFracNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] tpl build thr",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score) { best.TemplatesUsed?.Dispose(); best = res; improved = true; }
                    FlushTemplates(cands);
                }

                // 11) Close gating
                {
                    var cands = BuildCloseGateNeighbors(best.Params, steps);
                    var res = await ProgramPin.EvaluateCandidatesParallel(
                        _items, cands, $"{label} [pass {pass}] close gating",
                        _threads, tplStrategy, fixedTemplates
                    );
                    if (res.Score + improveTol < best.Score) { best.TemplatesUsed?.Dispose(); best = res; improved = true; }
                    FlushTemplates(cands);
                }

                if (!improved)
                {
                    steps.Shrink();
                    if (steps.IsTiny())
                    {
                        Console.WriteLine($"{label}: steps are tiny -> stop at pass {pass}");
                        break;
                    }
                }
            }

            return best;

            static void FlushTemplates(List<ParameterSet> _) { /* no-op, GC */ }
        }

        private static float Clamp(float v, float lo, float hi) => Math.Max(lo, Math.Min(hi, v));

        private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

        private static float ClampExp(float v, double mul, float lo, float hi) => Clamp((float)(v * mul), lo, hi);

        private static List<ParameterSet> Dedup(List<ParameterSet> list)
        {
            // Грубий дедуп по ключових параметрах
            var seen = new HashSet<string>();
            var outList = new List<ParameterSet>();

            foreach (var c in list)
            {
                string key = $"{c.Scale:0.000}|{c.Blur}|{c.OpenSize}|{c.CloseSize}|{c.ErodeIterations}|{c.DilateIterations}|{c.MinAreaRatio:0.######}|{c.MaxAreaRatio:0.######}|"
                           + string.Join("|", c.Colors.Select(cc => $"{cc.HueCenter}:{cc.HueTol}:{cc.SatMin}:{cc.ValMin}"));
                if (seen.Add(key))
                    outList.Add(c);
            }
            return outList;
        }

        private static void FixAreaConsistency(ParameterSet p)
        {
            // гарантуємо розумні межі і співвідношення
            p.MinAreaRatio = Clamp(p.MinAreaRatio, 1e-6f, 0.02f);
            p.MaxAreaRatio = Clamp(p.MaxAreaRatio, 0.0005f, 0.05f);
            if (p.MaxAreaRatio < p.MinAreaRatio * 1.7f)
                p.MaxAreaRatio = Math.Max(p.MaxAreaRatio, p.MinAreaRatio * 1.7f);
        }

        private static int ToOddClamp(int v, int lo, int hi) => Util.ToOdd(Clamp(v, lo, hi));

        private List<ParameterSet> BuildAreaNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();

            // Min up/down
            foreach (var mul in new[] { 1.0 / s.MinAreaMul, s.MinAreaMul })
            {
                var c = p.Clone();
                c.MinAreaRatio = Clamp((float)(c.MinAreaRatio * mul), 1e-6f, 0.02f);
                FixAreaConsistency(c);
                if (Math.Abs(c.MinAreaRatio - p.MinAreaRatio) > 1e-9f) list.Add(c);
            }

            // Max up/down
            foreach (var mul in new[] { 1.0 / s.MaxAreaMul, s.MaxAreaMul })
            {
                var c = p.Clone();
                c.MaxAreaRatio = Clamp((float)(c.MaxAreaRatio * mul), 0.0005f, 0.05f);
                FixAreaConsistency(c);
                if (Math.Abs(c.MaxAreaRatio - p.MaxAreaRatio) > 1e-9f) list.Add(c);
            }

            // Обидва одночасно
            foreach (var mulMin in new[] { 1.0 / s.MinAreaMul, s.MinAreaMul })
                foreach (var mulMax in new[] { 1.0 / s.MaxAreaMul, s.MaxAreaMul })
                {
                    var c = p.Clone();
                    c.MinAreaRatio = Clamp((float)(c.MinAreaRatio * mulMin), 1e-6f, 0.02f);
                    c.MaxAreaRatio = Clamp((float)(c.MaxAreaRatio * mulMax), 0.0005f, 0.05f);
                    FixAreaConsistency(c);
                    if (Math.Abs(c.MinAreaRatio - p.MinAreaRatio) > 1e-9f ||
                        Math.Abs(c.MaxAreaRatio - p.MaxAreaRatio) > 1e-9f)
                    {
                        list.Add(c);
                    }
                }

            return Dedup(list);
        }

        private List<ParameterSet> BuildColorNeighbors(ParameterSet p, Steps s, PinColor color)
        {
            var list = new List<ParameterSet>();
            int idx = (int)color;
            var cp = p.Colors[idx];

            // HueTol ±
            foreach (var dh in new[] { +s.HueTolStep, -s.HueTolStep })
            {
                var c = p.Clone();
                c.Colors[idx].HueTol = Clamp(cp.HueTol + dh, 1, 40);
                if (c.Colors[idx].HueTol != cp.HueTol) { FixAreaConsistency(c); list.Add(c); }
            }

            // SatMin ±
            foreach (var ds in new[] { +s.SatStep, -s.SatStep })
            {
                var c = p.Clone();
                c.Colors[idx].SatMin = Clamp(cp.SatMin + ds, 30, 220);
                if (c.Colors[idx].SatMin != cp.SatMin) { FixAreaConsistency(c); list.Add(c); }
            }

            // ValMin ±
            foreach (var dv in new[] { +s.ValStep, -s.ValStep })
            {
                var c = p.Clone();
                c.Colors[idx].ValMin = Clamp(cp.ValMin + dv, 30, 220);
                if (c.Colors[idx].ValMin != cp.ValMin) { FixAreaConsistency(c); list.Add(c); }
            }

            return Dedup(list);
        }

        private List<ParameterSet> BuildErodeDilateNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            int[] deltas = { s.EdStep, -s.EdStep };
            foreach (var de in deltas)
            {
                var c = p.Clone();
                c.ErodeIterations = Clamp(c.ErodeIterations + de, 0, 4);
                if (c.ErodeIterations != p.ErodeIterations)
                {
                    FixAreaConsistency(c);
                    list.Add(c);
                }
            }
            foreach (var dd in deltas)
            {
                var c = p.Clone();
                c.DilateIterations = Clamp(c.DilateIterations + dd, 0, 4);
                if (c.DilateIterations != p.DilateIterations)
                {
                    FixAreaConsistency(c);
                    list.Add(c);
                }
            }
            // Комбо
            foreach (var de in deltas)
                foreach (var dd in deltas)
                {
                    var c = p.Clone();
                    c.ErodeIterations = Clamp(c.ErodeIterations + de, 0, 4);
                    c.DilateIterations = Clamp(c.DilateIterations + dd, 0, 4);
                    if (c.ErodeIterations != p.ErodeIterations || c.DilateIterations != p.DilateIterations)
                    {
                        FixAreaConsistency(c);
                        list.Add(c);
                    }
                }
            return Dedup(list);
        }

        private List<ParameterSet> BuildOpenCloseNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            int[] deltas = { s.MorphStep, -s.MorphStep };
            foreach (var dOpen in deltas)
                foreach (var dClose in deltas)
                {
                    var c = p.Clone();
                    c.OpenSize = ToOddClamp(c.OpenSize + dOpen, 1, 15);
                    c.CloseSize = ToOddClamp(c.CloseSize + dClose, 1, 15);
                    if (c.OpenSize != p.OpenSize || c.CloseSize != p.CloseSize)
                    {
                        FixAreaConsistency(c);
                        list.Add(c);
                    }
                }
            return Dedup(list);
        }

        private List<ParameterSet> BuildScaleBlurNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            var blurGrid = new[] { 1, 3, 5, 7 };
            int idx = Array.IndexOf(blurGrid, p.Blur);
            var blurOpts = new List<int>();
            if (idx > 0) blurOpts.Add(blurGrid[idx - 1]);
            if (idx >= 0 && idx < blurGrid.Length - 1) blurOpts.Add(blurGrid[idx + 1]);

            // Scale up/down з поточним blur
            foreach (var mul in new[] { 1.0 / s.ScaleMul, s.ScaleMul })
            {
                var c = p.Clone();
                c.Scale = ClampExp(c.Scale, mul, 0.5f, 3.0f);
                FixAreaConsistency(c);
                if (Math.Abs(c.Scale - p.Scale) > 1e-6) list.Add(c);

                foreach (var b in blurOpts)
                {
                    var cb = p.Clone();
                    cb.Scale = c.Scale;
                    cb.Blur = b;
                    FixAreaConsistency(cb);
                    if (cb.Blur != p.Blur || Math.Abs(cb.Scale - p.Scale) > 1e-6) list.Add(cb);
                }
            }

            // Тільки blur-рух
            foreach (var b in blurOpts)
            {
                var c = p.Clone();
                c.Blur = b;
                FixAreaConsistency(c);
                if (c.Blur != p.Blur) list.Add(c);
            }

            return Dedup(list);
        }
        private static double ClampD(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

        // NEW: сусіди NMS
        private List<ParameterSet> BuildNmsNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            foreach (var d in new[] { +s.NmsRadiusStep, -s.NmsRadiusStep })
            {
                var c = p.Clone();
                c.NmsRadius = ClampD(c.NmsRadius + d, 8.0, 50.0);
                if (Math.Abs(c.NmsRadius - p.NmsRadius) > 1e-9) { FixAreaConsistency(c); list.Add(c); }
            }
            return Dedup(list);
        }

        // NEW: сусіди AspectMin
        private List<ParameterSet> BuildAspectNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            foreach (var d in new[] { +s.AspectMinStep, -s.AspectMinStep })
            {
                var c = p.Clone();
                c.AspectMin = ClampD(c.AspectMin + d, 0.9, 1.6);
                if (Math.Abs(c.AspectMin - p.AspectMin) > 1e-9) { FixAreaConsistency(c); list.Add(c); }
            }
            return Dedup(list);
        }

        // NEW: сусіди TplMinScore
        private List<ParameterSet> BuildTplScoreNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            foreach (var d in new[] { +s.TplMinScoreStep, -s.TplMinScoreStep })
            {
                var c = p.Clone();
                c.TplMinScore = ClampD(c.TplMinScore + d, 0.0, 1.0);
                if (Math.Abs(c.TplMinScore - p.TplMinScore) > 1e-9) { FixAreaConsistency(c); list.Add(c); }
            }
            return Dedup(list);
        }

        // NEW: сусіди ваги шаблону
        private List<ParameterSet> BuildTplWeightNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            foreach (var d in new[] { +s.TplEdgeWeightStep, -s.TplEdgeWeightStep })
            {
                var c = p.Clone();
                c.TplEdgeWeight = ClampD(c.TplEdgeWeight + d, 0.0, 1.0);
                if (Math.Abs(c.TplEdgeWeight - p.TplEdgeWeight) > 1e-9) { FixAreaConsistency(c); list.Add(c); }
            }
            return Dedup(list);
        }

        // NEW: сусіди порогів побудови шаблонів
        private List<ParameterSet> BuildTplBuildFracNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            foreach (var df in new[] { +s.TplBuildFracStep, -s.TplBuildFracStep })
            {
                var c1 = p.Clone();
                c1.TemplateBuildFillFrac = ClampD(c1.TemplateBuildFillFrac + df, 0.3, 0.8);
                if (Math.Abs(c1.TemplateBuildFillFrac - p.TemplateBuildFillFrac) > 1e-9) { FixAreaConsistency(c1); list.Add(c1); }

                var c2 = p.Clone();
                c2.TemplateBuildEdgeFrac = ClampD(c2.TemplateBuildEdgeFrac + df, 0.3, 0.8);
                if (Math.Abs(c2.TemplateBuildEdgeFrac - p.TemplateBuildEdgeFrac) > 1e-9) { FixAreaConsistency(c2); list.Add(c2); }
            }
            return Dedup(list);
        }

        // NEW: сусіди для close gating
        private List<ParameterSet> BuildCloseGateNeighbors(ParameterSet p, Steps s)
        {
            var list = new List<ParameterSet>();
            foreach (var dv in new[] { +s.CloseGateStep, -s.CloseGateStep })
            {
                var c = p.Clone();
                c.CloseIfComponentsLE = Clamp(c.CloseIfComponentsLE + dv, 0, 2);
                if (c.CloseIfComponentsLE != p.CloseIfComponentsLE) { FixAreaConsistency(c); list.Add(c); }
            }
            return Dedup(list);
        }

        private sealed class Steps
        {
            public int EdStep = 1;
            public int HueTolStep = 2;
            public double MaxAreaMul = 1.3;
            public double MinAreaMul = 1.6;
            public int MorphStep = 2;
            public int SatStep = 10;
            public double ScaleMul = 1.25;
            public int ValStep = 10;

            // NEW:
            public double NmsRadiusStep = 3.0;
            public double AspectMinStep = 0.03;
            public double TplMinScoreStep = 0.05;
            public double TplEdgeWeightStep = 0.05;
            public double TplBuildFracStep = 0.05;
            public int CloseGateStep = 1;

            public bool IsTiny()
            {
                return ScaleMul <= 1.05
                       && MorphStep <= 1
                       && EdStep <= 1
                       && MinAreaMul <= 1.15
                       && MaxAreaMul <= 1.10
                       && HueTolStep <= 1
                       && SatStep <= 3
                       && ValStep <= 3

                       // NEW tiny checks
                       && NmsRadiusStep <= 1.0
                       && AspectMinStep <= 0.01
                       && TplMinScoreStep <= 0.02
                       && TplEdgeWeightStep <= 0.02
                       && TplBuildFracStep <= 0.02
                       && CloseGateStep <= 1;
            }

            public void Shrink()
            {
                ScaleMul = Math.Max(1.05, Math.Sqrt(ScaleMul));
                MorphStep = Math.Max(1, (MorphStep + 1) / 2);
                EdStep = Math.Max(1, EdStep / 2);
                MinAreaMul = Math.Max(1.15, Math.Sqrt(MinAreaMul));
                MaxAreaMul = Math.Max(1.10, Math.Sqrt(MaxAreaMul));
                HueTolStep = Math.Max(1, HueTolStep / 2);
                SatStep = Math.Max(3, SatStep / 2);
                ValStep = Math.Max(3, ValStep / 2);

                // NEW shrink
                NmsRadiusStep = Math.Max(1.0, NmsRadiusStep / 1.5);
                AspectMinStep = Math.Max(0.01, AspectMinStep / 1.5);
                TplMinScoreStep = Math.Max(0.02, TplMinScoreStep / 1.5);
                TplEdgeWeightStep = Math.Max(0.02, TplEdgeWeightStep / 1.5);
                TplBuildFracStep = Math.Max(0.02, TplBuildFracStep / 1.5);
                CloseGateStep = Math.Max(1, CloseGateStep / 2);
            }
        }
    }
}