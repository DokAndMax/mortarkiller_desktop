namespace PUBGVisionTest.Core.Detection;

/// <summary>
/// Інформація про один пік у профілі.
/// </summary>
public class PeakInfo
{
    public int Position { get; set; }
    public double Value { get; set; }
    public string Type { get; set; } = "";
}

/// <summary>
/// Кандидат на період сітки (внутрішня структура аналізатора).
/// </summary>
internal class PeriodCandidate
{
    public double Period { get; set; }
    public double Score { get; set; }
    public string Method { get; set; } = "";
    public List<PeakInfo> Spikes { get; set; } = [];
    public List<PeakInfo> Dips { get; set; } = [];
    public int PeakCount { get; set; }
    public double Consistency { get; set; }
    public bool Is1000mDerived { get; set; }
    public double Raw1000mPeriod { get; set; }
    public string Direction { get; set; } = "";
}