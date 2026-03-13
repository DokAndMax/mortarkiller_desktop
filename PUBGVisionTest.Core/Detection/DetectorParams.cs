namespace PUBGVisionTest.Core.Detection;

/// <summary>
/// Параметри детектора сітки.
/// </summary>
public class DetectorParams
{
    /// <summary>Мінімальний період пошуку (пікселі)</summary>
    public int PMin = 10;

    /// <summary>Максимальний період пошуку (пікселі)</summary>
    public int PMax = 450;

    /// <summary>Розмір ядра морфологічної операції для виділення ліній</summary>
    public int MorphKernelSize = 3;

    public DetectorParams Clone() => (DetectorParams)MemberwiseClone();
}