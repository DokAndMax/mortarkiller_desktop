using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using static mortarkiller.GridScaleDetector;
using Mat = Emgu.CV.Mat;
using Point = System.Drawing.Point;
using ReduceDimension = Emgu.CV.CvEnum.ReduceDimension;
using Size = System.Drawing.Size;

namespace mortarkiller;

// ===== Дані і метрики =====

public static class GridScaleDetector
{
    public static DetectionResult Detect100m(Mat bgr, DetectorParams p, bool produceDebug, double? priorPeriodPx = null)
    {
        var res = new DetectionResult { Params = p, Success = false };
        try
        {
            var gray = new Mat();
            if (bgr.NumberOfChannels == 3)
                CvInvoke.CvtColor(bgr, gray, ColorConversion.Bgr2Gray);
            else if (bgr.NumberOfChannels == 4)
                CvInvoke.CvtColor(bgr, gray, ColorConversion.Bgra2Gray);
            else
                gray = bgr.Clone();

            if (p.Invert) CvInvoke.BitwiseNot(gray, gray);

            var claheDst = new Mat();
            CvInvoke.CLAHE(gray, p.ClaheClipLimit, new Size(p.ClaheTileGrid, p.ClaheTileGrid), claheDst);
            gray.Dispose();
            gray = claheDst;
            if (p.BlurSigma > 0.01) CvInvoke.GaussianBlur(gray, gray, new Size(0, 0), p.BlurSigma);
            if (p.UnsharpAmount > 0.01)
            {
                var blur = new Mat();
                CvInvoke.GaussianBlur(gray, blur, new Size(0, 0), Math.Max(p.BlurSigma, 0.8));
                CvInvoke.AddWeighted(gray, 1 + p.UnsharpAmount, blur, -p.UnsharpAmount, 0, gray);
                blur.Dispose();
            }
            if (p.UseTophat)
            {
                using var kernel = CvInvoke.GetStructuringElement(
                    MorphShapes.Ellipse,
                    new Size(p.TophatKSize, p.TophatKSize),
                    new Point(-1, -1)
                );
                var th = new Mat();
                CvInvoke.MorphologyEx(
                    gray,
                    th,
                    MorphOp.Tophat,
                    kernel,
                    new Point(-1, -1),
                    1,
                    BorderType.Reflect,
                    new MCvScalar()
                );
                gray.Dispose();
                gray = th;
                kernel.Dispose();
            }
            var dx = new Mat(); var dy = new Mat();
            CvInvoke.Sobel(gray, dx, DepthType.Cv32F, 1, 0, p.SobelKSize);
            CvInvoke.Sobel(gray, dy, DepthType.Cv32F, 0, 1, p.SobelKSize);
            var absdx = AbsMat(dx); var absdy = AbsMat(dy);
            if (Math.Abs(p.GradGamma - 1.0) > 1e-3) { CvInvoke.Pow(absdx, p.GradGamma, absdx); CvInvoke.Pow(absdy, p.GradGamma, absdy); }
            float[] projX = ReduceSum(absdx, dim: 0);
            float[] projY = ReduceSum(absdy, dim: 1);
            projX = Detrend(projX, p.DetrendWindow);
            projY = Detrend(projY, p.DetrendWindow);
            int pminX = Math.Clamp(p.PMin, 2, Math.Max(2, (int)(projX.Length * 0.45)));
            int pmaxX = Math.Clamp(p.PMax, pminX + 1, Math.Max(10, projX.Length - 2));
            int pminY = Math.Clamp(p.PMin, 2, Math.Max(2, (int)(projY.Length * 0.45)));
            int pmaxY = Math.Clamp(p.PMax, pminY + 1, Math.Max(10, projY.Length - 2));

            var (acfX, peaksX, rawX, relX) = AnalyzeAxis(projX, pminX, pmaxX, p.PeakCount);
            var (acfY, peaksY, rawY, relY) = AnalyzeAxis(projY, pminY, pmaxY, p.PeakCount);

            if (rawX <= 0 && rawY <= 0) { res.FailReason = "No valid ACF peaks"; return res; }

            res.RawPeriodX = rawX > 0 ? rawX : double.NaN;
            res.RawPeriodY = rawY > 0 ? rawY : double.NaN;
            double avgRaw = rawX > 0 && rawY > 0 ? 0.5 * (rawX + rawY) : rawX > 0 ? rawX : rawY;
            res.RawConsistency = rawX > 0 && rawY > 0 && avgRaw > 0 ? Math.Abs(rawX - rawY) / avgRaw : double.NaN;
            res.Consistency = double.IsNaN(res.RawConsistency) ? 0 : res.RawConsistency;
            var baseCandidates = BuildBaseCandidates(peaksX, peaksY, rawX, rawY, p);
            if (baseCandidates.Count == 0) { res.FailReason = "No base candidates"; return res; }
            var candScores = new List<CandidateScore>();
            int bestBase = -1;
            double bestScore = double.NegativeInfinity;
            int bestShiftX = 0, bestShiftY = 0;

            var anchor = FindAnchor(peaksX, peaksY, p);
            var candidates = AcquireCandidates(peaksX, peaksY, rawX, rawY, p, anchor);

            foreach (var b in candidates.OrderBy(x => x))
            {
                var harm = CombinedHarmonicScore(peaksX, peaksY, b, p);
                var goert = GoertzelAmpNorm(projX, b) + GoertzelAmpNorm(projY, b);

                int sX, sY;
                var gX = ScorePeriodWindowed(projX, b, p.LineHalfWidth, out sX);
                var gY = ScorePeriodWindowed(projY, b, p.LineHalfWidth, out sY);
                var grid = gX + gY;

                double inter = ScoreIntersections(projX, projY, b, sX, sY);
                double coverX = TileCoverage(projX, b, Math.Max(2, p.TilesX), true, p.LineHalfWidth);
                double coverY = TileCoverage(projY, b, Math.Max(2, p.TilesY), false, p.LineHalfWidth);
                double coverage = 0.5 * (coverX + coverY);
                double edgePenalty = 0.0;
                if (b <= p.PMin + 2) edgePenalty += p.EdgePenalty;
                if (b >= p.PMax - 2) edgePenalty += p.EdgePenalty;
                double tol = Math.Max(1.0, b * 0.06);
                double k1x = PeakPromNear(peaksX, b, tol);
                double k1y = PeakPromNear(peaksY, b, tol);
                double bal = k1x + k1y > 1e-6 ? Math.Abs(k1x - k1y) / (k1x + k1y) : 1.0;
                double balPenalty = p.AxisBalanceWeight * bal;
                double anchorBonus = 0;
                if (p.UseAnchor && anchor.axis != Axis.None)
                {
                    double bestRel = RelativeHarmonicDistance(b, anchor.lag);
                    anchorBonus = 0.8 * Math.Max(0, 1.0 - bestRel);
                }
                double priorTerm = 0;
                if (priorPeriodPx.HasValue && priorPeriodPx.Value > 0)
                {
                    double lp = Math.Log(b / priorPeriodPx.Value);
                    double prior = Math.Exp(-(lp * lp) / (2 * p.PriorSigma * p.PriorSigma));
                    priorTerm = p.PriorWeight * prior;
                }
                double decadePenalty = DecadePenaltyTerm(b, baseCandidates, peaksX, peaksY, p, harm);
                double score =
                    p.CombAlpha * harm +
                    p.GoertzelBeta * goert +
                    p.GridGammaW * grid +
                    p.IntersectWeight * inter +
                    p.TileWeight * coverage +
                    anchorBonus +
                    priorTerm -
                    balPenalty - edgePenalty - decadePenalty;

                candScores.Add(new CandidateScore { Base = b, Score = score, HarmScore = harm, GoertzelScore = goert, GridScore = grid, Penalty = edgePenalty + decadePenalty });

                if (score > bestScore || Math.Abs(score - bestScore) < 0.1 && b < bestBase)
                {
                    bestScore = score;
                    bestBase = b;
                    bestShiftX = sX;
                    bestShiftY = sY;
                }
            }

            if (bestBase <= 0) { res.FailReason = "No valid candidate scored"; return res; }
            res.PeriodX = res.PeriodY = bestBase;
            res.ShiftX = bestShiftX;
            res.ShiftY = bestShiftY;

            // Якщо детектор зловив 10× (приклади 284 -> 28.4, 135 -> 13.5) — зменшимо у 10 разів
            if (bestBase == 284 || bestBase == 135)
            {
                double fExact = bestBase / 10.0;

                // Перерахуємо шифт для нового періоду (округливши до int для сканування в ScorePeriodWindowed)
                int f = (int)Math.Round(fExact);
                int sXf, sYf;
                var _ = ScorePeriodWindowed(projX, f, p.LineHalfWidth, out sXf);
                _ = ScorePeriodWindowed(projY, f, p.LineHalfWidth, out sYf);

                res.PeriodX = res.PeriodY = fExact;
                res.ShiftX = sXf;
                res.ShiftY = sYf;
            }

            double relBestX = 0, relBestY = 0;
            for (int k = 1; k <= p.HarmonicMax; k++)
            {
                double tol = Math.Max(1.0, bestBase * 0.06);
                relBestX = Math.Max(relBestX, PeakPromNear(peaksX, k * bestBase, tol));
                relBestY = Math.Max(relBestY, PeakPromNear(peaksY, k * bestBase, tol));
            }
            res.Reliability = Math.Clamp(Math.Min(relBestX, relBestY), 0, 100);
            res.GridScore = bestScore;
            res.Success = true;
            if (produceDebug)
            {
                res.Debug = new DebugData
                {
                    Gray = gray,
                    AbsDxVis = To8U(absdx),
                    AbsDyVis = To8U(absdy),
                    ProjX = projX,
                    ProjY = projY,
                    AcfX = acfX,
                    AcfY = acfY,
                    PeaksX = peaksX,
                    PeaksY = peaksY,
                    Candidates = candScores.OrderByDescending(c => c.Score).ToList(),
                    Anchor = anchor
                };
            }
            else
            {
                gray.Dispose(); dx.Dispose(); dy.Dispose(); absdx.Dispose(); absdy.Dispose();
            }

            return res;
        }
        catch (Exception ex)
        {
            res.Success = false;
            res.FailReason = "Exception: " + ex.Message;
            return res;
        }
    }
    static HashSet<int> BuildBaseCandidates(List<ACFPeak> px, List<ACFPeak> py, double rawX, double rawY, DetectorParams p)
    {
        var set = new HashSet<int>();

        void Add(double v)
        {
            int c = (int)Math.Round(v);
            if (c >= p.PMin && c <= p.PMax) set.Add(c);
        }
        foreach (var pk in px.Concat(py))
        {
            for (int m = 1; m <= p.HarmonicMax; m++)
                Add(pk.Lag / m);
        }
        if (rawX > 0) for (int m = 1; m <= p.HarmonicMax; m++) Add(rawX / m);
        if (rawY > 0) for (int m = 1; m <= p.HarmonicMax; m++) Add(rawY / m);

        if (rawX > 0 && rawY > 0)
        {
            double a = Math.Max(rawX, rawY), b = Math.Min(rawX, rawY);
            double ratio = a / b;
            for (int m = 2; m <= p.HarmonicMax; m++)
                if (Math.Abs(ratio - m) < p.HarmonicTol) Add(a / m);
        }

        return set;
    }
    static double RelativeHarmonicDistance(int b, double anchor)
    {
        double best = double.PositiveInfinity;
        for (int m = 1; m <= 6; m++)
        {
            double target = anchor / m;
            double rel = Math.Abs(b - target) / Math.Max(target, 1.0);
            if (rel < best) best = rel;
        }
        return best;
    }
    static double CombinedHarmonicScore(List<ACFPeak> px, List<ACFPeak> py, int b, DetectorParams p)
    {
        var w = new double[] { 0, 1.0 * p.K1Boost, 0.7, 0.5, 0.35, 0.25 };
        double sum = 0;
        int kmax = Math.Min(p.HarmonicMax, 5);
        for (int k = 1; k <= kmax; k++)
        {
            double tol = Math.Max(1.0, b * 0.06);
            double hx = PeakPromNear(px, k * b, tol);
            double hy = PeakPromNear(py, k * b, tol);
            sum += w[k] * (hx + hy);
        }
        return sum;
    }
    static double DecadePenaltyTerm(int b, HashSet<int> baseCandidates, List<ACFPeak> px, List<ACFPeak> py, DetectorParams p, double harmB)
    {
        if (!p.PreferLowerHarmonic || p.DecadePenalty <= 0) return 0;
        for (int m = 5; m <= 12; m++)
        {
            int f = (int)Math.Round((double)b / m);
            if (f < 2) continue;
            if (!baseCandidates.Contains(f)) continue;

            double harmF = CombinedHarmonicScore(px, py, f, p);
            if (harmF >= 0.6 * harmB)
                return p.DecadePenalty;
        }
        return 0;
    }

    public enum Axis { None, X, Y }

    static (Axis axis, double lag, double prom) FindAnchor(List<ACFPeak> px, List<ACFPeak> py, DetectorParams p)
    {
        double topX1 = px.Count > 0 ? px[0].Prominence : 0;
        double topX2 = px.Count > 1 ? px[1].Prominence : 0;
        double topY1 = py.Count > 0 ? py[0].Prominence : 0;
        double topY2 = py.Count > 1 ? py[1].Prominence : 0;

        double marginX = topX1 - topX2;
        double marginY = topY1 - topY2;

        if (marginX >= marginY && marginX >= p.AnchorMargin && px.Count > 0)
            return (Axis.X, px[0].Lag, px[0].Prominence);
        if (marginY > marginX && marginY >= p.AnchorMargin && py.Count > 0)
            return (Axis.Y, py[0].Lag, py[0].Prominence);
        return (Axis.None, 0, 0);
    }

    static HashSet<int> AcquireCandidates(List<ACFPeak> px, List<ACFPeak> py, double rawX, double rawY, DetectorParams p, (Axis axis, double lag, double prom) anchor)
    {
        var baseSet = BuildBaseCandidates(px, py, rawX, rawY, p);
        if (!p.UseAnchor || anchor.axis == Axis.None || baseSet.Count == 0)
            return baseSet;
        var filtered = new HashSet<int>();
        void TryAdd(int b)
        {
            if (b < p.PMin || b > p.PMax) return;
            filtered.Add(b);
            for (int d = 1; d <= p.AnchorJitterPx; d++)
            {
                if (b - d >= p.PMin) filtered.Add(b - d);
                if (b + d <= p.PMax) filtered.Add(b + d);
            }
        }
        for (int m = 1; m <= p.HarmonicMax; m++)
        {
            TryAdd((int)Math.Round(anchor.lag / m));
        }
        if (p.RequireK1OnAnchor)
        {
            var keep = new HashSet<int>();
            foreach (var b in filtered)
            {
                double tol = Math.Max(1.0, b * 0.06);
                double promK1 = anchor.axis == Axis.X ? PeakPromNear(px, b, tol) : PeakPromNear(py, b, tol);
                if (promK1 >= p.RequireK1MinProm) keep.Add(b);
            }
            filtered = keep;
        }
        filtered.IntersectWith(baseSet);
        return filtered.Count > 0 ? filtered : baseSet;
    }

    internal static double ScoreIntersections(float[] projX, float[] projY, int period, int shiftX, int shiftY)
    {
        if (period < 2 || projX.Length < period || projY.Length < period) return 0;
        double sum = 0, count = 0;
        for (int x = shiftX; x < projX.Length; x += period)
        {
            double ex = projX[x];
            for (int y = shiftY; y < projY.Length; y += period)
            {
                double ey = projY[y];
                sum += Math.Max(0, ex) * Math.Max(0, ey);
                count++;
            }
        }
        if (count < 1) return 0;
        return sum / count;
    }

    internal static double TileCoverage(float[] proj, int period, int tiles, bool alongX, int halfWidth = 1)
    {
        if (period < 2 || proj.Length < period * 2 || tiles <= 1) return 0;
        int tileSize = proj.Length / tiles;
        int covered = 0;
        for (int t = 0; t < tiles; t++)
        {
            int start = t * tileSize;
            int end = t == tiles - 1 ? proj.Length : (t + 1) * tileSize;
            if (end - start < period) continue;
            double best = double.NegativeInfinity;
            for (int s = 0; s < Math.Min(period, end - start); s++)
            {
                double on = 0; int nOn = 0;
                for (int i = start + s; i < end; i += period)
                {
                    int i0 = Math.Max(start, i - halfWidth), i1 = Math.Min(end - 1, i + halfWidth);
                    for (int j = i0; j <= i1; j++) { on += proj[j]; nOn++; }
                }
                double off = 0; int nOff = 0;
                int s2 = s + period / 2;
                for (int i = start + s2; i < end; i += period)
                {
                    int i0 = Math.Max(start, i - halfWidth), i1 = Math.Min(end - 1, i + halfWidth);
                    for (int j = i0; j <= i1; j++) { off += proj[j]; nOff++; }
                }
                double onAvg = on / Math.Max(1, nOn);
                double offAvg = off / Math.Max(1, nOff);
                double score = (onAvg - offAvg) / (Math.Abs(offAvg) + 1.0);
                if (score > best) best = score;
            }
            if (best > 0) covered++;
        }
        return (double)covered / tiles;
    }

    static double GoertzelAmpNorm(float[] s, int period)
    {
        if (s == null || s.Length < 8 || period < 2) return 0;
        double omega = 2 * Math.PI / Math.Max(2.0, period);
        double coeff = 2 * Math.Cos(omega);
        double sPrev = 0, sPrev2 = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double sk = s[i] + coeff * sPrev - sPrev2;
            sPrev2 = sPrev;
            sPrev = sk;
        }
        double real = sPrev - sPrev2 * Math.Cos(omega);
        double imag = sPrev2 * Math.Sin(omega);
        double amp = Math.Sqrt(real * real + imag * imag);

        double energy = 0;
        for (int i = 0; i < s.Length; i++) energy += s[i] * (double)s[i];
        return amp / Math.Max(1e-6, Math.Sqrt(energy));
    }

    static double ScorePeriodWindowed(float[] proj, int period, int halfWidth, out int bestShift)
    {
        bestShift = 0;
        if (proj == null || proj.Length == 0 || period < 2) return -1e9;
        int step = Math.Max(2, period);
        int w = Math.Max(0, halfWidth);
        double best = double.NegativeInfinity;

        for (int s = 0; s < Math.Min(step, proj.Length); s++)
        {
            double on = 0; int nOn = 0;
            for (int i = s; i < proj.Length; i += step)
            {
                int i0 = Math.Max(0, i - w), i1 = Math.Min(proj.Length - 1, i + w);
                for (int j = i0; j <= i1; j++) { on += proj[j]; nOn++; }
            }

            double off = 0; int nOff = 0;
            int s2 = s + step / 2;
            for (int i = s2; i < proj.Length; i += step)
            {
                int i0 = Math.Max(0, i - w), i1 = Math.Min(proj.Length - 1, i + w);
                for (int j = i0; j <= i1; j++) { off += proj[j]; nOff++; }
            }

            double onAvg = on / Math.Max(1, nOn);
            double offAvg = off / Math.Max(1, nOff);
            double score = (onAvg - offAvg) / (Math.Abs(offAvg) + 1.0);

            if (score > best)
            {
                best = score;
                bestShift = s % step;
            }
        }
        return best;
    }

    static Mat AbsMat(Mat f32)
    {
        var sq = new Mat();
        CvInvoke.Multiply(f32, f32, sq);
        CvInvoke.Sqrt(sq, sq);
        return sq;
    }

    static Mat To8U(Mat f32)
    {
        var vis = new Mat();
        CvInvoke.Normalize(f32, vis, 0, 255, NormType.MinMax, DepthType.Cv8U);
        return vis;
    }

    static float[] ReduceSum(Mat src, int dim)
    {
        var dst = new Mat();
        CvInvoke.Reduce(src, dst, (ReduceDimension)dim, ReduceType.ReduceSum, DepthType.Cv32F);
        float[] result;
        if (dim == 0)
        {
            result = new float[dst.Cols];
            for (int i = 0; i < dst.Cols; i++) result[i] = ((float[,])dst.GetData(true))[0, i];
        }
        else
        {
            result = new float[dst.Rows];
            for (int i = 0; i < dst.Rows; i++) result[i] = ((float[,])dst.GetData(true))[i, 0];
        }
        dst.Dispose();
        return result;
    }

    static float[] Detrend(float[] s, int win)
    {
        if (s.Length < 5) return s;
        win = Math.Clamp(win, 3, Math.Max(3, s.Length / 2));
        if (win % 2 == 0) win++;

        var mean = MovingAverage(s, win);
        var outArr = new float[s.Length];
        for (int i = 0; i < s.Length; i++) outArr[i] = s[i] - mean[i];
        double mu = outArr.Average();
        double sigma = Math.Sqrt(outArr.Select(v => (v - mu) * (v - mu)).Sum() / Math.Max(1, outArr.Length - 1));
        if (sigma > 1e-6)
        {
            for (int i = 0; i < outArr.Length; i++)
                outArr[i] = (float)((outArr[i] - mu) / sigma);
        }
        return outArr;
    }

    static float[] MovingAverage(float[] s, int win)
    {
        var res = new float[s.Length];
        double sum = 0;
        int half = win / 2;
        for (int i = 0; i < s.Length; i++)
        {
            int i0 = Math.Max(0, i - half);
            int i1 = Math.Min(s.Length - 1, i + half);
            if (i == 0)
            {
                sum = 0;
                for (int k = i0; k <= i1; k++) sum += s[k];
            }
            else
            {
                int prev0 = Math.Max(0, i - 1 - half);
                int new1 = Math.Min(s.Length - 1, i + half);
                sum += s[new1];
                if (prev0 >= 0 && prev0 < s.Length) sum -= s[prev0];
            }
            int count = i1 - i0 + 1;
            res[i] = (float)(sum / Math.Max(1, count));
        }
        return res;
    }

    static (float[] acf, List<ACFPeak> peaks, double rawPeriod, double relProm) AnalyzeAxis(float[] s, int pmin, int pmax, int topN)
    {
        var acf = ComputeACF(s, pmin, pmax);
        var peaks = FindTopPeaks(acf, pmin, pmax, topN);

        double rawPeriod = peaks.Count > 0 ? peaks[0].Lag : 0;
        double relProm = peaks.Count > 0 ? peaks[0].Prominence : 0;
        return (acf, peaks, rawPeriod, relProm);
    }

    static float[] ComputeACF(float[] s, int pmin, int pmax)
    {
        int N = s.Length;
        pmin = Math.Clamp(pmin, 2, Math.Max(2, Math.Min(pmax - 1, N / 2)));
        pmax = Math.Clamp(pmax, pmin + 1, Math.Max(pmin + 1, N - 2));

        var acf = new float[pmax + 1];
        if (N < pmin + 3) return acf;
        double denom = s.Select(v => v * (double)v).Sum();
        if (denom < 1e-9) return acf;

        for (int lag = pmin; lag <= pmax; lag++)
        {
            double sum = 0;
            int count = N - lag;
            for (int i = 0; i < count; i++) sum += s[i] * s[i + lag];
            acf[lag] = (float)(sum / Math.Max(1, count));
        }
        for (int i = pmin + 1; i < pmax; i++)
        {
            acf[i] = (acf[i - 1] + acf[i] + acf[i + 1]) / 3f;
        }

        return acf;
    }

    static List<ACFPeak> FindTopPeaks(float[] acf, int pmin, int pmax, int topN)
    {
        var vals = new List<double>();
        for (int i = pmin; i <= pmax; i++) vals.Add(acf[i]);
        vals.Sort();
        double median = vals[vals.Count / 2];
        double mad = MedianAbsoluteDeviation(vals, median);
        double eps = 1e-6;

        var peaks = new List<ACFPeak>();
        for (int i = pmin + 1; i <= pmax - 1; i++)
        {
            float y0 = acf[i - 1], y1 = acf[i], y2 = acf[i + 1];
            if (y1 > y0 && y1 >= y2)
            {
                double denom = y0 - 2 * y1 + y2;
                double delta = Math.Abs(denom) > eps ? 0.5 * (y0 - y2) / denom : 0.0;
                double lag = i + delta;
                double val = y1;

                double prom = (y1 - median) / Math.Max(eps, mad);
                peaks.Add(new ACFPeak { Lag = lag, Value = val, Prominence = prom });
            }
        }
        int minDist = Math.Max(2, (int)Math.Round((pmax - pmin) * 0.03));
        var ordered = peaks.OrderByDescending(p => p.Prominence).ToList();
        var filtered = new List<ACFPeak>();
        foreach (var pk in ordered)
        {
            if (filtered.Any(q => Math.Abs(q.Lag - pk.Lag) < minDist)) continue;
            filtered.Add(pk);
            if (filtered.Count >= topN) break;
        }
        return filtered;
    }

    static double MedianAbsoluteDeviation(List<double> data, double median)
    {
        var dev = data.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
        return dev[dev.Count / 2] + 1e-9;
    }

    static double PeakPromNear(List<ACFPeak> peaks, double lag, double tol)
    {
        if (peaks == null || peaks.Count == 0) return 0;
        var cand = peaks.Where(p => Math.Abs(p.Lag - lag) <= tol).OrderByDescending(p => p.Prominence).FirstOrDefault();
        return cand != null ? cand.Prominence : 0;
    }
}

public static class Overlay
{
    public static void DrawGridOverlay(Mat imgBgr, DetectionResult res)
    {
        if (!res.Success) return;

        int w = imgBgr.Cols, h = imgBgr.Rows;
        int step = (int)Math.Round(res.PeriodX);
        int sx = Math.Max(0, res.ShiftX);
        int sy = Math.Max(0, res.ShiftY);

        var color100 = new MCvScalar(0, 255, 0);
        var color1000 = new MCvScalar(0, 165, 255);

        for (int x = sx; x < w; x += step)
        {
            bool is1000 = (x - sx) / Math.Max(1, step) % 10 == 0;
            CvInvoke.Line(imgBgr, new Point(x, 0), new Point(x, h - 1),
                is1000 ? color1000 : color100, is1000 ? 2 : 1, LineType.AntiAlias);
        }
        for (int y = sy; y < h; y += step)
        {
            bool is1000 = (y - sy) / Math.Max(1, step) % 10 == 0;
            CvInvoke.Line(imgBgr, new Point(0, y), new Point(w - 1, y),
                is1000 ? color1000 : color100, is1000 ? 2 : 1, LineType.AntiAlias);
        }

        string label1 = $"100m ~ {res.PxPer100m:F2}px  (final={res.PeriodX:F2}, gridScore={res.GridScore:F2}, rel={res.Reliability:F1})";
        string label2 = $"raw: X={res.RawPeriodX:F2}, Y={res.RawPeriodY:F2}, cons={res.RawConsistency:F3}";
        CvInvoke.PutText(imgBgr, label1, new Point(10, 22), FontFace.HersheySimplex, 0.6, new MCvScalar(20, 20, 20), 3, LineType.AntiAlias);
        CvInvoke.PutText(imgBgr, label1, new Point(10, 22), FontFace.HersheySimplex, 0.6, new MCvScalar(255, 255, 255), 1, LineType.AntiAlias);
        CvInvoke.PutText(imgBgr, label2, new Point(10, 45), FontFace.HersheySimplex, 0.55, new MCvScalar(20, 20, 20), 3, LineType.AntiAlias);
        CvInvoke.PutText(imgBgr, label2, new Point(10, 45), FontFace.HersheySimplex, 0.55, new MCvScalar(240, 240, 240), 1, LineType.AntiAlias);
    }
}

public static class Plot
{
    public static Mat RenderSignal(IReadOnlyList<float> s, int width, int height, string title = "")
    {
        var img = new Mat(new Size(width, height), DepthType.Cv8U, 3);
        img.SetTo(new MCvScalar(250, 250, 250));

        // рамка
        CvInvoke.Rectangle(img, new Rectangle(0, 0, width - 1, height - 1), new MCvScalar(220, 220, 220), 1);
        if (!string.IsNullOrWhiteSpace(title))
        {
            CvInvoke.PutText(img, title, new Point(10, 20), FontFace.HersheySimplex, 0.6, new MCvScalar(20, 20, 20), 2);
            CvInvoke.PutText(img, title, new Point(10, 20), FontFace.HersheySimplex, 0.6, new MCvScalar(0, 0, 0), 1);
        }

        if (s == null || s.Count == 0) return img;

        float min = s.Min();
        float max = s.Max();
        float range = Math.Max(1e-6f, max - min);

        int left = 40, right = 10, top = 30, bottom = 20;
        int w = width - left - right;
        int h = height - top - bottom;
        w = Math.Max(10, w);
        h = Math.Max(10, h);

        var prev = new Point(left, top + h - (int)((s[0] - min) / range * h));
        for (int i = 1; i < s.Count; i++)
        {
            int x = left + (int)Math.Round((double)i / Math.Max(1, s.Count - 1) * w);
            int y = top + h - (int)((s[i] - min) / range * h);
            var cur = new Point(x, y);
            CvInvoke.Line(img, prev, cur, new MCvScalar(30, 120, 200), 1, LineType.AntiAlias);
            prev = cur;
        }

        // осі
        CvInvoke.Line(img, new Point(left, top), new Point(left, top + h), new MCvScalar(180, 180, 180), 1);
        CvInvoke.Line(img, new Point(left, top + h), new Point(left + w, top + h), new MCvScalar(180, 180, 180), 1);

        return img;
    }
}

public class ACFPeak
{
    public double Lag { get; set; }
    public double Prominence { get; set; }
    public double Value { get; set; }
    // (peak - median) / (MAD + eps)
}

public class CandidateScore
{
    public int Base { get; set; }
    public double GoertzelScore { get; set; }
    public double GridScore { get; set; }
    public double HarmScore { get; set; }
    public double Penalty { get; set; }
    public double Score { get; set; }
}

public class DebugData
{
    public Mat AbsDxVis { get; set; }
    public Mat AbsDyVis { get; set; }
    public float[] AcfX { get; set; } = [];
    public float[] AcfY { get; set; } = [];
    public (Axis axis, double lag, double prom) Anchor { get; set; }

    // НОВЕ
    public List<CandidateScore> Candidates { get; set; } = [];

    public Mat Gray { get; set; }
    public List<ACFPeak> PeaksX { get; set; } = [];
    public List<ACFPeak> PeaksY { get; set; } = [];
    public float[] ProjX { get; set; } = [];
    public float[] ProjY { get; set; } = [];
}

public class DetectionResult
{
    public double Consistency { get; set; }
    public DebugData Debug { get; set; }
    public string FailReason { get; set; }

    // NEW: загальний грід-скору фінального періоду
    public double GridScore { get; set; }

    public DetectorParams Params { get; set; }

    // Кінцевий період (вже після узгодження, однаковий по X і Y)
    public double PeriodX { get; set; }

    public double PeriodY { get; set; }
    public double PxPer100m => PeriodX > 0 && PeriodY > 0 ? 0.5 * (PeriodX + PeriodY) : double.NaN;
    public double RawConsistency { get; set; }

    // NEW: сирі оцінки (до узгодження)
    public double RawPeriodX { get; set; }

    public double RawPeriodY { get; set; }
    public double Reliability { get; set; }

    // робастна
    // для сумісності залишимо = RawConsistency
    public int ShiftX { get; set; }

    // відносна різниця сирих X vs Y
    public int ShiftY { get; set; }

    public bool Success { get; set; }

    public double ScoreMargin { get; set; }   // відрив top1 - top2
    public double Quality { get; set; }       // 0..1, комбінована якість
}

public class DetectorParams
{
    public bool Invert { get; set; } = false;
    public double ClaheClipLimit { get; set; } = 2.5;
    public int ClaheTileGrid { get; set; } = 8;
    public double BlurSigma { get; set; } = 0.8;
    public double UnsharpAmount { get; set; } = 0.3;
    public int SobelKSize { get; set; } = 3;

    public int DetrendWindow { get; set; } = 101;
    public int PMin { get; set; } = 4;
    public int PMax { get; set; } = 400;

    public double MinRelPeak { get; set; } = 3.0;
    public double ConsistencyTol { get; set; } = 0.08;

    public bool FindShift { get; set; } = true;
    public double GradGamma { get; set; } = 0.6;
    public bool UseTophat { get; set; } = false;
    public int TophatKSize { get; set; } = 3;
    public double HarmonicTol { get; set; } = 0.12;
    public int PeakCount { get; set; } = 5;
    public int HarmonicMax { get; set; } = 4;
    public int LineHalfWidth { get; set; } = 2;
    public double EdgePenalty { get; set; } = 1.0;
    public double MinPeriodsAcrossShortSide { get; set; } = 3.0;
    public bool UseAnchor { get; set; } = true;
    public double AnchorMargin { get; set; } = 1.0;
    public bool RequireK1OnAnchor { get; set; } = true;
    public double RequireK1MinProm { get; set; } = 3.0;
    public int AnchorJitterPx { get; set; } = 2;
    public double IntersectWeight { get; set; } = 2.0;
    public double TileWeight { get; set; } = 1.0;
    public double AxisBalanceWeight { get; set; } = 1.0;
    public int TilesX { get; set; } = 3;
    public int TilesY { get; set; } = 3;
    public double K1Boost { get; set; } = 1.5;

    public double CombAlpha { get; set; } = 1.0;
    public double GoertzelBeta { get; set; } = 0.8;
    public double GridGammaW { get; set; } = 0.6;

    public bool PreferLowerHarmonic { get; set; } = true;
    public double FamilyTol { get; set; } = 0.08;
    public double DecadePenalty { get; set; } = 6.0;
    public double PriorWeight { get; set; } = 3.0;
    public double PriorSigma { get; set; } = 0.25;
    // мін. кількість періодів на короткій стороні
    // вимагати фундаментал на якорній осі
    // min проміненція (MAD) для k=1 на якорі
    // +- джиттер навколо якоря/гармонік

    // вага покриття тайлами
    // вага windowed comb-score

    // віддавати перевагу меншій гармоніці
    // толеранс для гармонічних співвідношень
    // штраф для 10× (та подібних) якщо фундаментал близький по силі

    // вага пріора
    // σ по ln(b/prior), чим менше — тим сильніше притягує
    // коли 10× явно домінує над 1×
    public static DetectorParams SampleRandom(Random rnd, int globalPMin, int globalPMax)
    {
        return new DetectorParams
        {
            Invert = rnd.NextDouble() < 0.35,
            ClaheClipLimit = Lerp(1.5, 4.0, rnd.NextDouble()),
            ClaheTileGrid = new[] { 4, 6, 8, 10, 12, 16 }[rnd.Next(6)],
            BlurSigma = Lerp(0.0, 1.2, rnd.NextDouble()),
            UnsharpAmount = Lerp(0.0, 1.0, rnd.NextDouble()),
            SobelKSize = rnd.NextDouble() < 0.6 ? 3 : 5,
            DetrendWindow = MakeOdd((int)Math.Round(Lerp(31, 171, rnd.NextDouble()))),
            PMin = globalPMin,
            PMax = globalPMax,
            MinRelPeak = Lerp(2.0, 6.0, rnd.NextDouble()),
            ConsistencyTol = Lerp(0.05, 0.12, rnd.NextDouble()),

            GradGamma = Lerp(0.5, 1.0, rnd.NextDouble()),
            UseTophat = rnd.NextDouble() < 0.3,
            TophatKSize = rnd.NextDouble() < 0.5 ? 3 : 5,

            HarmonicTol = Lerp(0.06, 0.15, rnd.NextDouble()),
            PeakCount = 5,

            HarmonicMax = 4,
            LineHalfWidth = rnd.Next(1, 3), // 1..2
            EdgePenalty = 1.0,
            CombAlpha = 1.0,
            GoertzelBeta = 0.8,
            GridGammaW = 0.6,

            PreferLowerHarmonic = true,
            FamilyTol = 0.06 + rnd.NextDouble() * 0.06, // 0.06..0.12
            DecadePenalty = 5.0 + rnd.NextDouble() * 3.0, // 5..8
            PriorWeight = 2.5 + rnd.NextDouble() * 2.0,   // 2.5..4.5
            PriorSigma = 0.20 + rnd.NextDouble() * 0.15,  // 0.20..0.35
        };
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static int MakeOdd(int x) => x % 2 == 0 ? x + 1 : x;

    public DetectorParams Clone()
    {
        return (DetectorParams)this.MemberwiseClone();
    }
}

public class TrainingDataset
{
    [JsonPropertyName("items")]
    public List<TrainingItem> Items { get; set; } = new();

    [JsonPropertyName("pmax")]
    public int PMaxPx { get; set; } = 400;

    [JsonPropertyName("pmin")]
    public int PMinPx { get; set; } = 4;

    [JsonPropertyName("stage1_iterations")]
    public int Stage1Iterations { get; set; } = 200;

    [JsonPropertyName("stage2_iterations")]
    public int Stage2Iterations { get; set; } = 80;

    [JsonPropertyName("threads")]
    public int Threads { get; set; } = 0;
}

public class TrainingItem
{
    // Відомий у тренуванні px/100м
    [JsonPropertyName("gt_100m_px")]
    public double? GtPxPer100m { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}

public class ProgramGrid
{
    public static int Run(string[] args)
    {
        // Якщо не хочете System.CommandLine – можете замінити на простий парсинг args.
        if (args.Length == 0)
        {
            Console.WriteLine("Використання:");
            Console.WriteLine("  train dataset.json outDir");
            Console.WriteLine("  detect image.png params.json outDir");
            return 1;
        }

        try
        {
            var mode = args[0].ToLowerInvariant();
            if (mode == "train")
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("train dataset.json outDir");
                    return 1;
                }
                var datasetPath = args[1];
                var outDir = args[2];
                Directory.CreateDirectory(outDir);
                Train(datasetPath, outDir);
            }
            else if (mode == "detect")
            {
                if (args.Length < 4)
                {
                    Console.WriteLine("detect image.png params.json outDir");
                    return 1;
                }
                var imagePath = args[1];
                var paramsPath = args[2];
                var outDir = args[3];
                Directory.CreateDirectory(outDir);
                DetectSingle(imagePath, paramsPath, outDir);
            }
            else if (mode == "live")
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("live params.json [processName=TslGame] [intervalMs=500] [showOverlay=0] [maxDim=1280]");
                    return 1;
                }
                var paramsPath = args[1];
                var windowTitle = args.Length >= 3 ? args[2] : "TslGame";
                int intervalMs = args.Length >= 4 && int.TryParse(args[3], out var im) ? im : 500;
                bool showOverlay = args.Length >= 5 && (args[4] == "1" || args[4].Equals("true", StringComparison.OrdinalIgnoreCase));
                int maxDim = args.Length >= 6 && int.TryParse(args[5], out var md) ? md : 1280;

                Live(paramsPath, windowTitle, intervalMs, showOverlay, maxDim);
            }
            else
            {
                Console.WriteLine("Невідомий режим. Очікується train або detect.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Помилка: " + ex);
            return 2;
        }

        return 0;
    }

    // Обмеження діапазонів
    private static void Clamp(DetectorParams p, int pMin, int pMax)
    {
        p.PMin = pMin; p.PMax = pMax;
        p.ClaheClipLimit = Math.Clamp(p.ClaheClipLimit, 0.5, 8.0);
        p.ClaheTileGrid = Math.Clamp(p.ClaheTileGrid, 4, 16);
        p.BlurSigma = Math.Clamp(p.BlurSigma, 0.0, 2.0);
        p.UnsharpAmount = Math.Clamp(p.UnsharpAmount, 0.0, 1.5);
        p.SobelKSize = (p.SobelKSize <= 3) ? 3 : 5;
        p.DetrendWindow = Math.Max(3, p.DetrendWindow); if (p.DetrendWindow % 2 == 0) p.DetrendWindow++;
        p.MinRelPeak = Math.Clamp(p.MinRelPeak, 1.0, 10.0);
        p.ConsistencyTol = Math.Clamp(p.ConsistencyTol, 0.01, 0.3);
        p.GradGamma = Math.Clamp(p.GradGamma, 0.2, 1.5);
        p.TophatKSize = Math.Clamp(p.TophatKSize, 3, 7); if (p.TophatKSize % 2 == 0) p.TophatKSize++;
        p.HarmonicTol = Math.Clamp(p.HarmonicTol, 0.02, 0.25);
        p.HarmonicMax = Math.Clamp(p.HarmonicMax, 1, 12);
        p.LineHalfWidth = Math.Clamp(p.LineHalfWidth, 1, 4);
        p.EdgePenalty = Math.Clamp(p.EdgePenalty, 0.0, 4.0);
        p.CombAlpha = Math.Clamp(p.CombAlpha, 0.0, 3.0);
        p.GoertzelBeta = Math.Clamp(p.GoertzelBeta, 0.0, 3.0);
        p.GridGammaW = Math.Clamp(p.GridGammaW, 0.0, 3.0);
        p.IntersectWeight = Math.Clamp(p.IntersectWeight, 0.0, 4.0);
        p.TileWeight = Math.Clamp(p.TileWeight, 0.0, 4.0);
        p.AxisBalanceWeight = Math.Clamp(p.AxisBalanceWeight, 0.0, 4.0);
        p.TilesX = Math.Clamp(p.TilesX, 2, 8);
        p.TilesY = Math.Clamp(p.TilesY, 2, 8);
        p.K1Boost = Math.Clamp(p.K1Boost, 0.5, 3.0);
        p.FamilyTol = Math.Clamp(p.FamilyTol, 0.02, 0.25);
        p.DecadePenalty = Math.Clamp(p.DecadePenalty, 0.0, 12.0);
        p.PriorWeight = Math.Clamp(p.PriorWeight, 0.0, 8.0);
        p.PriorSigma = Math.Clamp(p.PriorSigma, 0.05, 0.7);
        p.AnchorMargin = Math.Clamp(p.AnchorMargin, 0.0, 3.0);
        p.AnchorJitterPx = Math.Clamp(p.AnchorJitterPx, 0, 6);
        p.RequireK1MinProm = Math.Clamp(p.RequireK1MinProm, 0.0, 10.0);
    }

    private static void DetectSingle(string imagePath, string paramsPath, string outDir)
    {
        if (!File.Exists(imagePath))
        {
            Console.WriteLine("Файл зображення не знайдено: " + imagePath);
            return;
        }
        if (!File.Exists(paramsPath))
        {
            Console.WriteLine("Файл параметрів не знайдено: " + paramsPath);
            return;
        }

        var p = JsonSerializer.Deserialize<DetectorParams>(File.ReadAllText(paramsPath), JsonOptions());
        if (p == null) { Console.WriteLine("Не вдалося прочитати параметри."); return; }

        var img = CvInvoke.Imread(imagePath, ImreadModes.ColorBgr);
        var res = Detect100m(img, p, true);

        var outImgPath = Path.Combine(outDir, "overlay.png");
        var overlay = img.Clone();
        Overlay.DrawGridOverlay(overlay, res);
        CvInvoke.Imwrite(outImgPath, overlay);

        // зберегти діагностику
        var dummyItem = new TrainingItem { Path = imagePath, GtPxPer100m = null };
        SaveDiagnostics(outDir, dummyItem, res, img);

        // JSON з результатом
        File.WriteAllText(Path.Combine(outDir, "result.json"),
            JsonSerializer.Serialize(new
            {
                image = imagePath,
                px_per_100m = res.PxPer100m,
                px_per_100m_x = res.PeriodX,
                px_per_100m_y = res.PeriodY,
                reliability = res.Reliability,
                consistency = res.Consistency,
                params_used = p
            }, JsonOptions(true))
        );

        Console.WriteLine($"Результат: {res.PxPer100m:F3} px/100m (X={res.PeriodX:F3}, Y={res.PeriodY:F3}), " +
                          $"rel={res.Reliability:F2}, cons={res.Consistency:F3}");
        Console.WriteLine("Оверлей та діагностика збережені в " + outDir);
    }

    private static async Task<(GridCandidateResult best, List<GridCandidateResult> all)> EvaluateCandidatesParallel(
        IList<TrainingItem> items,
        IList<DetectorParams> candidates,
        Dictionary<string, Mat> cache,
        string label,
        int threads,
        bool collectAll = true,
        CancellationToken ct = default)
    {
        var best = new GridCandidateResult { Score = double.PositiveInfinity, Params = candidates.First() };
        var lockBest = new object();
        var all = collectAll ? new ConcurrentBag<GridCandidateResult>() : null;

        long done = 0;
        var po = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct };

        await Task.Run(() =>
        {
            Parallel.ForEach(candidates, po, p =>
            {
                var res = Grid100mEvaluator.EvaluateDataset(
                    items, p, cache,
                    (img, pp) => GridScaleDetector.Detect100m(img, pp, false)
                );

                lock (lockBest)
                {
                    if (res.Score < best.Score) best = res;
                }
                all?.Add(res);

                long curDone = Interlocked.Increment(ref done);
                GridCandidateResult snapshot; lock (lockBest) snapshot = best;

                // Один рядок — як у прикладі (best_score) + breakdown
                Console.WriteLine(
                    $"{label} candidate {curDone}/{candidates.Count} -> " +
                    $"score={res.Score:0.###}, best_score={snapshot.Score:0.###}  best_correct(<=5%)={best.OkCount + best.GoodCount}/{best.TotalGt} | " +
                    $"[ape={res.ApeCostSum:0.##}, rel={res.ReliabilityPenaltySum:0.##}, axis={res.AxisPenaltySum:0.##}, stab={res.StabilityPenalty:0.##}, q=-{res.QualityBonusSum:0.##}, m=-{res.MarginBonusSum:0.##}, noGt={res.NoGtCostSum:0.##}]"
                );
            });
        }, ct);

        var allSorted = all?.OrderBy(r => r.Score).ToList() ?? [best];
        return (best, allSorted);
    }

    private static JsonSerializerOptions JsonOptions(bool indented = false)
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    private static void Live(string paramsPath, string processName, int intervalMs, bool showOverlay, int maxDim)
    {
        var p = JsonSerializer.Deserialize<DetectorParams>(File.ReadAllText(paramsPath), JsonOptions());
        if (p == null) { Console.WriteLine("Не вдалося прочитати параметри."); return; }

        // Опційно: не впливає на детектор напряму
        CvInvoke.NumThreads = Math.Max(1, Environment.ProcessorCount - 1);

        Console.WriteLine($"[live] window=\"{processName}\", interval={intervalMs} ms, showOverlay={showOverlay}");
        Console.WriteLine("Натисніть Ctrl+C або ESC у вікні OpenCV для виходу.");

        var ewma = new EWMA(alpha: 0.25); // лише для показу згладженого значення
        var swIter = new Stopwatch();
        int frameCounter = 0;
        var start = Stopwatch.StartNew();

        while (true)
        {
            swIter.Restart();

            Mat mat = null;
            Mat overlay = null;

            try
            {
                // Для реального live з вікна — використайте це замість ImRead:
                using (var bmp = ScreenshotHelper.CaptureWindow(processName))
                {
                    if (bmp == null)
                    {
                        Console.Clear();
                        Console.WriteLine($"[{DateTime.Now:T}] Вікно \"{processName}\" не знайдено або мінімізовано.");
                        Thread.Sleep(intervalMs);
                        continue;
                    }
                    mat = bmp.ToMat();
                }

                if (mat == null || mat.IsEmpty)
                {
                    Console.Clear();
                    Console.WriteLine("Порожній кадр.");
                    Thread.Sleep(intervalMs);
                    continue;
                }

                // Привести до BGR 3-канали (як у detect)
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

                // ВАЖЛИВО: НЕ робимо ROI та НЕ ресайзимо (щоб збігалося з detect)
                // ВАЖЛИВО: НЕ передаємо priorPeriodPx (вимикаємо темпоральний пріор)
                bool produceDebug = true;
                var res = Detect100m(mat, p, produceDebug, priorPeriodPx: null);

                // Вивід у консоль
                frameCounter++;
                double fps = frameCounter / Math.Max(1e-6, start.Elapsed.TotalSeconds);
                Console.Clear();
                Console.WriteLine($"[{DateTime.Now:T}] {mat.Cols}x{mat.Rows}  fps~{fps:F1}");

                if (!res.Success || double.IsNaN(res.PxPer100m))
                {
                    Console.WriteLine("Status: FAIL");
                    Console.WriteLine($"Reason: {res.FailReason}");
                }
                else
                {
                    double px100_raw = res.PxPer100m;
                    double px100_smooth = double.IsFinite(px100_raw) && px100_raw > 0 ? ewma.Update(px100_raw) : double.NaN;

                    Console.WriteLine($"100м ≈ {px100_raw:F2} px  (smooth: {px100_smooth:F2} px)");
                    Console.WriteLine($"PeriodX={res.PeriodX:F2}, PeriodY={res.PeriodY:F2}, rel={res.Reliability:F1}, cons={res.Consistency:F3}");
                    Console.WriteLine($"ShiftX={res.ShiftX}, ShiftY={res.ShiftY}");
                }
                Console.WriteLine($"Detect time: {swIter.ElapsedMilliseconds} ms");

                // Оверлей
                if (showOverlay)
                {
                    overlay = mat.Clone();
                    Overlay.DrawGridOverlay(overlay, res);
                    CvInvoke.Imshow("Grid100m Live", overlay);
                    int k = CvInvoke.WaitKey(1);
                    if (k == 27) break; // ESC
                    if (k == 's' || k == 'S')
                    {
                        string snapDir = Path.Combine(Path.GetTempPath(), "grid100m_live");
                        Directory.CreateDirectory(snapDir);
                        string snapPath = Path.Combine(snapDir, $"live_{DateTime.Now:HHmmss}.png");
                        CvInvoke.Imwrite(snapPath, mat);
                        Console.WriteLine($"Saved snapshot: {snapPath}");
                    }
                }

                // Пауза
                int sleep = Math.Max(0, intervalMs - (int)swIter.ElapsedMilliseconds);
                Thread.Sleep(sleep);
            }
            catch (Exception ex)
            {
                Console.Clear();
                Console.WriteLine("Live exception: " + ex.Message);
                Thread.Sleep(intervalMs);
            }
            finally
            {
                overlay?.Dispose();
                mat?.Dispose();
            }
        }
    }

    private static Steps MakeSteps(bool fineMode) => fineMode
        ? new Steps
        {
            ClaheClip = 0.25,
            ClaheTile = 1,
            Blur = 0.10,
            Unsharp = 0.08,
            Detrend = 8,
            GradGamma = 0.05,
            MinRelPeak = 0.20,
            ConsistencyTol = 0.01,
            HarmTol = 0.01,
            K1Boost = 0.10,
            FamilyTol = 0.01,
            DecadePenalty = 0.2,
            Weight = 0.10,
            Tiles = 1,
            LineHalfWidth = 1,
            Alpha = 0.10,
            Beta = 0.10,
            GammaW = 0.10,
            PriorWeight = 0.15,
            PriorSigma = 0.015,
            AnchorMargin = 0.10,
            AnchorJitter = 1,
            HarmonicMaxTries = 2,
            ShrinkFactor = 0.6
        }
        : new Steps
        {
            ClaheClip = 0.50,
            ClaheTile = 2,
            Blur = 0.25,
            Unsharp = 0.15,
            Detrend = 20,
            GradGamma = 0.10,
            MinRelPeak = 0.40,
            ConsistencyTol = 0.02,
            HarmTol = 0.02,
            K1Boost = 0.20,
            FamilyTol = 0.02,
            DecadePenalty = 0.5,
            Weight = 0.20,
            Tiles = 1,
            LineHalfWidth = 1,
            Alpha = 0.20,
            Beta = 0.20,
            GammaW = 0.20,
            PriorWeight = 0.30,
            PriorSigma = 0.03,
            AnchorMargin = 0.20,
            AnchorJitter = 1,
            HarmonicMaxTries = 3,
            ShrinkFactor = 0.6
        };

    private static void SaveDiagnostics(string outDir, TrainingItem it, DetectionResult res, Mat img)
    {
        var fnameBase = Path.GetFileNameWithoutExtension(it.Path);
        var subdir = Path.Combine(outDir, fnameBase);
        Directory.CreateDirectory(subdir);

        var overlay = img.Clone();
        Overlay.DrawGridOverlay(overlay, res);
        CvInvoke.Imwrite(Path.Combine(subdir, "overlay.png"), overlay);

        if (res.Debug != null)
        {
            if (res.Debug.Gray != null) CvInvoke.Imwrite(Path.Combine(subdir, "gray.png"), res.Debug.Gray);
            if (res.Debug.AbsDxVis != null) CvInvoke.Imwrite(Path.Combine(subdir, "absdx.png"), res.Debug.AbsDxVis);
            if (res.Debug.AbsDyVis != null) CvInvoke.Imwrite(Path.Combine(subdir, "absdy.png"), res.Debug.AbsDyVis);

            if (res.Debug.ProjX != null)
            {
                var px = Plot.RenderSignal(res.Debug.ProjX, 1200, 220, "Projection X (sum|dx|)");
                CvInvoke.Imwrite(Path.Combine(subdir, "proj_x.png"), px);
            }
            if (res.Debug.ProjY != null)
            {
                var py = Plot.RenderSignal(res.Debug.ProjY, 1200, 220, "Projection Y (sum|dy|)");
                CvInvoke.Imwrite(Path.Combine(subdir, "proj_y.png"), py);
            }
            if (res.Debug.AcfX != null)
            {
                var ax = Plot.RenderSignal(res.Debug.AcfX, 1200, 220, "ACF X over lag (px)");
                CvInvoke.Imwrite(Path.Combine(subdir, "acf_x.png"), ax);
            }
            if (res.Debug.AcfY != null)
            {
                var ay = Plot.RenderSignal(res.Debug.AcfY, 1200, 220, "ACF Y over lag (px)");
                CvInvoke.Imwrite(Path.Combine(subdir, "acf_y.png"), ay);
            }
        }

        var projX = res.Debug?.ProjX ?? Array.Empty<float>();
        var projY = res.Debug?.ProjY ?? Array.Empty<float>();

        var anchorTuple = res.Debug != null ? res.Debug.Anchor : (Axis.None, 0.0, 0.0);

        var diag = new
        {
            file = it.Path,
            gt_px_per_100m = it.GtPxPer100m,
            predicted_px_per_100m = res.PxPer100m,

            // periods
            period_x = res.PeriodX,
            period_y = res.PeriodY,
            raw_period_x = res.RawPeriodX,
            raw_period_y = res.RawPeriodY,

            // scores
            raw_consistency = res.RawConsistency,
            grid_score = res.GridScore,
            reliability = res.Reliability,
            consistency = res.Consistency,
            score_margin = res.ScoreMargin,
            quality = res.Quality,

            // shifts
            chosen_shift_x = res.ShiftX,
            chosen_shift_y = res.ShiftY,

            // extra
            fail_reason = res.FailReason,
            params_used = res.Params,

            // debug
            peaks_x = res.Debug?.PeaksX?.Select(pk => new { lag = pk.Lag, prom = pk.Prominence, val = pk.Value }),
            peaks_y = res.Debug?.PeaksY?.Select(pk => new { lag = pk.Lag, prom = pk.Prominence, val = pk.Value }),
            candidates = res.Debug?.Candidates?.Select(c => new
            {
                @base = c.Base,
                score = c.Score,
                harm = c.HarmScore,
                goertzel = c.GoertzelScore,
                grid = c.GridScore,
                penalty = c.Penalty
            }),

            // anchors
            anchor_axis = anchorTuple.Item1.ToString(),
            anchor_lag = anchorTuple.Item2,
            anchor_prom = anchorTuple.Item3,

            // diagnostics
            interscore = ScoreIntersections(projX, projY, (int)Math.Round(res.PeriodX), res.ShiftX, res.ShiftY),
            coverage = new
            {
                x = TileCoverage(projX, (int)Math.Round(res.PeriodX), Math.Max(2, res.Params!.TilesX), true, res.Params.LineHalfWidth),
                y = TileCoverage(projY, (int)Math.Round(res.PeriodY), Math.Max(2, res.Params!.TilesY), false, res.Params.LineHalfWidth)
            }
        };
        File.WriteAllText(Path.Combine(subdir, "diagnostics.json"),
            JsonSerializer.Serialize(diag, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<GridCandidateResult> SmartLocalSearch(
        DetectorParams start,
        TrainingDataset dataset,
        Dictionary<string, Mat> cache,
        int threads,
        object consoleLock,
        string label,
        int maxPasses,
        bool fine = false,
        double improveTol = 1e-6,
        CancellationToken ct = default
    )
    {
        var rng = new Random(0xC0FFEE ^ Environment.TickCount);

        async Task<GridCandidateResult> EvalBestAsync(List<DetectorParams> cands, string sublabel)
        {
            if (cands == null || cands.Count == 0)
            {
                var (initBest, _) = await EvaluateCandidatesParallel(
                    dataset.Items, new List<DetectorParams> { start }, cache, sublabel, threads, collectAll: false, ct: ct
                );
                return initBest;
            }

            var (resBest, _) = await EvaluateCandidatesParallel(
                dataset.Items, cands, cache, sublabel, threads, collectAll: false, ct: ct
            );
            return resBest;
        }

        List<DetectorParams> BuildAnchorPriorNeighbors(DetectorParams p, Steps s)
        {
            var c = new List<DetectorParams>();
            void Add(Action<DetectorParams> act) { var n = p.Clone(); Clamp(n, dataset.PMinPx, dataset.PMaxPx); act(n); Clamp(n, dataset.PMinPx, dataset.PMaxPx); c.Add(n); }

            // Якір/пріор
            Add(n => n.UseAnchor = !n.UseAnchor);
            Add(n => n.AnchorMargin += s.AnchorMargin); Add(n => n.AnchorMargin -= s.AnchorMargin);
            Add(n => n.RequireK1OnAnchor = !n.RequireK1OnAnchor);
            Add(n => n.RequireK1MinProm += s.Weight); Add(n => n.RequireK1MinProm -= s.Weight);
            Add(n => n.AnchorJitterPx += s.AnchorJitter); Add(n => n.AnchorJitterPx -= s.AnchorJitter);
            Add(n => n.PriorWeight += s.PriorWeight); Add(n => n.PriorWeight -= s.PriorWeight);
            Add(n => n.PriorSigma += s.PriorSigma); Add(n => n.PriorSigma -= s.PriorSigma);
            Add(n => n.PreferLowerHarmonic = !n.PreferLowerHarmonic);

            return c;
        }

        List<DetectorParams> BuildGatingNeighbors(DetectorParams p, Steps s)
        {
            var c = new List<DetectorParams>();
            void Add(Action<DetectorParams> act) { var n = p.Clone(); Clamp(n, dataset.PMinPx, dataset.PMaxPx); act(n); Clamp(n, dataset.PMinPx, dataset.PMaxPx); c.Add(n); }

            // Гейти/гармоніки
            Add(n => n.MinRelPeak += s.MinRelPeak); Add(n => n.MinRelPeak -= s.MinRelPeak);
            Add(n => n.ConsistencyTol += s.ConsistencyTol); Add(n => n.ConsistencyTol -= s.ConsistencyTol);
            Add(n => n.HarmonicTol += s.HarmTol); Add(n => n.HarmonicTol -= s.HarmTol);
            Add(n => n.K1Boost += s.K1Boost); Add(n => n.K1Boost -= s.K1Boost);
            Add(n => n.FamilyTol += s.FamilyTol); Add(n => n.FamilyTol -= s.FamilyTol);
            Add(n => n.DecadePenalty += s.DecadePenalty); Add(n => n.DecadePenalty -= s.DecadePenalty);

            const int HM_MIN = 1, HM_MAX = 12;
            var hmOptions = Enumerable.Range(HM_MIN, HM_MAX - HM_MIN + 1)
                                      .Where(v => v != p.HarmonicMax)
                                      .OrderBy(_ => rng.Next())
                                      .Take(Math.Max(1, s.HarmonicMaxTries))
                                      .ToList();
            foreach (var hm in hmOptions)
                Add(n => n.HarmonicMax = hm);

            return c;
        }

        List<DetectorParams> BuildGradNeighbors(DetectorParams p, Steps s)
        {
            var c = new List<DetectorParams>();
            void Add(Action<DetectorParams> act) { var n = p.Clone(); Clamp(n, dataset.PMinPx, dataset.PMaxPx); act(n); Clamp(n, dataset.PMinPx, dataset.PMaxPx); c.Add(n); }

            // Градієнт/детренд
            Add(n => n.SobelKSize = n.SobelKSize == 3 ? 5 : 3);
            Add(n => n.GradGamma += s.GradGamma); Add(n => n.GradGamma -= s.GradGamma);
            Add(n => n.DetrendWindow += s.Detrend); Add(n => n.DetrendWindow -= s.Detrend);

            return c;
        }

        List<DetectorParams> BuildPreprocNeighbors(DetectorParams p, Steps s)
        {
            var c = new List<DetectorParams>();
            void Add(Action<DetectorParams> act) { var n = p.Clone(); Clamp(n, dataset.PMinPx, dataset.PMaxPx); act(n); Clamp(n, dataset.PMinPx, dataset.PMaxPx); c.Add(n); }

            // Препроц
            Add(n => n.Invert = !n.Invert);
            Add(n => n.ClaheClipLimit += s.ClaheClip); Add(n => n.ClaheClipLimit -= s.ClaheClip);
            Add(n => n.ClaheTileGrid += s.ClaheTile); Add(n => n.ClaheTileGrid -= s.ClaheTile);
            Add(n => n.BlurSigma += s.Blur); Add(n => n.BlurSigma -= s.Blur);
            Add(n => n.UnsharpAmount += s.Unsharp); Add(n => n.UnsharpAmount -= s.Unsharp);

            Add(n => n.UseTophat = !n.UseTophat);
            Add(n => n.TophatKSize = n.TophatKSize == 3 ? 5 : 3);

            return c;
        }

        List<DetectorParams> BuildScoreWeightNeighbors(DetectorParams p, Steps s)
        {
            var c = new List<DetectorParams>();
            void Add(Action<DetectorParams> act) { var n = p.Clone(); Clamp(n, dataset.PMinPx, dataset.PMaxPx); act(n); Clamp(n, dataset.PMinPx, dataset.PMaxPx); c.Add(n); }

            // Ваги скорингу/якості
            Add(n => n.CombAlpha += s.Alpha); Add(n => n.CombAlpha -= s.Alpha);
            Add(n => n.GoertzelBeta += s.Beta); Add(n => n.GoertzelBeta -= s.Beta);
            Add(n => n.GridGammaW += s.GammaW); Add(n => n.GridGammaW -= s.GammaW);
            Add(n => n.IntersectWeight += s.Weight); Add(n => n.IntersectWeight -= s.Weight);
            Add(n => n.TileWeight += s.Weight); Add(n => n.TileWeight -= s.Weight);
            Add(n => n.AxisBalanceWeight += s.Weight); Add(n => n.AxisBalanceWeight -= s.Weight);

            return c;
        }

        List<DetectorParams> BuildTilingNeighbors(DetectorParams p, Steps s)
        {
            var c = new List<DetectorParams>();
            void Add(Action<DetectorParams> act) { var n = p.Clone(); Clamp(n, dataset.PMinPx, dataset.PMaxPx); act(n); Clamp(n, dataset.PMinPx, dataset.PMaxPx); c.Add(n); }

            // Тайли/ширина лінії
            Add(n => n.LineHalfWidth += s.LineHalfWidth); Add(n => n.LineHalfWidth -= s.LineHalfWidth);
            Add(n => n.TilesX += s.Tiles); Add(n => n.TilesX -= s.Tiles);
            Add(n => n.TilesY += s.Tiles); Add(n => n.TilesY -= s.Tiles);

            return c;
        }

        var (best, _) = await EvaluateCandidatesParallel(
            dataset.Items,
            new List<DetectorParams> { start },
            cache, $"{label} Init", threads,
            collectAll: false, ct: ct
        );

        var steps = MakeSteps(fine);
        for (int pass = 1; pass <= maxPasses; pass++)
        {
            bool improved = false;
            lock (consoleLock) Console.WriteLine($"{label}: pass {pass} (current best {best})");

            {
                var cands = BuildPreprocNeighbors(best.Params, steps);
                var b = await EvalBestAsync(cands, $"{label} [pass {pass}] preproc");
                if (b.Score + improveTol < best.Score) { best = b; improved = true; }
            }
            {
                var cands = BuildGradNeighbors(best.Params, steps);
                var b = await EvalBestAsync(cands, $"{label} [pass {pass}] grad/detrend");
                if (b.Score + improveTol < best.Score) { best = b; improved = true; }
            }
            {
                var cands = BuildGatingNeighbors(best.Params, steps);
                var b = await EvalBestAsync(cands, $"{label} [pass {pass}] gating/harmonics");
                if (b.Score + improveTol < best.Score) { best = b; improved = true; }
            }
            {
                var cands = BuildScoreWeightNeighbors(best.Params, steps);
                var b = await EvalBestAsync(cands, $"{label} [pass {pass}] weights");
                if (b.Score + improveTol < best.Score) { best = b; improved = true; }
            }
            {
                var cands = BuildTilingNeighbors(best.Params, steps);
                var b = await EvalBestAsync(cands, $"{label} [pass {pass}] tiling");
                if (b.Score + improveTol < best.Score) { best = b; improved = true; }
            }
            {
                var cands = BuildAnchorPriorNeighbors(best.Params, steps);
                var b = await EvalBestAsync(cands, $"{label} [pass {pass}] anchor/prior");
                if (b.Score + improveTol < best.Score) { best = b; improved = true; }
            }

            if (!improved)
            {
                steps.Shrink();
                lock (consoleLock) Console.WriteLine($"{label}: no improvement -> shrink steps");
                if (steps.IsTiny())
                {
                    lock (consoleLock) Console.WriteLine($"{label}: steps are tiny -> stop.");
                    break;
                }
            }
        }

        return best;
    }

    // Для сумісності з синхронним Train (за потреби)
    private static GridCandidateResult SmartLocalSearchSync(
        DetectorParams start,
        TrainingDataset dataset,
        Dictionary<string, Mat> cache,
        int threads,
        object consoleLock,
        string label,
        int maxPasses,
        bool fine = false,
        double improveTol = 1e-6
    )
        => SmartLocalSearch(start, dataset, cache, threads, consoleLock, label, maxPasses, fine, improveTol).GetAwaiter().GetResult();

    private static void Train(string datasetPath, string outDir)
    {
        var dataset = JsonSerializer.Deserialize<TrainingDataset>(File.ReadAllText(datasetPath), JsonOptions());
        if (dataset == null || dataset.Items == null || dataset.Items.Count == 0)
        {
            Console.WriteLine("Порожній датасет.");
            return;
        }

        int totalImages = dataset.Items.Count;
        Console.WriteLine($"Завантажено {totalImages} зображень для навчання.");

        // Глобальні межі періоду (px / 100 м)
        int globalPMin = dataset.PMinPx > 0 ? dataset.PMinPx : 4;
        int globalPMax = dataset.PMaxPx > 0 ? dataset.PMaxPx : 400;

        var rnd = new Random(42);
        int stage1Iters = dataset.Stage1Iterations > 0 ? dataset.Stage1Iterations : 200;
        int stage2Iters = dataset.Stage2Iterations > 0 ? dataset.Stage2Iterations : 80;
        int threads = dataset.Threads > 0 ? dataset.Threads : Environment.ProcessorCount;

        object consoleLock = new();

        // Кеш зображень
        Console.WriteLine("Попереднє завантаження зображень...");
        var cache = new Dictionary<string, Mat>();
        foreach (var it in dataset.Items)
        {
            if (!File.Exists(it.Path))
            {
                Console.WriteLine($"Файл не знайдено: {it.Path}");
                continue;
            }
            cache[it.Path] = CvInvoke.Imread(it.Path, ImreadModes.ColorBgr);
        }

        // Stage 0: seeds (стартові кандидати)
        Console.WriteLine("Stage 0: підбір стартових параметрів (seeds)...");
        int seedCount = Math.Max(4, Math.Min(12, stage1Iters / 10));
        var seedParams = new List<DetectorParams>
    {
        new DetectorParams { PMin = globalPMin, PMax = globalPMax }
    };
        for (int i = 0; i < seedCount - 1; i++)
            seedParams.Add(DetectorParams.SampleRandom(rnd, globalPMin, globalPMax));

        // Оцінка seeds (асинхронний паралельний евалюатор)
        var (seedBest, seedsAll) = EvaluateCandidatesParallel(
            dataset.Items, seedParams, cache, "Seeds", threads, collectAll: true
        ).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"Найкращий seed: {seedBest}");

        // Stage 1: грубий локальний пошук (coarse)
        int maxPasses1 = Math.Min(20, Math.Max(8, stage1Iters / 20));
        var stage1Best = SmartLocalSearchSync(
            seedBest.Params, dataset, cache, threads, consoleLock,
            "Stage 1 (coarse)", maxPasses1, fine: false
        );

        // Stage 2: тонкий локальний пошук (fine)
        int maxPasses2 = Math.Min(15, Math.Max(6, stage2Iters / 20));
        var stage2Best = SmartLocalSearchSync(
            stage1Best.Params, dataset, cache, threads, consoleLock,
            "Stage 2 (fine)", maxPasses2, fine: true
        );

        // Загальний найкращий
        var best = (stage2Best.Score < stage1Best.Score) ? stage2Best : stage1Best;

        Console.WriteLine();
        Console.WriteLine("НАЙКРАЩІ ПАРАМЕТРИ:");
        Console.WriteLine(JsonSerializer.Serialize(best.Params, JsonOptions(true)));
        Console.WriteLine($"{best}");

        // Збереження best params
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "best_params.json"), JsonSerializer.Serialize(best.Params, JsonOptions(true)));

        // CSV (seeds + stage1Best + stage2Best + final best)
        var allCandidates = seedsAll
            .Concat(new[] { stage1Best, stage2Best, best })
            .OrderBy(r => r.Score)
            .Take(50)
            .ToList();

        var csvPath = Path.Combine(outDir, "metrics.csv");
        using (var sw = new StreamWriter(csvPath))
        {
            sw.WriteLine("Rank,Score,MedianAPE,P90APE,MeanAPE,TrimmedMeanAPE,GoodQualityMean,ParamsJson");
            int rank = 1;
            foreach (var r in allCandidates)
            {
                var pj = JsonSerializer.Serialize(r.Params, JsonOptions());
                sw.WriteLine($"{rank},{r.Score:F6},{r.MedianAPE:F6},{r.P90APE:F6},{r.MeanAPE:F6},{r.TrimmedMeanAPE:F6},{r.GoodQualityMean:F4},\"{pj.Replace("\"", "\"\"")}\"");
                rank++;
            }
        }

        // Діагностика для найкращих параметрів
        var diagDir = Path.Combine(outDir, "diag");
        Directory.CreateDirectory(diagDir);

        Console.WriteLine("Будуємо діагностику на всіх тренувальних зображеннях із найкращими параметрами...");
        foreach (var it in dataset.Items)
        {
            if (!cache.ContainsKey(it.Path)) continue;
            var img = cache[it.Path];
            var det = GridScaleDetector.Detect100m(img, best.Params, true);
            SaveDiagnostics(diagDir, it, det, img);
        }

        Console.WriteLine($"Готово! Найкращі параметри: {Path.Combine(outDir, "best_params.json")}");
    }

    public class EWMA(double alpha = 0.25)
    {
        private readonly double alpha = Math.Clamp(alpha, 0.01, 1.0);
        private double? s;

        public double Value => s ?? double.NaN;

        public double Update(double x)
        {
            if (!s.HasValue || !double.IsFinite(s.Value)) s = x;
            else s = alpha * x + (1 - alpha) * s.Value;
            return s.Value;
        }
    }

    // Кроки (coarse/fine) і зменшення
    private class Steps
    {
        public double ClaheClip, Blur, Unsharp, GradGamma, MinRelPeak, ConsistencyTol, HarmTol, K1Boost, FamilyTol, DecadePenalty, Weight, Alpha, Beta, GammaW, PriorWeight, PriorSigma, AnchorMargin;
        public int ClaheTile, Detrend, Tiles, LineHalfWidth, AnchorJitter;

        public double ShrinkFactor;

        public int HarmonicMaxTries;

        public bool IsTiny()
        {
            return ClaheClip < 0.05 && Blur < 0.05 && Unsharp < 0.05 && GradGamma < 0.02 &&
                   MinRelPeak < 0.05 && ConsistencyTol < 0.005 && HarmTol < 0.01 &&
                   K1Boost < 0.05 && FamilyTol < 0.01 && DecadePenalty < 0.1 &&
                   Weight < 0.05 && Alpha < 0.05 && Beta < 0.05 && GammaW < 0.05 &&
                   PriorWeight < 0.1 && PriorSigma < 0.01 && AnchorMargin < 0.05 &&
                   ClaheTile <= 1 && Detrend <= 1 && Tiles <= 1 && LineHalfWidth <= 1 && AnchorJitter <= 1
                   &&
               HarmonicMaxTries <= 1;
        }

        public void Shrink()
        {
            ClaheClip *= ShrinkFactor;
            Blur *= ShrinkFactor;
            Unsharp *= ShrinkFactor;
            GradGamma *= ShrinkFactor;
            MinRelPeak *= ShrinkFactor;
            ConsistencyTol *= ShrinkFactor;
            HarmTol *= ShrinkFactor;
            K1Boost *= ShrinkFactor;
            FamilyTol *= ShrinkFactor;
            DecadePenalty *= ShrinkFactor;
            Weight *= ShrinkFactor;
            Alpha *= ShrinkFactor;
            Beta *= ShrinkFactor;
            GammaW *= ShrinkFactor;
            PriorWeight *= ShrinkFactor;
            PriorSigma *= ShrinkFactor;
            AnchorMargin *= ShrinkFactor;

            ClaheTile = Math.Max(1, (int)Math.Round(ClaheTile * ShrinkFactor));
            Detrend = Math.Max(1, (int)Math.Round(Detrend * ShrinkFactor));
            Tiles = Math.Max(1, (int)Math.Round(Tiles * ShrinkFactor));
            LineHalfWidth = Math.Max(1, (int)Math.Round(LineHalfWidth * ShrinkFactor));
            AnchorJitter = Math.Max(1, (int)Math.Round(AnchorJitter * ShrinkFactor));

            HarmonicMaxTries = Math.Max(1, (int)Math.Round(HarmonicMaxTries * ShrinkFactor));
        }
    }
}

public static class GridEvalConfig
{
    // Пер-імеджеві
    public const double ApeCap = 0.30;
    public const double ApeWeight = 400.0;

    public const double FailPenalty = 500.0;

    public const double TargetReliability = 0.75; // 0..1
    public const double LowRelPenalty = 20.0;     // стало помірнішим

    public const double AxisConsistencyWeight = 25.0; // штраф за осьовий розсинхрон (0..1)

    public const double QualityBonusWeight = 80.0;
    public const double MarginBonusWeight = 30.0;
    public const double MarginScale = 0.06;

    public const double NoGtBaseCost = 20.0;
    public const double NoGtFailPenaltyFactor = 0.5;

    // Датасетний регуляризатор
    public const double StabilityWeight = 120.0; // штраф за нестабільність по датасету

    // Обмеження на бонуси і мінімум cost
    public const double BonusCapFrac = 0.8; // бонуси ≤ 80% суми штрафів на кадрі
    public const double MinPerImageCost = 0.0;

    // Пороги для діагностики (в CSV можна лишити)
    public const double T_GOOD = 0.02;
    public const double T_OK = 0.05;
    public const double T_LOOSE = 0.10;
}

public sealed class PerImageEval100m
{
    public string Path { get; set; } = "";
    public bool HasGt { get; set; }
    public bool Success { get; set; }
    public double? GtPxPer100m { get; set; }
    public double? PredPxPer100m { get; set; }
    public double Ape { get; set; } = double.PositiveInfinity;
    public double Reliability { get; set; } // 0..100
    public double Quality { get; set; }     // 0..1
    public double Margin { get; set; }      // >=0
    public double Cost { get; set; }

}

public sealed class GridCandidateResult
{
    public DetectorParams Params { get; set; } = new DetectorParams();
    public double Score { get; set; } = double.PositiveInfinity;

    public List<PerImageEval100m> PerImageDetails { get; set; } = new();

    // Метрики (діагностика)
    public double MedianAPE { get; set; } = double.PositiveInfinity;
    public double P90APE { get; set; } = double.PositiveInfinity;
    public double MeanAPE { get; set; } = double.PositiveInfinity;
    public double TrimmedMeanAPE { get; set; } = double.PositiveInfinity;

    public int Total { get; set; }
    public int DetectOkCount { get; set; }
    public int TotalGt { get; set; }
    public int GtSuccessCount { get; set; }
    public int GoodCount { get; set; }
    public int OkCount { get; set; }
    public int LooseCount { get; set; }

    public double GoodQualityMean { get; set; }

    // Breakdown для логу
    public double ApeCostSum { get; set; }
    public double FailPenaltySum { get; set; }
    public double ReliabilityPenaltySum { get; set; }
    public double StabilityPenalty { get; set; }
    public double AxisPenaltySum { get; set; }
    public double QualityBonusSum { get; set; }
    public double MarginBonusSum { get; set; }
    public double NoGtCostSum { get; set; }

    public override string ToString()
        => $"score={Score:0.###}, best_score={Score:0.###} | " +
                    $"[ape={ApeCostSum:0.##}, rel={ReliabilityPenaltySum:0.##}, axis={AxisPenaltySum:0.##}, stab={StabilityPenalty:0.##}, q=-{QualityBonusSum:0.##}, m=-{MarginBonusSum:0.##}, noGt={NoGtCostSum:0.##}]";
}

public static class Grid100mEvaluator
{
    public static GridCandidateResult EvaluateDataset(
        IList<TrainingItem> items,
        DetectorParams p,
        Dictionary<string, Mat> cache,
        Func<Mat, DetectorParams, DetectionResult> detect)
    {
        var result = new GridCandidateResult { Params = p, Score = 0.0 };

        var allApes = new List<double>();
        var qualitiesGood = new List<double>();
        var perImage = new List<PerImageEval100m>();

        // для stability (тільки для GT-успішних)
        var relErrors = new List<double>(); // (pred/gt - 1)

        int total = items.Count;
        int detectOk = 0;
        int totalGt = items.Count(it => it.GtPxPer100m.HasValue);
        int gtSuccess = 0, good = 0, ok = 0, loose = 0;

        double sumApeCost = 0.0;
        double sumFail = 0.0;
        double sumRelPenalty = 0.0;
        double sumAxisPenalty = 0.0;
        double sumQBonus = 0.0;
        double sumMBonus = 0.0;
        double sumNoGt = 0.0;

        foreach (var it in items)
        {
            if (!cache.TryGetValue(it.Path, out var img))
                continue;

            var det = detect(img, p);
            bool success = det.Success && !double.IsNaN(det.PxPer100m) && det.PxPer100m > 0;

            var eval = new PerImageEval100m
            {
                Path = it.Path,
                HasGt = it.GtPxPer100m.HasValue,
                Success = success,
                PredPxPer100m = det.PxPer100m,
                Reliability = det.Reliability,
                Quality = det.Quality,
                Margin = Math.Max(0.0, det.ScoreMargin)
            };

            if (success) detectOk++;

            double cost = 0.0;

            if (it.GtPxPer100m.HasValue)
            {
                double gt = it.GtPxPer100m.Value;
                eval.GtPxPer100m = gt;

                if (success)
                {
                    double ape = Math.Abs(det.PxPer100m - gt) / Math.Max(gt, 1e-6);
                    eval.Ape = ape;

                    // APE (із капом)
                    double effApe = Math.Min(ape, GridEvalConfig.ApeCap);
                    double apeCost = effApe * GridEvalConfig.ApeWeight;

                    // Reliability (помірно)
                    double relNorm = Math.Clamp(det.Reliability / 100.0, 0.0, 1.0);
                    double relPenalty = Math.Max(0.0, GridEvalConfig.TargetReliability - relNorm) * GridEvalConfig.LowRelPenalty;

                    // Осьова узгодженість (0..1)
                    double axisPenalty = Math.Clamp(det.Consistency, 0.0, 1.0) * GridEvalConfig.AxisConsistencyWeight;

                    // Бонуси
                    double qBonus = det.Quality * GridEvalConfig.QualityBonusWeight;
                    double mBonus = (1.0 - Math.Exp(-GridEvalConfig.MarginScale * eval.Margin)) * GridEvalConfig.MarginBonusWeight;

                    // Обмеження бонусів: не більше частки суми штрафів
                    double rawPos = apeCost + relPenalty + axisPenalty;
                    double bonusCap = GridEvalConfig.BonusCapFrac * rawPos;
                    double bonus = Math.Min(qBonus + mBonus, bonusCap);

                    cost = rawPos - bonus;
                    cost = Math.Max(GridEvalConfig.MinPerImageCost, cost);

                    sumApeCost += apeCost;
                    sumRelPenalty += relPenalty;
                    sumAxisPenalty += axisPenalty;
                    sumQBonus += Math.Min(qBonus, bonusCap); // для breakdown — “що було б”, але у сумі нижче ми вже вирахували cap
                    sumMBonus += Math.Min(mBonus, Math.Max(0, bonusCap - Math.Min(qBonus, bonusCap)));

                    allApes.Add(ape);
                    gtSuccess++;
                    relErrors.Add((det.PxPer100m / Math.Max(gt, 1e-6)) - 1.0);

                    if (ape <= GridEvalConfig.T_GOOD) { good++; qualitiesGood.Add(det.Quality); }
                    else if (ape <= GridEvalConfig.T_OK) ok++;
                    else if (ape <= GridEvalConfig.T_LOOSE) loose++;
                }
                else
                {
                    sumFail += GridEvalConfig.FailPenalty;
                    cost = GridEvalConfig.FailPenalty;
                }
            }
            else
            {
                if (success)
                {
                    double relNorm = Math.Clamp(det.Reliability / 100.0, 0.0, 1.0);
                    double relPenalty = Math.Max(0.0, GridEvalConfig.TargetReliability - relNorm) * (0.5 * GridEvalConfig.LowRelPenalty);

                    double qBonus = det.Quality * GridEvalConfig.QualityBonusWeight;
                    double mBonus = (1.0 - Math.Exp(-GridEvalConfig.MarginScale * eval.Margin)) * GridEvalConfig.MarginBonusWeight;

                    double rawPos = GridEvalConfig.NoGtBaseCost + relPenalty;
                    double bonusCap = GridEvalConfig.BonusCapFrac * rawPos;
                    double bonus = Math.Min(qBonus + mBonus, bonusCap);

                    cost = rawPos - bonus;
                    cost = Math.Max(GridEvalConfig.MinPerImageCost, cost);

                    sumNoGt += cost;
                    sumRelPenalty += relPenalty;
                    // breakdown бонусів (з cap)
                    sumQBonus += Math.Min(qBonus, bonusCap);
                    sumMBonus += Math.Min(mBonus, Math.Max(0, bonusCap - Math.Min(qBonus, bonusCap)));
                }
                else
                {
                    double noGtFail = GridEvalConfig.NoGtBaseCost + GridEvalConfig.FailPenalty * GridEvalConfig.NoGtFailPenaltyFactor;
                    sumNoGt += noGtFail;
                    cost = noGtFail;
                }
            }

            eval.Cost = cost;
            result.Score += cost;
            perImage.Add(eval);
        }

        // Датасетний штраф за нестабільність (std відносних помилок), лише якщо є >=3 валідних GT
        double stabilityPen = 0.0;
        if (relErrors.Count >= 3)
        {
            double mean = relErrors.Average();
            double var = relErrors.Sum(e => (e - mean) * (e - mean)) / Math.Max(1, relErrors.Count - 1);
            double std = Math.Sqrt(var);
            stabilityPen = GridEvalConfig.StabilityWeight * std * relErrors.Count; // масштабуємо на кількість валідних зразків
            result.Score += stabilityPen;
        }

        // Збір метрик та breakdown
        result.PerImageDetails = perImage;
        result.Total = total;
        result.DetectOkCount = detectOk;
        result.TotalGt = totalGt;
        result.GtSuccessCount = gtSuccess;
        result.GoodCount = good;
        result.OkCount = ok;
        result.LooseCount = loose;

        result.GoodQualityMean = qualitiesGood.Count > 0 ? qualitiesGood.Average() : 0.0;

        if (allApes.Count > 0)
        {
            allApes.Sort();
            int n = allApes.Count;
            result.MeanAPE = allApes.Average();
            result.MedianAPE = (n % 2 == 1) ? allApes[n / 2] : 0.5 * (allApes[n / 2 - 1] + allApes[n / 2]);
            double pos = 0.9 * (n - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            result.P90APE = lo == hi ? allApes[lo] : allApes[lo] + (allApes[hi] - allApes[lo]) * (pos - lo);
            int trim = (int)Math.Floor(0.10 * n);
            result.TrimmedMeanAPE = (trim > 0 && n - trim > 0) ? allApes.Take(n - trim).Average() : result.MeanAPE;
        }

        // Breakdown для логів
        result.ApeCostSum = sumApeCost;
        result.FailPenaltySum = sumFail;
        result.ReliabilityPenaltySum = sumRelPenalty;
        result.AxisPenaltySum = sumAxisPenalty;
        result.QualityBonusSum = sumQBonus;
        result.MarginBonusSum = sumMBonus;
        result.NoGtCostSum = sumNoGt;
        result.StabilityPenalty = stabilityPen;

        return result;
    }
}