using System.Collections.Generic;
using System.Linq;

namespace mortarkiller.Detection;

public sealed class PhaseMetrics
{
    private readonly Dictionary<string, List<long>> _timings = new();
    private readonly List<string> _order = new();

    public int Iterations { get; private set; }
    public long TotalPhaseMs { get; set; } // Загальний час перебування всередині фази

    public void IncrementIterations() => Iterations++;

    public void Record(string name, long ms)
    {
        if (!_timings.TryGetValue(name, out var list))
        {
            list = new List<long>();
            _timings[name] = list;
            _order.Add(name);
        }
        list.Add(ms);
    }

    public string Format(string phaseLabel)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{phaseLabel}: iters={Iterations}, TotalTime={TotalPhaseMs}ms");

        foreach (var name in _order)
        {
            var values = _timings[name];
            if (values.Count == 0) continue;
            
            sb.AppendLine($"  {name}: n={values.Count}, " +
                          $"avg={values.Average():F1}ms, " +
                          $"min={values.Min()}ms, " +
                          $"max={values.Max()}ms");
        }
        return sb.ToString();
    }
}

public sealed class DetectionMetrics
{
    public PhaseMetrics Phase1 { get; } = new();
    public PhaseMetrics Phase2 { get; } = new();
    
    public long SetupMs { get; set; }
    public long TransitionMs { get; set; }   // Час між фазами (відкриття карти)
    public long PostProcessMs { get; set; }  // Час після фази 2 (події, UI, математика)
    public long AudioFeedbackMs { get; set; }
    public long TotalMs { get; set; }

    public int DetectionNumber { get; set; }
    public string PinClass { get; set; } = "";
    public string PlayerClass { get; set; } = "";

    public string FormatSummary()
    {
        long sumOfParts = SetupMs + Phase1.TotalPhaseMs + TransitionMs + Phase2.TotalPhaseMs + PostProcessMs;
        long unaccounted = TotalMs - sumOfParts; // Має бути близько 0-5 мс (накладні витрати коду)

        return $"DetNo={DetectionNumber}\n" +
               $"Pin={PinClass} | Player={PlayerClass}\n" +
               $"==============================\n" +
               $"TotalDetectionTime = {TotalMs}ms\n" +
               $"  |- Setup       = {SetupMs}ms\n" +
               $"  |- Phase 1     = {Phase1.TotalPhaseMs}ms\n" +
               $"  |- Transition  = {TransitionMs}ms\n" +
               $"  |- Phase 2     = {Phase2.TotalPhaseMs}ms\n" +
               $"  |- PostProcess = {PostProcessMs}ms\n" +
               $"  |- Audio (TTS) = {AudioFeedbackMs}ms\n" +
               $"  |- Unaccounted = {unaccounted}ms\n" +
               $"==============================\n\n" +
               Phase1.Format("Phase1 Details") + "\n" +
               Phase2.Format("Phase2 Details");
    }
}