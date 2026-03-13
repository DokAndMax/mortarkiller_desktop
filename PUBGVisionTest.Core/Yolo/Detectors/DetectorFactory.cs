// Core/Yolo/Detectors/DetectorFactory.cs
namespace PUBGVisionTest.Core.Yolo.Detectors;

/// <summary>
/// Фабрика для створення YOLO-детекторів за типом бекенду.
/// </summary>
public static class DetectorFactory
{
    public static IYoloDetector Create(DetectorBackend backend)
    {
        return backend switch
        {
            DetectorBackend.EmguCpu or
            DetectorBackend.EmguGpu or
            DetectorBackend.EmguGpuFp16 => new EmguYoloDetector(backend),

            DetectorBackend.OnnxCpu => new OnnxYoloDetector(useGpu: false),
            DetectorBackend.OnnxGpu => new OnnxYoloDetector(useGpu: true),

            _ => throw new ArgumentException($"Unknown backend: {backend}")
        };
    }

    /// <summary>
    /// Визначає які бекенди реально доступні на поточній машині.
    /// </summary>
    public static List<DetectorBackend> DetectAvailableBackends()
    {
        var available = new List<DetectorBackend>();

        // OnnxCpu — завжди
        available.Add(DetectorBackend.OnnxCpu);
        Console.WriteLine("  [OK] OnnxRuntime CPU");

        // OnnxGpu
        try
        {
            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions();
            opts.AppendExecutionProvider_CUDA(0);
            opts.Dispose();
            available.Add(DetectorBackend.OnnxGpu);
            Console.WriteLine("  [OK] OnnxRuntime CUDA GPU");
        }
        catch
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  [--] OnnxRuntime GPU: not available");
            Console.ResetColor();
        }

        // EmguCpu — завжди
        available.Add(DetectorBackend.EmguCpu);
        Console.WriteLine("  [OK] Emgu.CV CPU");

        // EmguGpu
        try
        {
            available.Add(DetectorBackend.EmguGpu);
            if (Emgu.CV.CvInvoke.HaveOpenCLCompatibleGpuDevice)
                Console.WriteLine("  [OK] Emgu.CV CUDA GPU");
            else
                Console.WriteLine("  [??] Emgu.CV GPU: will try (CUDA may work without OpenCL)");
        }
        catch
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  [--] Emgu.CV GPU: not available");
            Console.ResetColor();
        }

        return available;
    }
}