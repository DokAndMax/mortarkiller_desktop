// AutoMortarConfig.cs
using System;
using System.IO;

namespace mortarkiller;

/// <summary>
/// Конфігурація AutoMortarRunner — всі параметри в одному місці.
/// </summary>
public sealed class AutoMortarConfig
{
    public string ProcessName { get; init; } = "TslGame";
    public int IntervalMs { get; init; } = 200;
    public double CropTopPercent { get; init; } = 0.08;
    public double CropSidePercent { get; init; } = 0.47;
    public bool EnableDebug { get; init; } = true;
    public string DebugRoot { get; init; } =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug");
    public bool UseScaleSmoothing { get; init; } = false;
}