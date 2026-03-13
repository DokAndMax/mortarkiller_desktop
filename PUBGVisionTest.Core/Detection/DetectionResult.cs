namespace PUBGVisionTest.Core.Detection;

/// <summary>
/// Результат детекції сітки на зображенні.
/// </summary>
public class DetectionResult
{
    public bool Success;
    public string? FailReason;

    /// <summary>Крок малої сітки (100м) у пікселях</summary>
    public double SmallGridStep;
    /// <summary>Крок великої сітки (1км) у пікселях</summary>
    public double LargeGridStep;
    /// <summary>Відношення великої до малої сітки</summary>
    public double Ratio;

    /// <summary>Зсув сітки по X (для оверлею)</summary>
    public int ShiftX;
    /// <summary>Зсув сітки по Y (для оверлею)</summary>
    public int ShiftY;

    /// <summary>Період, знайдений по горизонтальному профілю</summary>
    public int DetectedPeriodH;
    /// <summary>Період, знайдений по вертикальному профілю</summary>
    public int DetectedPeriodV;

    /// <summary>px на 100м</summary>
    public double PxPer100m => SmallGridStep;

    /// <summary>Метод, яким знайдено результат</summary>
    public string? Method;

    /// <summary>Скор найкращого кандидата</summary>
    public double BestScore;

    // ── Debug дані ──
    public DebugData? Debug;

    public DetectionResult Fail(string reason)
    {
        Success = false;
        FailReason = reason;
        return this;
    }
}

/// <summary>
/// Діагностичні дані для візуалізації та відлагодження.
/// </summary>
public class DebugData : IDisposable
{
    public Emgu.CV.Mat? MaskVert, MaskHoriz, MaskCombined, ValidArea;
    public double[]? HorizProfile, VertProfile;
    public double[]? AutocorrH, AutocorrV, AutocorrCombined;
    public List<(int lag, double value)> AutocorrPeaks = new();
    public List<string> DebugLog = new();
    public List<PeakInfo> SpikePeaks = new();
    public List<PeakInfo> DipPeaks = new();

    public void Dispose()
    {
        MaskVert?.Dispose();
        MaskHoriz?.Dispose();
        MaskCombined?.Dispose();
        ValidArea?.Dispose();
    }
}