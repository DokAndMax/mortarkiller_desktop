namespace GridDetector.Core.Helpers;

/// <summary>
/// Exponentially Weighted Moving Average — згладжування значень.
/// </summary>
public class EWMA
{
    private readonly double _alpha;
    private double? _s;

    public EWMA(double alpha = 0.25)
    {
        _alpha = Math.Clamp(alpha, 0.01, 1.0);
    }

    public double Value => _s ?? double.NaN;

    public double Update(double x)
    {
        _s = !_s.HasValue || !double.IsFinite(_s.Value)
            ? x
            : _alpha * x + (1 - _alpha) * _s.Value;
        return _s.Value;
    }

    public void Reset() => _s = null;
}