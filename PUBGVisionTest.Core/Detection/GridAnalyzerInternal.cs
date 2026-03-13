using System;

namespace PUBGVisionTest.Core.Detection;

/// <summary>
/// Внутрішній результат аналізу — передається від GridAnalyzerInternal
/// назад у GridScaleDetector. Зовнішній код використовує DetectionResult.
/// </summary>
internal class GridAnalyzerInternalResult
{
    public bool Success;
    public string? FailReason;
    public double SmallGridStep;
    public int DetectedPeriodH;
    public int DetectedPeriodV;
    public string Method = "";
    public double BestScore;
    public double[]? AutocorrH;
    public double[]? AutocorrV;
    public double[]? AutocorrCombined;
    public List<(int lag, double value)> AutocorrPeaks = new();
    public List<string> DebugLog = new();
    public List<PeakInfo> SpikePeaks = new();
    public List<PeakInfo> DipPeaks = new();
}

/// <summary>
/// Ядро детектора: автокореляція + threshold descent + крос-валідація.
/// </summary>
internal class GridAnalyzerInternal
{
    private readonly List<string> _debugLog = new();
    private readonly int _pmin;
    private readonly int _pmax;
    private readonly int _minRealisticPeriod;

    public GridAnalyzerInternal(int pmin = 4, int pmax = 400)
    {
        _pmin = pmin;
        _pmax = pmax;
        _minRealisticPeriod = Math.Max(8, pmin);
    }

    public GridAnalyzerInternalResult Analyze(double[] featuresH, double[] featuresV)
    {
        _debugLog.Clear();
        Log($"=== Grid Analysis Start ===");
        Log($"Profile H={featuresH.Length}, V={featuresV.Length}");
        Log($"pmin={_pmin}, pmax={_pmax}, minRealistic={_minRealisticPeriod}");

        var result = new GridAnalyzerInternalResult();

        if (featuresH.Length == 0 || featuresV.Length == 0)
        {
            result.FailReason = "Empty profiles";
            result.DebugLog = new List<string>(_debugLog);
            return result;
        }

        var allCandidates = new List<PeriodCandidate>();

        // 1. Автокореляція
        var (acorrCandH, acfH) = AutocorrelationAnalysis(featuresH, "H");
        var (acorrCandV, acfV) = AutocorrelationAnalysis(featuresV, "V");
        allCandidates.AddRange(acorrCandH);
        allCandidates.AddRange(acorrCandV);

        // Зберігаємо ACF для debug
        result.AutocorrH = acfH;
        result.AutocorrV = acfV;

        // Об'єднаний ACF
        if (acfH != null && acfV != null)
        {
            int acfLen = Math.Min(acfH.Length, acfV.Length);
            result.AutocorrCombined = new double[acfLen];
            for (int i = 0; i < acfLen; i++)
                result.AutocorrCombined[i] = (acfH[i] + acfV[i]) / 2.0;
        }

        // 2. Threshold descent
        allCandidates.AddRange(AnalyzeProfile(featuresH, "H"));
        allCandidates.AddRange(AnalyzeProfile(featuresV, "V"));

        // 3. Крос-валідація
        CrossValidate(allCandidates);

        // 4. Фільтр + сортування
        allCandidates = allCandidates
            .Where(c => c.Period >= _minRealisticPeriod)
            .OrderByDescending(c => c.Score)
            .ToList();

        Log($"\n=== Top 20 candidates (filtered P>={_minRealisticPeriod}) ===");
        foreach (var c in allCandidates.Take(20))
            Log($"  P={c.Period:F1}px Score={c.Score:F4} Peaks={c.PeakCount} " +
                $"Cons={c.Consistency:F3} {c.Method}");

        // 5. Вибір найкращого
        var best = allCandidates.FirstOrDefault();

        if (best != null && best.Score > 0.3)
        {
            result.Success = true;
            result.SmallGridStep = best.Period;
            result.Method = best.Method;
            result.BestScore = best.Score;
            result.SpikePeaks = best.Spikes ?? new();
            result.DipPeaks = best.Dips ?? new();

            // Окремі періоди по H та V
            var bestH = allCandidates
                .Where(c => c.Direction == "H")
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();
            var bestV = allCandidates
                .Where(c => c.Direction == "V")
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();

            result.DetectedPeriodH = bestH != null ? (int)Math.Round(bestH.Period) : 0;
            result.DetectedPeriodV = bestV != null ? (int)Math.Round(bestV.Period) : 0;

            // ACF піки для debug
            if (result.AutocorrCombined != null)
            {
                int peakRadius = Math.Max(3, _pmax / 100);
                result.AutocorrPeaks = FindACFDebugPeaks(
                    result.AutocorrCombined, _minRealisticPeriod,
                    Math.Min(_pmax, result.AutocorrCombined.Length - 1), peakRadius);
            }
        }
        else
        {
            result.FailReason = best == null
                ? "No candidates found"
                : $"Best score too low: {best.Score:F4}";
        }

        result.DebugLog = new List<string>(_debugLog);
        return result;
    }

    // =============================================
    // АВТОКОРЕЛЯЦІЯ
    // =============================================

    private (List<PeriodCandidate> candidates, double[]? acf) AutocorrelationAnalysis(
        double[] profile, string dir)
    {
        var results = new List<PeriodCandidate>();

        double mean = profile.Average();
        double[] centered = profile.Select(v => v - mean).ToArray();

        double norm = centered.Sum(v => v * v);
        if (norm < 1e-6) return (results, null);

        int maxLag = Math.Min(_pmax * 3, profile.Length / 2);
        double[] acorr = new double[maxLag + 1];
        acorr[0] = 1.0;

        for (int lag = 1; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i < profile.Length - lag; i++)
                sum += centered[i] * centered[i + lag];
            acorr[lag] = sum / norm;
        }

        // Знаходимо піки автокореляції
        var peaks = new List<(int lag, double val)>();
        for (int lag = _minRealisticPeriod; lag < maxLag - 1; lag++)
        {
            if (acorr[lag] > acorr[lag - 1] && acorr[lag] > acorr[lag + 1] && acorr[lag] > 0.03)
                peaks.Add((lag, acorr[lag]));
        }
        peaks.Sort((a, b) => b.val.CompareTo(a.val));

        Log($"\n  Autocorrelation {dir}: {peaks.Count} peaks found");
        foreach (var p in peaks.Take(10))
            Log($"    lag={p.lag}, corr={p.val:F4}");

        // Аналіз кожного піку
        foreach (var peak in peaks.Take(8))
        {
            double period = peak.lag;

            // Перевіряємо гармоніки
            int harmonics = 0;
            double harmonicScore = 0;
            for (int mult = 2; mult <= 6; mult++)
            {
                int hLag = (int)Math.Round(period * mult);
                if (hLag > maxLag) break;
                double maxNearby = MaxNearby(acorr, hLag, 3, maxLag);
                if (maxNearby > 0.02)
                {
                    harmonics++;
                    harmonicScore += maxNearby;
                }
            }

            // Перевіряємо субгармоніку
            bool hasSubHalf = false, hasSubTenth = false;
            foreach (int div in new[] { 2, 5, 10 })
            {
                int subLag = (int)Math.Round(period / div);
                if (subLag >= _minRealisticPeriod && subLag <= maxLag)
                {
                    double maxNearby = MaxNearby(acorr, subLag, 2, maxLag);
                    // Поріг для 100м ліній (div 10) значно вищий, щоб ігнорувати шум
                    if (div == 10 && maxNearby > 0.10) hasSubTenth = true;
                    if (div == 2 && maxNearby > 0.05) hasSubHalf = true;
                }
            }

            // Score
            double score = peak.val * 0.40;
            score += Math.Min(1.0, harmonics / 3.0) * 0.25;
            score += Math.Min(1.0, harmonicScore / 0.5) * 0.15;
            score += (period < profile.Length / 2.0 ? 0.10 : 0.05);
            score += (hasSubHalf ? 0.05 : 0);
            score += (hasSubTenth ? 0.05 : 0);

            if (period >= _minRealisticPeriod && period <= _pmax)
            {
                results.Add(new PeriodCandidate
                {
                    Period = period,
                    Score = score,
                    Method = $"{dir}_acorr_{peak.lag}",
                    PeakCount = 1 + harmonics,
                    Consistency = peak.val,
                    Direction = dir
                });
            }

            // Як 1000m → 100m
            double p100 = period / 10.0;
            if (p100 >= _minRealisticPeriod && p100 <= _pmax)
            {
                double score1000 = score * 0.85;
                if (hasSubTenth)
                {
                    if (peak.val > 0.40) score1000 *= 1.45;
                    else score1000 *= 1.10;
                }

                results.Add(new PeriodCandidate
                {
                    Period = p100,
                    Score = score1000,
                    Method = $"{dir}_acorr1000m_{peak.lag}",
                    PeakCount = 1 + harmonics,
                    Consistency = peak.val,
                    Is1000mDerived = true,
                    Raw1000mPeriod = period,
                    Direction = dir
                });
            }
        }

        return (results, acorr);
    }

    // =============================================
    // THRESHOLD DESCENT
    // =============================================

    private List<PeriodCandidate> AnalyzeProfile(double[] profile, string dir)
    {
        var results = new List<PeriodCandidate>();

        double[] deviations = profile.Select(v => v - 128.0).ToArray();
        var allSpikes = FindPeaks(profile, deviations, true);
        var allDips = FindPeaks(profile, deviations, false);

        double maxSpike = allSpikes.Count > 0 ? allSpikes.Max(s => s.Value) : 0;
        double maxDip = allDips.Count > 0 ? allDips.Max(s => Math.Abs(s.Value)) : 0;

        // Базові адаптивні пороги для відсікання дрібного текстурного шуму
        double minSpikeThr = Math.Max(25.0, maxDip * 0.30);
        double minDipThr = Math.Max(25.0, maxSpike * 0.30);

        // --- ФІЛЬТРАЦІЯ ДІЙСНИХ ЛІНІЙ ---
        // Світлі: беремо тільки ті, що вище порогу шуму
        var validSpikes = allSpikes.Where(s => s.Value >= minSpikeThr).ToList();

        // Чорні: мають бути стабільно глибокими (не менше 60% від найглибшої).
        // Це ГАРАНТУЄ, що 1 реальна лінія і 1 шумова ямка не створять фантомну 1000м пару.
        double realBlackLineThr = Math.Max(minDipThr, maxDip * 0.60);
        var strongDips = allDips.Where(d => Math.Abs(d.Value) >= realBlackLineThr).ToList();

        // --- ІНТЕГРАЦІЯ ЧОРНИХ ЛІНІЙ У СВІТЛІ ---
        var unifiedSpikes = new List<PeakInfo>(allSpikes);
        foreach (var dip in strongDips)
        {
            // Даємо інтегрованій чорній лінії амплітуду найсильнішої світлої
            unifiedSpikes.Add(new PeakInfo
            {
                Position = dip.Position,
                Value = maxSpike > 0 ? maxSpike : Math.Abs(dip.Value),
                Type = "spike_from_dip"
            });
        }
        unifiedSpikes = unifiedSpikes.OrderBy(p => p.Position).ToList();
        double newMaxSpike = unifiedSpikes.Count > 0 ? unifiedSpikes.Max(s => s.Value) : 0;

        Log($"\n--- Profile {dir}: valid_spikes={validSpikes.Count} (max={maxSpike:F2}), " +
            $"strong_dips={strongDips.Count} (max={maxDip:F2}) ---");

        // 1. Spikes -> світлі лінії (100м) + "заховані" під чорними лініями
        // Запускаємо пошук ТІЛЬКИ якщо є хоча б 2 РЕАЛЬНІ світлі лінії для бази
        if (validSpikes.Count >= 2 && newMaxSpike > minSpikeThr)
            results.AddRange(ThresholdDescent(unifiedSpikes, profile.Length, dir, "spike", newMaxSpike, minSpikeThr));

        // 2. Dips -> чорні лінії (1000м)
        // Шукаємо 1000м кроки ТІЛЬКИ якщо є мінімум ДВІ реальні чорні лінії
        if (strongDips.Count >= 2)
            results.AddRange(Analyze1000mDips(strongDips, profile.Length, dir, maxDip, realBlackLineThr));

        return results;
    }

    private List<PeakInfo> FindPeaks(double[] profile, double[] dev, bool findSpikes)
    {
        var peaks = new List<PeakInfo>();
        int w = Math.Max(2, _pmin / 3);
        for (int i = 1; i < profile.Length - 1; i++)
        {
            if (findSpikes && dev[i] <= 0) continue;
            if (!findSpikes && dev[i] >= 0) continue;
            bool ok = true;
            for (int j = Math.Max(0, i - w); j <= Math.Min(profile.Length - 1, i + w); j++)
            {
                if (j == i) continue;
                if (findSpikes && profile[j] > profile[i]) { ok = false; break; }
                if (!findSpikes && profile[j] < profile[i]) { ok = false; break; }
            }
            if (ok)
                peaks.Add(new PeakInfo
                {
                    Position = i,
                    Value = dev[i],
                    Type = findSpikes ? "spike" : "dip"
                });
        }
        return MergeClose(peaks, Math.Max(2, _pmin / 2));
    }

    private static List<PeakInfo> MergeClose(List<PeakInfo> peaks, int minDist)
    {
        if (peaks.Count == 0) return peaks;
        var sorted = peaks.OrderBy(p => p.Position).ToList();
        var merged = new List<PeakInfo> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Position - merged[^1].Position < minDist)
            {
                if (Math.Abs(sorted[i].Value) > Math.Abs(merged[^1].Value))
                    merged[^1] = sorted[i];
            }
            else merged.Add(sorted[i]);
        }
        return merged;
    }

    private List<PeriodCandidate> ThresholdDescent(
        List<PeakInfo> allPeaks, int len, string dir, string type, double maxVal, double minThr)
    {
        var results = new List<PeriodCandidate>();
        for (int ti = 0; ti < 50; ti++)
        {
            double thr = maxVal * (1.0 - (double)ti / 50);
            if (thr < minThr) break;
            var active = allPeaks
                .Where(p => Math.Abs(p.Value) >= thr)
                .OrderBy(p => p.Position).ToList();
            if (active.Count < 3) continue;

            var pr = EstimatePeriod(active, len);
            if (pr == null) continue;
            var (period, cons) = pr.Value;
            if (period < _minRealisticPeriod || period > _pmax) continue;

            double score = ComputeScore(active, period, cons, thr, maxVal, len);
            results.Add(new PeriodCandidate
            {
                Period = period,
                Score = score,
                Method = $"{dir}_{type}_t{thr:F1}",
                PeakCount = active.Count,
                Consistency = cons,
                Direction = dir,
                Spikes = type == "spike" ? new(active) : new(),
                Dips = type == "dip" ? new(active) : new()
            });
        }
        return results;
    }

    private List<PeriodCandidate> ThresholdDescentCombined(
        List<PeakInfo> spikes, List<PeakInfo> dips, int len, string dir, double minThr)
    {
        var results = new List<PeriodCandidate>();
        double maxVal = Math.Max(
            spikes.Count > 0 ? spikes.Max(s => s.Value) : 0,
            dips.Count > 0 ? dips.Max(s => Math.Abs(s.Value)) : 0);

        for (int ti = 0; ti < 50; ti++)
        {
            double thr = maxVal * (1.0 - (double)ti / 50);
            if (thr < minThr) break;
            var aS = spikes.Where(s => s.Value >= thr).ToList();
            var aD = dips.Where(s => Math.Abs(s.Value) >= thr).ToList();
            var all = aS.Concat(aD).OrderBy(p => p.Position).ToList();
            if (all.Count < 3) continue;

            var pr = EstimatePeriod(all, len);
            if (pr == null) continue;
            var (period, cons) = pr.Value;
            if (period < _minRealisticPeriod || period > _pmax) continue;

            double bonus = (aS.Count > 0 && aD.Count > 0) ? 1.08 : 1.0;
            double score = ComputeScore(all, period, cons, thr, maxVal, len) * bonus;
            results.Add(new PeriodCandidate
            {
                Period = period,
                Score = score,
                Method = $"{dir}_comb_t{thr:F1}",
                PeakCount = all.Count,
                Consistency = cons,
                Direction = dir,
                Spikes = new(aS),
                Dips = new(aD)
            });
        }
        return results;
    }

    private List<PeriodCandidate> Analyze1000mDips(
        List<PeakInfo> dips, int len, string dir, double maxVal, double minThr)
    {
        var results = new List<PeriodCandidate>();
        for (int ti = 0; ti < 50; ti++)
        {
            double thr = maxVal * (1.0 - (double)ti / 50);
            if (thr < minThr) break;
            var active = dips
                .Where(p => Math.Abs(p.Value) >= thr)
                .OrderBy(p => p.Position).ToList();
            if (active.Count < 2) continue;

            var pr = EstimatePeriod(active, len);
            if (pr == null) continue;
            var (p1000, cons) = pr.Value;
            double p100 = p1000 / 10.0;
            if (p100 < _minRealisticPeriod || p100 > _pmax || p1000 < 30) continue;

            double score = ComputeScore(active, p1000, cons, thr, maxVal, len);
            // Даємо бонус, якщо чорна сітка дуже стабільна
            if (cons > 0.8) score *= 1.15;
            results.Add(new PeriodCandidate
            {
                Period = p100,
                Score = score,
                Method = $"{dir}_1000m_t{thr:F1}",
                PeakCount = active.Count,
                Consistency = cons,
                Direction = dir,
                Dips = new(active),
                Is1000mDerived = true,
                Raw1000mPeriod = p1000
            });
        }
        return results;
    }

    // =============================================
    // КРОС-ВАЛІДАЦІЯ
    // =============================================

    private void CrossValidate(List<PeriodCandidate> candidates)
    {
        Log($"\n=== Cross-validation ===");

        var clusters = new List<(double centerPeriod, List<PeriodCandidate> members)>();
        foreach (var c in candidates
            .Where(x => x.Period >= _minRealisticPeriod)
            .OrderByDescending(x => x.Score))
        {
            bool found = false;
            foreach (var cl in clusters)
            {
                if (Math.Abs(c.Period - cl.centerPeriod) / cl.centerPeriod < 0.05)
                {
                    cl.members.Add(c);
                    found = true;
                    break;
                }
            }
            if (!found)
                clusters.Add((c.Period, new List<PeriodCandidate> { c }));
        }

        foreach (var cluster in clusters)
        {
            double p = cluster.centerPeriod;
            var members = cluster.members;

            bool hasH = members.Any(m => m.Direction == "H");
            bool hasV = members.Any(m => m.Direction == "V");
            bool bothDirs = hasH && hasV;

            bool has1000m = clusters.Any(cl =>
                Math.Abs(cl.centerPeriod - p * 10) / (p * 10) < 0.08);

            bool confirmedBy100m = clusters.Any(cl =>
                Math.Abs(cl.centerPeriod * 10 - p) / p < 0.08 &&
                cl.centerPeriod >= _minRealisticPeriod);

            double bonus = 1.0;
            string reasons = "";

            if (bothDirs) { bonus *= 1.12; reasons += "bothDir "; }
            if (has1000m) { bonus *= 1.10; reasons += "1000mExists "; }
            if (confirmedBy100m) { bonus *= 1.08; reasons += "100mConf "; }

            if (bonus > 1.01)
            {
                foreach (var m in members)
                    m.Score *= bonus;
                Log($"  Cluster P≈{p:F1}: bonus={bonus:F3} ({reasons}) " +
                    $"{members.Count} candidates");
            }
        }
    }

    // =============================================
    // PERIOD ESTIMATION
    // =============================================

    private (double period, double consistency)? EstimatePeriod(
        List<PeakInfo> peaks, int len)
    {
        if (peaks.Count < 2) return null;
        var gaps = new List<double>();
        for (int i = 1; i < peaks.Count; i++)
            gaps.Add(peaks[i].Position - peaks[i - 1].Position);
        if (gaps.Count == 0) return null;

        var sorted = gaps.OrderBy(g => g).ToList();
        var candidates = new HashSet<double>
        {
            sorted[0],
            sorted[sorted.Count / 2]
        };

        foreach (var g in sorted.Take(Math.Min(12, sorted.Count)))
            for (int d = 1; d <= 15; d++)
            {
                double c = g / d;
                if (c >= _minRealisticPeriod && c <= _pmax)
                    candidates.Add(Math.Round(c, 1));
            }

        for (int i = 0; i < Math.Min(gaps.Count, 8); i++)
            for (int j = i + 1; j < Math.Min(gaps.Count, 8); j++)
            {
                double gcd = ApproxGCD(gaps[i], gaps[j]);
                if (gcd >= _minRealisticPeriod && gcd <= _pmax)
                    candidates.Add(Math.Round(gcd, 1));
            }

        double bestP = 0, bestC = 0;
        foreach (var c in candidates)
        {
            if (c < _minRealisticPeriod || c > _pmax) continue;
            double cons = EvalCons(gaps, c);
            if (cons > bestC) { bestC = cons; bestP = c; }
        }

        if (bestP > 0)
        {
            bestP = Refine(gaps, bestP);
            bestC = EvalCons(gaps, bestP);
        }
        if (bestP < _minRealisticPeriod || bestC < 0.1) return null;
        return (bestP, bestC);
    }

    private double EvalCons(List<double> gaps, double p)
    {
        if (p < 0.5) return 0;
        double total = 0;
        foreach (var g in gaps)
        {
            double r = g / p;
            int n = Math.Max(1, (int)Math.Round(r));
            double score = Math.Max(0, 1.0 - Math.Abs(r - n) / n * 5.0);
            score *= Math.Pow(0.85, n - 1);
            total += score;
        }
        return gaps.Count > 0 ? total / gaps.Count : 0;
    }

    private double Refine(List<double> gaps, double init)
    {
        double best = init, bestS = EvalCons(gaps, init);
        for (double d = -init * 0.15; d <= init * 0.15; d += init * 0.005)
        {
            double c = init + d;
            if (c < _minRealisticPeriod) continue;
            double s = EvalCons(gaps, c);
            if (s > bestS) { bestS = s; best = c; }
        }
        for (double d = -init * 0.01; d <= init * 0.01; d += 0.1)
        {
            double c = best + d;
            if (c < _minRealisticPeriod) continue;
            double s = EvalCons(gaps, c);
            if (s > bestS) { bestS = s; best = c; }
        }
        return best;
    }

    // =============================================
    // SCORING
    // =============================================

    private static double ComputeScore(
        List<PeakInfo> peaks, double period, double cons,
        double thr, double maxDev, int len)
    {
        double fCons = cons;
        double fCount = peaks.Count switch
        {
            <= 2 => 0.10,
            3 => 0.25,
            4 => 0.35,
            5 => 0.45,
            <= 8 => 0.50 + (peaks.Count - 5) * 0.06,
            <= 15 => 0.70 + (peaks.Count - 8) * 0.03,
            _ => Math.Min(1.0, 0.90 + (peaks.Count - 15) * 0.005)
        };

        double span = peaks.Last().Position - peaks.First().Position;
        double fCoverage = Math.Min(1.0, span / (len * 0.3));

        double expectedPeaks = span / period;
        double fDensity = expectedPeaks > 0
            ? Math.Min(1.0, peaks.Count / expectedPeaks) : 0;

        double fPeriodOk = period < len / 3.0
            ? 1.0 : Math.Max(0.2, 1.0 - (period / len - 0.33) * 3.0);

        return fCons * 0.30 + fCount * 0.25 + fCoverage * 0.10 +
               fDensity * 0.20 + fPeriodOk * 0.15;
    }

    // ===========================================
    //  HELPERS
    // ===========================================

    private static double MaxNearby(double[] arr, int center, int radius, int maxIdx)
    {
        double max = 0;
        for (int d = -radius; d <= radius; d++)
        {
            int idx = center + d;
            if (idx >= 1 && idx <= maxIdx)
                max = Math.Max(max, arr[idx]);
        }
        return max;
    }

    private static double ApproxGCD(double a, double b)
    {
        if (a < b) (a, b) = (b, a);
        int iter = 50;
        while (b > 0.5 && iter-- > 0) { var t = b; b = a % b; a = t; }
        return a;
    }

    // =============================================
    // Debug helpers
    // =============================================

    private List<(int lag, double value)> FindACFDebugPeaks(
        double[] acf, int pmin, int pmax, int radius)
    {
        var peaks = new List<(int lag, double value)>();
        for (int lag = pmin; lag <= pmax && lag < acf.Length; lag++)
        {
            double val = acf[lag];
            if (val <= 0) continue;

            bool isPeak = true;
            int jMin = Math.Max(1, lag - radius);
            int jMax = Math.Min(acf.Length - 1, lag + radius);
            for (int j = jMin; j <= jMax; j++)
            {
                if (j != lag && acf[j] > val) { isPeak = false; break; }
            }
            if (isPeak) peaks.Add((lag, val));
        }
        return peaks;
    }

    private void Log(string msg) => _debugLog.Add(msg);
}
