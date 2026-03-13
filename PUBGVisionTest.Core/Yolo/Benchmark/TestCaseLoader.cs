// Core/Yolo/Benchmark/TestCaseLoader.cs
using System.Text.Json;

namespace PUBGVisionTest.Core.Yolo.Benchmark;

public static class TestCaseLoader
{
    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".tiff" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static List<TestCase> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Directory not found: {directory}");

        var testCases = new List<TestCase>();

        var imageFiles = Directory.GetFiles(directory)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        Console.WriteLine($"Found {imageFiles.Count} test images in '{directory}'");

        foreach (var imagePath in imageFiles)
        {
            var tc = new TestCase { ImagePath = imagePath };

            var jsonPath = Path.ChangeExtension(imagePath, ".json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var json = File.ReadAllText(jsonPath);
                    var annotation = JsonSerializer.Deserialize<TestCaseAnnotation>(json, JsonOpts);
                    if (annotation?.ExpectedObjects != null)
                    {
                        tc.ExpectedObjects = annotation.ExpectedObjects;
                        Console.WriteLine($"  {tc.ImageName}: {tc.ExpectedObjects.Count} expected objects");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: {jsonPath}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"  {tc.ImageName}: no annotation (speed-only)");
            }

            testCases.Add(tc);
        }

        return testCases;
    }

    public static void CreateAnnotationTemplate(string imagePath)
    {
        var jsonPath = Path.ChangeExtension(imagePath, ".json");

        var template = new TestCaseAnnotation
        {
            ExpectedObjects = new List<ExpectedObject>
            {
                new() { Label = "Player", CenterX = 960, CenterY = 540, ToleranceRadius = 50 },
                new() { Label = "Pin", CenterX = 500, CenterY = 300, ToleranceRadius = 30, UseBottomTip = true }
            }
        };

        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
        Console.WriteLine($"Created template: {jsonPath}");
    }
}