// Core/Yolo/Benchmark/ModelParser.cs
using PUBGVisionTest.Core.Yolo.Detectors;
using System.Text.RegularExpressions;

namespace PUBGVisionTest.Core.Yolo.Benchmark;

/// <summary>
/// Парсинг .onnx файлів з імені та метаданих.
/// </summary>
public static class ModelParser
{
    public static List<ModelInfo> ParseModels(string modelsDir, int numLabels)
    {
        var models = new List<ModelInfo>();

        if (!Directory.Exists(modelsDir))
        {
            Directory.CreateDirectory(modelsDir);
            return models;
        }

        var rxTrainExport = new Regex(@"train(\d+).*export(\d+)", RegexOptions.IgnoreCase);
        var rxImgsz = new Regex(@"imgsz(\d+)", RegexOptions.IgnoreCase);

        foreach (var path in Directory.GetFiles(modelsDir, "*.onnx").OrderBy(f => f))
        {
            var fn = Path.GetFileNameWithoutExtension(path);
            bool fp16 = fn.Contains("fp16", StringComparison.OrdinalIgnoreCase);

            string fnClean = fn;
            if (fp16) fnClean = Regex.Replace(fnClean, @"_?fp16$", "", RegexOptions.IgnoreCase);
            bool isSliced = fnClean.EndsWith("_sliced", StringComparison.OrdinalIgnoreCase);

            int trainSz = 640, exportSz = 640;

            var m = rxTrainExport.Match(fn);
            if (m.Success)
            {
                trainSz = int.Parse(m.Groups[1].Value);
                exportSz = int.Parse(m.Groups[2].Value);
            }
            else
            {
                m = rxImgsz.Match(fn);
                if (m.Success) trainSz = exportSz = int.Parse(m.Groups[1].Value);
            }

            // Перевірка реального input size
            try
            {
                using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(path);
                var inputMeta = session.InputMetadata.First().Value;
                var dims = inputMeta.Dimensions;
                if (dims.Length == 4 && dims[2] > 0)
                {
                    int realSize = dims[2];
                    if (realSize != exportSz)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  NOTE: {fn} filename says export={exportSz} " +
                            $"but model input is {realSize}x{realSize}. Using {realSize}.");
                        Console.ResetColor();
                        exportSz = realSize;
                    }
                }
            }
            catch { }

            // Автодетекція версії YOLO
            YoloVersion version = YoloVersion.V8_V11;
            try
            {
                version = YoloVersionDetector.Detect(path, numLabels);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  WARNING: Cannot detect YOLO version for {fn}: {ex.Message}");
                Console.ResetColor();
            }

            string versionStr = version == YoloVersion.V10 ? "YOLOv10" : "YOLOv8/v11";
            string slicedStr = isSliced ? " SLICED" : "";
            Console.WriteLine($"  {fn}: Train={trainSz} Export={exportSz} FP16={fp16} " +
                $"Version={versionStr}{slicedStr}");

            models.Add(new ModelInfo
            {
                Path = path,
                TrainImgsz = trainSz,
                ExportImgsz = exportSz,
                IsFp16 = fp16,
                IsSliced = isSliced,
                YoloVersion = version
            });
        }

        return models;
    }

    /// <summary>
    /// Генерує всі конфігурації бенчмарку з знайдених моделей та доступних бекендів.
    /// </summary>
    public static List<BenchmarkRunConfig> BuildConfigs(
        List<ModelInfo> models,
        string[] labels,
        List<DetectorBackend> availableBackends)
    {
        var configs = new List<BenchmarkRunConfig>();

        foreach (var model in models)
        {
            foreach (var backend in availableBackends)
            {
                // FP16 ONNX не працює з Emgu бекендами
                if (model.IsFp16 && (backend == DetectorBackend.EmguCpu
                                  || backend == DetectorBackend.EmguGpu
                                  || backend == DetectorBackend.EmguGpuFp16))
                    continue;

                // YOLOv10 не працює з Emgu (OpenCV DNN не підтримує вбудований NMS)
                if (model.YoloVersion == YoloVersion.V10
                    && (backend == DetectorBackend.EmguCpu
                     || backend == DetectorBackend.EmguGpu
                     || backend == DetectorBackend.EmguGpuFp16))
                    continue;

                // Native inference
                configs.Add(new BenchmarkRunConfig
                {
                    Name = $"{model.FileName}_{backend}",
                    ModelPath = model.Path,
                    Backend = backend,
                    TrainImgsz = model.TrainImgsz,
                    ExportImgsz = model.ExportImgsz,
                    IsFp16 = model.IsFp16 || backend == DetectorBackend.EmguGpuFp16,
                    Labels = labels,
                    YoloVersion = model.YoloVersion,
                    IsSliced = model.IsSliced,
                    SliceSize = model.ExportImgsz,
                    SliceOverlap = 0.2f
                });

                // Даунскейл варіанти
                if (model.ExportImgsz >= 1280)
                {
                    int[] dsWidths = model.ExportImgsz switch
                    {
                        >= 1920 => new[] { 1280, 960, 640 },
                        >= 1280 => new[] { 960, 640 },
                        _ => Array.Empty<int>()
                    };

                    foreach (var dsW in dsWidths)
                    {
                        configs.Add(new BenchmarkRunConfig
                        {
                            Name = $"{model.FileName}_{backend}_ds{dsW}",
                            ModelPath = model.Path,
                            Backend = backend,
                            TrainImgsz = model.TrainImgsz,
                            ExportImgsz = model.ExportImgsz,
                            IsFp16 = model.IsFp16 || backend == DetectorBackend.EmguGpuFp16,
                            DownscaleScreenshot = true,
                            ScreenshotDownscaleWidth = dsW,
                            Labels = labels,
                            YoloVersion = model.YoloVersion,
                            IsSliced = model.IsSliced,
                            SliceSize = model.ExportImgsz,
                            SliceOverlap = 0.2f
                        });
                    }
                }
            }
        }

        return configs;
    }
}